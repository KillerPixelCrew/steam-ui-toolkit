using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One Steam library the badge can name, and the games it holds.</summary>
/// <param name="Name">The badge text for a game in this library. Untrusted display text, bounded by the gate.</param>
/// <param name="Connected">
/// Whether the library is attached right now. Steam's own installed flag decides the badge's
/// colour where it can; this stands in only for a game whose overview cannot say.
/// </param>
/// <param name="AppIds">The Steam app ids installed in this library.</param>
public sealed record SteamLibraryBadgeLibrary(string Name, bool Connected, IReadOnlyList<long> AppIds);

/// <summary>Everything the badge needs to name a game's library.</summary>
/// <param name="Libraries">
/// The libraries worth naming — every tracked removable one, attached or not, so a game on an
/// absent card keeps naming the card. A game in none of them is on the internal library.
/// </param>
/// <param name="InternalLabel">The badge text for an installed game that no listed library holds.</param>
/// <param name="Revision">Monotonic host observation revision.</param>
public sealed record SteamLibraryBadgeState(
    IReadOnlyList<SteamLibraryBadgeLibrary> Libraries,
    string InternalLabel = "Internal",
    long Revision = 0);

/// <summary>What the library badge tells its host.</summary>
public interface ISteamLibraryBadgeBackend
{
    /// <summary>Reports Steam's Big Picture Home layout: Big Art Mode on or off.</summary>
    /// <remarks>
    /// Sent when the gate first resolves the setting and again whenever a tile render sees it
    /// change, so a host learns of a toggle without polling.
    /// </remarks>
    /// <param name="bigArt">Whether <c>library_home_big_art</c> is on.</param>
    /// <param name="cancellationToken">Cancels the handling.</param>
    /// <returns>The outcome. A refusal is reported rather than swallowed.</returns>
    Task<SteamUiCommandResult> HomeLayoutAsync(bool bigArt, CancellationToken cancellationToken);
}

