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

/// <summary>Outcome of reading one app's details.</summary>
/// <param name="Reachable">Whether a validated Steam target ran the read.</param>
/// <param name="Details">The details, or null when Steam did not answer for this id.</param>
/// <param name="Error">Why the read produced no details.</param>
public readonly record struct SteamAppDetailsResult(bool Reachable, SteamAppDetails? Details, string? Error);

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
public sealed record SteamLogoPosition(string Anchor, int WidthPercent, int HeightPercent);

/// <summary>Outcome of asking the running client to create a non-Steam shortcut.</summary>
/// <param name="Reachable">
///     Whether a validated Steam target ran the request. An unreachable client created nothing.
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
    bool Reachable,
    uint AppId,
    bool Confirmed,
    string? Error,
    string? Mismatch = null)
{
    /// <summary>Whether Steam was reached and a shortcut id is known.</summary>
    public bool Succeeded => Reachable && AppId != 0;
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

/// <summary>Outcome of reading every non-Steam shortcut in the library.</summary>
/// <param name="Reachable">Whether a validated Steam target ran the read.</param>
/// <param name="Shortcuts">
///     Every shortcut, or null when the library could not be read whole. Empty means the library has
///     none; it never stands in for a read that failed.
/// </param>
/// <param name="Error">Why the read produced no list.</param>
public readonly record struct SteamShortcutListResult(
    bool Reachable,
    IReadOnlyList<SteamShortcut>? Shortcuts,
    string? Error)
{
    /// <summary>Whether the whole list was read.</summary>
    public bool Succeeded => Reachable && Shortcuts is not null;
}

/// <summary>
///     Reads and changes per-app configuration in the running client through
///     <c>SteamClient.Apps</c>, over the session's transport (<see cref="SteamUiTransportSession" />).
/// </summary>
/// <remarks>
///     <para>
///         Steam stores launch values <em>verbatim</em>: it neither adds nor strips the quotes its own
///         shortcuts carry and leaves backslashes alone. It persists them to <c>shortcuts.vdf</c> and
///         <c>localconfig.vdf</c> immediately, so no restart is needed.
///     </para>
///     <para>
///         Every change goes through one gate, one at a time. Each is a separate evaluation against a
///         client that is mutating its own library store: two in flight at once is how that store gets
///         corrupted, and a shortcut added by one caller while another diffs the library would make the
///         other misread which entry it created.
///     </para>
///     <para>
///         A store title and a non-Steam shortcut take different calls. A store title's launch options
///         expand <c>%command%</c> to the game's own command line; a shortcut ignores an exe-replacing
///         launch option and runs its Target anyway, so replacing what a shortcut runs means writing its
///         Target and Launch Arguments.
///     </para>
/// </remarks>
public static class SteamApps
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

    // One change to the client's library at a time, whoever asks for it.
    internal static readonly SemaphoreSlim Writes = new(1, 1);

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
    /// <returns>The outcome. Never throws for an unreachable client.</returns>
    public static Task<SteamAppDetailsResult> ReadDetailsAsync(
        uint appId,
        CancellationToken cancellationToken = default)
    {
        return ReadDetailsAsync(null, appId, Budget, cancellationToken);
    }

    /// <summary>Replaces a store title's launch options.</summary>
    /// <param name="appId">The Steam app id.</param>
    /// <param name="launchOptions">The new value, stored verbatim.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether Steam accepted the change.</returns>
    public static async Task<SteamClientWriteResult> SetLaunchOptionsAsync(
        uint appId,
        string launchOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchOptions);
        var expression = SteamClientScript.Write(
            "await SteamClient.Apps.SetAppLaunchOptions(" + SteamClientScript.AppId(appId) + "," +
            SteamCef.JsString(launchOptions) + ");");
        return await WriteAsync(expression, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces what a non-Steam shortcut runs.</summary>
    /// <param name="appId">The shortcut's generated id.</param>
    /// <param name="target">The new Target, stored verbatim (quote a path that contains spaces).</param>
    /// <param name="launchArguments">The new Launch Arguments, stored verbatim.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether Steam accepted both values.</returns>
    /// <remarks>The start directory is never written, so the shortcut keeps its working directory.</remarks>
    public static async Task<SteamClientWriteResult> SetShortcutLaunchAsync(
        uint appId,
        string target,
        string launchArguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(launchArguments);
        var expression = SteamClientScript.Write(
            "const app=" + SteamClientScript.AppId(appId) + ";" +
            "await SteamClient.Apps.SetShortcutExe(app," + SteamCef.JsString(target) + ");" +
            "await SteamClient.Apps.SetShortcutLaunchOptions(app," + SteamCef.JsString(launchArguments) + ");");
        return await WriteAsync(expression, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces what a non-Steam shortcut runs and where it runs from.</summary>
    /// <param name="appId">The shortcut's generated id.</param>
    /// <param name="target">The new Target, stored verbatim (quote a path that contains spaces).</param>
    /// <param name="startDirectory">The new start directory, stored verbatim.</param>
    /// <param name="launchArguments">The new Launch Arguments, stored verbatim.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether Steam accepted all three values.</returns>
    /// <remarks>
    ///     For a caller that owns the whole command. Moving a shortcut from one program to another and
    ///     leaving the old start directory behind runs the new program from the old one's folder.
    /// </remarks>
    public static async Task<SteamClientWriteResult> SetShortcutLaunchAsync(
        uint appId,
        string target,
        string startDirectory,
        string launchArguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(startDirectory);
        ArgumentNullException.ThrowIfNull(launchArguments);
        var expression = SteamClientScript.Write(
            "const app=" + SteamClientScript.AppId(appId) + ";" +
            "await SteamClient.Apps.SetShortcutExe(app," + SteamCef.JsString(target) + ");" +
            "await SteamClient.Apps.SetShortcutStartDir(app," + SteamCef.JsString(startDirectory) + ");" +
            "await SteamClient.Apps.SetShortcutLaunchOptions(app," + SteamCef.JsString(launchArguments) + ");");
        return await WriteAsync(expression, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads every non-Steam shortcut in the library, with what each one runs, in one call.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The shortcuts, or why the library could not be read whole.</returns>
    /// <remarks>
    ///     All or nothing. A shortcut whose details Steam did not return fails the read rather than
    ///     appearing with empty fields: a caller comparing what it wrote against what Steam holds would
    ///     read an empty Target as somebody else's entry.
    /// </remarks>
    public static async Task<SteamShortcutListResult> ListShortcutsAsync(CancellationToken cancellationToken = default)
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
        var result = await SteamUiTransportSession.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
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
    public static async Task<SteamShortcutAddResult> AddShortcutAsync(
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

        var expression = SteamClientScript.Read(
            "const A=SteamClient?.Apps;" +
            "if(typeof A?.AddShortcut!=='function')" +
            "return JSON.stringify({ok:false,err:'This Steam client does not expose AddShortcut.'});" +
            SteamClientScript.ShortcutIdsFunction +
            "const before=shortcutIds();" +
            "if(!before)return JSON.stringify({ok:false,err:'Steam has not loaded its library yet, so a new " +
            "shortcut could not be confirmed. Nothing was created.'});" +
            "const name=" + SteamCef.JsString(name) + ",exe=" + SteamCef.JsString(target) +
            ",dir=" + SteamCef.JsString(startDirectory) + ",args=" + SteamCef.JsString(launchArguments) + ";" +
            "const raw=await A.AddShortcut(name,exe,dir,args);" +
            "const returned=Number(raw)>>>0;" +
            "let gained=[];" +
            "for(let waited=0;waited<=" + AddAppearMs.ToString(CultureInfo.InvariantCulture) + ";waited+=100){" +
            "const now=shortcutIds();gained=now?[...now].filter(x=>!before.has(x)):[];" +
            "if(gained.length)break;await new Promise(r=>setTimeout(r,100));}" +
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
            "const details=" + SteamClientScript.AppDetailsFunction(DetailsTimeoutMs) + ";" +
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

        cancellationToken.ThrowIfCancellationRequested();
        await Writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        CefEvalResult result;
        try
        {
            result = await SteamUiTransportSession.EvaluateAsync(expression, Budget, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            Writes.Release();
        }

        var outcome = ParseAddShortcut(result);
        if (outcome is { Reachable: true, Confirmed: false })
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
    /// <returns>Whether Steam accepted the removal.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="appId" /> is not in the generated-shortcut range. A store title has no
    ///     shortcut entry to delete, and removing one is not something this call can undo.
    /// </exception>
    public static async Task<SteamClientWriteResult> RemoveShortcutAsync(
        uint appId,
        CancellationToken cancellationToken = default)
    {
        if (!IsShortcutAppId(appId))
        {
            throw new ArgumentOutOfRangeException(
                nameof(appId), appId, "Only a non-Steam shortcut id can be removed.");
        }

        var expression = SteamClientScript.Write(
            "if(typeof SteamClient?.Apps?.RemoveShortcut!=='function')" +
            "throw new Error('This Steam client does not expose RemoveShortcut.');" +
            "await SteamClient.Apps.RemoveShortcut(" + SteamClientScript.AppId(appId) + ");");
        return await WriteAsync(expression, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces one custom artwork slot. Steam persists and renders it without a restart.</summary>
    /// <param name="appId">The Steam app id, or a shortcut's generated id.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="image">The encoded image.</param>
    /// <param name="format">The image encoding.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether Steam accepted the image.</returns>
    /// <exception cref="ArgumentException">The image is empty.</exception>
    public static async Task<SteamClientWriteResult> SetCustomArtworkAsync(
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

        var base64 = await Task.Run(() => Convert.ToBase64String(image.Span), cancellationToken)
            .ConfigureAwait(false);
        var extension = format switch
        {
            SteamArtworkFormat.Jpeg => "\"jpg\"",
            SteamArtworkFormat.Webp => "\"webp\"",
            SteamArtworkFormat.Png => "\"png\"",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown artwork format.")
        };
        var expression = SteamClientScript.Write(
            "const app=" + SteamClientScript.AppId(appId) + ",type=" + SlotLiteral(slot) + ";" +
            "await SteamClient.Apps.ClearCustomArtworkForApp(app,type);" +
            SteamClientScript.Settle(ArtworkClearSettleMs) +
            "await SteamClient.Apps.SetCustomArtworkForApp(app,\"" + base64 + "\"," + extension + ",type);");
        return await WriteAsync(expression, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resets one artwork slot to Steam's official art.</summary>
    /// <param name="appId">The Steam app id, or a shortcut's generated id.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether Steam accepted the reset.</returns>
    public static async Task<SteamClientWriteResult> ClearCustomArtworkAsync(
        uint appId,
        SteamArtworkSlot slot,
        CancellationToken cancellationToken = default)
    {
        var expression = SteamClientScript.Write(
            "await SteamClient.Apps.ClearCustomArtworkForApp(" + SteamClientScript.AppId(appId) + "," +
            SlotLiteral(slot) + ");");
        return await WriteAsync(expression, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Points a non-Steam shortcut at a local icon file through Steam's own API.</summary>
    public static async Task<SteamClientWriteResult> SetShortcutIconAsync(
        uint appId, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var expression = SteamClientScript.Write(
            "await SteamClient.Apps.SetShortcutIcon(" + SteamClientScript.AppId(appId) + "," +
            SteamCef.JsString(path) + ");");
        return await WriteAsync(expression, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Clears a non-Steam shortcut's custom icon through Steam's own API.</summary>
    public static async Task<SteamClientWriteResult> ClearShortcutIconAsync(
        uint appId, CancellationToken cancellationToken = default)
    {
        var expression = SteamClientScript.Write(
            "await SteamClient.Apps.SetShortcutIcon(" + SteamClientScript.AppId(appId) + ",'');");
        return await WriteAsync(expression, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks Steam to refresh cached icon data for a store app.</summary>
    public static async Task<SteamClientWriteResult> RefreshIconAsync(
        uint appId, CancellationToken cancellationToken = default)
    {
        var expression = SteamClientScript.Write(
            "await SteamClient.Apps.RequestIconDataForApp(" + SteamClientScript.AppId(appId) + ");");
        return await WriteAsync(expression, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads Steam's official icon URL for an app.</summary>
    public static async Task<string?> ReadOfficialIconUrlAsync(
        uint appId, CancellationToken cancellationToken = default)
    {
        var expression = SteamClientScript.Read(
            "const a=window.appStore?.GetAppOverviewByAppID?.(" + SteamClientScript.AppId(appId) + ");" +
            "const u=a?window.appStore?.GetIconURLForApp?.(a):null;" +
            "return JSON.stringify({ok:typeof u==='string'&&u.length>0,value:u||''});");
        return await ReadStringAsync(expression, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the active Steam account id used for the userdata directory.</summary>
    public static async Task<uint?> ReadAccountIdAsync(CancellationToken cancellationToken = default)
    {
        var expression = SteamClientScript.Read(
            "const s=window.App?.m_CurrentUser?.strSteamID||'';" +
            "if(!/^\\d{17}$/.test(s))return JSON.stringify({ok:false});" +
            "return JSON.stringify({ok:true,value:String(BigInt(s)&0xffffffffn)});");
        var value = await ReadStringAsync(expression, cancellationToken).ConfigureAwait(false);
        return uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var accountId)
            ? accountId
            : null;
    }

    /// <summary>Saves a custom logo position through Steam's app-details store.</summary>
    public static async Task<SteamClientWriteResult> SaveLogoPositionAsync(
        uint appId, SteamLogoPosition position, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.WidthPercent is < 5 or > 100 || position.HeightPercent is < 5 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        var expression = SteamClientScript.Write(
            "const a=window.appStore?.GetAppOverviewByAppID?.(" + SteamClientScript.AppId(appId) + ");" +
            "if(!a||!window.appDetailsStore?.SaveCustomLogoPosition)throw new Error('logo position unavailable');" +
            "await window.appDetailsStore.SaveCustomLogoPosition(a,{pinnedPosition:" +
            SteamCef.JsString(position.Anchor) + ",nWidthPct:" +
            position.WidthPercent.ToString(CultureInfo.InvariantCulture) + ",nHeightPct:" +
            position.HeightPercent.ToString(CultureInfo.InvariantCulture) + "});");
        return await WriteAsync(expression, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Clears a custom logo position through Steam's app-details store.</summary>
    public static async Task<SteamClientWriteResult> ClearLogoPositionAsync(
        uint appId, CancellationToken cancellationToken = default)
    {
        var expression = SteamClientScript.Write(
            "const a=window.appStore?.GetAppOverviewByAppID?.(" + SteamClientScript.AppId(appId) + ");" +
            "if(!a||!window.appDetailsStore?.ClearCustomLogoPosition)throw new Error('logo position unavailable');" +
            "await window.appDetailsStore.ClearCustomLogoPosition(a);");
        return await WriteAsync(expression, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads the current custom logo position, or null when Steam has none and when the stored
    ///     dimensions are outside the 5 to 100 percent range <see cref="SaveLogoPositionAsync" /> accepts.
    /// </summary>
    public static async Task<SteamLogoPosition?> ReadLogoPositionAsync(
        uint appId, CancellationToken cancellationToken = default)
    {
        var expression = SteamClientScript.Read(
            "const a=window.appStore?.GetAppOverviewByAppID?.(" + SteamClientScript.AppId(appId) + ");" +
            "const p=a&&window.appDetailsStore?.GetCustomLogoPosition?" +
            "window.appDetailsStore.GetCustomLogoPosition(a):null;" +
            "return JSON.stringify({ok:true,anchor:p?.pinnedPosition||'',width:p?.nWidthPct||0,height:p?.nHeightPct||0});");
        var result = await SteamUiTransportSession.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            var anchor = SteamClientScript.StringOf(root, "anchor");
            if (!SteamClientScript.IsOk(root) || anchor.Length == 0
                                                || !root.TryGetProperty("width", out var width)
                                                || !width.TryGetInt32(out var widthValue)
                                                || !root.TryGetProperty("height", out var height)
                                                || !height.TryGetInt32(out var heightValue))
            {
                return null;
            }

            // Steam reports 0 for an app that has no stored position, and the save path accepts
            // only 5 through 100. Anything outside that is not a position this library can hand
            // straight back to SaveLogoPositionAsync, so it reads as none rather than as a value.
            return widthValue is >= 5 and <= 100 && heightValue is >= 5 and <= 100
                ? new SteamLogoPosition(anchor, widthValue, heightValue)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads one app's details through a specific transport, or the session's.</summary>
    /// <param name="transport">The transport, or null for the session's.</param>
    /// <param name="appId">The app id.</param>
    /// <param name="timeout">The evaluation deadline.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal static async Task<SteamAppDetailsResult> ReadDetailsAsync(
        ISteamUiTransport? transport,
        uint appId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var expression = SteamClientScript.Read(
            "const d=await " + SteamClientScript.AppDetailsPromise(appId, DetailsTimeoutMs) + ";" +
            "if(!d){return JSON.stringify({ok:false,err:'Steam has no details for this app.'});}" +
            "return JSON.stringify({ok:true,launch:d.strLaunchOptions||'',exe:d.strShortcutExe||''," +
            "args:d.strShortcutLaunchOptions||'',dir:d.strShortcutStartDir||'',install:d.strInstallFolder||''});");
        var result = await SteamClientScript.EvaluateAsync(
                transport,
                SteamUiTargetRole.SharedJsContext,
                expression,
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
        return ParseDetails(result);
    }

    /// <summary>Maps a details reply to a result. Pure, for tests.</summary>
    /// <param name="result">The evaluation outcome.</param>
    internal static SteamAppDetailsResult ParseDetails(CefEvalResult result)
    {
        if (!result.Reachable)
        {
            return new SteamAppDetailsResult(false, null, result.Error);
        }

        if (result.Value is null)
        {
            return new SteamAppDetailsResult(true, null, "No response from Steam.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (!SteamClientScript.IsOk(root))
            {
                return new SteamAppDetailsResult(
                    true,
                    null,
                    SteamClientScript.ErrorOf(root) ?? "Steam returned no app details.");
            }

            return new SteamAppDetailsResult(
                true,
                new SteamAppDetails(
                    SteamClientScript.StringOf(root, "launch"),
                    SteamClientScript.StringOf(root, "exe"),
                    SteamClientScript.StringOf(root, "args"),
                    SteamClientScript.StringOf(root, "dir"),
                    SteamClientScript.StringOf(root, "install")),
                null);
        }
        catch (JsonException ex)
        {
            return new SteamAppDetailsResult(true, null, $"Steam app details were invalid: {ex.Message}");
        }
    }

    /// <summary>Maps an add-shortcut reply to a result. Pure, for tests.</summary>
    /// <param name="result">The evaluation outcome.</param>
    /// <remarks>
    ///     The id arrives as a decimal string because Steam's shortcut ids occupy the top half of the
    ///     unsigned 32-bit range, where a JSON number reads back signed. Anything that is not a
    ///     shortcut id is refused rather than returned: a store id here would mean the reply did not
    ///     describe the entry that was just created.
    /// </remarks>
    internal static SteamShortcutAddResult ParseAddShortcut(CefEvalResult result)
    {
        if (!result.Reachable)
        {
            return new SteamShortcutAddResult(false, 0, false, result.Error);
        }

        if (result.Value is null)
        {
            return new SteamShortcutAddResult(true, 0, false, "No response from Steam.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (!SteamClientScript.IsOk(root))
            {
                return new SteamShortcutAddResult(
                    true, 0, false, SteamClientScript.ErrorOf(root) ?? "Steam refused to create the shortcut.");
            }

            var value = SteamClientScript.StringOf(root, "value");
            if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var appId))
            {
                return new SteamShortcutAddResult(true, 0, false, "Steam reported an unreadable shortcut id.");
            }

            if (!IsShortcutAppId(appId))
            {
                return new SteamShortcutAddResult(
                    true, 0, false, $"Steam reported {appId}, which is not a non-Steam shortcut id.");
            }

            var confirmed = root.TryGetProperty("confirmed", out var flag) && flag.ValueKind == JsonValueKind.True;
            var mismatch = SteamClientScript.StringOf(root, "mismatch");
            return new SteamShortcutAddResult(
                true,
                appId,
                confirmed,
                confirmed
                    ? null
                    : SteamClientScript.ErrorOf(root) ?? "The library did not confirm the new shortcut.",
                mismatch.Length > 0 ? mismatch : null);
        }
        catch (JsonException ex)
        {
            return new SteamShortcutAddResult(true, 0, false, $"Steam's shortcut reply was invalid: {ex.Message}");
        }
    }

    /// <summary>Maps a shortcut-list reply to a result. Pure, for tests.</summary>
    /// <param name="result">The evaluation outcome.</param>
    internal static SteamShortcutListResult ParseShortcuts(CefEvalResult result)
    {
        if (!result.Reachable)
        {
            return new SteamShortcutListResult(false, null, result.Error);
        }

        if (result.Value is null)
        {
            return new SteamShortcutListResult(true, null, "No response from Steam.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (!SteamClientScript.IsOk(root)
                || !root.TryGetProperty("shortcuts", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return new SteamShortcutListResult(
                    true, null, SteamClientScript.ErrorOf(root) ?? "Steam returned no shortcut list.");
            }

            List<SteamShortcut> shortcuts = [];
            foreach (var item in items.EnumerateArray())
            {
                if (!uint.TryParse(SteamClientScript.StringOf(item, "id"), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var appId)
                    || !IsShortcutAppId(appId))
                {
                    return new SteamShortcutListResult(true, null, "Steam listed a shortcut with an unreadable id.");
                }

                shortcuts.Add(new SteamShortcut(
                    appId,
                    SteamClientScript.StringOf(item, "name"),
                    SteamClientScript.StringOf(item, "exe"),
                    SteamClientScript.StringOf(item, "dir"),
                    SteamClientScript.StringOf(item, "args")));
            }

            return new SteamShortcutListResult(true, shortcuts, null);
        }
        catch (JsonException ex)
        {
            return new SteamShortcutListResult(true, null, $"Steam's shortcut list was invalid: {ex.Message}");
        }
    }

    private static string SlotLiteral(SteamArtworkSlot slot)
    {
        if (!Enum.IsDefined(slot))
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown artwork slot.");
        }

        return ((int)slot).ToString(CultureInfo.InvariantCulture);
    }

    private static async Task<SteamClientWriteResult> WriteAsync(
        string expression,
        CancellationToken cancellationToken)
    {
        await Writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        CefEvalResult result;
        try
        {
            result = await SteamUiTransportSession.EvaluateAsync(expression, Budget, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Writes.Release();
        }

        var outcome = SteamClientScript.ParseWrite(result);
        if (outcome is { Reachable: true, Accepted: false })
        {
            SteamUiLog.Warn($"Steam rejected an app change: {outcome.Error}.");
        }

        return outcome;
    }

    private static async Task<string?> ReadStringAsync(
        string expression, CancellationToken cancellationToken)
    {
        var result = await SteamUiTransportSession.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            return SteamClientScript.IsOk(root) ? SteamClientScript.StringOf(root, "value") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
