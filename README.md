# SteamUiToolkit

Add, hide and reorganize elements in Steam's Big Picture front-end, from .NET.

> ## ⚠️ Very early. Expect it to change under you.
>
> This was extracted from one application last week and has exactly one consumer. **The API will
> break, repeatedly, without deprecation cycles.** Names, shapes and whole types are still moving as
> the second consumer's needs become clear, and nothing here has been through the round of "someone
> else tried to use it and found the sharp edge" that settles a library down.
>
> It is public now because the knowledge in it is worth more shared than sat on — not because it is
> ready to build on. If you use it, **pin an exact version, read the commit log before bumping, and
> expect to fix call sites.** Issues and pull requests are very welcome; a promise of stability is
> not something this can honestly offer yet.

```
dotnet add package SteamUiToolkit
```

Steam's client UI is a Chromium application and it will talk to a CDP client on loopback. That much
is widely known. This library is about everything after that: doing it in a way that survives a
Steam update, cleans up after itself, and tells you why when it does not work.

## What it gives you

- **A persistent CDP transport** that owns one connection, tracks execution-context and document
  generations, and verifies the debug port actually belongs to Steam before connecting.
- **A patch lifecycle** — probe, apply, verify, remove — where each patch declares what it owns,
  proves it found its target before touching anything, and is removed and re-probed when it cannot
  be verified.
- **Three stable ways to change the client**, which between them cover every gate this came from:

| | What it does | What removal owes |
| --- | --- | --- |
| **Feed a data construct** | supply a store the shape it was written against, where the client has none | delete it |
| **Answer an RPC** | overlay a method the client already has | restore what was displaced |
| **Reveal what is gated** | flip the one flag or getter hiding a surface the client can already serve | restore the original |

- **An extension host**, so a consumer can let third parties add surfaces of their own.

## The rules it enforces, and why

Each of these is here because getting it wrong cost a debugging session against a live client.

- **Every patch carries an ownership marker.** A patch that cannot recognise its own work either
  refuses forever or overwrites something that was never its to change. Worse: a probe that requires
  the pre-patch condition its own apply invalidates tears itself down on every poll. That happened
  three separate times before the claim primitive existed.
- **Removal restores exactly what was displaced**, read from the object rather than from the closure
  that installed it — a bridge replaced in place has no closure left, and restoring `undefined`
  leaves the client worse than never patching.
- **Reveal the surface, never the platform.** Setting Steam's own "is this SteamOS" constant gives
  you the row you wanted and changes unrelated client behaviour everywhere.
- **Never iterate the webpack module registry constructing exports.** Probes name literal module ids
  and inspect factory or prototype source. Enumerating and calling everything once restarted the
  machine and signed Steam out.
- **Every refusal is logged with its reason.** The injected side has nowhere to put an error, so a
  control the user operated that quietly did nothing otherwise leaves no trace at all.

`eng/check-ownership-claims.mjs` runs the claim primitives out of the **emitted** prelude — the
bytes that get injected — against exactly those scenarios. It is in CI, and it is not a test that
passes by construction: it caught a real defect the day it was written.

## Using it

The library is the machinery; the surfaces are yours. You supply:

- **a logger** (`ISteamUiLog`), so diagnostics land wherever your application's do;
- **the script you inject** (`SteamUiInjectedAsset`) — this library's prelude fragments compiled
  together with your own, since the whole thing is evaluated in one CDP call and is therefore one
  script;
- **your modules** (`ISteamUiModule`), each one surface: the patches that install it, the state it
  publishes, and the commands it answers.

The module set derives the bridge's exact state/command vocabulary. Pass
`SteamUiModuleSet.AllowedCommands` to `SteamUiBridgeHost`; the toolkit deliberately carries no
application-specific patch ids or command names of its own.

Your fragments call `registerGate(name, gate)`; your patches reach them through
`window[namespace].gate(name)`. `SteamUiModuleRuntime` then runs the two traffic directions between
your modules and the client.

What patches should be applied when stays yours. That is application policy, every host's rules
differ, and a constructor full of predicates describing one host's would help nobody.

## Status

**0.1.0. Early, single-consumer, and moving.** See the warning at the top — this section is the
detail behind it.

Two separate things are unstable here, and it is worth keeping them apart:

- **The API**, because it has been shaped by exactly one application. The parts most likely to
  change are the ones WSGM happens not to stress: the extension host has no second implementer, the
  module contract has never been built against by anyone who did not also write it, and several
  types went from `internal` to `public` the day a consumer first reached for them. That process is
  not finished.
- **What Steam does**, which nothing here controls. Every module id, localization token and class
  name this reaches for is coupled to a Steam build. The probe-first design is what makes a Steam
  update degrade to Valve's own behaviour rather than break — but compatibility is something you
  verify against a running client, not something a version number can promise.

The second one will not go away at 1.0. The first should.

Extracted from [WSGM](https://github.com/NightHammer1000/WSGM), which reconstructs SteamOS Game Mode
on Windows handhelds and is where all of this was found. Its `_plan/steam-ui-toolkit.md` records
what has been done and what has not.

## Licence

MIT. See `LICENSE`.
