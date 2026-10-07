# SteamUiToolkit source map

This is the reading order for the whole implementation. The [README](../README.md) shows how to use
it; the [reference](reference.md) defines its contracts, limits and diagnostics. XML comments beside
public C# declarations supply member and parameter details. The library targets .NET 10 on Windows;
the injected program is readable ES2022 JavaScript compiled from TypeScript. It has no WSGM policy
or Windows audio, display, radio or device backend of its own.

## Follow one control end to end

```text
host readiness and feature policy
  -> PersistentSteamUiTransport: validated endpoint and current generations
  -> SteamUiPatchManager: bridge first, then probe/apply/verify for each enabled patch
  -> SteamUiModuleRuntime: read a surface state and publish it through SteamUiBridgeHost
  -> bridge.ts: reassemble a complete delivery and notify that patch's subscribers
  -> surface gate or components.ts: render Steam's fields with the published state
  -> request(patchId, command, payload): generation-bound Runtime binding
  -> bridge authorizer -> module command validator -> host backend
  -> command result, then the host's next observed-state publication
```

The backend owns actual OS/device writes. A failed command is a result with a reason; a throwing
callback quarantines its module. A command response and a state publication are different messages.
Publishing null from a typed C# reader means no message, not a reset: publish the surface's explicit
empty state to clear content. Removing a patch withdraws its subscriptions and restores its owned
members; replacing the document invalidates old work before it can report success.

One-shot client calls bypass the bridge and patch manager, borrowing the same transport. Library
writes share `SteamClient`'s serialized write lane. An unanswered write may have executed; the
library never retries it. A running-app lease is the one client reader that installs a resident
observer, and the lease removes it.

## Transport and lifecycle

All paths in this table start in [src/SteamUiToolkit](../src/SteamUiToolkit).

| Source                                                                                                                               | What to read it for                                                                                                                                               |
| ------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [SteamCef.cs](../src/SteamUiToolkit/SteamCef.cs)                                                                                     | Debugging opt-in flag, loopback/owner predicates and JSON-safe JavaScript strings. Creating the flag takes effect on Steam's next cold start.                     |
| [NativeTcp.cs](../src/SteamUiToolkit/NativeTcp.cs), [SteamUiEndpointDiscovery.cs](../src/SteamUiToolkit/SteamUiEndpointDiscovery.cs) | Windows listener ownership before HTTP, bounded discovery and unique target-role matching. Optional MainWindow presence gates all attachments.                    |
| [SteamUiCdpConnection.cs](../src/SteamUiToolkit/SteamUiCdpConnection.cs)                                                             | WebSocket framing, request correlation, send completion, notification pump and connection teardown. Slow notification handlers do not block response correlation. |
| [PersistentSteamUiTransport.cs](../src/SteamUiToolkit/PersistentSteamUiTransport.cs)                                                 | Per-role leases, reconnect, health, domain initialization, master switch and rejection of late connections after an owner leaves.                                 |
| [SteamUiTransportModels.cs](../src/SteamUiToolkit/SteamUiTransportModels.cs)                                                         | Transport interface, six generation counters, snapshots and the `Closed`/`NotSent`/`Unanswered`/`Answered` dispatch distinction.                                  |
| [SteamUiPatchManager.cs](../src/SteamUiToolkit/SteamUiPatchManager.cs)                                                               | Patch interfaces/results, sorted registry, one scheduler, bounded phases, switch cancellation, generation epochs and bridge-first apply/bridge-last removal.      |
| [SteamUiPatchEvaluation.cs](../src/SteamUiToolkit/SteamUiPatchEvaluation.cs)                                                         | Structured probe and operation-result parsing; full failure detail remains available even when log output is bounded.                                             |
| [SteamUiShared.cs](../src/SteamUiToolkit/SteamUiShared.cs)                                                                           | Shared timeout validation, diagnostic truncation, payload string redaction and safe cancellation.                                                                 |
| [SteamUiLog.cs](../src/SteamUiToolkit/SteamUiLog.cs)                                                                                 | Replaceable logging sink; the default discards messages.                                                                                                          |
| [Properties/AssemblyInfo.cs](../src/SteamUiToolkit/Properties/AssemblyInfo.cs)                                                       | Internal visibility for the library's contract tests.                                                                                                             |

