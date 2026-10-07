# Injected Steam UI source

These files compile into one lexical scope inside the bridge's IIFE. They are not independently
loaded ES modules. The C# side supplies the configuration and asset hash, owns attachment and patch
lifetime, and routes semantic commands to host backends. The
[whole-system reference](../../../../docs/reference.md) defines the public contracts; the
[source map](../../../../docs/code-map.md) pairs every surface with its C# state, backend and patch.

## Composition and execution

[`eng/steam-ui-fragments.mjs`](../../../../eng/steam-ui-fragments.mjs) discovers the source and owns
its order for the toolkit and its consumers:

1. `types.ts` declares ambient and cross-fragment types; no runtime code may be emitted from it.
2. `bridge.ts` opens the IIFE and initializes the generation/asset-bound bridge.
3. `ownership.ts` and `rpc.ts` define the mutation and service-reply primitives.
4. Other top-level `.ts` files in sorted order define shared helpers and registrations.
5. `gates/*.ts` in sorted order register the surface gates.
6. `components.ts` registers the Quick Access row host.
7. Consumer `.ts` directories are appended in their declared order, files sorted within each.
8. `epilogue.ts` returns the installation result; the compiler adds the closing IIFE.

The compiler inserts a `// @fragment <path>` label before each fragment after `bridge.ts`. The
fragment's first declaration must survive type stripping, so a leading erased `type` declaration
must not consume its label. Top-level registration may reference hoisted functions, but must not
read a later fragment's uninitialized `const`. Type checking uses strict ES2022 and type stripping;
there is no bundler, minifier or downlevel helper output. `module-resolver.ts` must also remain
valid JavaScript because C# embeds its exact source for standalone probes.

`npm run prelude:build` writes ignored `dist/prelude.js` (open IIFE) and `dist/steam-ui.js`
(complete standalone asset). Consumers with their own fragments call `compileSteamUiAsset`; they
must hash and ship the resulting complete program. A documentation-only change in this README does
not change emitted bytes.

## Shared fragments and script contracts

| Fragment                                   | Contract and owner                                                                                                                                                                                                                                                                                        |
| ------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [bridge.ts](bridge.ts)                     | `request`, `subscribe`, refusal replay, `deliver`/`deliverPart`, gate registration and bridge disposal. Subscribers synchronously receive the most recently delivered state. Host deliveries are assembled whole; incomplete messages never reach a surface.                                              |
| [types.ts](types.ts)                       | Wire, page, gate and renderer declarations shared across the program. Runtime validators still check data received across the bridge.                                                                                                                                                                     |
| [ownership.ts](ownership.ts)               | `supplyNamespace`/`withdrawNamespace`, property/member/accessor claims and their releases. Durable descriptor snapshots survive a later evaluation and reclaim underlying originals instead of stacking wrappers. Shared `interceptMemo` and `interceptElements` claims let independent surfaces coexist. |
| [rpc.ts](rpc.ts)                           | `transportReply`, `transportFailure`, query-client resolution and invalidation for Steam service contracts.                                                                                                                                                                                               |
| [module-resolver.ts](module-resolver.ts)   | `createSteamUiModuleResolver`: unique authored-token source matching, export-shape selection and runtime-aware caching. `count` and `findUnique` inspect sources; `resolve` and `exported` may load only a unique match. No raw registry/loader is exposed.                                               |
| [gate-helpers.ts](gate-helpers.ts)         | Shared fingerprints, Steam component/localizer resolution, dropdowns/modals, local state stores, route validation, fold state, React tree/fiber discovery and mounted-type adoption. Prefer Steam's rendered handles where available.                                                                     |
| [icons.ts](icons.ts)                       | Stable toolkit SVG glyph vocabulary for rows, sections and host UI.                                                                                                                                                                                                                                       |
| [ui-kit.ts](ui-kit.ts)                     | `steamUiKitStyle` and reusable groups, action grids, notes, swatches, cards, galleries, banners, toolbars, chips, prompts and confirms. Render the stylesheet once per document root using the kit.                                                                                                       |
| [settings.ts](settings.ts)                 | `renderSteamSettings`, shared native field rendering and `useSteamSettingDrafts`. Pending values and refusal text stay with the row; changing publication revisions reconciles drafts.                                                                                                                    |
| [page-gate.ts](page-gate.ts)               | `registerSteamPage`: consumer page state/refusal subscriptions, renderer readiness, lifecycle status and a visible reason when content cannot draw. Routing itself belongs to `gates/pages.ts`.                                                                                                           |
| [file-picker.ts](file-picker.ts)           | `showSteamFilePicker`: Steam modal backed by the file-picker semantic commands. The host C# side reads filesystem names.                                                                                                                                                                                  |
| [library-capsule.ts](library-capsule.ts)   | Steam library class resolution and capsule construction for host pages.                                                                                                                                                                                                                                   |
| [plugin-frontends.ts](plugin-frontends.ts) | `pluginFrontends` gate and shared contribution slots. Bundle code receives the documented unrestricted `api`; scoped registrations, guarded callbacks, owner failure and cleanup are described in [plugin-frontends.md](../../../../docs/plugin-frontends.md).                                            |
| [components.ts](components.ts)             | Row-kind registry, native Quick Access panel interception and final composition. Uses shared memo ownership; keeps Reset to Default after fixed, dynamic and plugin rows. Host layout and fold state are publications.                                                                                    |
| [epilogue.ts](epilogue.ts)                 | Returns the installation result after every registration.                                                                                                                                                                                                                                                 |

