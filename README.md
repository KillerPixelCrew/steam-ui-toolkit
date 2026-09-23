# SteamUiToolkit

Add, hide and revive elements in Steam's Big Picture front end, from .NET. This README is the
orientation and the how-to. The contract, with every limit, state and log key, is in
[`docs/reference.md`](docs/reference.md).

> **Very early. Expect it to change under you.** This was extracted from one application and has one
> consumer. Names, shapes and whole types are still moving, and the API will break without
> deprecation cycles. Pin an exact version, read the commit log before bumping, and expect to fix
> call sites. Issues and pull requests are welcome, but I cannot honestly offer a stability promise
> yet.

```
dotnet add package SteamUiToolkit
```

Steam's client UI is a Chromium application that talks to a CDP client on loopback. This library is
about everything after that: doing it in a way that survives a Steam update, cleans up after itself,
and tells you why when it does not work.

## The machinery

**A persistent CDP transport** that owns one connection per target, tracks execution-context and
document generations, and verifies the debug port belongs to Steam before connecting.

**A patch lifecycle** of probe, apply, verify and remove, where each patch declares what it owns,
proves it found its target before touching anything, and is removed and re-probed when it cannot be
verified.

**Three ways to change the client,** which between them cover every surface below:

| Way                   | What it does                                                              | What removal owes                                 |
| --------------------- | ------------------------------------------------------------------------- | ------------------------------------------------- |
| Feed a data construct | supply a store-shaped namespace where the client has none                 | delete it                                         |
| Answer an RPC         | overlay a method the client already has                                   | restore what was displaced                        |
| Reveal what is gated  | flip the one flag or getter hiding a surface the client can already serve | restore the original, never the platform constant |

**Host-rendered extension surfaces,** so a consumer can let third-party packages publish bounded
commands without handing them Steam objects or an injection API.

## The revived surfaces

Valve's audio page, Internet page, Bluetooth page, brightness slider, Performance tab and TDP rows
all ship in the Windows client and are inert only because nothing answers behind them. Each one is a
`Steam*Surface`: the injected gate that supplies or reveals it, the patch that probes and verifies
it, a typed state record you fill in, and a backend interface you implement. Quick Access rows built
on Valve's own field primitives (frame limit, variable refresh, resolution, advanced audio format
and spatial sound, automatic power limit, controller target, charge and lighting) are `Steam*Row`s
of the same shape. You say "this is our data, and it maps to that feature", and the CEF work stays
in here.

**`SteamNavigationPanelSurface.Module`** makes Steam's left slideout navigation panel an extension
surface. Publish `SteamNavigationPanelState` to add entries and hide Steam's own, and implement
`ISteamNavigationPanelBackend` to answer an added entry's activation. Entries are placed relative to
Steam's by route (`/library`) or by Valve's descriptor key (`power`) rather than by index, because
routes and keys are stable across client builds and languages while the labels are localized. Hiding
is applied before insertion. An entry whose anchor is not in the panel goes to the end and is
reported as orphaned rather than dropped. The claim is on the one exported handle and is restored on
removal.

**`SteamPageSurface.Module`** registers custom pages with Steam's own router. Publish
`SteamPageState` with a path, a title and an id. The path goes to Steam's matcher, so
`/my-plugin/page/:id` takes parameters the way Valve's routes do. Pages are built with Steam's
back-stack `Route` rather than react-router's, so they push and pop the back stack like a native
page instead of rendering correctly and losing B. `Override` decides whether a page replaces a Steam
route of the same path or adds a new one: Steam's switch takes the first match, so an override is
inserted ahead of Valve's routes and an addition behind them. Adding is the default.

Consumers may compile a bounded renderer fragment beside the toolkit and register it with
`registerSteamPageRenderer(template, render)`. `SteamPageState.Template` selects that renderer while
the router, ownership and back-stack mechanism stay generic. The toolkit intentionally contains no
product page or artwork browser.

**`SteamLibraryBadgeSurface.Module`** draws a library badge on every library tile, immediately left
of Valve's Steam Input badge in the tile's icon row: the name of the library holding the game, green
when the game is installed and grey when it is not. Publish `SteamLibraryBadgeState` with the
libraries worth naming and their app ids; a game in none of them is on the internal library and gets
`InternalLabel`. The claim is on the tile memo's `type`, so Home's carousel and the library grid are
covered by one claim, and the badge shows exactly when Valve shows the icon row. Implement
`ISteamLibraryBadgeBackend` to hear `homeLayout`, which reports Steam's own Big Art Mode setting
when the gate resolves it and whenever a tile render sees it change.

