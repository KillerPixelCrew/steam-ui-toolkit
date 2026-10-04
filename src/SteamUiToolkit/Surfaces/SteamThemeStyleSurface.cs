using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One stylesheet block a host wants installed in Steam's windows.</summary>
/// <param name="Id">
///     Stable identity within one publication: letters, digits, <c>_</c>, <c>.</c>, <c>:</c> and
///     <c>-</c>. The gate keys its nodes by it.
/// </param>
/// <param name="Css">The stylesheet text, installed verbatim.</param>
/// <param name="Targets">
///     Which windows the block is for, in CSSLoader's vocabulary: a whole-title regular expression,
///     tried against the window's own name as well as its title, <c>~text~</c> for a URL substring, or
///     <c>!name</c> for a class on the document's root elements. At least one.
/// </param>
/// <param name="Hash">
///     A short digest of <paramref name="Css" />. The gate compares hashes
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
///         Steam renders from SharedJSContext, so the same documents are reachable from the one context
///         this toolkit already holds: the ones its popup manager lists, and the ones its React trees
///         render into through portals, which on Windows is where Quick Access, the main menu and the
///         toasts are. The gate installs the published blocks into every window once, and touches a
///         window again only when the blocks change or Steam announces a new window through the popup
///         manager's created callback. It never polls: looking every two seconds meant walking Steam's
///         whole React tree on its own thread, which slowed every image Big Picture loads.
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
    ///     download sort and the side-menu snapshot read <c>SteamUIStore</c>, and whether SharedJSContext
    ///     has a mounted React tree; either is a way to the windows, and the gate uses both. Nothing
    ///     about webpack is required: the gate touches documents, not modules.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "themeStyles",
        "steam-theme-styles-v1:popup-manager",
        """
        (()=>{try{
          const manager=window.g_PopupManager;
          const popupManager=!!manager&&typeof manager.GetPopups==='function'?1:0;
          let popups=0;
          if(popupManager){try{popups=Array.from(manager.GetPopups()??[]).length;}catch{popups=0;}}
          // A mounted React tree, whose portals are the windows the popup manager does not list.
          let reactRoot=0;
          const host=document.getElementById('root');
          if(host&&Object.keys(host).some(name=>name.startsWith('__reactContainer$')))reactRoot=1;
          return JSON.stringify({popupManager,popups,reactRoot});
        }catch(error){return JSON.stringify({error:String(error)}); } })()
        """,
        root => SteamUiPatchEvaluation.IsOne(root, "popupManager") || SteamUiPatchEvaluation.IsOne(root, "reactRoot"),
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