`request(patchId, command, payload)` is a semantic backend call, not a general evaluation endpoint.
The union of registered module contracts determines admitted pairs. A new action uses a positive,
monotonic action generation; the bridge also owns its request sequence, document/context identity
and timeout. The page allows 32 pending requests. C# receives them through a bounded notification
channel, so large host-to-page streamed publications do not imply unlimited page-to-host requests.

## Gates

C# owns the probe/apply/verify/remove scheduler; each gate supplies installation, status and removal
mechanisms inside the document. Verification must establish ownership after installation and the
absence of that ownership after removal. These registrations are implementations, not blanket
permission to install every surface.

| Fragment                                                 | Registered behavior                                                                                                           |
| -------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| [gates/audio.ts](gates/audio.ts)                         | `audio`: missing audio namespace, store availability and device/volume state.                                                 |
| [gates/bluetooth.ts](gates/bluetooth.ts)                 | `bluetooth`: service-stub claims, discovery/devices and query invalidation.                                                   |
| [gates/brightness.ts](gates/brightness.ts)               | `brightness`: availability and brightness setter claims with observable state.                                                |
| [gates/network.ts](gates/network.ts)                     | `network`: existing network store, availability getter, scanning wrappers and synthetic access points.                        |
| [gates/performance.ts](gates/performance.ts)             | `perf`: missing performance namespace, native state projection and decoded setting deltas.                                    |
| [gates/elements.ts](gates/elements.ts)                   | `elements`: the shared JSX interceptor's registration front door for scripts outside this lexical scope.                      |
| [gates/extensions-tab.ts](gates/extensions-tab.ts)       | `extensionsTab`: bounded host-rendered plugin sections, actions and primitive settings.                                       |
| [gates/game-context-menu.ts](gates/game-context-menu.ts) | `gameContextMenu`: selected-game host actions reached before the menu's first render.                                         |
| [gates/home-carousel.ts](gates/home-carousel.ts)         | `homeCarousel`: native carousel ordering, disconnected-game filtering and mounted Home adoption.                              |
| [gates/library-badge.ts](gates/library-badge.ts)         | `libraryBadge` and `libraryDetails`: tile badge/game-page stat and plugin addition slots, sharing native JSX/type ownership.  |
| [gates/native-settings.ts](gates/native-settings.ts)     | `nativeSettings`: reactive host slots in Display/Power/Audio/Controller and narrow Power visibility.                          |
| [gates/navigation.ts](gates/navigation.ts)               | `navigationPanel`: add/hide native menu descriptors and adopt mounted panels.                                                 |
| [gates/pages.ts](gates/pages.ts)                         | `pages`: native route list, back-stack Route, declared templates and override precedence.                                     |
| [gates/power-menu.ts](gates/power-menu.ts)               | `powerMenu`: revive Switch to Desktop through a semantic host action.                                                         |
| [gates/screensaver.ts](gates/screensaver.ts)             | `screensaver`: host display-off rows and reports of Steam's own idle choices.                                                 |
| [gates/storage.ts](gates/storage.ts)                     | `storage`: one narrow service transport claim; handles StorageDeviceManager messages and passes other service calls through.  |
| [gates/sound-overrides.ts](gates/sound-overrides.ts)     | `soundOverrides`: exact resource map, asynchronous decode with revision rejection and reversible Gamepad audio-manager claim. |
| [gates/theme-styles.ts](gates/theme-styles.ts)           | `themeStyles`: owned stylesheet nodes in current and newly discovered matching popup documents.                               |

## Change and teardown obligations

A source fingerprint uses authored strings, never a client-build module id, export name or minifier
variable spelling. Probe and install must match the same evidence and accept the already-owned
state. A memo-type replacement must reach an already mounted instance when the feature promises an
immediate update; changing only the exported type affects a future mount.

Every subscription, timer, listener, adopted fiber and property/style claim needs an owner and
cleanup. Restore the actual saved descriptor, including inherited membership. Do not fabricate a
Valve default, claim Steam's global platform identity or wrap `useMemo` independently. An
incompatible surface leaves native behavior available and reports a reason.

`eng/run-checks.mjs` discovers every `eng/check-*.mjs`. Its harness extracts complete emitted
fragments and exercises the same ownership/helper code that ships. The reference lists the C# and
frontend regression sources. Run them according to the host repository's validation timing; a
passing fake-runtime check does not establish current Steam rendering or actual hardware behavior.