**`SteamHomeCarouselSurface.Module`** makes Big Picture Home's carousel list the games on the
libraries attached right now: the most recently played first, installed games by last played merged
with unplayed recent purchases by purchase time, never-played installed games after those, and owned
uninstalled games greyed out when `SteamHomeCarouselState.IncludeUninstalled` asks for them. Games
in `DisconnectedAppIds` leave the list. The gate replaces the one app-id array Home hands its
carousel and background, so Steam's own components draw it, and puts the virtualized carousel's
overscan back to the component's default, since Home otherwise mounts every tile. A Home already on
screen when the gate installs is adopted and re-rendered at once rather than waiting for the next
navigation. Implement `ISteamHomeCarouselBackend` to hear what the carousel holds after each rebuild.

**`SteamExtensionsTabSurface.Module`** adds one shared Quick Access tab whose plugin sections,
actions and primitive settings are supplied by `SteamExtensionsTabState`; implement
`ISteamExtensionsTabBackend` to receive exact action and configuration requests. Secret values are
write-only and must not be published back. The tab is host-rendered and limits publication to 64
plugins. **`SteamGameContextMenuSurface.Module`** adds host-owned commands to the selected game's
library and gear menu. Its backend receives Steam's positive app ID and an exact command ID, after
the surface has rejected every other payload shape. The shared JSX interceptor recognizes the
private menu class before its first render, so the first opening includes the commands without a
visible-DOM scan. Extensions-tab actions use Steam's native focusable Panel for controller and
pointer activation; the probe recognizes its own installed wrapper.

**`SteamStorageSurface.Module`** revives Steam's own SteamOS storage management on Windows. The
whole UI hangs off one unanswered service question, so the gate claims `SendMsg` on the service
transport and answers `StorageDeviceManager.*` from `SteamStorageState`. Implement
`ISteamStorageBackend` to adopt, eject, format and trim. Every other service message Steam sends
passes straight through with its own arguments and receiver, and removal deletes the claim so
Valve's method shows through again. The injected half performs no storage operation itself, which
keeps one Windows implementation behind both Steam's pages and your own.

**`SteamPowerProfileRow.Module`** adds a Windows power-profile dropdown to QAM Performance. Supply
stable ids, display labels and observed state through `SteamPowerProfileState`, and implement
`ISteamPowerProfileBackend` to validate and apply selections. **`SteamPowerPresetRow.Module`** adds
independent AC and battery assignments with `SteamPowerPresetState` and `ISteamPowerPresetBackend`.
The active preset is read-only, Custom included. A host may also publish `custom` as a saved source
assignment, which is displayed only for that source and never sent as a selection command. Empty
preset options hide those controls. The toolkit does not change OS power settings itself.

Performance controls use titled native sections. Quick Settings places display controls before
Steam's common settings, then separate Charging and RGB lighting sections.

## Reading and driving the client

Beyond changing the front-end, the library reads and drives the running client: app details, launch
options and custom artwork (`SteamApps`), library folders (`SteamInstallFolders`), the download
queue (`SteamDownloadActivity`), collections, games and store tags (`SteamLibraryData`), the game
page in view (`SteamCurrentPage`) and the apps Steam is running (`SteamRunningAppsProbe`). These are
one-shot calls over the same transport, and each separates "Steam was never reached" from "Steam
refused", because only the second one is an answer.

**`SteamAppLifetimeMonitor`** raises `AppStarted` and `AppStopped` from Steam's own lifetime
notifications, so an application can react to a game launching or closing without watching
processes.

### Game state, end to end

A complete program. It needs a `net10.0-windows` target and a reference to this package, and nothing
else: these calls evaluate and read, so knowing what Steam is doing costs you the transport and no
injected script, bridge or patch.

