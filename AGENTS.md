# SteamUiToolkit contributor guide

## Scope and sources of truth

SteamUiToolkit is a pre-1.0 Windows library for modifying Steam Big Picture through its Chromium
debugging interface. It owns endpoint discovery, persistent CDP transport, patch lifecycle, in-page
bridge, module runtime, extension discovery, and the built-in surfaces.

`README.md` is the orientation and usage guide. `docs/reference.md` is the whole-system contract.
XML documentation on each public member is the authoritative API wording. Read all three relevant
layers before changing a contract, and update them together.

Consumer policy does not belong here. WSGM decides when the transport and individual surfaces are
enabled; this repository supplies mechanisms and truthful state.

## Repository map

- `src/SteamUiToolkit/SteamUiEndpointDiscovery.cs`, `NativeTcp.cs`: port ownership and target
  discovery.
- `SteamUiCdpConnection.cs`, `PersistentSteamUiTransport.cs`: framed CDP traffic, subscriptions,
  health, reconnection, and generations.
- `SteamUiPatchManager.cs`, `SteamUiPatchEvaluation.cs`: probe/apply/verify/remove lifecycle.
- `SteamUiShared.cs`: internal bounds, timeout validation and safe cancellation the core shares.
- `SteamUiBridge.cs`, `SteamUiBridgeIdentity.cs`: host binding, authorization, delivery, and
  generation identity.
- `SteamUiModule.cs`, `SteamUiModuleRuntime.cs`: state publication and semantic command routing.
- `SteamUiExtension*.cs`: package discovery, validation, and claim conflict handling.
- `src/SteamUiToolkit/Surfaces`: typed states, backend contracts, patches, and modules.
- `src/SteamUiToolkit/Client`: one-shot reads and writes against `SteamClient.*` and Steam's stores,
  and the running-app observer behind the app lifetime events.
- `SteamUiAssets/Source`: TypeScript bridge, ownership helpers, RPC support, shared gate helpers,
  gates, the component host, and `settings.ts`, which draws a host's settings pages with Steam's
  own routed sidebar, sections and fields.
- `eng/build-prelude.mjs`: deterministic source composition and TypeScript validation.
- `eng/run-checks.mjs`: builds the prelude and runs every emitted-asset check, stopping at the first
  failure; `eng/check-harness.mjs` is what the checks share (asset loading, marker slices, gate
  instantiation over the real ownership primitives and gate helpers, and a React stand-in).
- `eng/check-*.mjs`: the emitted-asset checks. `check-ownership-claims` (claim primitives),
  `check-startup` (module resolver, component host, network probe, bridge replay),
  `check-power-profile` (row dropdowns, glyphs, device controls, sections, power sliders and the
  slider echo), `check-service-gates` (Bluetooth and brightness), `check-navigation-panel`,
  `check-settings-fields` (the settings renderer), `check-pages`, `check-storage`, `check-library` (library badge, details stat and the JSX claim),
  `check-extension-surfaces` (the Extensions tab and the game context menu),
  `check-home-carousel` and `check-screensaver`.
- `tests/SteamUiToolkit.Tests`: transport, bridge, lifecycle, extension, and surface contracts, with
  one shared set of fakes, builders and the recording backend under `Fakes/`.

Paths without a leading directory in the map above are relative to `src/SteamUiToolkit`. `dist/` is
generated and ignored. Edit the TypeScript source, never generated prelude output.

## Architecture boundaries

Keep Steam-build-specific facts in the surface and injected-asset layer, not in endpoint discovery,
transport, bridge, patch-manager, or module-runtime core. A surface owns its source fingerprints,
export shapes and store fields, while shared asset helpers centralize localization and
row-placement vocabulary used by several surfaces. Never write down a webpack module id or a
minified export name: client builds renumber and rename both (the September 2026 beta did). Do not
describe minified code either. A fingerprint names tokens an author typed and says nothing about the
identifiers or spacing a minifier chose between them; a regex that spelled a local as a single
character took the custom-page gate off an otherwise compatible client on 2026-09-24.

A complete surface owns its whole vertical slice:

- typed C# state and backend interface;
- stable patch id and exact command vocabulary;
- patch probe, apply, verify, and remove implementation;
- TypeScript gate or component behavior;
- bounded payload parsing and fixed refusal reasons;
- module wiring and contract tests.

Register the bridge and dependent surface patches in the same manager, but do not rely on
registration call order: the manager synchronizes by patch id and retries unmet conditions. Quick
Access rows share the documented performance-root resource so their mutations serialize.

## Discovery and transport invariants

Before making an HTTP request, prove that port 8080 is owned by an accepted Steam process and that
the endpoint is loopback. Do not weaken the foreign-process, wildcard-listener, URL, response-size,
or timeout gates.

The persistent transport owns one connection per target role. Preserve:

- subscriber ownership and rejection of late connections from a previous owner;
- bounded request and event channels;
- correlation of responses without blocking on slow notification handlers;
- separate browser, target, session, frame, execution-context, and document generations;
- enabling every required CDP domain before publishing a connection as ready;
- reconnection and health restoration after transient failure;
- the session-wide master switch and deterministic disposal.