/// <summary>
/// A library badge beside Valve's Steam Input badge on every library tile: the name of the library
/// that holds the game, green when the game is installed and grey when it is not.
/// </summary>
/// <remarks>
/// The tile and the Steam Input badge are exported from one module, but the tile draws the badge
/// through its module-local name, so the claim is on the tile memo's <c>type</c> and the anchor is
/// found in what the tile renders by element type — identity with the export, never a generated
/// class name. Every caller draws the tile through that export, so one claim reaches Home's
/// carousel and the library grid alike, and the badge inherits the icon row's own visibility,
/// which Valve shows on the focused tile only.
/// <para>
/// Big Art Mode is Steam's own <c>library_home_big_art</c> client setting, read from the settings
/// store the Home component reads it from and reported to the host through <c>homeLayout</c>.
/// The badge itself is tile-relative and moves with the carousel in either layout.
/// </para>
/// <para>
/// Mapped against the September 2026 client beta on 2026-09-11: <c>appportrait_</c> occurs in
/// exactly one of the 2622 loaded modules, that module has exactly one <c>React.memo</c> export
/// and exactly one function export drawing the controller-support icon, and the memo's
/// <c>type</c> is a writable, configurable own property, which is what makes the claim restorable.
/// </para>
/// </remarks>
public static class SteamLibraryBadgeSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.library-badge";

    /// <summary>The exact command vocabulary the injected gate sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["homeLayout"];

    /// <summary>The gate that claims the tile and draws the badge from the published libraries.</summary>
    /// <remarks>
    /// The probe requires each structural fact the gate resolves on, separately, so an incompatible
    /// client says which one moved. The settings store is reported but not required: without it
    /// the badge still draws and Big Art Mode reads as unknown. It accepts a tile this gate has
    /// already claimed, for the reason every gate does.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        id: PatchId,
        resourceKey: "steam-ui.library-tile",
        gateName: "libraryBadge",
        fingerprint: "steam-library-badge-v1:unique-tile-module+single-memo-export+single-badge-export",
        probeExpression: $$"""
            {{SteamUiProbeJs.CountingPreamble("steam_ui_library_badge_probe_")}}
              const tile=req.findUnique(['ControllerSupportIcon','appportrait_']);
              if(!tile)return JSON.stringify({tileModule:0});
              const exports=req(tile[0]);
              const memoType=Symbol.for('react.memo');
              // The tile is the module's one memo; the badge is the one function drawing the
              // controller-support icon. Chosen by what they are, never by a minified name.
              const tiles=Object.keys(exports).filter(name=>{
                const value=exports[name];
                return value&&typeof value==='object'&&value.$$typeof===memoType;
              });
              const badges=Object.keys(exports).filter(name=>{
                const value=exports[name];
                return typeof value==='function'&&String(value).includes('ControllerSupportIcon');
              });
              const memo=tiles.length===1?exports[tiles[0]]:null;
              const descriptor=memo?Object.getOwnPropertyDescriptor(memo,'type'):null;
              return JSON.stringify({
                tileModule:1,
                tileExports:tiles.length,
                badgeExports:badges.length,
                // Writable and configurable, or the claim could neither replace nor restore it.
                claimable:!!descriptor&&descriptor.writable===true&&descriptor.configurable===true,
                // Already ours is compatible; see the remarks on this patch.
                claimed:!!memo&&memo.type.__steamUiLibraryBadgeClaimed===true,
                settingsModule:count(['get clientSettings()','m_setDeferredSettings']),
                react:count(['react.transitional.element','useState','cloneElement','createElement'])
              });
            }catch(error){return JSON.stringify({error:String(error)}); } })()
            """,
        compatible: root =>
            SteamUiPatchEvaluation.IsOne(root, "tileModule")
            && SteamUiPatchEvaluation.IsOne(root, "tileExports")
            && SteamUiPatchEvaluation.IsOne(root, "badgeExports")
            && SteamUiPatchEvaluation.IsOne(root, "react")
            && SteamGatePatch.Flag(root, "claimable"),
        verifyOk: "status.installed&&status.resolved&&status.claimed",
        removeOk: "!status.claimed",
        subject: "Library badge gate");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamLibraryBadgeState state) =>
        JsonSerializer.SerializeToElement(
            state, SteamSurfaceJsonContext.Default.SteamLibraryBadgeState);

    /// <summary>Reads the exact <c>homeLayout</c> payload: <c>{ bigArt: bool }</c> and nothing else.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="bigArt">The reported mode.</param>
    /// <returns>Whether the payload had that shape.</returns>
    public static bool TryReadHomeLayout(JsonElement payload, out bool bigArt)
    {
        bigArt = false;
        if (payload.ValueKind != JsonValueKind.Object
            || !SteamUiPayload.HasExactly(payload, 1)
            || !payload.TryGetProperty("bigArt", out JsonElement value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        bigArt = value.ValueKind is JsonValueKind.True;
        return true;
    }

    /// <summary>Declares the surface as one module: the gate, the libraries, and the layout report.</summary>
    /// <param name="enabled">Whether the libraries may be published right now.</param>
    /// <param name="read">The libraries to name, or null when there is nothing to say yet.</param>
    /// <param name="backend">What hears the Home layout report.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamLibraryBadgeState?>> read,
        ISteamLibraryBadgeBackend backend,
        string id = "library-badge")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            patches: [Patch],
            publications:
            [
                SteamSurfaceModule.Publication(
                    PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamLibraryBadgeState),
            ],
            commands:
            [
                new(PatchId, "homeLayout", (request, cancellationToken) =>
                    TryReadHomeLayout(request.Payload, out bool bigArt)
                        ? backend.HomeLayoutAsync(bigArt, cancellationToken)
                        : SteamSurfaceModule.Invalid("The home layout payload is invalid.")),
            ]);
    }
}
