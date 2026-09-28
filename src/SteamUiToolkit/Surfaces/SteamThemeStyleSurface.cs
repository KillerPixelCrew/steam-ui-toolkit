using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One stylesheet block a host wants installed in Steam's windows.</summary>
/// <param name="Id">
///     Stable identity within one publication: letters, digits, <c>_</c>, <c>.</c>, <c>:</c> and
///     <c>-</c>, at most 96 characters. The gate keys its nodes by it.
/// </param>
/// <param name="Css">The stylesheet text, installed verbatim; at most 4 MiB.</param>
/// <param name="Targets">
///     Which windows the block is for, in CSSLoader's vocabulary: a whole-title regular expression,
///     <c>~text~</c> for a URL substring, or <c>!name</c> for a class on the document's root elements.
///     At least one, at most 32.
/// </param>
/// <param name="Hash">
///     A short digest of <paramref name="Css" />, at most 64 characters. The gate compares hashes
///     rather than text, so an unchanged block is never rebuilt.
/// </param>
public sealed record SteamThemeStyle(string Id, string Css, IReadOnlyList<string> Targets, string Hash);

/// <summary>The stylesheet blocks that should currently be installed, in cascade order.</summary>
/// <param name="Styles">The blocks. A later block's rules win over an earlier one's at equal specificity.</param>
/// <param name="Revision">Monotonic host observation revision, shared by publications and readback.</param>
public sealed record SteamThemeState(IReadOnlyList<SteamThemeStyle> Styles, long Revision = 0);

/// <summary>
///     CSSLoader-compatible stylesheets in every Steam window.
/// </summary>
/// <remarks>
///     <para>
///         CSSLoader attaches a debugger session to each of Steam's page targets and appends one
///         <c>&lt;style&gt;</c> per block to that document's head. Every one of those windows is a popup
///         Steam opens from SharedJSContext and keeps in its popup manager, so the same documents are
///         reachable from the one context this toolkit already holds. The gate reconciles every popup's
///         head with the published blocks, and looks again every two seconds for a popup Steam opened
///         or navigated since, which is what CSSLoader's forced re-injection and health check exist for.
///     </para>
///     <para>
///         The toolkit installs what it is given and reads none of it. Loading a theme's files,
///         translating its class names for the running client build, resolving its patches and ordering
///         its blocks are a host's, and a host that publishes nothing leaves Steam's own styling exactly
///         as it was. Removal takes every owned node out of every window.
///     </para>
/// </remarks>
public static class SteamThemeStyleSurface
{
    /// <summary>The patch id this surface publishes under.</summary>
    public const string PatchId = "steam-ui.theme-styles";

    /// <summary>The exact command vocabulary the injected gate sends.</summary>
    /// <remarks>Empty: the blocks are declared by publication and carry no user-initiated write.</remarks>
    public static IReadOnlyList<string> Commands { get; } = [];

    /// <summary>The gate that installs the blocks into Steam's popup documents.</summary>
    /// <remarks>
    ///     The probe reads Steam's popup manager by the name Valve publishes it under, the same way the
    ///     download sort and the side-menu snapshot read <c>SteamUIStore</c>, and counts the popups it
    ///     holds. Nothing about webpack is required: the gate touches documents, not modules.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "steam-ui.theme-styles",
        "themeStyles",
        "steam-theme-styles-v1:popup-manager",
        """
        (()=>{try{
          const manager=window.g_PopupManager;
          const ok=!!manager&&typeof manager.GetPopups==='function';
          let popups=0;
          if(ok){try{popups=Array.from(manager.GetPopups()??[]).length;}catch{popups=0;}}
          return JSON.stringify({popupManager:ok?1:0,popups});
        }catch(error){return JSON.stringify({error:String(error)}); } })()
        """,
        root => SteamUiPatchEvaluation.IsOne(root, "popupManager"),
        "status.installed&&status.resolved",
        "!status.installed",
        "Theme styles");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamThemeState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamThemeState);
    }

    /// <summary>Declares the surface as one module: the gate and the published blocks.</summary>
    /// <param name="enabled">Whether the blocks may be published right now.</param>
    /// <param name="read">The blocks that should be installed, or null when there is nothing to say.</param>
    /// <param name="revision">
    ///     The state's revision, so a publication round raised by another surface neither rebuilds nor
    ///     serializes megabytes of unchanged CSS.
    /// </param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamThemeState?>> read,
        Func<long> revision,
        string id = "theme-styles")
    {
        ArgumentNullException.ThrowIfNull(revision);
        return new SteamUiModule(
            id,
            [Patch],
            [
                SteamUiModuleBuilder.Publication(
                    PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamThemeState, revision)
            ],
            []);
    }
}
