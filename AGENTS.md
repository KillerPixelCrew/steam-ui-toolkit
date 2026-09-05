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
- `SteamUiBridge.cs`, `SteamUiBridgeIdentity.cs`: host binding, authorization, delivery, and
  generation identity.
- `SteamUiModule.cs`, `SteamUiModuleRuntime.cs`: state publication and semantic command routing.
- `SteamUiExtension*.cs`: package discovery, validation, and claim conflict handling.
- `src/SteamUiToolkit/Surfaces`: typed states, backend contracts, patches, and modules.
- `SteamUiAssets/Source`: TypeScript bridge, ownership helpers, RPC support, gates, and component
  host.
- `eng/build-prelude.mjs`: deterministic source composition and TypeScript validation.
- `eng/check-ownership-claims.mjs`: tests ownership behavior against the emitted JavaScript.
- `tests/SteamUiToolkit.Tests`: transport, bridge, lifecycle, extension, and surface contracts.

Paths without a leading directory in the map above are relative to `src/SteamUiToolkit`. `dist/` is
generated and ignored. Edit the TypeScript source, never generated prelude output.

## Architecture boundaries

Keep Steam-build-specific facts in the surface and injected-asset layer, not in endpoint discovery,
transport, bridge, patch-manager, or module-runtime core. A surface may own its literal webpack
module ids and store fields, while shared asset helpers centralize fingerprints, localization, and
row-placement vocabulary used by several surfaces.

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
  the shared module resolver, then inspect named module ids or source. Features supply fingerprints
  to `SteamUiModuleResolver`; they do not implement their own registry scans or raw require calls.
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
downlevel, or add helpers. New files under `gates/` are discovered in sorted order; changes to
fragment roles or ordering belong in the builder and reference documentation.

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

CI uses .NET 10 and Node 22. `prelude:claims` also builds the prelude.

Run focused tests during iteration, but retain the full gate for code or asset changes. A change to
Steam module matching, localization, layout, or runtime behavior also needs explicit validation
against a running Steam client; fake wires and fixtures cannot prove compatibility with a new client
build.

Respect `.editorconfig` and the established local TypeScript style. Avoid unrelated formatting. Do
not commit `bin/`, `obj/`, `node_modules/`, or `dist/`.
