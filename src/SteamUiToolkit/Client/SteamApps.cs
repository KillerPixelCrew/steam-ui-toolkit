using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>What the running client reports about one app's launch and install configuration.</summary>
/// <param name="LaunchOptions">A Steam title's launch options.</param>
/// <param name="ShortcutExe">A non-Steam shortcut's Target, verbatim (Steam stores it quoted).</param>
/// <param name="ShortcutLaunchOptions">A non-Steam shortcut's Launch Arguments.</param>
/// <param name="ShortcutStartDir">A non-Steam shortcut's start directory.</param>
/// <param name="InstallFolder">
///     A store title's install folder. Steam never exposes a store title's executable, so this is the
///     closest thing it reports to where the game runs from.
/// </param>
public sealed record SteamAppDetails(
    string LaunchOptions,
    string ShortcutExe,
    string ShortcutLaunchOptions,
    string ShortcutStartDir,
    string InstallFolder);

/// <summary>A custom artwork slot, numbered as Steam's <c>SetCustomArtworkForApp</c> expects.</summary>
/// <remarks>
///     Icons are deliberately absent. Steam has no client call that changes one: a store title's icon
///     lives in a versioned per-app cache, and a shortcut's needs a <c>shortcuts.vdf</c> edit and a
///     restart.
/// </remarks>
public enum SteamArtworkSlot
{
    /// <summary>Portrait capsule (600×900).</summary>
    Grid = 0,

    /// <summary>Hero banner (1920×620).</summary>
    Hero = 1,

    /// <summary>Transparent logo.</summary>
    Logo = 2,

    /// <summary>Wide capsule (460×215).</summary>
    Wide = 3
}

/// <summary>Image encodings Steam accepts for custom artwork.</summary>
public enum SteamArtworkFormat
{
    /// <summary>PNG.</summary>
    Png,

    /// <summary>JPEG.</summary>
    Jpeg,

    /// <summary>WebP.</summary>
    Webp
}

/// <summary>A custom logo placement in Steam's app-details store.</summary>
/// <param name="Anchor">
///     Steam's <c>pinnedPosition</c>: where the logo sits on the hero image, such as <c>BottomLeft</c>
///     or <c>CenterCenter</c>.
/// </param>
/// <param name="WidthPercent">The logo's width as a percentage of the hero's width, 5 to 100.</param>
/// <param name="HeightPercent">The logo's height as a percentage of the hero's height, 5 to 100.</param>
public sealed record SteamLogoPosition(string Anchor, int WidthPercent, int HeightPercent);

/// <summary>Outcome of asking the running client to create a non-Steam shortcut.</summary>
/// <param name="Outcome">
///     Whether the request was never sent, may have created an entry, was refused, or created one.
///     <see cref="SteamClientWriteOutcome.Unknown" /> means a shortcut may exist that nobody can name;
///     the caller reports it and scans again rather than adding a second time.
/// </param>
/// <param name="AppId">
///     The shortcut's id: the one the library gained when it gained exactly one, else the one Steam
///     returned. Zero when Steam reported nothing.
/// </param>
/// <param name="Confirmed">
///     Whether the library gained exactly this one shortcut. Only then is the id known to be the entry
///     this call made; an unconfirmed id may name another entry, or none, and must not be written to.
/// </param>
/// <param name="Error">Why the id is not confirmed, or why nothing was created. Null when confirmed.</param>
/// <param name="Mismatch">
///     The fields that did not read back as written, or null when all of them did. The shortcut exists
///     either way; this says what it holds is not what was asked for.
/// </param>
public readonly record struct SteamShortcutAddResult(
    SteamClientWriteOutcome Outcome,
    uint AppId,
    bool Confirmed,
    string? Error,
    string? Mismatch = null)
{
    /// <summary>Whether Steam created a shortcut and its id is known.</summary>
    public bool Succeeded => Outcome == SteamClientWriteOutcome.Applied && AppId != 0;
}

/// <summary>One non-Steam shortcut as the running client holds it.</summary>
/// <param name="AppId">Its generated id.</param>
/// <param name="Name">Its name in the library.</param>
/// <param name="Target">Its Target, verbatim.</param>
/// <param name="StartDirectory">Its start directory, verbatim.</param>
/// <param name="LaunchOptions">Its Launch Arguments, verbatim.</param>
public sealed record SteamShortcut(
    uint AppId,
    string Name,
    string Target,
    string StartDirectory,
    string LaunchOptions);