A Steam restart or document replacement is represented by generation changes. Stale work must not
publish success into a newer generation.

## Patch lifecycle invariants

Every patch must have a stable id, positive version, target role, resource key, and bounded
operations.

- Probe is read-only and returns a semantic fingerprint, not merely a module id.
- Compatibility must be unique. More than one structural match is unsafe.
- Apply touches only resources owned by that patch.
- Verify proves the resulting behavior.
- Remove retracts and verifies removal of only the patch's own work.
- An applied mutation that does not verify is removed.
- Generation changes cancel stale phases and require re-probing.
- Global and per-patch kill switches retract work; use awaited forms when shutdown or settings
  confirmation must know cleanup completed.
- Fail open to Valve behavior. An incompatible Steam build should leave the stock UI intact.

Every refusal and state transition needs a bounded, stable diagnostic. A control that silently does
nothing is a defect.

## Bridge and ownership invariants

The bridge is generation-bound and asset-hash-bound. Preserve schema checks, payload limits,
allowlisted patch/command pairs, positive sequence and action generations, replay rejection, and
readiness checks.

Ownership must survive separate CDP evaluations:

- Every mutation carries a marker and recognizes "already ours."
- Reclaiming prior toolkit work must recover the underlying original; wrappers must not stack.
- Removal restores exactly what was displaced, read from the live object or saved descriptor.
- Never restore an invented platform value.
- Reveal one gated surface or getter; never set Steam's global platform identity.
- Never iterate the webpack registry while constructing arbitrary exports. Capture the runtime by
  the shared module resolver, then resolve a module by a unique source fingerprint and an export by
  its shape (`exported`). Features supply fingerprints to `SteamUiModuleResolver`; they do not
  implement their own registry scans or raw require calls.
- Prefer a handle Steam has already handed you over any fingerprint. A component sitting in props
  Steam rendered, or a store it has already constructed, is the thing itself rather than a
  description of it, so no client build can rename it away. The page gate takes Steam's `Route` off
  the route list it has already located, for exactly that reason. A fingerprint is for a handle
  nothing rendered yet can supply, and where one has a fallback it must not be what decides whether
  a client is supported.
- React has one `useMemo`. A surface that needs to see what it returns registers a transform on the
  shared claim (`interceptMemo`/`releaseMemo`); it never wraps `useMemo` itself.
  Keep `module-resolver.ts` valid JavaScript because those exact bytes are also embedded for C#
  probes.
- A successful patch must remain compatible with its own next probe.

The extension host validates identity and conflicts; it is not a security sandbox. Keep path
containment, strict UTF-8, size, API-version, id-scope, and deterministic conflict rules intact.

## TypeScript asset contract

`eng/build-prelude.mjs` owns fragment order. The prelude remains an open IIFE for consumer
fragments; the complete asset appends `epilogue.ts` and closes it. `types.ts` is declarations only
and must not emit runtime code.

The emitted asset is intentionally readable, type-stripped ES2022 JavaScript. Do not bundle, minify,
downlevel, or add helpers. `types.ts`, `bridge.ts`, `ownership.ts` and `rpc.ts` come first; every
other top-level fragment and every file under `gates/` is discovered in sorted order, matching
WSGM's `build-steam-assets.mjs`. A new shared fragment therefore needs no builder change in either
repository, but it must not be read during bundle evaluation before its own definition. Changes to
fragment roles or ordering belong in both builders and the reference documentation.

A change to `ownership.ts` must be exercised against the emitted output through the ownership claims
gate, not only reasoned about from TypeScript source.

## Public API and documentation

Every public member requires complete XML documentation, including all parameters. The library
treats compiler warnings, CS1591, and CS1573 as errors.

When changing public constants, bridge fields, command payloads, extension manifests, limits, or
surface state, update:

- implementation and XML documentation;
- `docs/reference.md`, including constants and test coverage;
- README usage when consumer behavior changes;
- focused C# tests;
- emitted-prelude tests when injected behavior changes.

The package is pre-1.0, but changes should still be deliberate and visible to its pinned consumers.

## Validation

Use the same sequence as CI:

```powershell
dotnet build .\SteamUiToolkit.slnx --configuration Release
dotnet test .\SteamUiToolkit.slnx --configuration Release --no-build
npm ci --ignore-scripts --no-audit --no-fund
npm run prelude:claims
```

CI uses .NET 10 and Node 22. `prelude:claims` runs `eng/run-checks.mjs`, which builds the prelude
and runs every emitted-asset check against it. Given an asset path, the runner checks that asset
without building, which is how a consumer can check its own composed asset.

Run focused tests during iteration, but retain the full gate for code or asset changes. A change to
Steam module matching, localization, layout, or runtime behavior also needs explicit validation
against a running Steam client; fake wires and fixtures cannot prove compatibility with a new client
build.

Respect `.editorconfig` and the established local TypeScript style. Avoid unrelated formatting. Do
not commit `bin/`, `obj/`, `node_modules/`, or `dist/`.