`Ready` follows `Debugger.enable`, clearing exception pauses, `Debugger.disable`, then enabling
Runtime, Page and DOM. This protects against a retained debugger pause policy; it does not establish
Big Picture readiness. The consumer still decides when attachment is allowed.

## Bridge, modules and extension discovery

| Source                                                                                                                                     | Responsibility                                                                                                                                                                                |
| ------------------------------------------------------------------------------------------------------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [SteamUiBridge.cs](../src/SteamUiToolkit/SteamUiBridge.cs)                                                                                 | Request envelope and authorizer, binding installation, generation/readiness checks, responses, state deduplication, revision shortcuts and streamed host deliveries.                          |
| [SteamUiBridgeIdentity.cs](../src/SteamUiToolkit/SteamUiBridgeIdentity.cs)                                                                 | Stable namespace and binding names shared by probes and injected code.                                                                                                                        |
| [SteamUiInjectedAsset.cs](../src/SteamUiToolkit/SteamUiInjectedAsset.cs)                                                                   | Consumer-supplied source and its asset hash; the toolkit does not construct a product-specific bootstrap.                                                                                     |
| [SteamUiModule.cs](../src/SteamUiToolkit/SteamUiModule.cs)                                                                                 | Publications, commands, results, module failures and the module-set conflict checks that derive the closed bridge vocabulary.                                                                 |
| [SteamUiModuleBuilder.cs](../src/SteamUiToolkit/SteamUiModuleBuilder.cs)                                                                   | Source-generated typed publications and exact-payload command adapters.                                                                                                                       |
| [SteamUiModuleRuntime.cs](../src/SteamUiToolkit/SteamUiModuleRuntime.cs)                                                                   | Coalesced publication, command dispatch/cancellation, callback quarantine and replacing modules while the host is running.                                                                    |
| [SteamUiModuleResolver.cs](../src/SteamUiToolkit/SteamUiModuleResolver.cs)                                                                 | Embeds the same valid-JavaScript resolver source used by the frontend, so standalone probes and gates discover modules identically.                                                           |
| [SteamUiExtension.cs](../src/SteamUiToolkit/SteamUiExtension.cs), [SteamUiExtensionHost.cs](../src/SteamUiToolkit/SteamUiExtensionHost.cs) | `extension.steam-ui.json` models, strict file reading, package paths, API/id validation and deterministic conflict resolution. This loader returns source text; it does not execute packages. |

Three mechanisms have distinct trust and lifetime contracts: manifest discovery above, host-rendered
Extensions-tab/game-menu descriptors, and the unrestricted
[plugin frontend runtime](plugin-frontends.md). None of the manifest checks makes JavaScript a
sandbox. Hosts own package admission and shutdown.

## Client reads and writes

The [Client directory](../src/SteamUiToolkit/Client) implements direct calls, each with typed
outcomes and its own bounded evaluation. Preserve the difference between a valid empty result and
failure.

| Source                                                                            | Operations and lifetime                                                                                                             |
| --------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------- |
| [SteamClient.cs](../src/SteamUiToolkit/Client/SteamClient.cs)                     | Composition root, shared transport, truthful read/write outcomes and serialized library mutation lane.                              |
| [SteamClientScript.cs](../src/SteamUiToolkit/Client/SteamClientScript.cs)         | Common emitted script helpers and serialization across the C#/JavaScript boundary.                                                  |
| [SteamApps.cs](../src/SteamUiToolkit/Client/SteamApps.cs)                         | App details, launch configuration, shortcuts and custom artwork; app-id/GameID handling and readback belong here.                   |
| [SteamCollections.cs](../src/SteamUiToolkit/Client/SteamCollections.cs)           | Host-owned collection synchronization through Steam's live stores.                                                                  |
| [SteamInstallFolders.cs](../src/SteamUiToolkit/Client/SteamInstallFolders.cs)     | Add/remove/label a Steam library. Folder indices are stable identities, not array positions.                                        |
| [SteamLibraryData.cs](../src/SteamUiToolkit/Client/SteamLibraryData.cs)           | Games, collections and store-tag reads with explicit failure results.                                                               |
| [SteamDownloadActivity.cs](../src/SteamUiToolkit/Client/SteamDownloadActivity.cs) | Download overview; deciding whether activity should keep Windows awake belongs to the host.                                         |
| [SteamCurrentPage.cs](../src/SteamUiToolkit/Client/SteamCurrentPage.cs)           | The game page in view, separate from the set of running games.                                                                      |
| [SteamRunningApps.cs](../src/SteamUiToolkit/Client/SteamRunningApps.cs)           | One-reader lifetime-notification observer and its lease. Closed-by-host transport is handled separately from an unreachable client. |
| [SteamStartupMovie.cs](../src/SteamUiToolkit/Client/SteamStartupMovie.cs)         | Read/write Steam's startup-movie choice. Downloading, overriding files and restoring a saved choice are host policy.                |