/// <summary>Reads and changes per-app configuration in the running client through <c>SteamClient.Apps</c>.</summary>
/// <remarks>
///     <para>
///         Steam stores launch values <em>verbatim</em>: it neither adds nor strips the quotes its own
///         shortcuts carry and leaves backslashes alone. It persists them to <c>shortcuts.vdf</c> and
///         <c>localconfig.vdf</c> immediately, so no restart is needed.
///     </para>
///     <para>
///         Every change goes through the client's write lane (<see cref="SteamClient" />), one at a time.
///     </para>
///     <para>
///         A store title and a non-Steam shortcut take different calls. A store title's launch options
///         expand <c>%command%</c> to the game's own command line; a shortcut ignores an exe-replacing
///         launch option and runs its Target anyway, so replacing what a shortcut runs means writing its
///         Target and Launch Arguments.
///     </para>
/// </remarks>
public sealed class SteamApps
{
    private const uint ShortcutAppIdFloor = 0x80000000;

    // Steam answers a live app almost immediately, so a short bound keeps an unknown id from hanging.
    private const int DetailsTimeoutMs = 3_000;

    // How long a new shortcut's fields may take to read back as written. Steam applies each setter on
    // its own thread, so the first read can still see the old value; the read repeats until the fields
    // match or this passes, and only a difference left after it is reported.
    private const int ReadBackMs = 2_000;

    // Steam resolves ClearCustomArtworkForApp before the clear finishes, so a set issued immediately
    // can race it (observed in decky-steamgriddb). Nothing reports the clear's completion: the promise
    // is what resolves early, no notification names artwork, and the overview's rt_custom_image_mtime
    // counts whole seconds, so a clear and a set in the same second look alike.
    private const int ArtworkClearSettleMs = 500;

    // How long a new shortcut may take to appear in the library before the add is reported unconfirmed.
    private const int AddAppearMs = 2_000;

    // How many shortcuts' details one library read asks for at a time.
    private const int DetailsBatch = 32;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private readonly SteamClient _client;

    /// <summary>Creates the app and shortcut operations façade without opening a connection.</summary>
    /// <param name="client">Owning client; borrowed for dispatch and lifetime, never disposed by this façade.</param>
    internal SteamApps(SteamClient client)
    {
        _client = client;
    }

    /// <summary>
    ///     Converts a stored app id to the unsigned 32-bit id Steam's client API expects. A shortcut id
    ///     kept in a signed integer reads back negative.
    /// </summary>
    /// <param name="appId">The stored id.</param>
    /// <returns>The unsigned id.</returns>
    public static uint NormalizeAppId(long appId)
    {
        return unchecked((uint)appId);
    }

    /// <summary>Whether Steam models the app id as a non-Steam shortcut.</summary>
    /// <param name="appId">The unsigned app id.</param>
    /// <returns><see langword="true" /> for a generated shortcut id.</returns>
    public static bool IsShortcutAppId(uint appId)
    {
        return appId >= ShortcutAppIdFloor;
    }

    /// <summary>Reads one app's launch and install configuration.</summary>
    /// <param name="appId">The Steam app id, or a shortcut's generated id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The details. An answered failure (Steam has no details for the id) fails the read with Steam's
    ///     reason; never throws for an unreachable client.
    /// </returns>
    public Task<SteamReadResult<SteamAppDetails>> ReadDetailsAsync(
        uint appId,
        CancellationToken cancellationToken = default)
    {
        return ReadDetailsAsync(appId, Budget, cancellationToken);
    }

