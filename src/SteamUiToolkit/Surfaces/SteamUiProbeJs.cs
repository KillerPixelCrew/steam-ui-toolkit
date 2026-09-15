using System.Collections.Generic;
using System.Text.Json;

namespace SteamUiToolkit;

/// <summary>Shared JavaScript fragments for read-only webpack structural probes.</summary>
/// <remarks>
/// A probe names the modules it touches. The preamble captures webpack's require by pushing an
/// empty chunk, which evaluates nothing; a probe built on it then reads factory source as text and
/// resolves only literal module ids. Iterating the registry and constructing exports is the one
/// thing a probe must never do — it has restarted a machine and signed Steam out.
/// </remarks>
public static class SteamUiProbeJs
{
    /// <summary>Opens a probe IIFE, captures webpack's require and defines the token counter.</summary>
    /// <param name="chunkLabel">The stable chunk-label prefix, kept per probe for live diagnostics.</param>
    /// <returns>The opening of a probe expression that defines <c>req</c> and <c>count(tokens)</c>;
    /// the caller closes it with <see cref="Close"/>.</returns>
    public static string Preamble(string chunkLabel) => $$"""
        (()=>{try{
          const req={{SteamUiModuleResolver.CreateExpression(chunkLabel)}};
          const count=req.count;
        """;

    /// <summary>Closes a probe opened by <see cref="Preamble"/>.</summary>
    /// <remarks>A probe that throws answers with the error as its result, so the manager records
    /// why the client is incompatible instead of a bare JavaScript exception.</remarks>
    public const string Close = "}catch(error){return JSON.stringify({error:String(error)}); } })()";

    /// <summary>The source tokens that identify React's own module.</summary>
    internal const string ReactTokens =
        "['react.transitional.element','useState','cloneElement','createElement']";

    /// <summary>The source tokens of the module exporting Valve's slider and dropdown fields.</summary>
    internal const string NativeFieldTokens = "['DialogSlider_Container','DropDownField','SliderField']";

    /// <summary>The source tokens of Steam's localizer module.</summary>
    internal const string LocalizationTokens =
        "['Attempting to localize token','Unable to find localization token','LocalizeString']";

    /// <summary>The source token of mobx-react-lite, whose <c>useObserver</c> Steam builds lists on.</summary>
    internal const string ObserverTokens = "['mobx-react-lite requires React with Hooks support']";

    /// <summary>The source tokens of the module holding Steam's client settings store.</summary>
    internal const string SettingsStoreTokens = "['get clientSettings()','m_setDeferredSettings']";

    /// <summary>The source tokens of the module holding Steam's route table.</summary>
    internal const string RouteTableTokens = "['GameAPIOSK:','/gameapiosk']";

    /// <summary>The source tokens of the performance-actions module the rows ride by default.</summary>
    internal static IReadOnlyList<string> PerformanceActionTokens { get; } =
        ["SetFPSLimitEnabled", "SetFPSLimit", "SetPerfOverlayLevel", "SteamClient.System.Perf"];

    /// <summary>The source tokens of Valve's TDP-limit presentation component.</summary>
    internal static IReadOnlyList<string> TdpPresentationTokens { get; } =
        ["#QuickAccess_Tab_Perf_TDPLimitEnabled", "steamos_tdp_limit", "showBookendLabels"];

    /// <summary>Writes a token list as the JavaScript array a probe's counter takes.</summary>
    /// <param name="tokens">The literal source tokens.</param>
    /// <returns>A JSON array literal, which JavaScript reads unchanged.</returns>
    internal static string Tokens(IReadOnlyList<string> tokens) =>
        JsonSerializer.Serialize(tokens, SteamSurfaceJsonContext.Default.IReadOnlyListString);

    /// <summary>Whether a property descriptor could be replaced and later restored.</summary>
    /// <param name="descriptor">The JavaScript variable holding the descriptor, or null.</param>
    /// <returns>An expression that is true only for a writable, configurable descriptor.</returns>
    internal static string Replaceable(string descriptor) =>
        $"!!{descriptor}&&{descriptor}.writable===true&&{descriptor}.configurable===true";

    /// <summary>Whether a <c>SteamClient.System</c> namespace is absent, or present and ours.</summary>
    /// <param name="name">The namespace under <c>SteamClient.System</c>, such as <c>Audio</c>.</param>
    /// <returns>An expression evaluating to that verdict.</returns>
    /// <remarks>
    /// A namespace a gate installed is not evidence of a native backend. Treating it as one made the
    /// audio patch declare itself incompatible five seconds after a successful install, tear down,
    /// and orphan the namespace it had just defined, which left Steam's audio page empty until Steam
    /// itself restarted. An orphaned Perf namespace is worse: it leaves <c>SystemPerfStore</c>
    /// holding half-written state, which is what crashed the whole Performance tab. The
    /// <c>__wsgm*</c> spelling is the marker a build before the rename wrote; it is read as ours so
    /// that upgrade needs no Steam restart, and never written.
    /// </remarks>
    internal static string OwnedOrAbsentNamespace(string name) =>
        "(()=>{const n=window.SteamClient&&window.SteamClient.System&&window.SteamClient.System."
        + name
        + ";return !n||n.__steamUiOwnedNamespace===true||n.__wsgmOwnedNamespace===true;})()";
}
