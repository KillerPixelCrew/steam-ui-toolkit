using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>What Big Picture Home's carousel should draw from.</summary>
/// <param name="IncludeUninstalled">
///     Whether owned games that are not installed are listed, greyed, after the installed
///     ones.
/// </param>
/// <param name="DisconnectedAppIds">
///     Games that live on a library that is not attached right now. They leave the carousel even while
///     Steam still reports them installed, which it does for a moment after a card is pulled.
/// </param>
/// <param name="Revision">Monotonic host observation revision.</param>
public sealed record SteamHomeCarouselState(
    bool IncludeUninstalled,
    IReadOnlyList<long> DisconnectedAppIds,
    long Revision = 0);

/// <summary>What the carousel reported it holds, once per change.</summary>
/// <param name="Items">Entries in the list, the running-game separator included.</param>
/// <param name="Purchases">Recent purchases not yet played.</param>
/// <param name="Installed">Installed games from the attached libraries.</param>
/// <param name="Uninstalled">Owned games listed although not installed.</param>
/// <param name="Excluded">Games the host marked as on a disconnected library.</param>
/// <param name="Tracking">Whether Steam's own observer hook re-renders the carousel when a collection changes.</param>
/// <param name="Fallback">Whether nothing qualified and Steam's own list was shown instead.</param>
public sealed record SteamHomeCarouselReport(
    int Items,
    int Purchases,
    int Installed,
    int Uninstalled,
    int Excluded,
    bool Tracking,
    bool Fallback);

/// <summary>Hears what the Home carousel reports.</summary>
public interface ISteamHomeCarouselBackend
{
    /// <summary>Receives what the carousel holds after it rebuilt its list.</summary>
    /// <param name="report">The counts.</param>
    /// <param name="cancellationToken">Cancels the handling.</param>
    /// <returns>The outcome. A refusal is reported rather than swallowed.</returns>
    Task<SteamUiCommandResult> ReportAsync(SteamHomeCarouselReport report, CancellationToken cancellationToken);
}

/// <summary>
///     Big Picture Home's carousel, listing the games on the libraries attached right now instead of
///     Steam's own mix of recent and new games.
/// </summary>
/// <remarks>
///     The list Home draws is one array of app ids passed to the carousel and its background. The gate
///     replaces that array in what the carousel renders, so Steam's own components draw it, and bounds
///     the virtualized carousel's overscan to the component's default, which Home otherwise sets to the
///     whole list and so mounts every tile. Home, the carousel and the hook that builds Steam's list are
///     all module-local; Home is taken from the router's route list and its <c>type</c> is claimed.
///     <para>
///         Ordering happens in the gate because its inputs, every installed and owned game with its
///         timestamps, do not fit the bridge's payload bound. The host decides which libraries count and
///         whether uninstalled games appear.
///     </para>
///     <para>
///         Mapped from the September 2026 client beta's bundle on 2026-09-11: <c>HomeTabsActive</c> with
///         <c>#Showcase_RecentGames</c> occurs in exactly one module, the route for <c>/library/home</c>
///         renders a <c>React.memo</c> whose <c>type</c> is a writable and configurable own property, and
///         mobx-react-lite's <c>useObserver</c> is the hook Steam builds its own list on.
///     </para>
///     <para>
///         Since the client update of 2026-09-22 Big Picture mounts its router and Home together, the
///         moment Steam's services report initialized, so Home is on screen before the route list can
///         be found. A claim on the memo's <c>type</c> reaches later mounts only; install therefore also
///         adopts every mounted Home, and the probe reports how many it will adopt as <c>mounted</c>.
///         Removal hands back every Home drawing the claim, including ones mounted after install.
///     </para>
/// </remarks>
public static class SteamHomeCarouselSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.home-carousel";

    /// <summary>The exact command vocabulary the injected gate sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["report"];

    /// <summary>The gate that claims Home and feeds its carousel.</summary>
    /// <remarks>
    ///     The probe is the gate's own find. It answers not ready while the router has no
    ///     <c>/library/home</c> route mounted, which is Big Picture closed, and incompatible when the
    ///     Home module, the stores or a claimable Home are missing, with the search it made as its facts.
    ///     The observer hook is reported but not required: without it the carousel still follows the
    ///     host and Steam's own list, and a change elsewhere shows on its next render.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "homeCarousel",
        "steam-home-carousel-v1:unique-home-module+route-home-memo",
        "Home carousel gate");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamHomeCarouselState state)
    {
        return JsonSerializer.SerializeToElement(
            state, SteamSurfaceJsonContext.Default.SteamHomeCarouselState);
    }

    /// <summary>Reads the exact <c>report</c> payload: five counts and two flags, nothing else.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="report">The report, when this returns true.</param>
    /// <returns>Whether the payload had that shape.</returns>
    public static bool TryReadReport(JsonElement payload, out SteamHomeCarouselReport report)
    {
        const int Maximum = 100_000;
        report = new SteamHomeCarouselReport(0, 0, 0, 0, 0, false, false);
        if (!SteamUiPayload.HasExactly(payload, 7)
            || !SteamUiPayload.TryReadInt(payload, "items", 0, Maximum, out var items)
            || !SteamUiPayload.TryReadInt(payload, "purchases", 0, Maximum, out var purchases)
            || !SteamUiPayload.TryReadInt(payload, "installed", 0, Maximum, out var installed)
            || !SteamUiPayload.TryReadInt(payload, "uninstalled", 0, Maximum, out var uninstalled)
            || !SteamUiPayload.TryReadInt(payload, "excluded", 0, Maximum, out var excluded)
            || !SteamUiPayload.TryReadBoolean(payload, "tracking", out var tracking)
            || !SteamUiPayload.TryReadBoolean(payload, "fallback", out var fallback))
        {
            return false;
        }

        report = new SteamHomeCarouselReport(items, purchases, installed, uninstalled, excluded, tracking, fallback);
        return true;
    }

    /// <summary>Declares the surface as one module: the gate, the instruction, and the report.</summary>
    /// <param name="enabled">Whether the instruction may be published right now.</param>
    /// <param name="read">What the carousel should draw from, or null when there is nothing to say yet.</param>
    /// <param name="backend">What hears the carousel's report.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamHomeCarouselState?>> read,
        ISteamHomeCarouselBackend backend,
        string id = "home-carousel")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return SteamUiModuleBuilder.Module(
            id,
            PatchId,
            enabled,
            read,
            SteamSurfaceJsonContext.Default.SteamHomeCarouselState,
            [Patch],
            [
                SteamUiModuleBuilder.Command<SteamHomeCarouselReport>(
                    PatchId,
                    "report",
                    TryReadReport,
                    backend.ReportAsync,
                    "The home carousel report is invalid.")
            ]);
    }
}