## Surface implementations

Each linked file in [Surfaces](../src/SteamUiToolkit/Surfaces) contains the named contract, its
state and backend where applicable. The [surface reference](reference.md#15-surfaces) explains
command payloads, compatibility and placement. Row-only surfaces render through `components.ts`;
other surfaces identify their gate below. The code, not a patch-count total, is the inventory.

| C# source                                                                                                                                                                                  | Frontend or purpose                                                                                        |
| ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------- |
| [SteamAudioSurface.cs](../src/SteamUiToolkit/Surfaces/SteamAudioSurface.cs)                                                                                                                | `gates/audio.ts`: supply the missing audio namespace and feed Steam's store.                               |
| [SteamBluetoothSurface.cs](../src/SteamUiToolkit/Surfaces/SteamBluetoothSurface.cs)                                                                                                        | `gates/bluetooth.ts`: claim the service stub and invalidate its query.                                     |
| [SteamBrightnessSurface.cs](../src/SteamUiToolkit/Surfaces/SteamBrightnessSurface.cs)                                                                                                      | `gates/brightness.ts`: narrow availability/member claims for the native slider.                            |
| [SteamNetworkSurface.cs](../src/SteamUiToolkit/Surfaces/SteamNetworkSurface.cs)                                                                                                            | `gates/network.ts`: existing network store, scanning and header indicator.                                 |
| [SteamPerformanceSurface.cs](../src/SteamUiToolkit/Surfaces/SteamPerformanceSurface.cs)                                                                                                    | `gates/performance.ts`: performance namespace, native state and validated deltas.                          |
| [SteamPowerLimitSurface.cs](../src/SteamUiToolkit/Surfaces/SteamPowerLimitSurface.cs)                                                                                                      | Independent sustained/boost power controls and dispatched-value echo.                                      |
| [SteamAutoTdpRow.cs](../src/SteamUiToolkit/Surfaces/SteamAutoTdpRow.cs)                                                                                                                    | Automatic-power toggle; the control algorithm belongs to the backend.                                      |
| [SteamFrameLimitRow.cs](../src/SteamUiToolkit/Surfaces/SteamFrameLimitRow.cs), [SteamVariableRefreshRow.cs](../src/SteamUiToolkit/Surfaces/SteamVariableRefreshRow.cs)                     | Frame/refresh choices and VRR.                                                                             |
| [SteamResolutionRow.cs](../src/SteamUiToolkit/Surfaces/SteamResolutionRow.cs), [SteamAudioFormatRow.cs](../src/SteamUiToolkit/Surfaces/SteamAudioFormatRow.cs)                             | Quick Settings mode and complete audio-format/spatial choices.                                             |
| [SteamPowerProfileRow.cs](../src/SteamUiToolkit/Surfaces/SteamPowerProfileRow.cs), [SteamPowerPresetRow.cs](../src/SteamUiToolkit/Surfaces/SteamPowerPresetRow.cs)                         | Observed power profile and separate AC/battery preset assignments.                                         |
| [SteamCpuBoostRow.cs](../src/SteamUiToolkit/Surfaces/SteamCpuBoostRow.cs), [SteamHybridCoreRow.cs](../src/SteamUiToolkit/Surfaces/SteamHybridCoreRow.cs)                                   | Processor boost and hybrid-core policy choices.                                                            |
| [SteamControllerTargetRow.cs](../src/SteamUiToolkit/Surfaces/SteamControllerTargetRow.cs), [SteamDeviceControlsRow.cs](../src/SteamUiToolkit/Surfaces/SteamDeviceControlsRow.cs)           | Controller target, charging and lighting. Host availability controls what is drawn.                        |
| [SteamQuickAccessLayoutSurface.cs](../src/SteamUiToolkit/Surfaces/SteamQuickAccessLayoutSurface.cs), [SteamPanelFoldsSurface.cs](../src/SteamUiToolkit/Surfaces/SteamPanelFoldsSurface.cs) | Section placement, row marks and shared fold state; no persistent storage in the toolkit.                  |
| [SteamSettingsRows.cs](../src/SteamUiToolkit/Surfaces/SteamSettingsRows.cs), [SteamSettingsQuickAccessRow.cs](../src/SteamUiToolkit/Surfaces/SteamSettingsQuickAccessRow.cs)               | Shared field/section/page descriptors and host settings in Performance.                                    |
| [SteamNativeSettingsSurface.cs](../src/SteamUiToolkit/Surfaces/SteamNativeSettingsSurface.cs)                                                                                              | `gates/native-settings.ts`: append reactive slots to Display, Power, Audio and Controller.                 |
| [SteamScreensaverSurface.cs](../src/SteamUiToolkit/Surfaces/SteamScreensaverSurface.cs)                                                                                                    | `gates/screensaver.ts`: native timeout observations and host display-off choices.                          |
| [SteamStorageSurface.cs](../src/SteamUiToolkit/Surfaces/SteamStorageSurface.cs)                                                                                                            | `gates/storage.ts`: storage service replies and semantic operations; unrelated service calls pass through. |
| [SteamLibraryBadgeSurface.cs](../src/SteamUiToolkit/Surfaces/SteamLibraryBadgeSurface.cs)                                                                                                  | `gates/library-badge.ts`: tile badges and game-page library details, plus plugin addition slots.           |
| [SteamHomeCarouselSurface.cs](../src/SteamUiToolkit/Surfaces/SteamHomeCarouselSurface.cs)                                                                                                  | `gates/home-carousel.ts`: connected-library game order and mounted Home adoption.                          |
| [SteamNavigationPanelSurface.cs](../src/SteamUiToolkit/Surfaces/SteamNavigationPanelSurface.cs)                                                                                            | `gates/navigation.ts`: native menu additions/hiding and stable descriptor anchors.                         |
| [SteamPageSurface.cs](../src/SteamUiToolkit/Surfaces/SteamPageSurface.cs)                                                                                                                  | `gates/pages.ts`: shared native router/back-stack claims and declared routes.                              |
| [SteamFilePickerSurface.cs](../src/SteamUiToolkit/Surfaces/SteamFilePickerSurface.cs)                                                                                                      | `file-picker.ts`: host file/folder picker requests and native modal.                                       |
| [SteamExtensionsTabSurface.cs](../src/SteamUiToolkit/Surfaces/SteamExtensionsTabSurface.cs)                                                                                                | `gates/extensions-tab.ts`: host-rendered actions/settings in one shared QAM tab.                           |
| [SteamGameContextMenuSurface.cs](../src/SteamUiToolkit/Surfaces/SteamGameContextMenuSurface.cs)                                                                                            | `gates/game-context-menu.ts`: selected-app semantic actions before first menu render.                      |
| [SteamPowerMenuSurface.cs](../src/SteamUiToolkit/Surfaces/SteamPowerMenuSurface.cs)                                                                                                        | `gates/power-menu.ts`: Steam's Switch to Desktop entry backed by a host action.                            |
| [SteamThemeStyleSurface.cs](../src/SteamUiToolkit/Surfaces/SteamThemeStyleSurface.cs)                                                                                                      | `gates/theme-styles.ts`: ordered CSS blocks in matching popup documents.                                   |
| [SteamSoundOverrideSurface.cs](../src/SteamUiToolkit/Surfaces/SteamSoundOverrideSurface.cs)                                                                                                | `gates/sound-overrides.ts`: exact sound resources, decode revision/status and reversible playback claim.   |
| [SteamPluginFrontendSurface.cs](../src/SteamUiToolkit/Surfaces/SteamPluginFrontendSurface.cs)                                                                                              | `plugin-frontends.ts`: unrestricted bundles, contributions and owner-wide failure cleanup.                 |

The remaining surface files support those contracts or operate directly on native Steam windows:

| Source                                                                                                                                                           | Role                                                                                                  |
| ---------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------- |
| [SteamUiBridgePatch.cs](../src/SteamUiToolkit/Surfaces/SteamUiBridgePatch.cs)                                                                                    | Put bridge bootstrap/removal under the same patch manager as dependent surfaces.                      |
| [SteamGatePatch.cs](../src/SteamUiToolkit/Surfaces/SteamGatePatch.cs)                                                                                            | Reusable read-only probe and gate install/status/remove expressions.                                  |
| [SteamQuickAccessRowPatch.cs](../src/SteamUiToolkit/Surfaces/SteamQuickAccessRowPatch.cs)                                                                        | Compatibility, install and removal for one component-host row kind.                                   |
| [SteamPagePatch.cs](../src/SteamUiToolkit/Surfaces/SteamPagePatch.cs)                                                                                            | Lifecycle for a consumer renderer declared with `registerSteamPage`.                                  |
| [SteamUiProbeJs.cs](../src/SteamUiToolkit/Surfaces/SteamUiProbeJs.cs), [SteamUiPayload.cs](../src/SteamUiToolkit/Surfaces/SteamUiPayload.cs)                     | Shared structural fingerprints and exact bounded command readers.                                     |
| [SteamSurfaceJsonContext.cs](../src/SteamUiToolkit/Surfaces/SteamSurfaceJsonContext.cs)                                                                          | Source-generated camelCase serialization for built-in surface states.                                 |
| [SteamOverlayActivationPatch.cs](../src/SteamUiToolkit/Surfaces/SteamOverlayActivationPatch.cs)                                                                  | Own Steam's overlay-activation observer independently of custom QAM rows.                             |
| [SteamSideMenuSnapshot.cs](../src/SteamUiToolkit/Surfaces/SteamSideMenuSnapshot.cs)                                                                              | Observe native menus/keyboard and exact overlay identity; unknown is not closed.                      |
| [SteamNativeSurfaceCommands.cs](../src/SteamUiToolkit/Surfaces/SteamNativeSurfaceCommands.cs)                                                                    | Replay Home/QAM/keyboard against an observed process, app and generation without retry or fallback.   |
| [SteamGameWindowActivation.cs](../src/SteamUiToolkit/Surfaces/SteamGameWindowActivation.cs)                                                                      | Ask Steam to raise one existing game; a successful call is not proof of HWND focus.                   |
| [SteamRouteNavigation.cs](../src/SteamUiToolkit/Surfaces/SteamRouteNavigation.cs), [SteamSharedContext.cs](../src/SteamUiToolkit/Surfaces/SteamSharedContext.cs) | Navigate a host-requested route and check that the borrowed shared-context generation is still ready. |

## Injected source and build

The [frontend source guide](../src/SteamUiToolkit/SteamUiAssets/Source/README.md) documents all
shared fragments, their order and cleanup obligations. C# probes and JavaScript gates must agree
about source tokens, export shape and owned post-install state. Tests against fake React/runtime
objects cannot prove a particular installed Steam build still matches those contracts.

| Tool/configuration                                                                                                      | Purpose                                                                                                                                                      |
| ----------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| [eng/steam-ui-fragments.mjs](../eng/steam-ui-fragments.mjs)                                                             | Shared directory discovery, deterministic fragment ordering, TypeScript type checking and type stripping. Consumer builders import this same implementation. |
| [eng/build-prelude.mjs](../eng/build-prelude.mjs)                                                                       | Write ignored `dist/prelude.js` with an open IIFE and the complete `dist/steam-ui.js` for consumers without extra fragments.                                 |
| [eng/run-checks.mjs](../eng/run-checks.mjs), [eng/check-harness.mjs](../eng/check-harness.mjs)                          | Discover emitted-asset checks and extract complete fragments by label. A supplied asset path checks a consumer's composed bytes.                             |
| [SteamUiToolkit.csproj](../src/SteamUiToolkit/SteamUiToolkit.csproj), [Directory.Build.props](../Directory.Build.props) | Target/package metadata, embedded resolver, shipped TypeScript source and XML-documentation warnings as errors.                                              |
| [package.json](../package.json), [tsconfig.json](../src/SteamUiToolkit/SteamUiAssets/Source/tsconfig.json)              | Prelude scripts and the ES2022, declaration-only typing contract.                                                                                            |
| [tests/SteamUiToolkit.Tests](../tests/SteamUiToolkit.Tests), [eng](../eng)                                              | C# transport/client/surface contracts, shared fakes and `check-*.mjs` frontend checks. The reference's test section maps their coverage.                     |

For standalone library work the contributor guide specifies Release build, .NET tests, dependency
installation and `npm run prelude:claims`. A consumer repository may impose stricter timing, such as
WSGM's manual-first test policy. Documentation review is not a live compatibility pass. Generated
`dist`, `bin`, `obj` and consumer bootstrap files are never the source to edit.
