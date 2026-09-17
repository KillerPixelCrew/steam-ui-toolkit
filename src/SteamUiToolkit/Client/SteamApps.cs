using System;
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
    Jpeg
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

    // Steam applies each setter on its own thread; give the write a moment to land before a caller
    // reads the value back to confirm it.
    private const int WriteSettleMs = 400;

    // Steam resolves ClearCustomArtworkForApp before the clear finishes, so a set issued immediately
    // can race it (observed in decky-steamgriddb).
    private const int ArtworkClearSettleMs = 500;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

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
            SteamCef.JsString(launchOptions) + ");" +
            SteamClientScript.Settle(WriteSettleMs));
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
            "await SteamClient.Apps.SetShortcutLaunchOptions(app," + SteamCef.JsString(launchArguments) + ");" +
            SteamClientScript.Settle(WriteSettleMs));
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
        var extension = format == SteamArtworkFormat.Jpeg ? "\"jpg\"" : "\"png\"";
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
        var result = await SteamUiTransportSession.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        var outcome = SteamClientScript.ParseWrite(result);
        if (outcome is { Reachable: true, Accepted: false })
        {
            SteamUiLog.Warn($"Steam rejected an app change: {outcome.Error}.");
        }

        return outcome;
    }
}
