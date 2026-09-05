# SteamUiToolkit

Add, hide and revive elements in Steam's Big Picture front-end, from .NET. This README is the
orientation and the how-to; the contract, with every limit, state and log key, is in
[`docs/reference.md`](docs/reference.md).

> **Very early. Expect it to change under you.** This was extracted from one application and has
> one consumer. Names, shapes and whole types are still moving, and the API will break without
> deprecation cycles. Pin an exact version, read the commit log before bumping, and expect to fix
> call sites. Issues and pull requests are welcome; a stability promise is not something this can
> honestly offer yet.

```
dotnet add package SteamUiToolkit
```

Steam's client UI is a Chromium application that talks to a CDP client on loopback. This library
is about everything after that: doing it in a way that survives a Steam update, cleans up after
itself, and says why when it does not work.

## What it gives you

`SteamPowerProfileRow.Module` adds a Windows power-profile dropdown to QAM Performance. Supply stable ids,
display labels and observed state through `SteamPowerProfileState`; implement
`ISteamPowerProfileBackend` to validate and apply selections. `SteamPowerPresetRow.Module` adds
independent AC and battery assignments with `SteamPowerPresetState` and `ISteamPowerPresetBackend`.
The active preset is read-only, including Custom. Empty preset options hide those controls.
Performance controls use titled native sections. Quick Settings places display controls before
Steam's common settings, then separate Charging and RGB lighting sections. The toolkit does not
change OS power settings itself.

- A persistent CDP transport that owns one connection per target, tracks execution-context and
  document generations, and verifies the debug port belongs to Steam before connecting.
- A patch lifecycle (probe, apply, verify, remove) where each patch declares what it owns, proves it
  found its target before touching anything, and is removed and re-probed when it cannot be
  verified.
- Three ways to change the client, which between them cover every surface below:

| Way                     | What it does                                                             | What removal owes                    |
| ----------------------- | ------------------------------------------------------------------------ | ------------------------------------ |
| Feed a data construct   | supply a store-shaped namespace where the client has none                | delete it                            |
| Answer an RPC           | overlay a method the client already has                                  | restore what was displaced           |
| Reveal what is gated    | flip the one flag or getter hiding a surface the client can already serve | restore the original, never the platform constant |

- The revived surfaces themselves. Valve's audio page, Internet page, Bluetooth page, brightness
  slider, Performance tab and TDP rows ship in the Windows client and are inert only because
  nothing answers behind them. Each is a `Steam*Surface`: the injected gate that supplies or
  reveals it, the patch that probes and verifies it, a typed state record you fill, and a backend
  interface you implement. Quick Access rows built on Valve's own field primitives (frame limit,
  variable refresh, resolution, automatic power limit, controller target, charge and lighting) are
  `Steam*Row`s of the same shape. You say "this is our data, and it maps to that feature"; the CEF
  work stays here.
- An extension host, so a consumer can let third parties add surfaces of their own.

## The rules it enforces

Each of these cost a debugging session against a live client.

- Every patch carries an ownership marker and accepts "already ours". A patch that cannot
  recognise its own work either refuses forever or overwrites something that was never its to
  change, and a probe that requires the pre-patch condition its own apply invalidates tears itself
  down on every poll.
- Removal restores exactly what was displaced, read from the object rather than from the closure
  that installed it. A bridge replaced in place has no closure left, and restoring `undefined`
  leaves the client worse than never patching.
- Reveal the surface, never the platform. Setting Steam's "is this SteamOS" constant gives you the
  row you wanted and changes unrelated client behaviour everywhere.
- Never iterate the webpack module registry constructing exports. Probes name literal module ids
  and inspect factory or prototype source. Enumerating and calling everything once restarted the
  machine and signed Steam out.
- Every refusal is logged with its reason, because the injected side has nowhere to put an error.

`eng/check-ownership-claims.mjs` runs the claim primitives out of the emitted prelude, the bytes
that get injected, against those scenarios in CI. It caught a real defect the day it was written.

## Using it

For a host that must leave Steam's cold startup untouched, construct
`new PersistentSteamUiTransport(requireMainWindow: true)`. Discovery waits for one validated main
window before attaching to any role; the default constructor retains unrestricted target discovery.
Pass the configured opt-in explicitly to
`SteamCef.EnsureRemoteDebuggingEnabled(directory, enabled)`; the flag must be writable while the
transport is intentionally held closed.

Use `SteamUiModuleResolver.CreateExpression(scope)` in standalone feature scripts. The returned
resolver accepts a literal module id, or `resolve(tokens)` for a unique source fingerprint.
`count(tokens)` and `findUnique(tokens)` inspect source without loading exports. Missing factories
never enter webpack's loader, and ambiguous or failed resolution is explicit. Feature scripts must
not implement their own registry scan. The bridge and built-in probes use this same source.

The library is the machinery and the surfaces; the data behind them is yours. You supply:

- a logger (`ISteamUiLog`), so diagnostics land wherever your application's do;
- the script you inject (`SteamUiInjectedAsset`): `dist/steam-ui.js` as built by
  `npm run prelude:build`, or the same fragments compiled together with your own, since the whole
  thing is evaluated in one CDP call and is therefore one script;
- a backend per surface you want, and a reading of its state.

Each surface's `Module(...)` turns those into an `ISteamUiModule`. Register `SteamUiBridgePatch`
and the modules' patches in the same manager; synchronization uses stable patch-id order and retries
unmet conditions, so registration call order is not significant.

```csharp
ISteamUiModule audio = SteamAudioSurface.Module(
    enabled: () => quickAccessOn,
    read: () => new(myAudio.CurrentState),   // a SteamAudioState, or null to publish nothing
    backend: myAudio);                        // an ISteamAudioBackend: default device, volume
```

A surface's patch id and command vocabulary are constants on it (`PatchId`, `Commands`), and the
module set derives the bridge's exact state/command vocabulary from every module you register; pass
`SteamUiModuleSet.AllowedCommands` to `SteamUiBridgeHost`. A surface you do not register installs
nothing, and its Valve UI stays exactly as the client ships it.

A surface of your own is a fragment that calls `registerGate(name, gate)` and a patch that reaches
it through `window[namespace].gate(name)`, declared in a module like any other.
`SteamUiModuleRuntime` runs the two traffic directions between your modules and the client.

Which patches are on when stays yours; that is application policy and every host's rules differ.
`SteamUiPatchManager.SetGlobalEnabled` and `SetPatchEnabled` start synchronization immediately;
their `Async` counterparts wait until retraction or reapplication has finished. Use the awaited
forms when shutdown, a settings confirmation or an emergency kill switch must know cleanup is done.

## Status

0.1.0, single-consumer, moving. Two different things are unstable:

- The API, because one application shaped it. The parts most likely to change are the ones that
  consumer does not stress: the extension host has no second implementer, and the module contract
  has never been built against by anyone who did not also write it.
- What Steam does, which nothing here controls. Every module id, localization token and class name
  is coupled to a Steam build. The probe-first design makes a Steam update degrade to Valve's own
  behaviour rather than break, but compatibility is verified against a running client, not promised
  by a version number.

The second will not go away at 1.0. The first should.

Extracted from [WSGM](https://github.com/NightHammer1000/WSGM), which reconstructs SteamOS Game Mode
on Windows handhelds and is where all of this was found. Its `_plan/steam-ui-toolkit.md` records
what has been done and what has not.

## Licence

MIT. See `LICENSE`.
