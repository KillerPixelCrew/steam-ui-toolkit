# SteamUiToolkit

Add, hide and reorganize elements in Steam's Big Picture front-end, from .NET.

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

Your fragments call `registerGate(name, gate)`; your patches reach them through
`window[namespace].gate(name)`. `SteamUiModuleRuntime` then runs the two traffic directions between
your modules and the client.

What patches should be applied when stays yours. That is application policy, every host's rules
differ, and a constructor full of predicates describing one host's would help nobody.

## Status

**Pre-1.0, and staying there for a while.** Every module id, localization token and class name this
reaches for is coupled to a Steam build. The probe-first design is what makes a Steam update degrade
to Valve's own behaviour instead of breaking — but compatibility is something you verify, not
something a version number promises.

Extracted from [WSGM](https://github.com/NightHammer1000/WSGM), which reconstructs SteamOS Game Mode
on Windows handhelds and is where all of this was found.

## Licence

MIT. See `LICENSE`.