```csharp
using System;
using System.Linq;
using Microsoft.Win32;
using SteamUiToolkit;

// 1. Find Steam. The library never guesses this: it is the host's machine, not its own.
//    HKCU stores the path with forward slashes.
var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;

// 2. Opt Steam into its debug port. Steam reads this file when it starts, so a client that is
//    already running must be restarted once before anything below can reach it.
SteamCef.EnsureRemoteDebuggingEnabled(steamPath?.Replace('/', '\\'), enabled: true);

// 3. One transport for the process. It connects on first use and reconnects by itself.
await using var transport = new PersistentSteamUiTransport();

// 4. Attach it to the session that the one-shot calls borrow. Without this they answer
//    "Steam UI transport is not active" and nothing else works.
SteamUiTransportSession.Attach(transport);

// 5. Read the library once, so a running app can be named rather than numbered.
var names = (await SteamLibraryData.ListGamesAsync())
    .ToDictionary(app => (uint)app.AppId, app => app.Name);

// 6. Watch games start and stop. Handlers run on the monitor's worker thread and block its
//    next poll, so keep them short and hand real work to your own queue.
await using var games = new SteamAppLifetimeMonitor(transport);
games.AvailabilityChanged += (_, e) =>
    Console.WriteLine(e.Available ? "Steam is readable." : $"Steam is unreadable: {e.Diagnostic}");
games.AppStarted += (_, e) => Console.WriteLine(
    $"started: {Name(e.AppId)}{(e.Resynchronized ? " (already running)" : "")}");
games.AppStopped += (_, e) => Console.WriteLine($"stopped: {Name(e.AppId)}");
games.Start();

Console.WriteLine("Watching Steam. Press enter to stop.");
Console.ReadLine();

string Name(uint appId)
{
    return names.TryGetValue(appId, out var name) ? name : appId.ToString();
}
```

Steam must be running for readings to arrive, and its debug port exists only after a restart
following the first flag write. Until then the monitor reports itself unavailable with a reason
rather than failing.

The in-page observer keeps a numbered log of the last 64 notifications, so a game that starts and
stops between two polls still raises both events in order. `Resynchronized` marks a change derived
from comparing running sets instead: the first reading, where everything already running is reported
as started, a replaced Steam context, or more changes than the log holds. An unreachable client
raises `AvailabilityChanged` rather than stop events, because Steam being gone is not a game being
closed. `IsShortcut` on an event separates a non-Steam shortcut, whose generated id has no store
page, from a store title. See §16 of the reference.

## Windows, keyboards and menu state

**`SteamGameWindowActivation.RaiseAsync`** requests Steam activation for exactly one existing
overlay process. Your host still has to focus its own selected native window, because Steam can
raise a launcher console, and completion does not mean the overlay recovered. The call borrows the
existing transport, has a one-second budget, and never launches or retries a game.

**`SteamNativeSurfaceCommands.ReplayAsync`** invokes Steam's native Home or Quick Access handler on
an exact process, app window identity and CEF generation. It does not retry and does not fall back
to another window. The `Keyboard` action shows the native keyboard through that exact window's
keyboard manager and game-overlay route, and an already visible keyboard stays open. Snapshots
include nullable keyboard visibility.

**`SteamSideMenuObserver.ReadAsync`** reads main-window and overlay-window menu state through the
host's subscribed transport. Register `SteamOverlayActivationPatch` with the patch manager to
observe overlay activation too. Unknown activation stays unknown after attachment or reconnect, and
a closed QAM alone does not prove an in-game overlay is closed.

## Behaviour worth knowing

Bluetooth state includes optional operation progress for Steam's spinner. Failed backend commands
return failed transport replies, and device detail queries preserve the semantic device identity.

State callbacks are isolated during both cached replay and later publications, so a failing
subscriber cannot interrupt another subscriber or stop installation cleanup being registered.

Power controls use two Valve slider primitives, for sustained (PL1) and boost (PL2) power. Both
follow hardware observations, profile changes included, and write only on a completed user edit.
Steam's own saved TDP setting is never applied or polled. A consumer can also publish `Unified` and
`CanSelectMode` and implement `SetUnifiedModeAsync`, which renders one TDP slider with both limit
readbacks; its toggle saves policy only.

Semantic sliders render hardware observations and suppress unchanged completion echoes, so a live
power-limit update does not dispatch a manual write back to its owner.

Brightness hosts publish confirmed percentages with increasing observation revisions and return
readback in successful command responses. The native slider consumes that state without sending
programmatic refreshes back as hardware writes. See `SteamBrightnessSurface` in the reference.

## The rules it enforces

Each of these cost me a debugging session against a live client.

**Every patch carries an ownership marker and accepts "already ours".** A patch that cannot
recognise its own work either refuses forever or overwrites something that was never its to change,
and a probe that requires the pre-patch condition its own apply invalidates tears itself down on
every poll.

**Removal restores exactly what was displaced,** read from the object rather than from the closure
that installed it. A bridge replaced in place has no closure left, and restoring `undefined` leaves
the client worse than never patching.

**Reveal the surface, never the platform.** Setting Steam's "is this SteamOS" constant gives you the
row you wanted and changes unrelated client behaviour everywhere.