    /// <summary>Replaces a store title's launch options.</summary>
    /// <param name="appId">The Steam app id.</param>
    /// <param name="launchOptions">The new value, stored verbatim.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What became of the change.</returns>
    public Task<SteamClientWriteResult> SetLaunchOptionsAsync(
        uint appId,
        string launchOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchOptions);
        return WriteAsync(SteamClientScript.Write(
            "await SteamClient.Apps.SetAppLaunchOptions(" + SteamClientScript.AppId(appId) + "," +
            SteamCef.JsString(launchOptions) + ");"), cancellationToken);
    }

    /// <summary>Replaces what a non-Steam shortcut runs.</summary>
    /// <param name="appId">The shortcut's generated id.</param>
    /// <param name="target">The new Target, stored verbatim (quote a path that contains spaces).</param>
    /// <param name="launchArguments">The new Launch Arguments, stored verbatim.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What became of the change.</returns>
    /// <remarks>The start directory is never written, so the shortcut keeps its working directory.</remarks>
    public Task<SteamClientWriteResult> SetShortcutLaunchAsync(
        uint appId,
        string target,
        string launchArguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(launchArguments);
        return WriteAsync(SteamClientScript.Write(
            "const app=" + SteamClientScript.AppId(appId) + ";" +
            "await SteamClient.Apps.SetShortcutExe(app," + SteamCef.JsString(target) + ");" +
            "await SteamClient.Apps.SetShortcutLaunchOptions(app," + SteamCef.JsString(launchArguments) + ");"),
            cancellationToken);
    }

    /// <summary>Replaces what a non-Steam shortcut runs and where it runs from.</summary>
    /// <param name="appId">The shortcut's generated id.</param>
    /// <param name="target">The new Target, stored verbatim (quote a path that contains spaces).</param>
    /// <param name="startDirectory">The new start directory, stored verbatim.</param>
    /// <param name="launchArguments">The new Launch Arguments, stored verbatim.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What became of the change.</returns>
    /// <remarks>
    ///     For a caller that owns the whole command. Moving a shortcut from one program to another and
    ///     leaving the old start directory behind runs the new program from the old one's folder.
    /// </remarks>
    public Task<SteamClientWriteResult> SetShortcutLaunchAsync(
        uint appId,
        string target,
        string startDirectory,
        string launchArguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(startDirectory);
        ArgumentNullException.ThrowIfNull(launchArguments);
        return WriteAsync(SteamClientScript.Write(
            "const app=" + SteamClientScript.AppId(appId) + ";" +
            "await SteamClient.Apps.SetShortcutExe(app," + SteamCef.JsString(target) + ");" +
            "await SteamClient.Apps.SetShortcutStartDir(app," + SteamCef.JsString(startDirectory) + ");" +
            "await SteamClient.Apps.SetShortcutLaunchOptions(app," + SteamCef.JsString(launchArguments) + ");"),
            cancellationToken);
    }

    /// <summary>Reads every non-Steam shortcut in the library, with what each one runs, in one call.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The shortcuts, or why the library could not be read whole.</returns>
    /// <remarks>
    ///     All or nothing. A shortcut whose details Steam did not return fails the read rather than
    ///     appearing with empty fields: a caller comparing what it wrote against what Steam holds would
    ///     read an empty Target as somebody else's entry. An empty list means the library has none; it
    ///     never stands in for a read that failed.
    /// </remarks>
    public async Task<SteamReadResult<IReadOnlyList<SteamShortcut>>> ListShortcutsAsync(
        CancellationToken cancellationToken = default)
    {
        var expression = SteamClientScript.Read(
            SteamClientScript.ShortcutIdsFunction +
            "const apps=shortcutApps();" +
            "if(!apps)return JSON.stringify({ok:false,err:'Steam has not loaded its library yet.'});" +
            "const details=" + SteamClientScript.AppDetailsFunction(DetailsTimeoutMs) + ";" +
            "const out=[];" +
            "for(let i=0;i<apps.length;i+=" + DetailsBatch.ToString(CultureInfo.InvariantCulture) + "){" +
            "const batch=apps.slice(i,i+" + DetailsBatch.ToString(CultureInfo.InvariantCulture) + ");" +
            "const read=await Promise.all(batch.map(async a=>{const d=await details(a.id);" +
            "return d?{id:String(a.id),name:a.name,exe:d.strShortcutExe||'',dir:d.strShortcutStartDir||''," +
            "args:d.strShortcutLaunchOptions||''}:{id:String(a.id),missing:true};}));" +
            "const gap=read.find(s=>s.missing);" +
            "if(gap)return JSON.stringify({ok:false,err:'Steam did not return the details for shortcut '+gap.id+'.'});" +
            "out.push(...read);}" +
            "return JSON.stringify({ok:true,shortcuts:out});");
        var result = await _client.ReadAsync(expression, Budget, cancellationToken).ConfigureAwait(false);
        return ParseShortcuts(result);
    }

    /// <summary>Creates a non-Steam shortcut and confirms which entry in the library it is.</summary>
    /// <param name="name">The entry's name in the library.</param>
    /// <param name="target">The Target, stored verbatim (quote a path that contains spaces).</param>
    /// <param name="startDirectory">The working directory, stored verbatim.</param>
    /// <param name="launchArguments">The Launch Arguments, stored verbatim.</param>
    /// <param name="cancellationToken">
    ///     Cancels the request before it is sent. Once Steam may have the entry the call runs to its
    ///     answer, so a caller never loses track of a shortcut that was made.
    /// </param>
    /// <returns>The new id, whether it is confirmed, and what did not read back as written.</returns>
    /// <remarks>
    ///     <para>
    ///         Steam derives the id itself and persists the entry to <c>shortcuts.vdf</c> immediately, so
    ///         no restart is needed and no caller has to reproduce Steam's derivation.
    ///     </para>
    ///     <para>
    ///         The id is confirmed by two independent sources, what the client returned and a
    ///         before-and-after diff of the library, and the diff is the authority: the return value's
    ///         contract has never been verified across client builds, while the diff is observation.
    ///         The fields are then set on the entry the library gained, never on the returned id alone.
    ///         When Steam returns no id, the one entry the library gained is adopted only when its Target
    ///         and name are the ones asked for; anything else is left alone and reported
    ///         <see cref="SteamClientWriteOutcome.Unknown" />, because it may be somebody else's.
    ///     </para>
    ///     <para>
    ///         The fields are written twice on purpose. <c>AddShortcut</c>'s positional contract is not
    ///         one this library has verified either, while <c>SetShortcutName</c>,
    ///         <c>SetShortcutExe</c>, <c>SetShortcutStartDir</c> and <c>SetShortcutLaunchOptions</c> are
    ///         the calls every shortcut manager relies on. The name is the one that bites: a client
    ///         can ignore the name it is passed and call the entry after its executable. The fields are
    ///         read back until they match or two seconds pass, and any that still differs is reported.
    ///     </para>
    /// </remarks>
    public async Task<SteamShortcutAddResult> AddShortcutAsync(
        string name,
        string target,
        string startDirectory,
        string launchArguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(startDirectory);
        ArgumentNullException.ThrowIfNull(launchArguments);

        var expression = AddShortcutScript(name, target, startDirectory, launchArguments);
        if (cancellationToken.IsCancellationRequested)
        {
            return new SteamShortcutAddResult(
                SteamClientWriteOutcome.NotSent, 0, false, "The request was cancelled before it was sent.");
        }

        var result = await _client.WriteAsync(expression, Budget, CancellationToken.None).ConfigureAwait(false);
        var outcome = ParseAddShortcut(result);
        if (outcome is { Outcome: SteamClientWriteOutcome.Applied, Confirmed: false }
            or { Outcome: SteamClientWriteOutcome.Unknown })
        {
            SteamUiLog.Warn($"Steam did not confirm a new shortcut: {outcome.Error}");
        }
        else if (outcome.Mismatch is { } mismatch)
        {
            SteamUiLog.Warn($"A new shortcut did not read back as written: {mismatch}");
        }

        return outcome;
    }

    /// <summary>Deletes a non-Steam shortcut from the running client's library.</summary>
    /// <param name="appId">The shortcut's generated id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What became of the removal.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="appId" /> is not in the generated-shortcut range. A store title has no
    ///     shortcut entry to delete, and removing one is not something this call can undo.
    /// </exception>
    public Task<SteamClientWriteResult> RemoveShortcutAsync(
        uint appId,
        CancellationToken cancellationToken = default)
    {
        if (!IsShortcutAppId(appId))
        {
            throw new ArgumentOutOfRangeException(
                nameof(appId), appId, "Only a non-Steam shortcut id can be removed.");
        }

        return WriteAsync(SteamClientScript.Write(
            "if(typeof SteamClient?.Apps?.RemoveShortcut!=='function')" +
            "throw new Error('This Steam client does not expose RemoveShortcut.');" +
            "await SteamClient.Apps.RemoveShortcut(" + SteamClientScript.AppId(appId) + ");"), cancellationToken);
    }

    /// <summary>Replaces one custom artwork slot. Steam persists and renders it without a restart.</summary>
    /// <param name="appId">The Steam app id, or a shortcut's generated id.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="image">The encoded image.</param>
    /// <param name="format">The image encoding.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What became of the change.</returns>
    /// <exception cref="ArgumentException">The image is empty.</exception>
    public async Task<SteamClientWriteResult> SetCustomArtworkAsync(
        uint appId,
        SteamArtworkSlot slot,
        ReadOnlyMemory<byte> image,
        SteamArtworkFormat format,
        CancellationToken cancellationToken = default)
    {
        if (image.IsEmpty)
        {
            throw new ArgumentException("The image is empty.", nameof(image));
        }

        var extension = format switch
        {
            SteamArtworkFormat.Jpeg => "\"jpg\"",
            SteamArtworkFormat.Webp => "\"webp\"",
            SteamArtworkFormat.Png => "\"png\"",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown artwork format.")
        };
        var slotLiteral = SlotLiteral(slot);
        string base64;
        try
        {
            base64 = await Task.Run(() => Convert.ToBase64String(image.Span), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SteamClientWriteResult(
                SteamClientWriteOutcome.NotSent, "The request was cancelled before it was sent.");
        }

        return await WriteAsync(SteamClientScript.Write(
            "const app=" + SteamClientScript.AppId(appId) + ",type=" + slotLiteral + ";" +
            "await SteamClient.Apps.ClearCustomArtworkForApp(app,type);" +
            SteamClientScript.Settle(ArtworkClearSettleMs) +
            "await SteamClient.Apps.SetCustomArtworkForApp(app,\"" + base64 + "\"," + extension + ",type);"),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resets one artwork slot to Steam's official art.</summary>
    /// <param name="appId">The Steam app id, or a shortcut's generated id.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What became of the reset.</returns>
    public Task<SteamClientWriteResult> ClearCustomArtworkAsync(
        uint appId,
        SteamArtworkSlot slot,
        CancellationToken cancellationToken = default)
    {
        return WriteAsync(SteamClientScript.Write(
            "await SteamClient.Apps.ClearCustomArtworkForApp(" + SteamClientScript.AppId(appId) + "," +
            SlotLiteral(slot) + ");"), cancellationToken);
    }

    /// <summary>Points a non-Steam shortcut at a local icon file through Steam's own API.</summary>
    /// <param name="appId">The shortcut's generated id.</param>
    /// <param name="path">The icon file, as Steam should store it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What became of the change.</returns>
    public Task<SteamClientWriteResult> SetShortcutIconAsync(
        uint appId, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return WriteAsync(SteamClientScript.Write(
            "await SteamClient.Apps.SetShortcutIcon(" + SteamClientScript.AppId(appId) + "," +
            SteamCef.JsString(path) + ");"), cancellationToken);
    }

    /// <summary>Clears a non-Steam shortcut's custom icon through Steam's own API.</summary>
    /// <param name="appId">The shortcut's generated id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What became of the change.</returns>
    public Task<SteamClientWriteResult> ClearShortcutIconAsync(
        uint appId, CancellationToken cancellationToken = default)
    {
        return WriteAsync(SteamClientScript.Write(
            "await SteamClient.Apps.SetShortcutIcon(" + SteamClientScript.AppId(appId) + ",'');"), cancellationToken);
    }

    /// <summary>Asks Steam to refresh cached icon data for a store app.</summary>
    /// <param name="appId">The Steam app id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What became of the request.</returns>
    public Task<SteamClientWriteResult> RefreshIconAsync(
        uint appId, CancellationToken cancellationToken = default)
    {
        return WriteAsync(SteamClientScript.Write(
            "await SteamClient.Apps.RequestIconDataForApp(" + SteamClientScript.AppId(appId) + ");"),
            cancellationToken);
    }

    /// <summary>Reads Steam's official icon URL for an app.</summary>
    /// <param name="appId">The Steam app id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The URL; a failed read when Steam has none for the app or could not be asked.</returns>
    public async Task<SteamReadResult<string>> ReadOfficialIconUrlAsync(
        uint appId, CancellationToken cancellationToken = default)
    {
        var expression = SteamClientScript.Read(
            "const a=window.appStore?.GetAppOverviewByAppID?.(" + SteamClientScript.AppId(appId) + ");" +
            "const u=a?window.appStore?.GetIconURLForApp?.(a):null;" +
            "if(typeof u!=='string'||!u.length)return JSON.stringify({ok:false,err:'Steam has no icon for this app.'});" +
            "return JSON.stringify({ok:true,value:u});");
        var result = await _client.ReadAsync(expression, Budget, cancellationToken).ConfigureAwait(false);
        return SteamClientScript.ParseRead(result, "icon", static root => SteamClientScript.StringOf(root, "value"));
    }

    /// <summary>Reads the active Steam account id used for the userdata directory.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The 32-bit account id; a failed read when no user is signed in or Steam could not be asked.</returns>
    public async Task<SteamReadResult<uint>> ReadAccountIdAsync(CancellationToken cancellationToken = default)
    {
        var expression = SteamClientScript.Read(
            "const s=window.App?.m_CurrentUser?.strSteamID||'';" +
            "if(!/^\\d{17}$/.test(s))return JSON.stringify({ok:false,err:'No Steam user is signed in.'});" +
            "return JSON.stringify({ok:true,value:String(BigInt(s)&0xffffffffn)});");
        var result = await _client.ReadAsync(expression, Budget, cancellationToken).ConfigureAwait(false);
        return SteamClientScript.ParseRead(result, "account", static root =>
            uint.TryParse(SteamClientScript.StringOf(root, "value"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var accountId)
                ? accountId
                : throw new FormatException("The account id is not a number."));
    }

    /// <summary>Saves a custom logo position through Steam's app-details store.</summary>
    /// <param name="appId">The Steam app id, or a shortcut's generated id.</param>
    /// <param name="position">The placement, with both dimensions from 5 to 100 percent.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What became of the change.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is outside 5 to 100 percent.</exception>
    public Task<SteamClientWriteResult> SaveLogoPositionAsync(
        uint appId, SteamLogoPosition position, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.WidthPercent is < 5 or > 100 || position.HeightPercent is < 5 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        return WriteAsync(SteamClientScript.Write(
            "const a=window.appStore?.GetAppOverviewByAppID?.(" + SteamClientScript.AppId(appId) + ");" +
            "if(!a||!window.appDetailsStore?.SaveCustomLogoPosition)throw new Error('logo position unavailable');" +
            "await window.appDetailsStore.SaveCustomLogoPosition(a,{pinnedPosition:" +
            SteamCef.JsString(position.Anchor) + ",nWidthPct:" +
            position.WidthPercent.ToString(CultureInfo.InvariantCulture) + ",nHeightPct:" +
            position.HeightPercent.ToString(CultureInfo.InvariantCulture) + "});"), cancellationToken);
    }

    /// <summary>Clears a custom logo position through Steam's app-details store.</summary>
    /// <param name="appId">The Steam app id, or a shortcut's generated id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What became of the change.</returns>
    public Task<SteamClientWriteResult> ClearLogoPositionAsync(
        uint appId, CancellationToken cancellationToken = default)
    {
        return WriteAsync(SteamClientScript.Write(
            "const a=window.appStore?.GetAppOverviewByAppID?.(" + SteamClientScript.AppId(appId) + ");" +
            "if(!a||!window.appDetailsStore?.ClearCustomLogoPosition)throw new Error('logo position unavailable');" +
            "await window.appDetailsStore.ClearCustomLogoPosition(a);"), cancellationToken);
    }

    /// <summary>Reads the current custom logo position.</summary>
    /// <param name="appId">The Steam app id, or a shortcut's generated id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The position, or a successful read with a null value when Steam has none, including when the
    ///     stored dimensions are outside the 5 to 100 percent range <see cref="SaveLogoPositionAsync" />
    ///     accepts. A failed read means Steam could not be asked or answered with an error.
    /// </returns>
    public async Task<SteamReadResult<SteamLogoPosition>> ReadLogoPositionAsync(
        uint appId, CancellationToken cancellationToken = default)
    {
        var expression = SteamClientScript.Read(
            "const a=window.appStore?.GetAppOverviewByAppID?.(" + SteamClientScript.AppId(appId) + ");" +
            "const p=a&&window.appDetailsStore?.GetCustomLogoPosition?" +
            "window.appDetailsStore.GetCustomLogoPosition(a):null;" +
            "return JSON.stringify({ok:true,anchor:p?.pinnedPosition||'',width:p?.nWidthPct||0,height:p?.nHeightPct||0});");
        var result = await _client.ReadAsync(expression, Budget, cancellationToken).ConfigureAwait(false);
        return ParseLogoPosition(result);
    }

    /// <summary>Maps a logo-position reply to a read.</summary>
    /// <param name="result">The evaluation outcome.</param>
    /// <returns>The position, a successful null for absent/out-of-range position data, or a failed read.</returns>
    internal static SteamReadResult<SteamLogoPosition> ParseLogoPosition(SteamUiEvaluationResult result)
    {
        return SteamClientScript.ParseRead<SteamLogoPosition>(result, "logo position", static root =>
        {
            var anchor = SteamClientScript.StringOf(root, "anchor");
            if (anchor.Length == 0
                || !root.TryGetProperty("width", out var width) || !width.TryGetInt32(out var widthValue)
                || !root.TryGetProperty("height", out var height) || !height.TryGetInt32(out var heightValue))
            {
                return null;
            }

            // Steam reports 0 for an app that has no stored position, and the save path accepts
            // only 5 through 100. Anything outside that is not a position this library can hand
            // straight back to SaveLogoPositionAsync, so it reads as none rather than as a value.
            return widthValue is >= 5 and <= 100 && heightValue is >= 5 and <= 100
                ? new SteamLogoPosition(anchor, widthValue, heightValue)
                : null;
        });
    }

    /// <summary>Reads one app's details with a caller's deadline.</summary>
    /// <param name="appId">The app id.</param>
    /// <param name="timeout">The evaluation deadline.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The app details, or a failed read when Steam is unavailable, refuses or does not answer in time.</returns>
    internal async Task<SteamReadResult<SteamAppDetails>> ReadDetailsAsync(
        uint appId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var expression = SteamClientScript.Read(
            "const d=await " + SteamClientScript.AppDetailsPromise(appId, DetailsTimeoutMs) + ";" +
            "if(!d){return JSON.stringify({ok:false,err:'Steam has no details for this app.'});}" +
            "return JSON.stringify({ok:true,launch:d.strLaunchOptions||'',exe:d.strShortcutExe||''," +
            "args:d.strShortcutLaunchOptions||'',dir:d.strShortcutStartDir||'',install:d.strInstallFolder||''});");
        var result = await _client.ReadAsync(expression, timeout, cancellationToken).ConfigureAwait(false);
        return ParseDetails(result);
    }

    /// <summary>Maps a details reply to a read.</summary>
    /// <param name="result">The evaluation outcome.</param>
    /// <returns>App detail strings on success, or a failed read retaining the dispatch and error.</returns>
    internal static SteamReadResult<SteamAppDetails> ParseDetails(SteamUiEvaluationResult result)
    {
        return SteamClientScript.ParseRead(result, "app details", static root =>
            new SteamAppDetails(
                SteamClientScript.StringOf(root, "launch"),
                SteamClientScript.StringOf(root, "exe"),
                SteamClientScript.StringOf(root, "args"),
                SteamClientScript.StringOf(root, "dir"),
                SteamClientScript.StringOf(root, "install")));
    }

    /// <summary>The script <see cref="AddShortcutAsync" /> runs.</summary>
    /// <param name="name">Shortcut display name.</param>
    /// <param name="target">Executable or launch target passed to Steam.</param>
    /// <param name="startDirectory">Working directory passed to Steam.</param>
    /// <param name="launchArguments">Launch options passed to Steam.</param>
    /// <returns>An async expression creating one shortcut and checking its identity and saved fields.</returns>
    internal static string AddShortcutScript(string name, string target, string startDirectory, string launchArguments)
    {
        return SteamClientScript.Read(
            "const A=SteamClient?.Apps;" +
            "if(typeof A?.AddShortcut!=='function')" +
            "return JSON.stringify({ok:false,err:'This Steam client does not expose AddShortcut.'});" +
            SteamClientScript.ShortcutIdsFunction +
            "const before=shortcutIds();" +
            "if(!before)return JSON.stringify({ok:false,err:'Steam has not loaded its library yet, so a new " +
            "shortcut could not be confirmed. Nothing was created.'});" +
            "const name=" + SteamCef.JsString(name) + ",exe=" + SteamCef.JsString(target) +
            ",dir=" + SteamCef.JsString(startDirectory) + ",args=" + SteamCef.JsString(launchArguments) + ";" +
            "const details=" + SteamClientScript.AppDetailsFunction(DetailsTimeoutMs) + ";" +
            "const raw=await A.AddShortcut(name,exe,dir,args);" +
            "const returned=Number(raw)>>>0;" +
            "let gained=[];" +
            "for(let waited=0;waited<=" + AddAppearMs.ToString(CultureInfo.InvariantCulture) + ";waited+=100){" +
            "const now=shortcutIds();gained=now?[...now].filter(x=>!before.has(x)):[];" +
            "if(gained.length)break;await new Promise(r=>setTimeout(r,100));}" +
            // Without a returned id the one new entry is only ours when it holds what was asked for. One
            // somebody else added inside the same window is left exactly as it is.
            "if(!returned&&gained.length===1){" +
            "const g=gained[0],d=await details(g);" +
            "const gn=(shortcutApps()||[]).find(a=>a.id===g)?.name||'';" +
            "if(!d||String(d.strShortcutExe||'').toLowerCase()!==exe.toLowerCase()||gn!==name)" +
            "return JSON.stringify({ok:false,unconfirmed:true,err:'Steam returned no id, and the one new " +
            "shortcut in the library is not the one asked for, so it was left alone.'});}" +
            "if(gained.length!==1||(returned&&gained[0]!==returned)){" +
            "const id=gained.length===1?gained[0]:returned;" +
            "const err=gained.length===0?'Steam reported no new entry after creating this shortcut.'" +
            ":gained.length>1?'Steam gained '+gained.length+' entries at once, so which one this is cannot be told.'" +
            ":'Steam returned '+returned+' but the library gained '+gained[0]+'.';" +
            "return JSON.stringify({ok:true,value:String(id),confirmed:false,err});}" +
            "const id=gained[0];" +
            "if(typeof A.SetShortcutName==='function')await A.SetShortcutName(id,name);" +
            "if(typeof A.SetShortcutExe==='function')await A.SetShortcutExe(id,exe);" +
            "if(typeof A.SetShortcutStartDir==='function')await A.SetShortcutStartDir(id,dir);" +
            "if(typeof A.SetShortcutLaunchOptions==='function')await A.SetShortcutLaunchOptions(id,args);" +
            "const until=Date.now()+" + ReadBackMs.ToString(CultureInfo.InvariantCulture) + ";" +
            "let wrong=[];" +
            "for(;;){" +
            "const d=await details(id);" +
            "const o=window.appStore?.GetAppOverviewByAppID?.(id);" +
            "wrong=[];" +
            "if(!d)wrong.push('details');else{" +
            "if((d.strShortcutExe||'')!==exe)wrong.push('Target');" +
            "if((d.strShortcutStartDir||'')!==dir)wrong.push('start directory');" +
            "if((d.strShortcutLaunchOptions||'')!==args)wrong.push('launch options');}" +
            "if(o&&typeof o.display_name==='string'&&o.display_name!==name)wrong.push('name');" +
            "if(!wrong.length||Date.now()>=until)break;" +
            "await new Promise(r=>setTimeout(r,100));}" +
            "return JSON.stringify({ok:true,value:String(id),confirmed:true," +
            "mismatch:wrong.length?'Steam holds a different '+wrong.join(', ')+' than was written.':''});");
    }

    /// <summary>Maps an add-shortcut reply to a result.</summary>
    /// <param name="result">The evaluation outcome.</param>
    /// <remarks>
    ///     The id arrives as a decimal string because Steam's shortcut ids occupy the top half of the
    ///     unsigned 32-bit range, where a JSON number reads back signed. Anything that is not a
    ///     shortcut id is refused rather than returned: a store id here would mean the reply did not
    ///     describe the entry that was just created, so whether one was created is unknown.
    /// </remarks>
    /// <returns>The outcome, returned id and readback confirmation; unreadable or unconfirmed creation remains uncertain.</returns>
    internal static SteamShortcutAddResult ParseAddShortcut(SteamUiEvaluationResult result)
    {
        if (result.Dispatch != SteamUiDispatch.Answered)
        {
            return new SteamShortcutAddResult(
                SteamClientScript.Unread(result.Dispatch), 0, false, result.Error ?? "Steam did not answer.");
        }

        if (result.Error is not null)
        {
            return new SteamShortcutAddResult(SteamClientWriteOutcome.Rejected, 0, false, result.Error);
        }

        if (result.Value is null)
        {
            return new SteamShortcutAddResult(SteamClientWriteOutcome.Unknown, 0, false, "No response from Steam.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (!SteamClientScript.IsOk(root))
            {
                var unconfirmed = root.ValueKind == JsonValueKind.Object
                                  && root.TryGetProperty("unconfirmed", out var flag)
                                  && flag.ValueKind == JsonValueKind.True;
                return new SteamShortcutAddResult(
                    unconfirmed ? SteamClientWriteOutcome.Unknown : SteamClientWriteOutcome.Rejected,
                    0,
                    false,
                    SteamClientScript.RefusalOf(root) ?? "Steam refused to create the shortcut.");
            }

            var value = SteamClientScript.StringOf(root, "value");
            if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var appId))
            {
                return new SteamShortcutAddResult(
                    SteamClientWriteOutcome.Unknown, 0, false, "Steam reported an unreadable shortcut id.");
            }

            if (!IsShortcutAppId(appId))
            {
                return new SteamShortcutAddResult(
                    SteamClientWriteOutcome.Unknown, 0, false,
                    $"Steam reported {appId}, which is not a non-Steam shortcut id.");
            }

            var confirmed = root.TryGetProperty("confirmed", out var confirmedFlag)
                            && confirmedFlag.ValueKind == JsonValueKind.True;
            var mismatch = SteamClientScript.StringOf(root, "mismatch");
            return new SteamShortcutAddResult(
                SteamClientWriteOutcome.Applied,
                appId,
                confirmed,
                confirmed
                    ? null
                    : SteamClientScript.RefusalOf(root) ?? "The library did not confirm the new shortcut.",
                mismatch.Length > 0 ? mismatch : null);
        }
        catch (JsonException ex)
        {
            return new SteamShortcutAddResult(
                SteamClientWriteOutcome.Unknown, 0, false, $"Steam's shortcut reply was unreadable: {ex.Message}");
        }
    }

    /// <summary>Maps a shortcut-list reply to a read.</summary>
    /// <param name="result">The evaluation outcome.</param>
    /// <returns>The parsed shortcut list, or a failed read when any required id or list shape is invalid.</returns>
    internal static SteamReadResult<IReadOnlyList<SteamShortcut>> ParseShortcuts(SteamUiEvaluationResult result)
    {
        return SteamClientScript.ParseRead<IReadOnlyList<SteamShortcut>>(result, "shortcut list", static root =>
        {
            if (!root.TryGetProperty("shortcuts", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Steam returned no shortcut list.");
            }

            List<SteamShortcut> shortcuts = [];
            foreach (var item in items.EnumerateArray())
            {
                if (!uint.TryParse(SteamClientScript.StringOf(item, "id"), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var appId)
                    || !IsShortcutAppId(appId))
                {
                    throw new FormatException("Steam listed a shortcut with an unreadable id.");
                }

                shortcuts.Add(new SteamShortcut(
                    appId,
                    SteamClientScript.StringOf(item, "name"),
                    SteamClientScript.StringOf(item, "exe"),
                    SteamClientScript.StringOf(item, "dir"),
                    SteamClientScript.StringOf(item, "args")));
            }

            return shortcuts;
        });
    }

    private static string SlotLiteral(SteamArtworkSlot slot)
    {
        if (!Enum.IsDefined(slot))
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown artwork slot.");
        }

        return ((int)slot).ToString(CultureInfo.InvariantCulture);
    }

    private async Task<SteamClientWriteResult> WriteAsync(string expression, CancellationToken cancellationToken)
    {
        var result = await _client.WriteAsync(expression, Budget, cancellationToken).ConfigureAwait(false);
        var outcome = SteamClientScript.ParseWrite(result);
        if (outcome.Outcome is SteamClientWriteOutcome.Rejected or SteamClientWriteOutcome.Unknown)
        {
            SteamUiLog.Warn($"Steam app change {outcome.Outcome}: {outcome.Error}");
        }

        return outcome;
    }
}
