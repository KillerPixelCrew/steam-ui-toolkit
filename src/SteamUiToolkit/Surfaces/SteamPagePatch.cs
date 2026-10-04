using System;
using System.Collections.Generic;
using System.Linq;

namespace SteamUiToolkit;

/// <summary>One module a custom page draws from, which a probe requires to match exactly once.</summary>
/// <param name="Name">The name the probe reports its count under.</param>
/// <param name="Tokens">The module's source tokens, as the JavaScript array a probe's counter takes.</param>
public readonly record struct SteamPageProbe(string Name, string Tokens)
{
    /// <summary>Steam's React.</summary>
    public static SteamPageProbe React { get; } = new("react", SteamUiProbeJs.ReactTokens);

    /// <summary>Steam's Panel, which a page focuses rows with.</summary>
    public static SteamPageProbe Focusable { get; } = new("focusable", SteamUiProbeJs.NativeFocusableTokens);

    /// <summary>The fields module: toggle, dropdown, slider, text field and buttons.</summary>
    public static SteamPageProbe Fields { get; } = new("controls", SteamUiProbeJs.NativeFieldTokens);

    /// <summary>Steam's tabbed page.</summary>
    public static SteamPageProbe Tabs { get; } = new("tabs", SteamUiProbeJs.NativeTabsTokens);

    /// <summary>Steam's generic dialog.</summary>
    public static SteamPageProbe Modal { get; } = new("modal", SteamUiProbeJs.NativeModalTokens);

    /// <summary>Steam's modal manager.</summary>
    public static SteamPageProbe ShowModal { get; } = new("showModal", SteamUiProbeJs.NativeShowModalTokens);

    /// <summary>Steam's library item class map, which the library capsule is styled by.</summary>
    public static SteamPageProbe LibraryClasses { get; } = new("classes", SteamUiProbeJs.LibraryClassTokens);

    /// <summary>The routed sidebar Steam's Settings page is built on.</summary>
    public static SteamPageProbe SettingsSidebar { get; } = new("pages", SteamUiProbeJs.SettingsSidebarTokens);

    /// <summary>Steam's generic confirm modal.</summary>
    public static SteamPageProbe ConfirmModal { get; } = new("confirm", SteamUiProbeJs.ConfirmModalTokens);
}

/// <summary>The gate patch of a host's own page, declared with <c>registerSteamPage</c>.</summary>
/// <remarks>
///     A page's gate is the same patch for every host: a read-only probe that every module the page
///     draws from matches exactly once, then the page gate's own install, verified by its status and
///     removed through it. Only which modules differ, so a page names those and nothing else.
/// </remarks>
public static class SteamPagePatch
{
    /// <summary>Declares the patch for one page.</summary>
    /// <param name="patchId">The page's patch and state identity.</param>
    /// <param name="gateName">The name the page's gate registers under.</param>
    /// <param name="fingerprint">The structural fingerprint reported on a positive probe.</param>
    /// <param name="subject">What the page is called in diagnostics.</param>
    /// <param name="probes">
    ///     The modules the page draws from. Each name becomes a key of the probe's JSON, so it must be
    ///     an identifier and appear once.
    /// </param>
    /// <returns>The patch.</returns>
    /// <exception cref="ArgumentException">
    ///     No probe was given, a probe name is not an identifier, or two probes share a name.
    /// </exception>
    public static ISteamUiPatch Create(
        string patchId,
        string gateName,
        string fingerprint,
        string subject,
        IReadOnlyList<SteamPageProbe> probes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gateName);
        ArgumentNullException.ThrowIfNull(probes);
        if (probes.Count == 0)
        {
            throw new ArgumentException("A page draws from at least one module.", nameof(probes));
        }

        // A duplicate key would silently overwrite one count in the probe's JSON, and a name that is
        // not an identifier breaks the probe at runtime; both fail here, once, with the page named.
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var probe in probes)
        {
            if (!IsIdentifier(probe.Name))
            {
                throw new ArgumentException(
                    $"Page {patchId}: probe name '{probe.Name}' is not an identifier.", nameof(probes));
            }

            if (!names.Add(probe.Name))
            {
                throw new ArgumentException(
                    $"Page {patchId}: probe name '{probe.Name}' appears more than once.", nameof(probes));
            }
        }

        var counts = string.Join(",", probes.Select(probe => $"{probe.Name}:count({probe.Tokens})"));
        var expression = SteamUiProbeJs.Preamble($"steam_ui_{gateName}_probe_")
                         + $"return JSON.stringify({{{counts}}});"
                         + SteamUiProbeJs.Close;
        return new SteamGatePatch(
            patchId,
            gateName,
            fingerprint,
            expression,
            root => probes.All(probe => SteamUiPatchEvaluation.IsOne(root, probe.Name)),
            "status.installed&&status.resolved&&status.subscribed",
            "!status.installed",
            subject);
    }

    private static bool IsIdentifier(string? name)
    {
        if (string.IsNullOrEmpty(name) || !(char.IsAsciiLetter(name[0]) || name[0] is '_' or '$'))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '_' or '$'))
            {
                return false;
            }
        }

        return true;
    }
}