**Never iterate the webpack module registry constructing exports.** Probes find a module by a source
fingerprint that matches it alone, and an export by its shape. Enumerating and calling everything
once restarted the machine and signed Steam out.

**Never name a module id or a minified export name.** Client builds renumber modules and rename
exports. The September 2026 beta did both and refused every gate that had named them.

**Every refusal is logged with its reason,** because the injected side has nowhere else to put an
error.

`eng/check-ownership-claims.mjs` runs the claim primitives out of the emitted prelude, the actual
bytes that get injected, against those scenarios in CI. It caught a real defect the day it was
written. `npm run prelude:claims` runs it with the other emitted-asset checks.

## Using it

For a host that must leave Steam's cold startup untouched, construct
`new PersistentSteamUiTransport(requireMainWindow: true)`. Discovery then waits for one validated
main window before attaching to any role, while the default constructor keeps unrestricted target
discovery. Pass the configured opt-in explicitly to
`SteamCef.EnsureRemoteDebuggingEnabled(directory, enabled)`; the flag has to be writable while the
transport is intentionally held closed.

In standalone feature scripts, use `SteamUiModuleResolver.CreateExpression(scope)`. The returned
resolver's `resolve(tokens)` loads a module by a unique source fingerprint, and
`exported(tokens, predicate)` returns the one export of that module fitting the predicate.
`count(tokens)` and `findUnique(tokens)` inspect source without loading exports. Missing factories
never enter webpack's loader, and ambiguous or failed resolution is explicit. Feature scripts must
not implement their own registry scan; the bridge and the built-in probes use this same source.

Consumer-owned native pages can use the public `SteamUiProbeJs` token constants for preflight and
the composed asset's shared `resolveSteamUiComponents` helper for Steam's focusable, tabs, dialog
buttons, fields and modal manager. Missing or ambiguous controls are capabilities to refuse, not a
reason to draw lookalike controls.

The library is the machinery and the surfaces, and the data behind them is yours. You supply:

- a logger (`ISteamUiLog`), so diagnostics land wherever your application's do;
- the script you inject (`SteamUiInjectedAsset`): `dist/steam-ui.js` as built by
  `npm run prelude:build`, or the same fragments compiled together with your own, since the whole
  thing is evaluated in one CDP call and is therefore one script;
- a backend per surface you want, and a reading of its state.

Each surface's `Module(...)` turns those into an `ISteamUiModule`. Register `SteamUiBridgePatch` and
the modules' patches in the same manager. Synchronization uses stable patch-id order and retries
unmet conditions, so registration call order does not matter.

```csharp
ISteamUiModule audio = SteamAudioSurface.Module(
    enabled: () => quickAccessOn,
    read: () => new(myAudio.CurrentState),   // a SteamAudioState, or null to publish nothing
    backend: myAudio);                        // an ISteamAudioBackend: default device, volume
```

A surface's patch id and command vocabulary are constants on it (`PatchId`, `Commands`), and the
module set derives the bridge's exact state and command vocabulary from every module you register,
so pass `SteamUiModuleSet.AllowedCommands` to `SteamUiBridgeHost`. A surface you do not register
installs nothing, and its Valve UI stays exactly as the client ships it.

A surface of your own is a fragment that calls `registerGate(name, gate)` plus a patch that reaches
it through `window[namespace].gate(name)`, declared in a module like any other.
`SteamUiModuleRuntime` runs the two traffic directions between your modules and the client.

Which patches are on when stays yours, because that is application policy and every host's rules
differ. `SteamUiPatchManager.SetGlobalEnabled` and `SetPatchEnabled` start synchronization
immediately, and their `Async` counterparts wait until retraction or reapplication has finished. Use
the awaited forms when shutdown, a settings confirmation or an emergency kill switch has to know
cleanup is done.

## Status

0.1.0, single consumer, moving. Two different things are unstable.

**The API,** because one application shaped it. The parts most likely to change are the ones that
consumer does not stress: the extension host has no second implementer, and nobody has built against
the module contract who did not also write it.

**What Steam does,** which nothing here controls. Every fingerprint token, localization token and
store field is coupled to a Steam build, though far more loosely than a module id would be. The
probe-first design makes a Steam update degrade to Valve's own behaviour rather than break, but
compatibility is checked against a running client rather than promised by a version number.

The second will not go away at 1.0. The first should.

Extracted from [WSGM](https://github.com/KillerPixelCrew/WSGM), which rebuilds SteamOS Game Mode on
Windows handhelds and is where all of this was found. Its `_plan/steam-ui-toolkit.md` records what
has been done and what has not.

## Licence

MIT, see `LICENSE`.
