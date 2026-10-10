# SteamUiToolkit reference

The [sound override contract](sound-overrides.md) covers exact resource mapping, decoding gates and
audio-manager ownership.

The [plugin frontend contract](plugin-frontends.md) covers unrestricted bundle loading, surface
registrations, optional backend/state traffic, source attribution and owner-wide teardown.

The [source map](code-map.md) links every implementation area to its API, injected code and
regression sources. Use it with this reference when following a call end to end.

The contract of `SteamUiToolkit`: the transport that owns one CDP connection to Steam's Chromium
front-end, the probe/apply/verify/remove patch lifecycle, the in-page bridge, the module contract,
the ownership primitives, the extension host, the prelude build and the surfaces. The XML
documentation on each member is the authoritative wording; this document reads the library as a
whole, in the order a consumer meets it. How WSGM uses it (Steam discovery, gating the transport on
Big Picture, which surfaces it registers) is on the WSGM side in `docs/steam-cef-system.md`.

`SteamRouteNavigation.NavigateAsync` is the one route change that starts on the host side: a host
surface outside Steam, such as an overlay, handing the user to a page inside it. It borrows a ready
SharedJSContext, applies the same rules as the gates' `navigateSteamRoute` (absolute, not the root,
no control characters, any length), JSON-encodes the route into the expression and pushes it once on
`window.tempNavStore.m_history`. A replaced generation, a missing router or an expired request
reports false, and a push that may have happened is never retried. It is a one-shot evaluation on
purpose rather than a request field on the pages publication: published state is replayed to every
new subscriber and forgotten when the bridge restarts, so a request left in state would navigate
again after a gate reinstall. It does not focus Steam's window; that is the host's job.

`SteamGameWindowActivation.RaiseAsync` borrows an already subscribed transport and requires a ready
SharedJSContext. It resolves exactly one `OverlayWindows` entry with the requested nonzero PID,
requires a gamepad overlay, valid nonzero AppID and decimal 64-bit GameID string, then awaits
`SteamClient.Apps.RaiseWindowForGame` with that GameID. It never converts shortcut GameIDs to
Number. The one-second request budget includes an in-page expiry check before dispatch. A replaced
generation, missing method, missing/ambiguous overlay or JavaScript failure reports false. There is
no launch, retry, main-window fallback or DLL injection. True means the call completed, regardless
of Steam's native result code; it does not establish focus or overlay recovery. The host must select
and verify its exact native HWND because Steam may raise a launcher console. Cancellation cannot
recall a native operation already dispatched. Tests execute the expression in isolated Node fixtures
and cover generation replacement and cancellation with a fake transport. Live Windows overlay
recovery after task switching remains unverified. Offline inspection of the installed Windows Steam
bundle on 2026-09-13 confirmed that its own return-to-game path passes `gameid`, and its overlay
browser information maps `gameID` into `m_gameID`.

| Fact               | Value                                                                                              |
| ------------------ | -------------------------------------------------------------------------------------------------- |
| Package            | `SteamUiToolkit` 0.2.0, pre-1.0 on purpose                                                         |
| Target framework   | `net10.0-windows`                                                                                  |
| Licence            | MIT                                                                                                |
| Documentation gate | every public member is documented; an undocumented one fails the build                             |
| CI                 | build, tests, `npm ci`, `npm run prelude:claims` against the emitted prelude                       |
| Consumer supplies  | an `ISteamUiLog`, a `SteamUiInjectedAsset`, its `ISteamUiModule`s, and the Steam install directory |

## 1. The shape

### Steam surface observations

`SteamNativeSurfaceCommands.ReplayAsync` replays `QuickAccess` or `Home` through the existing window
handler. Supply the process/app identity and CEF generations from the target observation; zero/zero
identifies the main window. Overlay targets must match exactly one gamepad overlay. Missing,
ambiguous, desktop-overlay and stale-generation targets are refused without fallback. The result
means the handler was invoked, not that its surface opened; Steam keeps its native debounce and
availability policy. Consumers observe the resulting surface separately and must not retry an
uncertain dispatch. On 2026-09-09, the compiled main-window Quick Access command was observed
opening and closing live Big Picture QAM (0 to 2 to 0). No live game-overlay dispatch was exercised.

`Keyboard` uses the same exact target and generation checks. It closes side menus, enables dismissal
on Enter and shows the window keyboard, using Steam's native `/keyboard` route for game overlays. It
returns true for an already visible keyboard without toggling it. Missing keyboard methods refuse
the request before mutation. The installed main-window methods and route were inspected on
2026-09-09; actual keyboard interaction remains a field check.

`SteamSideMenuObserver` reads the known window/menu stores through an existing subscribed
`ISteamUiTransport`. It does not create a transport or control input ownership. Snapshots include
CEF generations, the main window and every overlay window, identified by process/application id. A
window whose keyboard state is unknown reports `KeyboardOpen = null`, never closed. Missing stores,
invalid identities, malformed menus and stale generations produce unknown data. Consumers must also
invalidate previously held snapshots when transport generations change.

Register `SteamOverlayActivationPatch` through the normal patch manager lifecycle. It owns one
`RegisterForOverlayActivated` subscription, replaces only a recognized older observer, and ignores
late callbacks after cleanup. It keeps one entry per game process seen in the document's life. A
callback with an invalid identity is ignored, and one with a non-boolean state makes that identity
unknown without touching the others. No synthetic closed event is supplied at startup.
`AllSideMenusClosed` concerns menus only. `AllSteamSurfacesClosed` additionally requires confirmed
inactive overlays and a confirmed closed keyboard on every window. Missing keyboard state remains
unknown. Controller leases, HidHide and recovery policy remain the consumer's responsibility.

On 2026-09-09, the installed Big Picture client accepted the compiled subscription and menu-read
expressions; the temporary subscription was removed afterward. Main-window QAM was separately
observed changing 0 to 2 to 0. No game-overlay window existed during this check, so game-overlay
activation and its callback identity mapping still require a live game scenario.

```text
consumer ──── ISteamUiLog ──────────────────▶ SteamUiLog.Use(sink)
consumer ──── SteamUiInjectedAsset ─────────▶ SteamUiBridgeHost (prelude + consumer fragments, one script)
consumer ──── ISteamUiModule[] ─────────────▶ SteamUiModuleSet ─▶ SteamUiPatchManager / SteamUiModuleRuntime
consumer ──── Steam directory ──────────────▶ SteamCef.EnsureRemoteDebuggingEnabled

PersistentSteamUiTransport   one CDP connection per target role, generations, health, reconnect
SteamUiPatchManager          probe → apply → verify → remove, per patch, per generation
SteamUiBridgeHost            Runtime.addBinding + injected namespace: requests up, state and responses down
SteamUiModuleRuntime         the two traffic directions between modules and the page
SteamUiExtensionHost         discovers and validates JavaScript extension packages
```

Every Steam-shaped fact (a source fingerprint, a store's field names, a localization token, a row's
placement) lives in a surface (§15), never in the machinery. The bridge's vocabulary is derived from
whichever modules the consumer registers. A consumer's own fragments call `registerGate`, and its
patches reach them through `window[namespace].gate(name)`, exactly as the shipped surfaces do.

## 2. Public surface

### Transport

| Type                                                                         | Role                                                                                                                                     |
| ---------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------- |
| `SteamCef` (static)                                                          | `EnsureRemoteDebuggingEnabled(steamDirectory, enabled)`, `JsString`, and the pure gates `IsAllowedDebuggerUrl` and `IsSteamPortOwner`.   |
| `SteamUiTargetRole`                                                          | `SharedJsContext`, `MainWindow`.                                                                                                         |
| `SteamUiTransportHealth`                                                     | `Idle`, `Connecting`, `Ready`, `Unavailable`, `Incompatible`, `Retrying`, `Disposed`.                                                    |
| `SteamUiGenerations`                                                         | `Browser`, `Target`, `Session`, `Frame`, `ExecutionContext`, `Document`.                                                                 |
| `SteamUiTransportSnapshot`, `SteamUiEvaluationResult`, `SteamUiNotification` | Sanitized state, evaluation result with its `SteamUiDispatch` (`NotSent`, `Closed`, `Unanswered`, `Answered`), bounded CDP notification. |
| `ISteamUiTransport`                                                          | `NotificationReceived`, `GenerationChanged`, `SubscribeAsync`, `EvaluateAsync`, `SetRuntimeBindingAsync`, `GetSnapshots`.                |
| `PersistentSteamUiTransport`                                                 | The production implementation; `SetEnabled` and `DefaultClosedReason` are the host's master switch.                                      |

### Other groups

| Group         | Types                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               |
| ------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Patches       | `ISteamUiPatch`, `SteamUiPatchProbeResult`, `SteamUiPatchOperationResult`, `SteamUiPatchContext`, `SteamUiPatchState`, `SteamUiPatchSnapshot`, `SteamUiPatchManager`, `SteamUiPatchEvaluation`                                                                                                                                                                                                                                                                                                                                                                                                      |
| Bridge        | `SteamUiBridgeHost`, `SteamUiBridgeRequest`, `SteamUiBridgeIdentity`, `SteamUiInjectedAsset` (the request authorizer is internal)                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| Modules       | `ISteamUiModule`, `SteamUiModule`, `SteamUiModuleSet`, `SteamUiModuleBuilder`, `SteamUiPayloadReader<T>`, `SteamUiStatePublication`, `SteamUiCommandHandler`, `SteamUiCommandDelegate`, `SteamUiCommandResult`, `SteamUiModuleRuntime`                                                                                                                                                                                                                                                                                                                                                              |
| Extensions    | `SteamUiExtensionHost` (static), `SteamUiExtension`, `SteamUiExtensionManifest`, `SteamUiExtensionRejection`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| Logging       | `ISteamUiLog { Info, Warn, Change(key, message, warning) }`, static `SteamUiLog` with a discarding default                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| Client        | `SteamClient`, `SteamClientWriteOutcome`, `SteamReadResult<T>`, `SteamApps`, `SteamAppDetails`, `SteamArtworkSlot`, `SteamArtworkFormat`, `SteamClientWriteResult`, `SteamInstallFolders` with its add/remove/label result types, `SteamDownloadActivity`, `SteamDownloadOverview`, `SteamLibraryData`, `SteamCollectionInfo`, `SteamCollections`, `SteamCollectionSyncResult`, `SteamStartupMovie`, `SteamStartupMovieChoice`, `SteamStartupMovieResult`, `SteamLibraryApp`, `SteamStoreTag`, `SteamCurrentPage`, `SteamCurrentApp`, `SteamRunningAppsProbe`, `SteamRunningAppsObservation` (§16)  |
| Surfaces      | `SteamAudioSurface`, `SteamNetworkSurface`, `SteamBluetoothSurface`, `SteamBrightnessSurface`, `SteamPerformanceSurface`, `SteamPowerLimitSurface`, `SteamFrameLimitRow`, `SteamVariableRefreshRow`, `SteamResolutionRow`, `SteamAudioFormatRow`, `SteamAutoTdpRow`, `SteamControllerTargetRow`, `SteamDeviceControlsRow`, `SteamNavigationPanelSurface`, `SteamPageSurface`, `SteamExtensionsTabSurface`, `SteamGameContextMenuSurface`, `SteamPowerMenuSurface`, `SteamStorageSurface`, `SteamLibraryBadgeSurface`, `SteamHomeCarouselSurface`, each with typed state and backend contracts (§15) |
| Patch helpers | `SteamUiBridgePatch`, `SteamGatePatch`, `SteamQuickAccessRowPatch`; readers `SteamUiPayload`, `SteamPerformanceDeltaReader`, `SteamOverlayLevelWire`; `SteamUiProbeJs`                                                                                                                                                                                                                                                                                                                                                                                                                              |
| Assets        | `SteamUiAssets/Source/types.ts`, `bridge.ts`, `ownership.ts`, `rpc.ts`, `gate-helpers.ts`, `icons.ts`, `module-resolver.ts`, `gates/*.ts`, `components.ts`, `epilogue.ts`; listed by `eng/steam-ui-fragments.mjs`, built by `eng/build-prelude.mjs`, checked by `eng/check-*.mjs`                                                                                                                                                                                                                                                                                                                   |

`SteamUiProbeJs` exposes the stable structural token arrays for React and the native focusable,
field, tab-page, generic-dialog and modal-manager providers. Consumer-owned surfaces use those
constants in read-only compatibility probes and use the shared injected `resolveSteamUiComponents`
helper at install time; neither layer names a module id or a minified export.

A probe must count the same token set the runtime resolves the component by, which is why those
constants exist rather than each surface writing its own. `NativeFocusableTokens` is the example
that cost a feature: the four property names that identify Panel's export inside its module read
like a fingerprint but match three modules, so a gate counting them never reaches one, refuses to
install, and its page silently never appears. `eng/check-steam-fingerprints.mjs` in a consumer
repository is what catches that, so keep consumer sources in its roots.

`SteamUiLog` is a settable static rather than a constructor parameter because there is one sink per
process. `Change` is the poll-loop primitive: a line is written once per transition of its key, and
suppressed repeats are counted rather than dropped. The TypeScript ships as source in the package so
a consumer can compile it together with its own fragments; `dist/steam-ui.js` is the complete asset
for a consumer with none.

Connected target roles capture `Runtime.consoleAPICalled`, `Runtime.exceptionThrown` and
`Log.entryAdded` through the same CDP connection. `ISteamUiLog.Console(key, message, level)`
receives warnings and errors by default; `ConsoleVerboseEnabled` additionally captures informational
output at `SteamUiConsoleLevel.Debug`. Existing sinks retain their change-aware warning behaviour
through the default interface implementation. WSGM maps these levels into `wsgm.log`, with
informational console output following its existing Verbose setting.

Messages identify the target role and source URL and include available synchronous and async parent
stack frames. Plain remote objects are inspected with `Runtime.getProperties` using own properties;
getters are not invoked and native replies such as
`{ result: 2, message: "CVRPathHelpers not found" }` remain readable. No stack is invented when CEF
supplies none. Object handles are released after inspection. Source URL queries and sensitive named
properties are omitted or redacted.

The diagnostic lane is separate from commands and generation events, with 64 queued messages, 64 KiB
parameter text, eight console arguments, four inspected objects, twelve printed properties, sixteen
frames per stack and four stack levels. Output is bounded to 8 KiB. Overflow is reported without
disconnecting Steam or delaying binding dispatch. This uses native CDP notifications; it installs no
console wrappers or JavaScript event listeners.

## 3. Discovery and the port gate

For attended investigation, first confirm from the current Steam logs that Steam and Big Picture
have fully started, before any debugger connection, MCP invocation or HTTP target discovery. Early
attachment can hang the entire Steam UI and leave Steam requiring force-close. Missing or ambiguous
log evidence means stay offline. This debugging prerequisite is separate from the host's production
readiness policy described below; the transport does not parse Steam logs. The contributor guide's
[startup preflight](../AGENTS.md#before-any-live-debugger-connection) gives the full rule.

### The opt-in flag

`SteamCef.EnsureRemoteDebuggingEnabled(steamDirectory, enabled)` creates an empty
`.cef-enable-remote-debugging` file in Steam's directory when it is missing and logs
`Steam CEF remote-debugging enabled (<path>).`. It writes nothing when the explicit configured
switch is off or the directory is null. It never deletes an existing flag: the file is shared with
other tools, and the library cannot know who created it. The flag takes effect on Steam's next cold
start. The transport's temporary readiness hold does not control this write.

### Port ownership

Before any HTTP probe, discovery verifies that TCP port 8080 is owned by Steam. `NativeTcp` reads
`iphlpapi!GetExtendedTcpTable` directly (address family 2, owner-PID listener table class 3, 24-byte
rows with the address at offset 4, the port at 8, the PID at 20), retrying three times on
`ERROR_INSUFFICIENT_BUFFER`. netstat is not used because its state column is localized, so a literal
match on `LISTENING` fails closed on a non-English machine. An unreadable table returns null, not an
empty list.

`SteamCef.IsSteamPortOwner` considers only the loopback and wildcard (`0.0.0.0`) rows, the only ones
a connect to `127.0.0.1` can reach, so a process bound to one LAN address on the port never decides
the verdict. It sorts candidates loopback-first so a `127.0.0.1` squatter cannot hide behind Steam's
wildcard row, skips rows whose process has exited, accepts `steamwebhelper` and `steam`, and reports
one of four reasons:

| Reason                                                                  | Meaning                                |
| ----------------------------------------------------------------------- | -------------------------------------- |
| the TCP listener table was unavailable                                  | the owner could not be verified        |
| nothing is listening on port 8080                                       | Steam is not up, or the flag is absent |
| port 8080 is owned by `<name>` (pid n), not Steam                       | decisive refusal                       |
| n listener(s) on port 8080 could not be attributed to a running process | stale rows only                        |

A refusal logs `Change("steam.ui.discovery", "Steam UI discovery for <role> refused: <reason>.")` as
a warning.

### HTTP discovery

`PersistentSteamUiTransport(requireMainWindow: true)` requires exactly one validated MainWindow in
the same target list before attaching to any role. Login popups, foreign websocket URLs and
ambiguous main windows do not satisfy this condition. This is an attachment gate; it does not
disconnect an established session when its window is minimized or hidden. The parameterless
constructor preserves the default discovery behavior. Hosts still own game-mode transition policy.

### Module resolution

`SteamUiModuleResolver.CreateExpression(scope)` embeds `module-resolver.ts`, kept valid JavaScript,
as a standalone expression. The same source is compiled into the bridge. The returned function
refuses a missing factory before invoking webpack. `resolve(tokens)` loads exports only for a unique
source match. `exported(tokens, predicate)` resolves the same way and returns the one distinct
export the predicate accepts, counting aliases of a value once and throwing `Steam export absent` or
`Steam export ambiguous` otherwise; a getter or predicate that throws counts as no fit.
`count(tokens)` and `findUnique(tokens)` inspect source without invoking factories. `findUnique`
returns an id/source pair or null. Invalid fingerprints, absent/ambiguous resolution and load
failures throw diagnostic errors. A fingerprint is a nonempty array of nonempty string tokens, with
no count or length limit. Standalone probes and the bridge share factory source and fingerprint
results within the current document and webpack runtime. Every lookup checks all registered ids and
factory identities; additions, removals or replacements invalidate fingerprint results before
checking uniqueness again. A gate that looks a module up after installation (a store singleton, the
JSX runtime) keeps one resolver for its lifetime rather than pushing a new chunk on every
publication. Factory failures are not cached: a later call can inspect the exports Steam retained.
The resolver exposes no raw registry or loader. This is not a sandbox for arbitrary page JavaScript,
nor proof that a factory's dependencies have initialized; hosts must enforce startup readiness as
well.

Probes and gates share this resolver. The network surface instead reads Steam's published
`window.SystemNetworkStore`, so inspecting availability cannot construct the singleton early, and
native surface replay and the side-menu snapshot read Steam's `window.SteamUIStore`.

Nothing in the toolkit names a webpack module id or a minified export name. The September 2026
client beta renumbered every module; the audio, performance, brightness and Bluetooth probes, which
named `1409`, `74514`, `59547` and `60517`, refused on its first start, and the Quick Access rows
refused because the localizer had been chosen by parameter names the new minifier changed. Export
names mostly survived that build and the route table's did not. A module is found by a fingerprint
that matches it alone and an export by its shape; WSGM's `eng/check-steam-fingerprints.mjs` counts
every fingerprint's matches in an installed client's bundle without attaching to Steam.
`eng/check-startup.mjs` exercises both the standalone source and emitted asset against the loader
failure shape that leaves empty exports cached after a missing-factory call. Native-component
installation catches discovery and dependency-resolution exceptions before installing the React hook
or registering a row. It returns `ok: false` and records the refusal in `status().lastError`.

The semantic runtime isolates a throwing publication or command callback by module identity. It
stops future traffic for that module and raises `ModuleFailed` with the module, operation, message
and managed stack so the consumer can retract that module's patches without interrupting the others.
The emitted-host checks cover missing webpack, early and late load failures, and successful removal.

### Endpoint validation

`SteamUiEndpointDiscovery` reads `http://127.0.0.1:8080/json/version` and `/json/list` with a 5 s
client timeout and a 1 MiB cap enforced on both `Content-Length` and the streamed body. The
browser's `webSocketDebuggerUrl` must pass `IsAllowedDebuggerUrl`: absolute, scheme `ws` or `wss`,
host `127.0.0.1` or `localhost`, port 8080; otherwise
`InvalidDataException("Steam UI browser endpoint was not loopback port 8080.")`. The target list
must be an array; every element needs `id`, `type`, `title`, `url` and an allowed socket URL. Two
matches for one role raise `Steam UI reported multiple <role> targets.`

### Target roles

| Role              | Match                                                                                                                                                               |
| ----------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `SharedJsContext` | `type == "page"`, title `SharedJSContext`, URL under `https://steamloopback.host/`. Headless: stores, webpack modules, React.                                       |
| `MainWindow`      | `type == "page"`, URL starting `about:blank?` containing `createflags` and `minwidth`, and not containing `browserviewpopup` or `openerid`. The Big Picture window. |

The main window is matched by its creation URL, not its title, because the title is localized
("Big-Picture-Modus" on a German client) and the navigated document address matches nothing.

## 4. The CDP connection

`SteamUiWebSocketWireFactory` opens a `ClientWebSocket` with a 20 s keep-alive. The wire accumulates
text frames in 16 KiB chunks until end-of-message, treats a close frame as null, refuses a non-text
frame, and caps a response at 8 MiB. There is deliberately no cap on what is sent: a 96 KiB
expression cap once rejected the glyph stylesheet and the Steam Input page silently kept Valve's
artwork. Disposal sends a normal close with a 500 ms budget.

`SteamUiCdpConnection` correlates JSON-RPC by integer id with at most 32 outstanding requests, a
256-slot notification channel and a 1 MiB cap on notification parameters. `EvaluateAsync` sends
`Runtime.evaluate` with `awaitPromise`, `returnByValue` and `userGesture` all true and returns the
answer as a value and an error. An `exceptionDetails` is an answer too: its error is
`Steam UI JavaScript exception: …` with complete exception details. A string value is returned
as-is, other kinds as raw JSON, no value as null. Each request requires a timeout in `(0, 30 s]`.
The frame is sent under the connection's lifetime only: a caller cancel or timeout stops waiting for
the send but never aborts it half-written, and the send gate is released when the frame is finished.
A send failure cancels the whole connection.

Inbound faults that end the connection: a non-object message, an invalid id, an `error` member, a
reply with neither `result` nor `error`, a notification without a method, oversized parameters, or a
full notification queue. An orphan response is only logged, three times at most. Teardown drains the
notification pump with a 1 s budget, fails every pending request with the failure or
`IOException("Steam UI CDP channel closed.")`, disposes the wire and invokes the closed callback. A
notification handler that throws is logged and does not poison the channel.

## 5. `PersistentSteamUiTransport`

One `TargetChannel` per role. `SubscribeAsync` increments the subscriber count and starts a
reconnect loop when enabled and none is live; a one-shot request holds a request-only subscription
that counts as a subscriber but starts no loop, and connects itself. Releasing the last subscriber
bumps the ownership generation, cancels the loop, disposes the connection and sets `Idle`.

### Reconnection

The loop connects, marks `Retrying` with the failure on error, waits for the connection's
completion, then sleeps 1 s, 4 s, 16 s, 30 s (clamped). An absent target is not a failure: the loop
polls it at the first step without advancing the backoff, so attachment follows Steam's window
within a second. Only a connection that stays up 30 s resets the backoff. Connecting runs discovery
(`Unavailable` with `Steam UI <role> target is absent.` when it returns null), enables `Debugger`,
clears pause-on-exceptions with `Debugger.setPauseOnExceptions {state: "none"}`, disables
`Debugger`, then enables `Runtime`, `Page` and `DOM`, each command under a 5 s budget, and only then
publishes the connection, so an in-place document replacement is observable from the first moment a
channel claims to be ready. Ownership is re-checked before publishing and again after the domains
are enabled; a connection that completes after its owner left logs
`Steam UI <role> connection completed after its owner left; discarding it.` and throws
`OperationCanceledException`.

### Generations

| Event                                                                   | Advances                                           |
| ----------------------------------------------------------------------- | -------------------------------------------------- |
| New browser id on connect                                               | Browser, Target, Frame, ExecutionContext, Document |
| New target id on connect                                                | Target, Frame, ExecutionContext, Document          |
| Every attachment                                                        | Session                                            |
| `Page.frameNavigated`                                                   | Frame, Document                                    |
| `Runtime.executionContextCreated`                                       | ExecutionContext                                   |
| `Runtime.executionContextDestroyed`, `Runtime.executionContextsCleared` | ExecutionContext, Document                         |
| `DOM.documentUpdated`                                                   | Document                                           |

`NotificationReceived` fires only for `Runtime.bindingCalled`, the one notification the bridge
consumes; the generation notifications above only advance generations, and other domain chatter is
dropped before it takes the channel lock. Bindings go through a 256-slot channel that refuses and
logs a call when full. `GenerationChanged` fires only when a generation changed, latest wins per
role: each role keeps only its newest undelivered snapshot, so a burst on one role skips that role's
intermediate snapshots but can never evict another role's. Handler exceptions are logged. A Steam
restart is detected through nothing more than this: the socket closes, the loop backs off, discovery
refuses while the port is closed, and the reconnect brings a new browser id that advances every
generation, which invalidates every patch and the bridge.

### Evaluation

`EvaluateAsync(role, expression, timeout, ct)` validates the timeout before connecting, answers
`Closed` with the closed reason when the host closed the transport (`SetEnabled`, below), takes a
temporary subscription for the call, and reports how far the request got in
`SteamUiEvaluationResult.Dispatch`:

| Outcome                                                    | `Dispatch`   | `Error`                                                | Health                       |
| ---------------------------------------------------------- | ------------ | ------------------------------------------------------ | ---------------------------- |
| transport closed by the host                               | `Closed`     | the closed reason                                      | unchanged                    |
| no target, failed connection, or a failure before the send | `NotSent`    | the reason                                             | `Retrying`                   |
| caller cancellation before the send                        | `NotSent`    | `Steam UI evaluation was cancelled.`                   | unchanged                    |
| deadline before the send                                   | `NotSent`    | `Steam UI evaluation timed out before it was sent.`    | unchanged                    |
| caller cancellation after the send began                   | `Unanswered` | `Steam UI evaluation was cancelled after it was sent.` | unchanged                    |
| deadline after the send began                              | `Unanswered` | `Steam UI evaluation timed out.`                       | unanswered run               |
| connection loss, CDP error or framing fault after the send | `Unanswered` | the reason                                             | `Retrying` or `Incompatible` |
| an answer, including a JavaScript exception                | `Answered`   | null, or the exception                                 | `Ready`                      |

The send boundary is the moment the frame starts: a started frame is always finished, so Steam may
run an expression whose caller already stopped waiting. `NotSent` and `Closed` mean nothing ran;
`Unanswered` means it may have run, which is what a writer has to report instead of "nothing
changed". Any answer, a thrown JavaScript exception included, proves the renderer is alive, so it
keeps the channel `Ready` and clears the unanswered run.

`SetRuntimeBindingAsync` throws rather than returning: `ObjectDisposedException` after disposal,
`InvalidOperationException` carrying the closed reason when closed,
`IOException("Steam UI target is unavailable.")` without a connection, and
`OperationCanceledException` on cancellation or the deadline. It issues `Runtime.addBinding` or
`Runtime.removeBinding`.

### The master switch

`PersistentSteamUiTransport.SetEnabled(bool, string? closedReason)` is the host's master switch:
false bumps ownership, cancels reconnects, closes connections and retains subscriber intent; true
restarts reconnects for channels with subscribers. While closed, every evaluation answers `Closed`
with the host's `closedReason` when it holds the transport for its own reason, such as waiting for
Big Picture, otherwise `DefaultClosedReason` (`Steam CEF integration disabled in settings.`). There
is no process-global session: a host composes one `SteamClient` over its transport and passes it to
everything that talks to Steam's client API (§16).

## 6. Patch lifecycle

### Declaring a patch

| `ISteamUiPatch` member | Contract                                                                                                                                                                                                          |
| ---------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Id`                   | Stable identity; the log key is `steam.ui.patch.<id>`. The fingerprint carries the revision.                                                                                                                      |
| `TargetRole`           | Which target the phases evaluate on.                                                                                                                                                                              |
| `OperationTimeout`     | One phase's budget, at most 30 s. Defaults to `SteamUiPatchManager.DefaultOperationTimeout` (8 s).                                                                                                                |
| `ProbeAsync`           | Read-only. Returns `SteamUiPatchProbeResult(TargetPresent, Compatible, Fingerprint, Diagnostic)`; compatible means exactly one structural match. The fingerprint is a semantic identity, never a module id alone. |
| `ApplyAsync`           | Touches only resources the patch owns.                                                                                                                                                                            |
| `VerifyAsync`          | Proves the applied work is functional.                                                                                                                                                                            |
| `RemoveAsync`          | Removes and verifies removal of only the patch's own work.                                                                                                                                                        |

`SteamUiPatchContext.EvaluateAsync` passes the operation timeout to the transport; an expression has
no size limit of its own. Registration refuses a missing id or target, a timeout outside (0, 30 s]
and a duplicate id, and may happen at any time; a patch registered later joins the next pass.
`UnregisterAsync(id)` removes the patch from the page under the scheduler and then forgets it, its
switch and any fault with it. Patches are kept sorted by id; there are no declared dependencies
between patches, and every pass runs under one scheduler gate.

### States

`Unknown`, `AbsentTarget`, `Incompatible`, `Applying`, `Applied`, `Verified`, `Degraded`,
`Disabled`, `RemoveFailed`, `Retrying`.

### One synchronization pass

A pass first removes every patch that should be off, in id order with the bridge last, because a
gate's removal goes through the bridge. Then it synchronizes every patch that should be on, the
bridge first. A patch should be on when the global switch, its own switch and no fault say so.

1. Off (global switch, its switch, or faulted): remove unless already `Disabled`; end in `Disabled`
   or `RemoveFailed` (`Patch removal timed out after <n> s.` on timeout); release the transport
   subscription. A faulted patch keeps its fault as the failure.
2. On: take a subscription lazily and read the snapshot. If the generations differ from those the
   patch was applied under, bump the patch's epoch and move `Applying`/`Applied`/`Verified` to
   `Retrying` with `Steam UI generation changed; reapply required.`. This catches a snapshot
   observed before its event arrives.
3. Probe under its own phase timeout. Exception: `Degraded`. Target absent: `AbsentTarget`, probed
   again 1, 2, 4, 8 and 16 s later within the same generation, because a window that has just loaded
   is probed before it has mounted everything. A target that appears after those 31 s stays
   `AbsentTarget` until the next generation change, registration or switch queues a pass. A probe
   the page answered with an error is `Incompatible` with that error. Not compatible or no
   fingerprint: if the patch was applied, retract it (`Incompatible`, or `RemoveFailed` when removal
   also failed); otherwise `Incompatible`.
4. A `Verified` patch whose fingerprint is unchanged only re-verifies; success keeps `Verified`
   without reapplying.
5. `Applying`: apply. A failed apply is removed again, once, under a fresh phase timeout, and is
   `Degraded` with the diagnostic (`RemoveFailed` when that removal failed too).
6. `Applied`: verify; success is `Verified`.
7. Verify failure: `Steam UI patch <id> applied but did not verify; removing it: …`, then remove the
   same way. An applied-but-unverified mutation is never left in the client.
8. A cancelled phase says why: `Patch operation cancelled by its switch.` (the removal pass the
   switch queued takes it off), `Steam UI generation changed during the operation.`, or
   `Patch operation timed out after <n> s.`. A timeout after the apply started removes what was
   applied and is `Retrying`, probed again on the settle schedule above rather than reapplied
   blindly. Any other exception after the apply started removes it too; every exception is
   `Degraded`.

Every phase gets its own cancellation source: one budget across probe, apply and verify once
cancelled an in-budget apply with nothing wrong.

### Generation events and kill switches

`OnGenerationChanged` compares the published snapshot's generations against the event for patches on
that role; on a difference it bumps the epoch, cancels the active phase and moves live states to
`Retrying`. Every state write goes through an epoch check so a stale phase cannot publish a result
for a replaced document.

`SetGlobalEnabled` and `SetPatchEnabled` flip the flag, cancel an active phase when disabling, and
queue a synchronization on the thread pool when the switch moved, so a settings change flipping
several switches does not run a pass inline per switch. `QueueSynchronization` asks for a pass
directly. The `…Async` variants await their own pass; use them when shutdown, a settings
confirmation or an emergency kill switch must know cleanup finished. `Synchronized` is raised after
each queued pass, once the scheduler is released and on a thread of its own, so a handler may await
a switch; a throwing handler is logged.

`Fault(id, reason)` takes a patch off for good: the module runtime calls it for every patch of a
module whose callback threw. The patch is removed on the next pass and stays off whatever its switch
says until it is unregistered; there is no reset.

`ShutdownAsync(token)` turns the global switch off and removes every patch, bridge last, each under
its own timeout. When the caller's token fires it stops, records the rest as `RemoveFailed` with
`Shutdown deadline reached before removal.` and names them in one line. `DisposeAsync` is
`ShutdownAsync` without a deadline.

Every transition logs `Change("steam.ui.patch.<id>", "Steam UI patch <id>: <State> — <failure>")`,
written after the patch's lock is released, as a warning unless the state is `Applying`, `Applied`,
`Verified` or `Disabled`.

### `SteamUiPatchEvaluation`

`EvaluateOutcomeAsync` parses the page's `JSON.stringify({ok, error})`: unreachable is a failure
with the transport's error or the fallback; `ok: true` succeeds; otherwise the page's error, the
complete raw value, or the fallback. Only log lines bound diagnostic text.
`IsSuccessful(value, requiredFlags)` treats an unparseable value as failure, never as an optimistic
success, and requires each named boolean, if any, to be true. Every reader treats a root that is not
an object as a failure rather than throwing. `IsOne` demands exactly one structural match, because a
second match means the Steam build has two candidate components and the patch cannot tell which it
would modify.

## 7. Modules and the runtime

A module here is a surface: the patches that install it, the state it publishes, and the commands it
answers. It is not a Steam webpack module.

| Type                                                                              | Contract                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| --------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `SteamUiStatePublication(PatchId, Read, Enabled)`                                 | `Read` returning null publishes nothing that round, keeping "momentarily unavailable" distinct from zero.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                |
| `SteamUiCommandHandler(PatchId, Command, Handle)`                                 | `Handle` returns `SteamUiCommandResult(Succeeded, Error, Payload)`; a failure without an `Error` is answered with `no reason reported`. It runs on the bridge's request pump in arrival order and must return its task promptly: a blocking read goes on `Task.Run` inside the handler, a write keeps its order through its backend. The builders refuse a blank patch id or command.                                                                                                                                                                                                                                                    |
| `SteamUiModuleSet(modules)`                                                       | Flattens once, in any declaration order. Throws on a duplicate module id, a patch registered by two modules, state published for one patch id by two modules, or a `(patchId, command)` answered twice, naming both modules. `AllowedCommands` maps every patch id to its commands; a publication-only patch appears with an empty list because subscriptions are guarded by the same vocabulary.                                                                                                                                                                                                                                        |
| `SteamUiModuleRuntime(bridge, modules, patches, commandsEnabled, publishEnabled)` | Runs both directions. `ReplaceModulesAsync(next)` swaps the set while running: an added module's patches are registered, the bridge vocabulary and the set are swapped, then a removed module's requests are cancelled and its patches retracted and unregistered; a patch is kept only when the same instance is in both sets. Added patches start switched on and nothing is applied until the consumer sets their switches and queues a synchronization, which also reinstalls the bridge when the vocabulary changed. `ShutdownAsync(token)` waits for the publication round and in-flight requests no longer than the token allows. |

Runtime behaviour: a `cancel` request cancels the in-flight source by sequence; duplicate sequences
are ignored. A refused command says why: `Refused` (`The requested semantic service is not active.`)
while commands are disabled, `Unhandled` (`No handler is registered for this command.`) when no
module answers it, and `Quarantined` (`This surface was turned off after an error.`) when its module
failed earlier. A handler exception becomes a failure with its message and quarantines the module
that declared the handler, never the one that installs the patch; its patches are faulted in the
manager. Every failure logs
`Change("steam.ui.request.<patch>.<command>", "Steam UI request <patch>/<command> did nothing: <error> Payload: <shape>")`,
where the shape keeps property names, numbers, booleans and nulls and replaces every string with its
length, because a command can carry a plugin's secret; an undelivered response logs
`steam.ui.response.<patch>.<command>`. Publications are coalesced into one pending round, skipped
while publishing is disabled or the bridge is not ready, and one failing publication does not block
the next (`steam.ui.publication.<id>`). `CancelAllInflight` is the generation-replacement path.

### Webpack modules

All webpack access goes through `SteamUiModuleResolver` and its shared `module-resolver.ts`
implementation. `getWebpackRuntime(scope)` captures that resolver lazily by pushing an empty chunk;
capture itself executes no unknown factory. A caller supplies authored source tokens to `resolve` or
`exported`, and only a unique match may load. `rpc.ts` uses that same resolver to find the query
client by source and export shape. No feature names a client-build module id or minified export.

The bridge keeps one resolver once capture succeeds, so gates share the capture and source cache.
Factory failures are not cached by the resolver: Steam may retain partial exports after a throw, and
a later lookup must inspect their current shape. Source lookup is not permission to initialize Steam
before its UI is ready. Prefer an existing published store or rendered component handle when one is
available. Never iterate the registry executing its factories or constructors: that once restarted a
machine and signed Steam out.

## 8. The bridge

### Identity and configuration

`SteamUiBridgeIdentity.Namespace = "__steamUi_v1_28d7c54a"`,
`BindingName = "__steamUiBridge_v1_7b24d11c"`. `SteamUiBridgeHost.SchemaVersion = 1`,
`DeliveryPartCharacters = 256 * 1024` UTF-16 characters per evaluation for host-to-document
delivery, `OperationTimeout = 5 s`, and a 64-slot request channel. Host state, responses and refusal
text have no aggregate bridge size cap; large envelopes are delivered in parts. Document-to-host
requests use `Runtime.bindingCalled`, whose complete notification parameters still have the CDP
connection's 1 MiB cap. They are not streamed in the opposite direction.

A delivery longer than one part goes as parts under one delivery id, each acknowledged before the
next, and the injected side's `deliverPart` reassembles them before any subscriber sees the state.
It reassembles by delivery id, so a large response and a large state publication whose parts
interleave both arrive. A set cut short by a failed or out-of-order part is dropped without touching
another delivery, never delivered half, and parts never split a surrogate pair. The injected
`request()` refuses only a command outside the allow map and a request past `maximumPending`, each
with a reason instead of a timeout. It adds no per-request size check of its own; the lower CDP
notification bound still applies.

A publication may declare a revision (`SteamUiModuleBuilder.Publication(..., revision)`). A round
whose revision the document already holds (`SteamUiBridgeHost.IsPublished`) skips the read and the
serialization entirely, so a large state is built only when it changed, not on every round another
surface raised. The revision has to change whenever the state would.

`BootstrapAsync` installs the binding, reads the snapshot after the install so a generation raised
by it is the baseline, substitutes the configuration JSON for the literal
`__STEAM_UI_CONFIGURATION_JSON__` in the asset, evaluates it, and is ready only when the reply is
`ok: true`, the reply's generations equal the snapshot's, and no generation epoch changed meanwhile.

Configuration fields: `version`, `namespace`, `binding`, `assetHash`, `contextGeneration`,
`documentGeneration`, `maximumPending` (32), `timeoutMilliseconds` (5000), `vocabularyRevision` (the
SHA-256 of the `allowed` text), `allowed` (patch id to commands). `assetHash` is load-bearing:
neither context nor document generation changes on a consumer update, so without it a new build kept
running the previous build's script until Steam restarted. `vocabularyRevision` does the same for
the allow map, which the injected side fixes at install.

`SetAllowedCommands(vocabulary)` replaces the vocabulary while the host runs. Requests are
authorized against the new one at once; a changed vocabulary also makes the host not ready and
invalidates a bootstrap in progress, so the bridge patch's next synchronization installs the bridge
again with the new `allowed` map. An equal vocabulary changes nothing.

### The injected side (`bridge.ts`)

| Member                                                  | Behaviour                                                                                                                                                                                                                                                                                                                                                                                         |
| ------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| reuse check                                             | If `window[namespace]` exists with equal `version`, `assetHash`, `vocabularyRevision`, `contextGeneration`, `documentGeneration` and a `gate` function, return `{ok:true, reused:true}` before any fragment runs. Otherwise the prior bridge is disposed with `dispose("generation replaced")`, and the gates it names go into the install result as `priorDisposeFailures`, which the host logs. |
| `request(patchId, command, payload, actionGeneration?)` | Rejects `command not allowlisted` and `bridge busy` (≥ `maximumPending`). Allocates a positive action generation when the caller passes none or zero, because the host rejects zero and several gates once passed exactly that. Sends the envelope through `window[binding](JSON.stringify(...))`; on timeout sends a `cancel` envelope and rejects `Steam UI bridge request timed out`.          |
| `subscribe(patchId, callback)`                          | Throws `subscription not allowlisted` unless the patch id is a key of `allowed`; replays the latest state. After dispose it registers nothing and returns a no-op unsubscribe, like `subscribeRefusal`.                                                                                                                                                                                           |
| `deliver(envelope)`                                     | Accepts only `response` and `state` envelopes whose version and generations match, and nothing after dispose; a response resolves or rejects the pending promise by sequence and patch/command; a state is stored and fanned out.                                                                                                                                                                 |
| `dispose(reason)`                                       | Calls `remove?.()` then `dispose?.()` on every registered gate and returns the names of those that threw or answered `{ok: false}`; rejects pending requests, clears the gates and maps.                                                                                                                                                                                                          |
| `gate(name)`                                            | Returns null for an unknown gate so a failed fragment reads as "gate absent".                                                                                                                                                                                                                                                                                                                     |
| `registerGate(name, gate)`                              | What consumer fragments call.                                                                                                                                                                                                                                                                                                                                                                     |

Subscriber exceptions are isolated during cached replay as well as later delivery. A throwing
callback cannot prevent `subscribe` from returning its cleanup handle or stop another subscriber.
The emitted `eng/check-startup.mjs` fixtures exercise both paths across the built-in module IDs.

`SteamPowerLimitSurface` publishes independent sustained (PL1) and boost (PL2) ranges with
availability, minimum/maximum/step watts, observed watts, progress and status. Its `powerLimit`
control uses Valve slider primitives and follows the host's value after profile or external edits.
Only a completed slider edit sends `setPrimaryLimit` or `setBoostLimit` with exactly `{watts}` (a
positive integer). The host validates the current descriptor; its range is the device's, with no
ceiling of the toolkit's. The sliders share a pending-command guard, show refusals and never retry
automatically. Mounting, publication and profile changes issue no writes.

No range row is gated on a reading. A valid descriptor (whole bounds, minimum below maximum, a
positive step) draws the power-limit, charge-limit and lighting-brightness sliders; the value shown
is the host's, clamped into the range, and an off-step one is shown as is until the user moves it. A
range with no value sits at its minimum with its number hidden and sends nothing until moved. The
frame-limit row draws with no observed or desired cap the same way, and the controller row draws its
dropdown with nothing selected when neither the observed nor the selected target is offered. The
former SteamOS Manager overlay and settings watcher are removed; Steam's persisted TDP setting has
no authority over this surface. `eng/check-power-profile.mjs` exercises profile updates, drag
echoes, independent commands, unavailable readback, bounds, pending commands and failures using the
emitted controls.

The bridge object is frozen and defined on `window` as non-enumerable, non-writable, configurable.
`installResult` is assigned, not returned; `epilogue.ts` returns it after every fragment ran,
because a return in `bridge.ts` once published a bridge with an empty registry while the bootstrap
patch still verified.

### Host-side authorization

`SteamUiBridgeAuthorizer.Authorize` rejects, in order:

| Rejection                                  | Rule                                            |
| ------------------------------------------ | ----------------------------------------------- |
| `schema version mismatch`                  | `version` must equal `SchemaVersion`            |
| `message type is not allowlisted`          | only `request` and `cancel`                     |
| `patch command is not allowlisted`         | `(patchId, command)` must be in `allowed`       |
| `sequence or action generation is invalid` | both must be positive                           |
| `payload is missing`                       | a request or cancel must carry a payload        |
| `stale bridge generation`                  | generations must match the current snapshot     |
| `cancel references an unknown request`     | a `cancel` sequence above the last accepted one |
| `request sequence was replayed`            | sequences are monotonic                         |
| `action generation was replayed`           | action generations are monotonic                |

The authorizer resets on every generation change. Only `Runtime.bindingCalled` notifications from
`SharedJsContext` with matching generations, the binding name and a string payload are accepted (the
connection's 1 MiB notification bound is the only size limit), deserialized with a camelCase
source-generated context (PascalCase once rejected every command). Rejections log
`Change("steam.ui.bridge.rejected", …)` with the payload's shape, never its values, bounded to 2048
characters like every diagnostic.

`RespondAsync` and `PublishStateAsync` require readiness and matching generations, then evaluate
`b.deliver(JSON.parse("..."))` and accept only a structured `{ok:true}`. Response envelopes carry
`version`, `type: "response"`, `patchId`, `command`, `sequence`, both generations, `ok`, `payload`,
`error` (whole: the page shows it, and delivery is chunked); state envelopes carry `type: "state"`,
`patchId`, both generations and `payload`. A `SharedJsContext` generation change drops readiness and
resets the authorizer. `RemoveAsync` removes the binding, evaluates
`b.dispose('Steam UI removed'); delete window[k]`, and logs any incomplete step. Disposal waits 2 s
for an in-progress bootstrap and 1 s for the request pump.

## 9. Ownership (`ownership.ts`)

The three ways to change the client, and what removal owes:

| API                   | Primitives                                                       | Removal owes                                      |
| --------------------- | ---------------------------------------------------------------- | ------------------------------------------------- |
| Feed a data construct | `supplyNamespace`, `withdrawNamespace`                           | delete it                                         |
| Answer an RPC         | `claimMember`, `releaseMember`, `memberClaimed`, plus `rpc.ts`   | restore what was displaced                        |
| Reveal what is gated  | `claimValue`, `releaseValue`, `claimAccessor`, `releaseAccessor` | restore the original; never the platform constant |

Three invariants: a claim must recognise its own work, must hand back exactly what was there, and
both facts must survive a separate CDP evaluation, which is why markers are string-keyed
non-enumerable fields rather than Symbols. Every claim writes `keys.marker = true` and
`keys.original = { kind: "steam-ui-property-snapshot-v1", hadOwn, descriptor, value }`; the caller
supplies the key names so a renamed key cannot orphan a marker a previous build left.

| Primitive                                                | Behaviour                                                                                                                                                                                                                                                                                                                                                        |
| -------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `claimValue(host, field, keys, next)`                    | Refuses `claim target unavailable` when the field is not in the host and `already set by the client` when the unmarked value already equals `next` (restoring later would hand back an invented value). Writes through an accessor's setter and reads back; throws `claim target is a read-only accessor`; rolls back field, marker and original on any failure. |
| `releaseValue`                                           | Restores through the setter, by redefining the saved descriptor, or by deleting so an inherited value shows through, then deletes both keys. Releasing an unclaimed field succeeds.                                                                                                                                                                              |
| `claimMember(host, member, keys, replacement(original))` | Puts the marker on the replacement, which may be an object or a function; a `typeof === "object"` check once let an overlaid method outlive its own removal. A reclaim passes the underlying original to the factory so wrappers never stack.                                                                                                                    |
| `supplyNamespace(host, name, marker, factory)`           | Refuses a real backend (`<name> already exists`), reclaims its own orphan (a namespace on `SteamClient` outlives the bridge that dies with the context), and defines non-writable rather than assigning, because assignment throws against a previous bridge's definition under strict mode. `withdrawNamespace` deletes only a marked one.                      |
| `claimAccessor(host, property, keys, getter)`            | Refuses a non-configurable property and marks the replacement getter with the whole original descriptor; `releaseAccessor` redefines it.                                                                                                                                                                                                                         |

The accessor rule in `claimValue` comes from a MobX crash in the Quick Access Menu.

`eng/check-ownership-claims.mjs` takes the ownership primitives out of the emitted prelude whole,
evaluates them with `new Function`, and runs more than thirty claim, reclaim, release and
stand-aside scenarios. It runs in CI; reintroducing the function-type defect fails four checks. The
other emitted-asset checks share `eng/check-harness.mjs`, which takes whole fragments out of the
asset by their `// @fragment` label and instantiates each gate over these same emitted primitives
and the shared gate helpers rather than stand-ins, and `eng/run-checks.mjs` runs every
`eng/check-*.mjs` it finds.

React has one `useMemo`, and more than one surface needs what it returns: the Quick Access tab list
and the Settings page list. `interceptMemo(react, name, transform)` takes one member claim on it for
all of them with the first transform, `releaseMemo(react, name)` hands it back with the last, and
`memoIntercepted(react, name)` says whether a named transform is live on the installed wrapper.
Transforms run in registration order, each on the previous one's result, and one that throws leaves
the value as it was. A surface never wraps `useMemo` itself, because two wrappers would hand each
other's originals back on removal.

The JSX runtime's `jsx` and `jsxs` are shared the same way, for what is drawn inside mobx observer
classes that no claim on a type or prototype can reach.
`interceptElements(runtime, name, transform)` claims both with the first transform,
`releaseElements(runtime, name)` hands both back with the last, and
`elementsIntercepted(runtime, name)` checks a named transform against the installed wrappers. A
transform receives `(create, type, props, key)` and returns the element to use, or undefined to
leave the call to the runtime; `create` is the runtime's own function, so a replacement never passes
through the transforms again. The first transform to answer wins and a throwing one is skipped.
Every element Steam creates passes through, so a transform tests a cheap property first. Both are
instances of one `createSharedClaim(keys, members, …)`, which owns the transform map, the installed
wrappers and the take-with-first, release-with-last rule. The wrappers loop over a frozen array of
the transforms, rebuilt when one is added or withdrawn, so a call allocates nothing.

A script outside the bundle cannot reach those functions from its own evaluation. The `elements`
gate is their front door: `gate("elements").register(name, transform)`, `unregister(name)` and
`registered(name)`, with any nonempty string as a name. WSGM's download sort registers there rather
than wrapping the runtime itself, which would put a second wrapper on `jsx`.

`rpc.ts` supplies `transportReply(body)` (the
`{BSuccess, BFailed, GetEResult: 1, Body().toObject()}` shape a Steam transport RPC answer takes),
`transportFailure(body)` (the same shape refused, with `GetEResult: 2`), `resolveQueryClient(req)`,
which finds the client's query client in the provider module carrying `ReactQueryDevtools` and
`offlineFirst` by its `invalidateQueries` and `getQueryState` and answers null otherwise, and
`invalidateQuery(req, queryKey)`, which calls `invalidateQueries` on it, swallowing every failure.
The Bluetooth and storage gates use all of them.

## 10. The extension host

`SteamUiExtensionHost` discovers and validates installed extension packages; it does not run them.
Composing an extension into modules and patches, and removing the patches a rejected extension
declared, is the consumer's job: a rejected extension whose manifest parsed still carries it. It is
not a sandbox: injected script has the same reach as the consumer's gates, and the checks are about
identity and collision only. The host loads no assembly and executes nothing; the returned script is
text until the consumer builds it into the injected asset.

| Manifest field (`extension.steam-ui.json`) | Rule                                                                                                          |
| ------------------------------------------ | ------------------------------------------------------------------------------------------------------------- |
| `id`                                       | `[a-z0-9._-]`, no leading or trailing separator, not `steam-ui` or under it, nor under a host-reserved prefix |
| `name`, `version`                          | free text                                                                                                     |
| `apiVersion`                               | must equal `SteamUiExtensionHost.ApiVersion` (1) exactly                                                      |
| `script`                                   | relative path that stays inside the package, exists, and is strict UTF-8                                      |
| `patches`                                  | safe, distinct ids each prefixed `<id>.`                                                                      |

`Discover(root, reservedPrefixes)` examines packages in directory-name order and returns every one
ordered by id, loaded or refused with a `SteamUiExtensionRejection` (`UnreadableManifest`,
`InvalidManifest`, `ApiVersionMismatch`, `UnreadableScript`, `UnscopedPatch`, `Conflict`,
`ReservedPrefix`) and a detail, named by directory when the manifest could not be read, so "my
extension does nothing" always has a reason. `ReservedPrefix` refuses an id or a patch under
`steam-ui` or under one of the host's own prefixes. Conflicts are resolved on the complete claim
set, so a rejected extension does not reserve claims that would make a later valid one look
conflicting. Log keys: `steam.ui.extensions.root`, `steam.ui.extension.<id>`.

## 11. The prelude build and the composition contract

`eng/steam-ui-fragments.mjs` is the one fragment list: `types.ts`, `bridge.ts`, `ownership.ts` and
`rpc.ts`, then every other top-level fragment sorted by name (`icons.ts`, `module-resolver.ts`, …),
then `gates/*.ts` sorted, then `components.ts`, then each consumer directory's fragments sorted,
then `epilogue.ts`. `eng/build-prelude.mjs` and a consumer's builder both import it, so the checks
run against the prelude see the shipped layout. Every fragment after `bridge.ts` opens with a
`// @fragment <label>` line: a toolkit fragment's path under `Source/` (`ownership.ts`,
`gates/audio.ts`) or `consumer/<path>` for a consumer's. The compile appends the IIFE close,
type-checks with TypeScript 7 under a strict, ES2022, type-stripping-only configuration, refuses an
output that lost a marker (TypeScript erases the comments that lead a `type` declaration, so a
fragment opens with runtime code), and starts the asset at the `// @steam-ui-bundle-start` marker.
`dist/prelude.js` is that asset cut before the `epilogue.ts` marker, with the IIFE left open.
`types.ts` sits above the marker so it types the compile and ships nothing. Compiling the prelude
alone is what proves it stands on its own: it stopped compiling the moment the bridge still named a
consumer's gates.

A consumer composes one script:

```text
(() => { "use strict"; let installResult; const config = __STEAM_UI_CONFIGURATION_JSON__;
  …bridge.ts…            reuse check, request/subscribe/deliver/dispose, registerGate, window[ns]
  …ownership.ts, rpc.ts…   then the other top-level fragments, sorted
  …gates/*.ts, components.ts…
  …consumer fragments…   hoisted function create…() + top-level registerGate(name, create…())
  …epilogue.ts…          return installResult;
})();
```

The host replaces the placeholder with the configuration, evaluates the whole thing in one
`Runtime.evaluate`, and passes the SHA-256 of the source as `assetHash`.

`gate-helpers.ts` holds what several gates share: the fingerprints of modules more than one surface
resolves (React, the field components, the JSX runtime, the settings store, mobx-react-lite and the
localizer), `resolveReact`, `findUseObserver`, `isLocalizer`, `isSteamDialogButton`, `classMapOf`,
`ReactMemoType`, the element helpers `keyed`, `mapChildren` and `descendInto`, and the lifecycle
steps `attemptResolution` and `endSubscription`. Every fingerprint names what an author wrote: the
localizer is the export that hands `LocalizeString` the token alone, and a dialog button is found by
its class names however they were passed. It holds constants and functions only, so nothing in it
runs while the bundle is evaluated and its place among the discovered fragments does not matter.

## 12. Rules

- Every patch carries an ownership marker and accepts "already ours"; a probe that requires the
  pre-patch condition its own apply invalidates tears itself down on every poll.
- Removal restores exactly what was displaced, read from the object rather than from the closure
  that installed it.
- Reveal the surface, never the platform: overriding a store getter is allowed; setting Steam's "is
  this SteamOS" constant is not.
- Never iterate the webpack module registry constructing exports.
- Every refusal is logged with its reason, because the injected side has nowhere to put an error.
- Every patch fails open to Valve behaviour, and a successful patch must not invalidate its own next
  probe.

| Gate                     | Example                        | Allowed response           |
| ------------------------ | ------------------------------ | -------------------------- |
| Absent JS namespace      | `SteamClient.System.Perf`      | supply it                  |
| Absent RPC response      | a manager's `GetState`         | supply it                  |
| RPC stub with no backend | a service whose methods refuse | replace the stub's methods |
| Deck-only store getter   | `networkManagementAvailable`   | override that one getter   |
| Global platform constant | `TS.IS_STEAMOS`                | never                      |

## 13. Constants

| Constant                                                                              | Value                                                          |
| ------------------------------------------------------------------------------------- | -------------------------------------------------------------- |
| Debug port, flag file                                                                 | 8080, `.cef-enable-remote-debugging`                           |
| Accepted port owners                                                                  | `steamwebhelper`, `steam`                                      |
| Discovery timeout, response cap                                                       | 5 s, 1 MiB                                                     |
| WebSocket keep-alive, receive chunk, max response, close budget                       | 20 s, 16 KiB, 8 MiB, 500 ms                                    |
| Outstanding requests, notification queue, notification params                         | 32, 256, 1 MiB                                                 |
| Per-request timeout bound                                                             | (0, 30 s]                                                      |
| Diagnostic bounds                                                                     | 2048 characters                                                |
| Reconnect backoff                                                                     | 1, 4, 16, 30 s                                                 |
| Domain enable timeout                                                                 | 5 s each                                                       |
| Transport event channels                                                              | 256 bindings (refused when full), latest generation per role   |
| Patch operation timeout default                                                       | 8 s                                                            |
| Absent-target re-probes within one generation                                         | 1, 2, 4, 8, 16 s                                               |
| Bridge schema, delivery part, operation timeout, request channel                      | 1, 262,144 UTF-16 characters, 5 s, 64                          |
| Injected `maximumPending`, `timeoutMilliseconds`                                      | 32, 5000                                                       |
| Bridge namespace, binding                                                             | `__steamUi_v1_28d7c54a`, `__steamUiBridge_v1_7b24d11c`         |
| Configuration placeholder, bundle marker                                              | `__STEAM_UI_CONFIGURATION_JSON__`, `// @steam-ui-bundle-start` |
| Property snapshot kind                                                                | `steam-ui-property-snapshot-v1`                                |
| Extension API version, identifier, reserved prefix                                    | 1, `[a-z0-9._-]`, `steam-ui`                                   |
| Client call budgets: app write, install folder, library read, page read, running apps | 20 s, 10 s, 12 s, 8 s, 4 s                                     |
| App-details subscription bound, new shortcut read-back, artwork clear settle          | 3 s, 2 s, 500 ms                                               |
| Running-app observer                                                                  | `window.__steamUiRunningApps`                                  |

## 14. Tests

| Suite                                                            | Locks                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| ---------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `SteamUiCdpConnectionTests`, `PersistentSteamUiTransportTests`   | CDP connection (orphan ids, malformed frames, cancellation, slow and throwing handlers); persistent transport (domains before publication, generation advances, one-shot leases, discarded late connections, master switch, health restoration, backoff, invalid deadlines)                                                                                                                                                                                                                                                                                                                                                                                                                                                              |
| `SteamUiPatchManagerTests`                                       | bounds, kill switches, retraction of an incompatible or unverified patch, removal failure, per-phase budgets, failure isolation between patches, re-verification without reapplying, generation epoch guards, required structural flags                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| `SteamUiBridgeHostTests`, `SteamUiBridgeAuthorizerTests`         | replay, malformed and non-binding notifications, a long refusal reaching the page whole, parted deliveries, generation replacement, structured acknowledgements, disposal; the authorizer's allowlist, replay, stale generation and cancel rules, and the real camelCase envelope captured from a live client                                                                                                                                                                                                                                                                                                                                                                                                                            |
| `SteamUiExtensionHostTests`                                      | every rejection reason and conflict rule                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| `SteamUiModuleTests`                                             | module set rules, publication isolation                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| `SteamUiEndpointDiscoveryTests`                                  | the two role matchers against real URLs                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| `NativeTcpTests`, `SteamCefTests`                                | the table decoder; the debug-flag opt-in, the URL gate, the four port-owner reasons                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| `SteamClientTests`                                               | the client layer: unreachable against refused, app-id normalization, the details and library parsers, install-folder script selection and reply statuses, download activity, and the running-app observer's reading and lease                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| `SteamSurfaceModuleTests`                                        | each surface's `Commands` against its module's vocabulary, each refusal reason against its payload, a null reading publishing nothing                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| `SteamGatePatchContractTests`                                    | each claiming gate's verify and remove predicates, and already-claimed compatibility                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |
| `SteamPanelFoldsTests`, `eng/check-power-profile.mjs` (sections) | the open list's wire shape, that the surface mounts nothing, and its one command with a nested id; the emitted panel drawing every section as a kit group with its glyph and summary, Profile scope fixed, Reset headless, folds sent under the section's title                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| `SteamChoiceRowTests`, `SteamWindowSurfaceTests`                 | power-profile, preset and core-preference serialization and dispatch; side-menu observation, native button replay, game-window and overlay activation                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| `SteamNavigationPanelTests`                                      | the panel probe's separate structural facts, selection by what an export draws rather than by its minified name, already-claimed compatibility, the published wire shape                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| `eng/check-navigation-panel.mjs`                                 | the emitted gate against an inert React fixture: descent to the panel root, anchoring by route and by descriptor key, orphan reporting, hiding before insertion, activation, exact restoration, reinstall                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                |
| `SteamPageTests`, `eng/check-pages.mjs`                          | the page probe's separate facts and its rendered-tree search; the emitted gate's route-list discovery by content, an addition losing to Steam's own route and an override winning, path validation, exact restoration, reinstall, and a page declared with `registerSteamPage` refusing, following state and refusals, and removed                                                                                                                                                                                                                                                                                                                                                                                                       |
| `SteamStorageTests`, `eng/check-storage.mjs`                     | the storage probe's service and transport facts and every action having a command; the emitted gate's availability answer, Steam's own state field names, action forwarding, unrelated service traffic passing through with its arguments and receiver, and restoration putting Valve's method back                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| `SteamHomeCarouselTests`, `eng/check-home-carousel.mjs`          | the Home probe's separate facts, finding Home by content rather than name, the mounted count, already-claimed compatibility, the published wire shape, the exact report payload; the emitted gate finding Home through the route list from the root's `current`, adopting a mounted Home on both fibers and asking the switch to render, replacing `games` for the carousel and the background, clearing the whole-list overscan, the documented order, disconnected games leaving, uninstalled games greyed, no rebuild or report without a change, the fallback to Steam's list, bounded publications, a mounted wrapper passing through after removal, exact restoration of the memo and the adopted fibers, reinstall adopting again |
| `SteamScreensaverTests`, `eng/check-screensaver.mjs`             | the probe's separate facts and that it names no module id or export, the published wire shape, the exact report and choice payloads and their refusals; the emitted gate wrapping only the customization page and only its Screensaver section, appending the rows after Steam's own, reporting on first read, on change and on page open, sending a choice once and disabling the row while pending, refusing a malformed state whole, the bounded first-report retry, keeping the shared `useMemo` claim for another surface on removal and handing it back with the last                                                                                                                                                              |
| `eng/check-startup.mjs` (resolver)                               | missing factories staying uncached, unique resolution, and `exported` counting aliases once, refusing two distinct fits, no fit, a missing module and an invalid predicate                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               |
| `SteamLibraryBadgeTests`, `eng/check-library.mjs` (details)      | the stat patch's compatibility, its Valve-named row lookup and shared runtime resource, the module declaring both patches under one publication; the emitted stat only on the stats row, Valve's classes and the localized label, the badge's naming rules and dimming, no duplicate, an `elements` gate transform coexisting on the one claim, a throwing transform skipped, and `jsx` and `jsxs` handed back only with the last transform                                                                                                                                                                                                                                                                                              |
| `eng/check-ui-kit.mjs`                                           | every kit element: the stylesheet's rules, the header's fold and its own caret, the group's body and hidden state, the action grid and its wide label, the card's parts and activation, the banner's dismiss, the toolbar, chips, box and gallery, a confirm sending only on OK, a prompt sending only a trimmed non-empty answer                                                                                                                                                                                                                                                                                                                                                                                                        |
| `SteamThemeStyleTests`, `eng/check-theme-styles.mjs`             | the theme-styles probe reading only Steam's popup manager, the wire shape, the revision-stamped publication; the emitted gate's targeting by title, URL and root class, the published order, an unchanged block kept, a changed block rebuilt, a late window styled on the next pass, a broken pattern matching nothing, removal leaving no node                                                                                                                                                                                                                                                                                                                                                                                         |
| `SteamLibraryBadgeTests`, `eng/check-library.mjs` (badge)        | the badge probe's separate structural facts, selection of the tile and the badge by what they are rather than by name, the published wire shape, the exact layout payload; the emitted gate placing the badge left of Valve's in one row, naming the library or the internal label, green for installed and grey otherwise, no badge for a game installed nowhere, an anchorless tile left untouched, Big Art reported once per change, exact restoration, reinstall                                                                                                                                                                                                                                                                     |

Additional regression sources execute the actual C# probe and lifecycle expressions
(`SteamProbeExecutionTests`), client scripts (`SteamClientScriptExecutionTests`), native replay,
sound publications, command refusals/cancellation and full error delivery. The surface module test
discovers all public surface factories rather than keeping a partial list. These sources still need
to run with the rebuilt emitted asset before their behavior is considered verified.

## 15. Surfaces

A surface is one Valve feature the Windows client ships inert, revived end to end: the injected gate
that supplies or reveals it, the C# patch that probes, applies, verifies and removes it, the typed
state a consumer feeds, and the backend interface a consumer implements. Every source fingerprint,
store field name, localization token and row placement lives here, in `Surfaces/` and
`SteamUiAssets/Source/gates/` plus `components.ts`, so a consumer never reads the client's bundle.

Most typed surface factories share these members; layout-only and dynamic frontend factories expose
the subset appropriate to their contract:

| Member                               | Meaning                                                                                   |
| ------------------------------------ | ----------------------------------------------------------------------------------------- |
| `PatchId`                            | The id its state is published under and its commands are addressed to.                    |
| `Commands`                           | The exact command vocabulary its injected side sends; what the module puts on the bridge. |
| `Patch` (rows: also `*Row` patches)  | The `ISteamUiPatch`(es) that install it.                                                  |
| `Module(enabled, read, backend, id)` | One `ISteamUiModule` from a publication gate, a state reading and a backend.              |

`read` returns the state or null; null publishes nothing that round, which keeps "momentarily
unavailable" distinct from a zero. `Serialize(state)` on each surface emits the exact wire payload,
for fixtures and diagnostics.

| Surface                       | Valve feature                                             | Gate kind                                                                                          | State                        | Backend answers                                                                            |
| ----------------------------- | --------------------------------------------------------- | -------------------------------------------------------------------------------------------------- | ---------------------------- | ------------------------------------------------------------------------------------------ |
| `SteamAudioSurface`           | audio page and Quick Settings audio                       | supplies `SteamClient.System.Audio`, feeds the running store                                       | `SteamAudioState`            | default device, volume                                                                     |
| `SteamNetworkSurface`         | Internet page and header Wi-Fi indicator                  | overrides `networkManagementAvailable`, feeds the network store                                    | `SteamNetworkState`          | scan start/stop                                                                            |
| `SteamBluetoothSurface`       | Bluetooth page and panel                                  | replaces the service stub's methods, invalidates the query                                         | `SteamBluetoothState`        | discovery, pair, connect, disconnect, forget; trusted and wake-allowed accepted by default |
| `SteamBrightnessSurface`      | brightness slider                                         | reveals the flag, claims `SetBrightness`, feeds the observable                                     | `SteamBrightnessState`       | set brightness                                                                             |
| `SteamPerformanceSurface`     | Performance tab and its Valve rows                        | supplies `SteamClient.System.Perf`, writes the store, decodes deltas                               | `SteamPerformanceState`      | apply a `SteamPerformanceDelta`                                                            |
| `SteamPowerLimitSurface`      | Sustained and boost power sliders                         | Valve field primitives driven by host observations or dispatched writes                            | `SteamPowerLimitState`       | set PL1 or PL2 independently                                                               |
| `SteamFrameLimitRow`          | unified frame-limit row                                   | row on Valve's slider and toggle                                                                   | `SteamFrameLimitState`       | frame cap, refresh rate                                                                    |
| `SteamVariableRefreshRow`     | VRR switch                                                | row on Valve's toggle                                                                              | `SteamVariableRefreshState`  | VRR on/off                                                                                 |
| `SteamResolutionRow`          | resolution dropdown (Quick Settings)                      | row on Valve's dropdown                                                                            | `SteamResolutionState`       | apply a mode                                                                               |
| `SteamAudioFormatRow`         | channel/default-format and spatial sound (Quick Settings) | rows on Valve's dropdown                                                                           | `SteamAudioFormatState`      | apply a format or spatial sound choice                                                     |
| `SteamAutoTdpRow`             | automatic power-limit switch                              | row on Valve's toggle                                                                              | `SteamAutoTdpState`          | setting on/off                                                                             |
| `SteamControllerTargetRow`    | controller-target dropdown                                | row on Valve's dropdown                                                                            | `SteamControllerTargetState` | choose a target                                                                            |
| `SteamDeviceControlsRow`      | charge limit, lighting brightness and colour              | rows on Valve's slider and dropdown                                                                | `SteamDeviceControlsState`   | three writes                                                                               |
| `SteamNavigationPanelSurface` | left slideout navigation panel                            | claims the exported memo's `type`, reaches the panel root by rendering                             | `SteamNavigationPanelState`  | activate an added entry                                                                    |
| `SteamPageSurface`            | custom pages in Steam's router                            | claims the router memo's `type`, inserts routes into the route list                                | `SteamPageState`             | none: a page is declared, not commanded                                                    |
| `SteamStorageSurface`         | SteamOS storage management pages                          | claims `SendMsg` on the service transport, answers `StorageDeviceManager.*`                        | `SteamStorageState`          | adopt, unmount, eject, format, trim                                                        |
| `SteamLibraryBadgeSurface`    | a library badge on every library tile                     | claims the tile memo's `type`, replaces the Steam Input badge element with a row of two            | `SteamLibraryBadgeState`     | hears the Home layout (Big Art Mode) report                                                |
| `SteamHomeCarouselSurface`    | Big Picture Home's carousel                               | claims Home's memo `type`, replaces the carousel's `games` array and bounds its overscan           | `SteamHomeCarouselState`     | hears what the carousel holds after each rebuild                                           |
| `SteamScreensaverSurface`     | host rows in the Screensaver settings section             | a transform on the shared `useMemo` claim wraps the customization page and its Screensaver section | `SteamScreensaverState`      | hears Steam's screensaver timeouts; applies a row's choice                                 |
| `SteamFilePickerSurface`      | the folder and file picker a host page opens              | none: `showSteamFilePicker` draws a Steam modal on the page that asks                              | none                         | lists the drives and user folders; lists one folder's subfolders and matching files        |
| `SteamThemeStyleSurface`      | CSSLoader-compatible stylesheets in every Steam window    | appends one `<style>` per block to each popup document its targets name, through `g_PopupManager`  | `SteamThemeState`            | none: the blocks are declared, not commanded                                               |

The other built-in surface contracts use the same runtime and lifetime rules:

| Surface                         | State and frontend                                        | Backend or command                                                   |
| ------------------------------- | --------------------------------------------------------- | -------------------------------------------------------------------- |
| `SteamPowerProfileRow`          | `SteamPowerProfileState`, component host                  | `setPowerProfile` through `ISteamPowerProfileBackend`.               |
| `SteamPowerPresetRow`           | `SteamPowerPresetState`, component host                   | Separate `setAcPowerPreset` / `setBatteryPowerPreset`.               |
| `SteamCpuBoostRow`              | `SteamCpuBoostState`, component host                      | `setCpuBoost` through `ISteamCpuBoostBackend`.                       |
| `SteamHybridCoreRow`            | `SteamHybridCoreState`, component host                    | `setHybridCores` through `ISteamHybridCoreBackend`.                  |
| `SteamPanelFoldsSurface`        | `SteamPanelFoldsState`, shared fold helper                | `setFolded`; the host persists state.                                |
| `SteamQuickAccessLayoutSurface` | `SteamQuickAccessLayout`, component host                  | Publication-only section/row placement.                              |
| `SteamSettingsQuickAccessRow`   | `SteamSettingsQuickAccessState`, shared settings renderer | `set {key,value}` through its backend.                               |
| `SteamNativeSettingsSurface`    | `SteamNativeSettingsState`, `nativeSettings` gate         | `set {key,value}` on existing Steam Settings pages.                  |
| `SteamExtensionsTabSurface`     | `SteamExtensionsTabState`, `extensionsTab` gate           | `activate` and `configure`, host-rendered plugin descriptors.        |
| `SteamGameContextMenuSurface`   | `SteamGameContextMenuState`, `gameContextMenu` gate       | `activate` with a selected positive app id and command id.           |
| `SteamPowerMenuSurface`         | `SteamPowerMenuState`, `powerMenu` gate                   | `switchToDesktop` delegated to the host.                             |
| `SteamSoundOverrideSurface`     | `SteamSoundOverrideState`, `soundOverrides` gate          | Revision-bound `status` decoding reports.                            |
| `SteamPluginFrontendSurface`    | Optional detached JSON state, `pluginFrontends` gate      | `invoke` and `failure`; unrestricted scripts and owner-wide cleanup. |

See the [source map](code-map.md#surface-implementations) for the complete file inventory, including
native-window observation/replay and lifecycle helpers which are not state-publishing surfaces.

### Marked rows

A host can mark a row, for example because the running game's own profile supplies its value. The
frame limit, VRR, CPU boost, power limit (each range, and the mode as `ModeAccent`), power preset
(`AcAccent` and `BatteryAccent`), device controls (charge limit, brightness and each lighting zone)
and controller-target states carry `Accent`. A marked row leads its description with the
`AccentLabel` of the host's Quick Access layout and draws that description in Steam's accent blue
(`#1a9fff`, `SteamAccentColor` in `gate-helpers.ts`), so a changed value stands out. The Edit color
toggle's description names the marked lighting zones after the same label, in plain text. The
toolkit holds no word for a mark and adds no control for it: what a mark means is the host's policy,
and how a user goes back is the host's too.

### SteamOS storage management

Big Picture ships a complete storage UI — drives, volumes, format, adopt, eject, trim — that never
appears on Windows. The whole surface hangs off one question: its hooks ask
`StorageDeviceManager.IsServiceAvailable#1` over the WebUI service transport, and every other query
is `enabled:` on that answer. The Windows client has no service behind it, so the answer never
arrives and the pages stay inert. Nothing is hidden by a SteamOS check; it is simply unanswered.

The claim is `SendMsg` on the live transport instance. It is defined on the transport prototype as
writable and configurable and the instance carries no own property, so the claim is an own property
that removal deletes, leaving Valve's method showing through untouched.

The probe (`steam-ui-storage-v3:unique-service+unique-singleton-accessor`) requires one service
module, one transport module and one singleton accessor. The shared resolver's `storageProvider()`
loads only the uniquely fingerprinted transport module and inspects its exports without calling
provider accessors. It accepts an ordinary zero-argument function whose entire body returns one
identifier. Webpack export getters are read only when their source is an empty-argument arrow
returning one identifier; other getters are skipped. Function source is read through the intrinsic
`Function.prototype.toString`, so a custom `toString` cannot impersonate the shape. Aliases of one
function count once. No match or multiple matches refuse installation with the match count.

Offline inspection on 2026-10-05 of the installed `steamui/chunk~2dcc5aaf7.js` (SHA-256
`F9606B111203C9140DCDD6FC016A0EE00E4C8406AEB786187C42873F08D071FB`) found that the transport module
has just one export, bound by a pure identifier-return getter to an accessor that only returns its
closed-over singleton. The singleton owns `GetDefaultTransport` and `m_transport`; the accessor
carries no stable author tokens. The selector fixes neither module ids nor identifiers. Probe and
gate share this selector in `module-resolver.ts`. Only the gate calls the unique accessor, once per
install, then obtains and checks its transport. A changed return shape refuses without trying other
exports. Claimability remains an install check. Storage probe and gate checks cover missing and
duplicate matches, unknown exports and getters, aliases, pass-through and restoration; they were
written but not run for this change. Build and live acceptance remain deferred.

**That one method carries every service call Steam makes**, which sets the rule for the whole gate:
the name prefix is checked first and nothing else happens on the pass-through path — same arguments,
same receiver, same return. The harness asserts exactly that, and asserts that after removal a
storage message goes to Valve like any other.

The message vocabulary is read from the client's own generated classes rather than guessed:

| Class                              | Fields                                                                                                          |
| ---------------------------------- | --------------------------------------------------------------------------------------------------------------- |
| `CStorageDeviceManagerDrive`       | `id`, `is_formattable`, `is_unformatted`                                                                        |
| `CStorageDeviceManagerBlockDevice` | `block_device_id`, `drive_id`, `mount_paths`, `has_steam_library`                                               |
| `CStorageDeviceManagerState`       | `drives`, `block_devices`, `is_adopt_supported`, `is_unmount_supported`, `is_trim_supported`, `is_trim_running` |

Responses are duck-typed to the two things Steam's callers ask of them — `BSuccess()` and `Body()` —
rather than built as protobuf messages. The wire format is the client's business, and mirroring it
would mean owning a second copy of it.

Every action is the host's. The injected half performs no storage operation at all, which is what
keeps one Windows implementation behind both Steam's pages and WSGM's own surfaces instead of two
that can disagree. An empty published state is still answered: "no removable drives" is a truthful
answer and the page renders it, where refusing to answer leaves Steam's spinner up forever.

### The library badge

`SteamLibraryBadgeState.Shortcuts` optionally supplies `SteamShortcutAvailability` records keyed by
the confirmed unsigned `uint` AppId. These records override the installed flag only in host
presentation: logical location, unavailable colour, desaturated tile and reactive details reason.
They never change Steam's global app type or installation data. Tiles and details subscribe through
the shared local-store primitive to their app alone, including instances mounted after the claim. A
publication refreshes only apps whose visible badge changed; mounted-tree adoption happens at
installation and restoration at removal. Native `RunGame` stays untouched because its callers also
update running state; the host's launch helper owns the final guard. `recheck { appId }` calls the
required `ISteamLibraryBadgeBackend.RecheckAsync(uint, CancellationToken)` method. The reader
accepts exactly one positive `uint` app ID. Removal restores the exact saved original tile type.

`SteamApps.SetShortcutNameAsync` writes an existing shortcut's name while preserving AppId. A
successful client write is applied without gating it on a later overview readback.
`SteamAppDetails.Name` contains the live name when available.

Extension actions appear below every section heading; settings fold beneath them. Action placement
has no per-item visibility flag.

`renderSteamUiSheet(ui, props, ...children)` draws a shared scrollable modal body, optional note and
error, and actions. Each action has `id`, `label`, `onClick`, optional `primary` and `disabled`. Use
it inside the existing `showSteamModal`; the caller owns dismissal and submitted work.
`renderSteamUiStringList(ui, { values, onChange, label?, addLabel?, removeLabel?, resetLabel? })`
draws a controlled list of text fields with add/remove and optional reset. Label callbacks receive
the zero-based row index. The caller owns validation and any application-specific limits.
`renderSteamUiLevel` can omit `onBack` for a standalone root, allowing Steam's route back-navigation
to handle B; nested levels continue supplying their own callback.

Every library tile — Home's carousel, the library grid, the collection views — is one exported
`React.memo`, a mobx observer, drawn by every caller through the export. Its icon row holds Valve's
Steam Input badge, which is also exported from the same module but called by the tile through its
module-local name, so claiming the badge export changes nothing the tile draws. The claim is on the
tile memo's `type`; the badge is found in what the tile renders by element type — identity with the
export — and replaced by one flex box holding this badge and then Valve's. The row is
`space-between` with Valve's badge pushed to its end by an auto margin, so a bare sibling would land
at the far left; the box keeps the two together wherever the row puts them.

Valve shows the icon row's badge on the focused or hovered tile only, by an opacity rule written
against the badge's own class, so a plain box beside it would show on every tile. The box therefore
wears two of Valve's classes, read by name from the tile stylesheet's class map — the module
carrying `ControllerSupportIcon:"`, `LibraryItemIcons:"` and `LibraryItemBox:"` — with the
glyph-sized geometry overridden inline: the badge class for the fade and the end-of-row margin, the
row class so Valve's icon stays a direct child of a row, which its pill background is written
against. No hash is written down anywhere. Without the map the box is plain and always visible, and
`status.classesResolved` says so.

The walk is over props alone: the tile's whole icon row is host elements and fragments below its
Focusable root, so nothing has to be rendered to reach the anchor, and function components on the
way keep every identity Valve's reconciler holds. It is bounded at twelve levels, with every child
of a level visited. A tile whose tree has no anchor — a music album, a tile with compat icons hidden
— renders exactly what Valve shipped and is counted in `lastOutcome` as unanchored.

The badge text is the library's name alone, and its colour is Steam's own installed flag on the app
overview: green installed, grey not, which is what a disconnected card amounts to. The published
`connected` stands in only for an overview that cannot say. The host names every library, internal
ones included; the toolkit has no name of its own. A game no published library holds gets no badge
and no library stat, and before the first publication no game has one.

Big Art Mode is `library_home_big_art`, a client setting the Home component reads through a settings
hook. The gate resolves the settings store by its own class body — the module carrying
`get clientSettings()` and `m_setDeferredSettings` — and the one export carrying `clientSettings`.
It reads the flag on every tile render and sends `homeLayout { bigArt }` once when it first resolves
and once per change, so a host learns of a toggle without a subscription into Valve's store;
`status.bigArt` carries the current reading and `null` when the store did not resolve. The store is
wanted, not required: the badge is tile-relative and draws the same in either layout.

Mapped against the September 2026 client beta on 2026-09-11: `appportrait_` occurs in exactly one of
the 2622 loaded modules, that module has exactly one memo export and exactly one function export
whose source draws the controller-support icon, five modules render the tile through the export, and
the memo's `type` is a writable and configurable own property. The probe checks each of those
separately and accepts a tile this gate already claimed.

### The library on a game's page

The same surface adds the library as a stat in the play bar of a game's own page, after Last Played
and Play Time, through a second patch, `steam-ui.library-details` (gate `libraryDetails`), which
reads the badge's publication and sends nothing. The badge and the stat name a game by the same
rules: the published library, the internal label while installed, nothing when installed nowhere.
The stat dims a library the game is not installed from.

The stats row cannot be claimed where it is drawn. The play bar, its status-and-stats block and the
stats section are all mobx observer classes, and mobx-react's class observer replaces `render` on
the prototype with a function that pins a non-writable, non-configurable `render` on each instance
the first time it runs; a prototype claim reaches no mounted instance and loses every later render.
What does reach the row is the JSX runtime: the section creates it as
`jsxs("div", { className: GameStatsSection, children })`. So the stat is a transform on the shared
element claim (§9): a `div` whose class is the play bar class map's `GameStatsSection` gets one more
child, unless it already has one with the stat's key. The app is the overview the row's own children
are given.

The stat is Valve's markup for Last Played without its tooltip: `GameStat LastPlayed`, then
`GameStatRight`, then a `PlayBarLabel` and a `PlayBarDetailLabel LastPlayedInfo`, every class read
by name from the class map carrying `GameStatsSection:"`, `PlayBarDetailLabel:"` and
`LastPlayedInfo:"`. The label is Steam's `#Settings_Page_Library` through the localizer export
chosen as the Quick Access host chooses it, and "Library" without one.

Read from the Stable client (UI build of 2026-09-06) and the September 2026 beta on 2026-09-11: the
app-details module is the same in both, the class map occurs once, the JSX runtime module occurs
once, and the probe requires the React, runtime and class map matches while reporting the localizer.

### The Home carousel

Home's carousel draws one array of app ids, passed as `games` to both the carousel and the hero
background behind it. Steam builds that array in a module-local hook from four collections
(`local-played`, `recent-purchased`, `local-install`, `recent`) and caps it at 20. Home, the
carousel and the hook are all module-local, so the handle is the page element under the
`/library/home` route — the route list found by content in SharedJSContext's React tree — and the
gate claims that Home memo's `type`. Until Big Picture has built that tree there is no route list to
find: on 2026-09-11 two probes during startup refused with the Home module, both stores and the
observer hook resolved but no Home, and the manager's next probe verified. The walk is breadth-first
over the fiber child and sibling links, bounded at 250,000 nodes, because a router sits near the top
of its tree and a depth-first walk can spend its bound inside a mounted library grid. It starts from
every container React attached to under the document and reads each root's `current`, the tree on
screen. A miss reports the roots searched, the nodes visited, how many route lists held
`/library/home` and what that route renders, in the probe's diagnostic and in `status.search`. In
what Home renders it finds the carousel memo by its source (`#Showcase_RecentGames`,
`RecentGamesContainer`) and replaces it with a memo of its own over the same inner function and
comparison. In what the carousel renders it replaces `games` on the two elements that take it, told
apart by shape: the background takes `refOnItemFocus`, the carousel `onItemFocus`.

The claim reaches Homes mounted after it, because React resolves a memo's function once at mount and
renders the cached `type` on the fiber afterwards. Since the client update of 2026-09-22 Big Picture
renders a placeholder until its services report initialized and then mounts the router and Home in
one commit, so Home is always on screen by the time the route list can be found; the claim alone
left the carousel Steam's until the user left Home and came back. Install therefore adopts every
mounted Home through the shared `adoptMountedType` helper: the wrapper, which adds no hooks, becomes
the cached `type` on the fiber and its alternate, the cached props are replaced so the memo cannot
bail out of the next render, and the nearest class ancestor — the router switch — is asked to render
through `forceUpdate`. The probe reports the mounted count as `mounted`; `status.mounted` carries
how many were adopted, whether a render was requested, and how many are still stale, which is an
adoption whose render is pending or a mount the claim never reached. Removal hands adopted fibers
back to Home's own function without requesting a render.

The carousel is a react-virtualized grid whose overscan defaults to 3 columns. Home passes
`overscan: games.length`, which mounts every tile; harmless at 20, a memory flood at a library. The
gate renders the carousel's own function component and clears that prop so the component default
applies, which is what the Play Next carousel on the same page already gets.

The order is decided in the gate because its inputs — every installed and owned game with its
timestamps — are Steam's own data, already in the document, and republishing them from the host on
every change would be the wrong direction. The host decides which games are excluded and whether
uninstalled games appear:

1. Steam's own running-game prefix, when the list has one: the running game and its separator.
2. The most recently played installed game, pinned first, as Steam pins it.
3. Installed games by last played (the later of local and account-wide), merged with unplayed
   purchases from `recent-purchased` by purchase time.
4. Installed games never played, by install time.
5. With `includeUninstalled`, owned games from `my-games` that are not installed, by last played
   then purchase time.

Ties fall back to app id. Steam's own exclusions apply — music albums, and tools never run — plus
the host's `disconnectedAppIds`. A played shortcut, which Steam's installed collection leaves out,
is taken from Steam's own list. Anything not installed is greyed by a rendered `<style>` scoped to a
`display: contents` container around the carousel, keyed on the grid cell's `data-id`. When nothing
qualifies, Steam's own list stands in, because the carousel renders nothing for an empty array.

The list is rebuilt only when an input changed: the host revision, Steam's own array, or the
identity of one of the three collections it reads. Those collections are read inside Steam's own
mobx-react-lite `useObserver`, resolved by that library's startup check, so a recomputed collection
re-renders the carousel; a publication re-renders it through `useSyncExternalStore`. The hook is
wanted, not required, and `report` says whether it resolved. After each rebuild the gate sends
`report` with its counts once per change.

Read from the September 2026 beta's shipped bundle on 2026-09-11: `HomeTabsActive` with
`#Showcase_RecentGames` occurs in one module, the Home route renders a memo, and mobx-react-lite's
startup check occurs once. The probe checks each separately and accepts a Home it already claimed.

### Screensaver settings

The September 2026 beta ships Big Picture's screensaver on Windows. Its settings are a section of
the Customization page, and on a machine Steam believes has no battery the section's last row is the
idle timeout, `system_idle_screensaver_ac_sec`. Steam keeps per-source timeouts on a Power page it
shows only with a battery or under gamescope. The surface appends host-owned timeout rows to that
section and reports Steam's timeouts, so a host can keep a timeout of its own in order with them.

The Settings root builds its page list with `React.useMemo`:
`{ visible, title, icon, route, content }` per page. A transform on the shared claim (§9) replaces
the content of the entry whose `route` is the route table's `Settings.Customization()` with a
wrapper that renders the page. In what the page renders, the one child whose type's source carries
`"#Settings_Customization_Screensaver"` and `ForceScreensaver` is replaced by a wrapper that renders
the section and appends the rows. Both wrappers are cached by what they wrap and pass Steam's tree
through once the gate is removed; the same input list always maps to the same output list. Two
customization entries, or a page without exactly one such section, is left as Steam drew it and says
so in `status.lastOutcome`.

The rows are Valve's `DropDownField`, controlled, one per published row: label, optional
description, the host's options and the observed value. A choice sends `setTimeout { row, seconds }`
once and disables the row until the response. The host decides which choices a row offers; the gate
renders them and nothing else. A publication is validated whole: any number of rows with unique ids
of a lowercase letter then lowercase letters, digits or hyphens, any number of options each, every
value 0 to 604,800 seconds and every option labelled. A state that fails keeps the last good rows.

The report is `{ acSeconds, batterySeconds, battery }`: the two client settings, the second null
when unset, and whether Steam's power store has a battery. It is read inside Steam's own
mobx-react-lite observer, so a change on the page re-renders the rows, and sent when it first
becomes readable (a bounded retry, 60 attempts two seconds apart, while the settings store is still
empty), on every change, and each time the page opens.

Read from the September 2026 beta's bundle on 2026-09-11: the section token with `ForceScreensaver`
occurs in one module, the route table's customization route is `/settings/customization`, and the
screensaver's own services are `Screensaver.GetActiveState`, `ForceScreensaver`,
`GetLocalScreensavers` and `NotifyActiveStateChanged`.

### Custom pages

Steam's router renders its routes as the children of its own switch, so registering a page is a list
operation on props rather than DOM work — unlike the navigation panel, whose entries do not exist
until its root renders.

Three facts decide the API, and all three were measured against the live client on 2026-09-10:

- **The switch takes the first matching child.** So inserting ahead of Steam's routes overrides one
  and inserting behind them adds one. `SteamPage.Override` is that distinction, and it defaults to
  adding, because shadowing a client route is not something a caller should get by accident.
- **The `Route` must be Steam's own, and it is borrowed rather than found.** Every element in the
  route list is the component Steam is rendering that route with, so the gate takes its `Route` off
  the `/library/home` element in the list it has already located. That is the component itself, not
  something that matched a description of it, and no client build can rename it away. React-router's
  `Route` renders the same content and silently loses back-navigation, which nobody would notice
  until they pressed B.

  The borrowed component is verified, never found, by two markers Valve wrote, `routePath:` and
  `.match?.path`, with nothing said about the minified code between them; `status.routeSource` reads
  `borrowed (unverified)` when the markers are absent, which is a page that draws and may have lost
  back navigation. The probe reports whether the module carrying `router-backstack` still exports
  such a Route, and requires nothing of it.

  The original fingerprint was decky-loader's regex, `routePath:.\.match\?\.path.`, which described
  the minified code and assumed a one-character local. The 2026-09-24 client emitted two characters,
  the probe answered `steamRoute:0`, the gate declared an otherwise compatible client incompatible,
  and every custom page rendered as an empty client. So: prefer a handle Steam hands you over a
  fingerprint, and when a fingerprint is unavoidable, name what an author typed and never how a
  minifier spelled it.

- **The router memo is not an export.** It is built locally inside its module — every export of that
  module was inspected and none carries it — so the handle comes from SharedJSContext's own React
  root, which is the tree every Steam window renders from. The walk is breadth-first over an
  explicit queue, bounded, and matches on component source. The earlier depth-first walk found the
  router in 659 visited nodes on the reference client; it recursed, and a long sibling chain could
  exhaust the stack before the bound was reached.

The route list is found by content: the array holding a route for a path every client has. Decky's
gamepad path indexes `children.props.children[0].props.children` instead, which is the kind of
selector that breaks on a client update with no diagnostic; its own desktop path searches by
`/library/home`, and that is the half worth following.

A page whose path is relative, or is `/`, is dropped rather than registered. A catch-all route
inserted ahead of Steam's own would black out the client.

Installing claims the memo's `type`, which reaches the next mount only, so install also adopts the
routers already on screen through `adoptMountedType`, the same helper the Home carousel uses. The
step this replaced swapped one fiber and called `forceUpdate` on the nearest class ancestor, which
cannot work: the router is a `React.memo` with the default comparison, so the parent re-renders, the
memo sees equal props and bails out, and the claimed type is never called. The gate then held a
claim that was correct and inert — `claimed` true, `lastOutcome` "never rendered", `routeCount` 0 —
and no page existed until the user navigated and changed the props themselves. `status.mounted`
reports how many routers the install reached and whether one is still drawing Steam's own.

### The navigation panel

The panel is module-private. Its root builds its own entry list from a local function, neither is
exported, and that function calls React hooks — calling the module's own exported list builder from
outside a render throws React error #321. So reading the entries and changing them both have to
happen during a render, and one wrapper serves both.

The only public handle is an exported `React.memo`, and the gate claims its `type`. From there it
reaches the panel root by rendering: a component's children do not exist until React renders it, so
a walk over `props.children` alone arrives nowhere. Function components met on the way down are
replaced by cached wrappers that render the original and keep descending, bounded at twelve levels.
This is the mechanism `hideNativeRows` already uses for the Performance tab, pointed at a different
target.

That claim is correct and, on the 2026-09-24 client, never reached: Big Picture's main menu is a
popup whose host is a module-local function mounted directly under a React root, exported nowhere,
and the memo is not in that render path at all. The gate therefore also adopts the mounted host by
source (`adoptMountedBySource`), recognised by three prop names its author destructures
(`MainNavMenuContainer`, `onFocusNavDeactivated`, `popup:`), on install and again on every
publication in case Steam has recreated it; `status.mounted.hosts` counts them. The host sits under
the root with no class ancestor, so no render can be requested. It persists while the menu is closed
and re-renders when `open` flips, which is when the entries appear. This is the same adoption
decky-loader's tabs hook makes on the Quick Access view, for the same reason.

Entries are addressed by route (`/library`) or by Valve's own descriptor key (`power`), never by
index or by a generated class name: routes and keys are stable across client builds and languages,
whereas the rendered labels are localized and the class names are content hashes. Hiding is applied
before insertion, so an anchor and the entry it anchors to cannot disagree about what the user can
see. An entry whose anchor is not in the panel goes to the end and is counted in `lastOutcome` as
orphaned rather than dropped, because a control that silently does nothing is a defect.

Mapped against the live client on 2026-09-10: `#MainMenu_Title` occurs in exactly one of the 2581
loaded modules, `MainNavMenuContainer` in exactly one, that module has exactly one export whose memo
renders the container, and that export's `type` is a writable and configurable own property, which
is what makes the claim restorable. The probe checks each of those separately so an incompatible
client says which one moved, and accepts a panel this gate already claimed.

An added entry is drawn by Valve's own entry components, never by an imitation. Re-read on
2026-09-24: the panel renders a route descriptor with a local route entry and an action descriptor
(Power) with a local action entry, and the route entry maps its route onto the action entry through
the router. Both draw the same row: Valve's `Focusable` with the menu's own item, icon and label
classes, the active dot, and mouse and gamepad activation. Neither is exported, so the gate takes
them from the entries the panel is rendering, together with the panel's own `onGamepadFocus`, which
clears the focused running app the way every native entry does. That is done over every child,
hidden ones included, so hiding Power does not cost the action entry.

- An item with a `route` is drawn by the route entry with `active: "if-within-route"`: it is active
  on its page and below it, and selecting it navigates with Valve's own route action, so the host is
  not asked. The route is held to `navigateSteamRoute`'s bounds where it is published, and is only
  followed when the user selects the row.
- An item without one is drawn by the action entry, and selecting it sends `activate`. An answer
  carrying a `route` is followed after `closeSteamSideMenus()`, as the Extensions tab does.
- An item whose component is not in the panel is not drawn, and `lastOutcome` counts it as
  `unrendered`.
- An item whose `route` is not a route (relative, `/`, or carrying a control character) is refused
  where it is published, and `status().rejectedRoutes` counts it.

An item's icon is a toolkit glyph by name, or `glyph`: SVG path data on a 24x24 grid, drawn by
`renderSteamGlyph` as one `currentColor` path with even-odd holes and no size of its own. That is
how Valve draws the menu's icons, which the row's icon box sizes, so a host's mark sits beside Home
and Library as one of them. Only path commands and numbers are accepted.

### Library capsules, the checkbox and the file picker

`library-capsule.ts` draws a library capsule the way Steam's gamepad library draws one, for titles
Steam does not know yet, whose app overview Steam's own capsule component would need.
`resolveSteamLibraryClasses(runtime)` finds the library item class map by the three tokens the
library badge uses and returns null unless `LibraryItemBox`, `Portrait`, `Landscape`,
`PortraitImage`, `LibraryItemBoxShine`, `LibraryItemOverlayOuterArea` and
`LibraryItemOverlayInnerArea` are all there. `createSteamCapsule(ui, classes)` returns one
component; make it once per resolution. Its props are `asset` (grid, wide, hero, logo or icon: the
shape), `image`, `placeholder`, `width`, `dimmed`, `overlay`, `caption` and `focus`, which is passed
to Steam's Focusable. The focus ring, the grow animation and the shine are Steam's CSS for those
classes.

`resolveSteamUiComponents` also returns `checkbox`: Steam's `DialogCheckbox`, which lives in its own
module beside the toggle's base class and takes the toggle's props. It is found in the module named
by `DialogCheckbox_Container` as the class whose prototype declares `SetChecked` and `Toggle` and
whose source names `"DialogCheckbox"`, never by how the minifier joined those. It is null on a
client without it; `steamCheckbox(ui)` answers it or, failing that, the toggle, which takes the same
props.

It also returns `dropdownControl`: the bare dropdown button that `DropDownField` wraps in a labelled
row, for a toolbar that wants the control on its own. It is the field module's export whose
prototype declares `SetSelectedOption` and `BuildMenu`, the members decky-frontend-lib chooses it
by, and takes the dropdown's own props: `rgOptions`, `selectedOption`, `onChange`, `disabled`,
`menuLabel`, `strDefaultLabel`.
`renderSteamDropdown(ui, {label, rgOptions, selectedOption, onChange, disabled})` draws it where the
client has it and the labelled field otherwise.

`showSteamModal(ui, {title, className, render, onCancel})` opens a Steam modal around a body
`render(close)` draws, calling `onCancel` when the user dismisses it; it answers false on a client
with no modal manager. `resolveSteamPanelComponents(runtime)` answers Valve's `PanelSection` and
`PanelSectionRow`, the pieces every Quick Access tab is built from, for the Quick Access row host
and the Extensions tab alike. `SteamGamepadButton` names the button codes a Focusable's
`onButtonDown` reports, and `onSteamTriggers(step)` turns LT and RT into a step of -1 and +1 and
stops a trigger it handled, so the same press does not also scroll the page.

`showSteamFilePicker(ui, {title, mode, extensions, start})` opens a folder or file picker as a Steam
modal and resolves with the chosen path, or null when cancelled or when the modal goes away any
other way; it settles once. A opens a folder or chooses a file, X uses the current folder, Y goes up
a level and B cancels. A listing that answers after a later one was asked for is dropped, and the
drive list opens the start folder only while nothing else was asked for, so the folder on screen is
always the one "Use this folder" accepts. It lists through the `steam-ui.file-picker` commands;
register `SteamFilePickerSurface.Module(enabled)` to answer them. The module has no patch and
publishes nothing. Both commands run on a worker, so a drive that stops answering never holds the
bridge; a request the page's timeout cancelled returns without an answer. It lists names only, skips
hidden and system entries, never opens a file, and answers a folder's whole listing. A `\\?\` path
is listed in its ordinary form, and Downloads is the user's known folder, wherever it was
redirected.

The capsule, the checkbox and the picker were mapped from the installed client offline on
2026-09-27; the capsule and the checkbox have had a live pass in a host's import page since.

### A host's own page

`registerSteamPage({template, gate, patchId, components, required, prepare, release, status, Page})`
declares a page a host draws inside Steam. It registers the renderer under `template` and a gate
under `gate` that resolves `components(runtime)`, refuses to install when a name in `required` is
missing or `prepare` answers a reason, subscribes to `patchId`'s state and its refusals, and on
removal drops both so a mounted page draws nothing rather than its last controls. It answers the
context the page reads while it renders: `react()`, `ui()`, `state()` and `refusal()`. `Page` is
drawn inside a frame of the toolkit's, which re-renders it on every change and, until the gate
holds, says why instead of showing "Loading…" for ever.

`SteamPagePatch.Create(patchId, gateName, fingerprint, subject, probes)` is the matching C# patch: a
read-only probe that every `SteamPageProbe` the page draws from matches exactly once, then the page
gate's install, verified by `installed`, `resolved` and `subscribed`.

### Script API for consumer fragments

Consumer fragments share the toolkit asset's IIFE scope. These names are the supported source API
for a pinned consumer; they are not new window globals. Keep their callers coherent when changing
them. These decisions retain the current API and reserve implementation state to the toolkit. No
fragment reorganization or new registration mechanism is required.

| Name                                                                      | Decision    | Purpose                                                                                                                                                                                         |
| ------------------------------------------------------------------------- | ----------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `registerSteamPage`                                                       | Retain      | Declare a page, its component requirements and its generation-bound gate.                                                                                                                       |
| `renderSteamSettings`, `renderSteamSettingRow`                            | Retain      | Draw a settings page or one typed row with Steam's fields.                                                                                                                                      |
| `useSteamSettingDrafts`                                                   | Retain      | Share per-row draft and refusal state. Returns `change(send)`, `draft(row)` and `row(row)`; settles only the submitted draft and clears committed drafts when the publication revision changes. |
| `resolveSteamSettingsComponents`, `SteamSettingsRequired`                 | Retain      | Resolve and check the native settings page components.                                                                                                                                          |
| `resolveSteamUiComponents`, `SteamUiTabbedPageRequired`                   | Retain      | Resolve page controls and check the tabbed page requirements.                                                                                                                                   |
| `steamUiKitStyle`                                                         | Retain      | Install the kit stylesheet in the rendered root.                                                                                                                                                |
| `renderSteamUiHeader`, `renderSteamUiGroup`                               | Retain      | Draw section headings and folding groups.                                                                                                                                                       |
| `renderSteamUiActions`, `renderSteamUiMore`                               | Retain      | Draw an action grid or a navigation row.                                                                                                                                                        |
| `renderSteamUiPane`, `renderSteamUiLevel`                                 | Retain      | Draw a page pane or a nested level with Back.                                                                                                                                                   |
| `renderSteamUiSwatch`, `renderSteamUiEmpty`                               | Retain      | Draw a color swatch or an empty/error message.                                                                                                                                                  |
| `renderSteamUiCard`, `renderSteamUiGrid`                                  | Retain      | Draw a focusable card and a card grid.                                                                                                                                                          |
| `renderSteamUiBanner`, `renderSteamUiToolbar`, `renderSteamUiTool`        | Retain      | Draw a dismissible notice and labelled toolbar controls.                                                                                                                                        |
| `renderSteamUiChips`, `renderSteamUiBox`                                  | Retain      | Draw selectable chips and a titled box.                                                                                                                                                         |
| `renderSteamUiGallery`, `renderSteamUiVideo`                              | Retain      | Draw image alternatives and a video preview.                                                                                                                                                    |
| `renderSteamUiGlyph`, `renderSteamGlyph`                                  | Retain      | Draw a named kit glyph or a supplied vector glyph.                                                                                                                                              |
| `renderSteamUiTabbedPage`, `renderSteamUiDetail`                          | Retain      | Draw a tabbed page and a detail page frame.                                                                                                                                                     |
| `showSteamUiConfirm`, `showSteamUiPrompt`                                 | Retain      | Open the kit's confirm and text-entry modals.                                                                                                                                                   |
| `showSteamModal`, `showSteamFilePicker`, `showSteamColorEditor`           | Retain      | Open a Steam modal, a host-backed file picker or a color editor.                                                                                                                                |
| `createSteamCapsule`, `resolveSteamLibraryClasses`                        | Retain      | Draw a Steam library capsule with its native classes.                                                                                                                                           |
| `renderSteamDropdown`, `steamCheckbox`, `onSteamTriggers`                 | Retain      | Draw native selectors and react to controller trigger input.                                                                                                                                    |
| `navigateSteamRoute`                                                      | Retain      | Navigate using Steam's route handlers.                                                                                                                                                          |
| `request`, `subscribe`, `registerGate`                                    | Retain      | Send an allowlisted semantic command, follow state and register a bounded gate.                                                                                                                 |
| `getWebpackRuntime`, `JsxRuntimeTokens`                                   | Retain      | Use the shared module resolver and the shared JSX source fingerprint.                                                                                                                           |
| `invalidateQuery`, `endSubscription`, `keyed`                             | Retain      | Invalidate a resolved query, end a subscription and preserve element keys.                                                                                                                      |
| `claimMember`, `memberClaimed`, `releaseMember`                           | Retain      | Claim a member and restore the exact displaced value.                                                                                                                                           |
| `interceptMemo`, `memoIntercepted`, `releaseMemo`                         | Retain      | Share the one memo interception and release a consumer's transform.                                                                                                                             |
| `interceptElements`, `elementsIntercepted`, `releaseElements`             | Retain      | Share the JSX interception and release a consumer's transform.                                                                                                                                  |
| Draft caches, gate maps, ownership records and bridge configuration state | Internalize | Keep these as toolkit implementation details. Consumers use the helpers above.                                                                                                                  |
| Listed consumer names                                                     | Remove none | Current consumers still use them, or they support an existing control.                                                                                                                          |

`useSteamSettingDrafts` is current in `settings.ts`, `components.ts` and the Extensions gate. Its
revision/draft handling is exercised by `eng/check-settings-fields.mjs` and
`eng/check-extension-surfaces.mjs`; retaining it does not require a consumer to draw a whole
settings page. A feature uses `getWebpackRuntime` to resolve a unique source fingerprint and an
export by shape; it does not sweep the registry or invoke unknown exports.

### The UI kit

`renderSteamUiChoice(ui, {label, showLabel, rgOptions, selectedOption, disabled, onChange})`
contains Steam's bare dropdown in narrow columns, with a below-layout full-field fallback. Use
`showLabel:false` when a table already names the row; the accessible label remains. The shared
styles contain Steam's fixed-width dropdown wrappers without changing the separately owned popup.
`renderSteamUiSelectRow(ui, {key, title, detail, status, selected, onClick})` keeps a native button
as one focus target and lays out title/status on one row and supporting text on its own line. Plain
adjacent spans are not a multi-column button layout.

File pickers size against their modal parent, never a viewport-derived minimum width. Their rail and
file list scroll independently; actions remain outside the scroll region. A file picker with no
extension filter sends `.*`, the one explicit all-files filter. An empty extension list still means
folders only to `SteamFilePickerSurface.ListFolder`. Other wildcard patterns are rejected.

`ui-kit.ts` is what a host draws around Steam's fields: the elements Steam ships none of, drawn once
from plain elements and one stylesheet in the vocabulary of Steam's own panels, so a host's page and
its Quick Access section look like the panels beside them. The rule is the toolkit's: Steam's field
where one fits, the kit for everything else, and never one-off HTML in a consumer's fragment. Every
element takes the page's resolved `ui`, builds with Steam's React, takes focus through Steam's
Focusable and lights up on the `gpfocus` class Steam sets. The rules are flat `steam-ui-kit-*`
classes; the root that uses the kit renders `steamUiKitStyle(react)` once, so the stylesheet lands
in that root's document.

| Element                                     | Draws                                                                                                                                                                                 |
| ------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `renderSteamUiHeader`                       | a section heading with a glyph, a detail line and, when it folds, Steam's Focusable and the kit's caret; `open` while unfolded, `plain` when fixed, `sub` for a switch's own settings |
| `renderSteamUiGroup`                        | a block: the heading over a body that stays mounted while folded; `hidden` leaves layout, no title makes a plain box                                                                  |
| `renderSteamUiActions`                      | Steam's buttons in a two-column grid; a label over eighteen characters, or `wide`, takes a row                                                                                        |
| `renderSteamUiSwatch`                       | a colour square                                                                                                                                                                       |
| `renderSteamUiCard`, `renderSteamUiGrid`    | a focusable card (16:10 image, stats strip, badge, title, meta) and the grid it sits in                                                                                               |
| `renderSteamUiEmpty`                        | what an empty list says, or why it failed                                                                                                                                             |
| `renderSteamUiBanner`                       | a notice or error with a Dismiss button                                                                                                                                               |
| `renderSteamUiToolbar`, `renderSteamUiTool` | a focusable row of labelled controls; a `grow` tool takes what is left                                                                                                                |
| `renderSteamUiChips`                        | small buttons in a wrapping row                                                                                                                                                       |
| `renderSteamUiBox`                          | a titled box, for a detail view's action column                                                                                                                                       |
| `renderSteamUiGallery`                      | one large image, thumbnails that pick it, and a counter                                                                                                                               |
| `renderSteamUiVideo`                        | a movie preview on the gallery's frame, muted on a loop over its still                                                                                                                |
| `renderSteamUiTabbedPage`                   | a host page's frame: the stylesheets, a banner, and Steam's tabs with only the active one drawn                                                                                       |
| `renderSteamUiDetail`                       | one item's media, heading and text beside its boxes, with Back, left with B                                                                                                           |
| `renderSteamUiMore`                         | a paged list's foot: one centred Load More button, since a list of thousands of cards stalls Steam's renderer                                                                         |
| `renderSteamUiPane`, `renderSteamUiLevel`   | a page's pane and a level over it, each taking the controller's focus when it appears, so B goes back a level instead of leaving the page from focus left on nothing                  |
| `renderSteamUiGlyph`, `SteamUiGlyphs`       | the store glyphs a card or box carries: download, star, heart, target                                                                                                                 |
| `SteamUiTabbedPageRequired`                 | the components a tabbed page needs resolved, for its `required`                                                                                                                       |
| `showSteamUiConfirm`, `showSteamUiPrompt`   | a confirmation, and a request for one line of text, in Steam's modal                                                                                                                  |

The Extensions tab draws its folding header, its action row and its nested rows with the kit and
gives its `PanelSection`s the block look through `steam-ui-kit-blocks`; the Performance and Quick
Settings row host draws its sections as kit groups, wraps Valve's Quick Settings sections in
`steam-ui-kit-valve` and Valve's battery line in `steam-ui-kit-battery`; a host's page frames itself
with `steam-ui-kit-page` and lays a tab's content out in a `steam-ui-kit-pane`; the settings
renderer's colour row draws its swatch with it. The stylesheet element and the icon renderer are one
per React, so a root re-rendering on every publication hands React the same elements.
`eng/check-ui-kit.mjs` covers every element's shape and callbacks against the emitted asset.

### Settings pages

`settings.ts` draws a host's settings with Steam's native routed sidebar, sections and fields, using
the shared UI kit for the surrounding elements such as color swatches. A host publishes
`SteamSettingsPage`s: pages of `SteamSettingsSection`s of `SteamSettingsRow`s, each described by
`SteamSettingsRowKind` rather than by component. The host's page renderer passes them to
`renderSteamSettings(ui, {route, pages, revision, onChange, onAction})` with components from
`resolveSteamSettingsComponents(runtime)`, and requires `SteamSettingsRequired`.
`onChange(row, value)` and `onAction(row)` answer the request they made, so a refusal is shown on
its row.

Mapped against the live client on 2026-09-24:

| Component        | Found by                                                                                                       | Draws                                                                                                                 |
| ---------------- | -------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------- |
| Routed sidebar   | the one module with `disableRouteReporting`; its one export with that prop                                     | Settings' page list and pages; each page is `route/<id>`, switched with `history.replace`, so B leaves the whole page |
| Settings section | the field module's export carrying `"DialogSettingsSection"`                                                   | a titled section                                                                                                      |
| Value field      | the field module's one-argument export naming `inlineWrap`, `"shift-children-below"` and `focusable`           | a label beside a value: notes, order values, action rows                                                              |
| Small button     | the field module's export whose class names are `DialogButton`, `_DialogLayout` and `Small`                    | order moves                                                                                                           |
| Confirm modal    | the one module with `strMiddleButtonText`, `bProgressDialog` and `bAlertDialog`; its one export with all three | confirmations, with `bDestructiveWarning`                                                                             |

The toggle, dropdown, slider, text field, dialog button and `showModal` are the ones
`resolveSteamUiComponents` already resolves.

- `boolean`, `choice`, `range`, `text` and `secret` are Steam's toggle, dropdown, slider and text
  fields. A slider sends when it settles (`onChangeComplete`), not on every step, and text is sent
  when the field loses focus, only if it changed.
- A `secret`'s value is never published. The field starts empty and shows the row's `text` as its
  placeholder. An untouched field sends nothing; one typed into and emptied sends `""`, which clears
  the secret.
- `order` is the value field per value with Steam's small buttons to move one up or down, and sends
  the whole list.
- `action` is a dialog button that sends the row to `onAction`, and `note` is a read-only value.
- A row with `accent` set draws its description, which the host writes, in Steam's accent blue, the
  same colour the marked Quick Access rows use. Nothing is added beside the control.
- A `range` with `labels` is one of them by index: Steam's slider names each notch with a label, the
  bounds are the labels' count, no number is shown beside the track, and the index is sent.
- `color` is the value field showing the colour's swatch and text with a small Edit button, which
  opens Steam's modal of four sliders (hue, saturation, lightness, opacity) over the hex or hsl(a)
  the row holds. Save sends the colour once, as `hsla()`, the form CSSLoader's own picker writes;
  Cancel and B send nothing. `ColorAlpha = false` hides opacity and keeps the saved color opaque,
  for hardware RGB controls. On a client without a modal the row is a text field.
- A kind the renderer does not know is shown as its label with nothing that sends.
- A row with `confirm` asks first, in Steam's confirm modal, when the new value equals `when`.
  Cancelling sends nothing, so the row keeps what the host last published.
- What the user changed is kept as a draft, so a toggle does not flick back while its write is in
  flight and typed text stays while it is typed. A draft is shown only while its row still carries
  the value it was made against, so a publication that changes another row keeps it. `onChange`
  answers the write's request: a refusal drops that row's draft and shows the reason as the row's
  description until the row is changed again or published with another value, and an accepted
  write's draft gives way to the next `revision`. `useSteamSettingDrafts(react, revision)` is the
  same draft keeping for a host that draws rows with `renderSteamSettingRow` itself, as the Quick
  Access settings sections and the Extensions tab do.

`SteamBrightnessState` carries the observed or written `Percent` and a monotonic `Revision`. A
successful `setBrightness` response reports a dispatched write and returns the written state in its
payload. Use the same revision sequence for responses and publications. The gate holds state
separately from pending requests, rejects old revisions and suppresses observable-update feedback
into the setter. Failures keep the last known level and expose `lastError`; they never retry
automatically. `eng/check-service-gates.mjs` exercises focused-slider echoes, stale revisions,
overlapping requests, failures and reinstall against the emitted JavaScript without a live Steam
session.

Semantic slider completion also suppresses an unchanged observed value. Programmatic refresh and
command acknowledgments cannot become new user writes. `eng/check-power-profile.mjs` checks the
emitted echo hook with an inert React fixture.

`SteamPowerProfileRow` adds a dropdown on Performance through patch `steam-ui.power-profile`, kind
`powerProfile`, and command `setPowerProfile`. Payloads are exactly `{ target: "id" }`, validated
with `TryReadTarget`. `SteamPowerProfileState` carries any number of unique id/label options,
observed `Current`, `Available` and `StatusText`. Unknown current ids select nothing. Labels arrive
whole. Unavailable state with options stays visible but disabled; a state with no options hides the
row and records its status text in `renderOutcomes`. Selection is also disabled while its request is
pending, and a refused selection shows the host's reason as the row's description until the next
selection. The host owns validation, OS writes and persistence. Readback does not decide write
success or control availability. `SteamChoiceRowTests` covers serialization and dispatch,
`SteamSurfaceModuleTests` the module vocabulary; `eng/check-power-profile.mjs` checks the emitted
dropdown, rejected choices, malformed states and Performance placement with inert React/bridge
fixtures.

`SteamHybridCoreRow` adds a second dropdown on Performance through patch `steam-ui.hybrid-cores`,
kind `hybridCores`, and command `setHybridCores`. `SteamHybridCoreState` is the same shape as the
power-profile state and reuses `SteamPowerProfileOption`, because it is the same control: host-named
choices, the one observed, and a status line. The host owns what a choice means, whether the machine
supports any, and the OS write. An empty `Current` is the honest answer for a machine set to
something the host does not offer, and selects nothing. A machine without a choice publishes no
options, which hides the row like the power-profile dropdown.

`SteamCpuBoostRow` adds a third dropdown through patch `steam-ui.cpu-boost`, kind `cpuBoost`, and
command `setCpuBoost`. `SteamCpuBoostState` is the power-profile shape plus `Accent`, which the row
draws as the same marked description the other rows use. The host owns the modes (Handheld
Companion's five), the per-game resolution and the Windows write.

The three rows share one dropdown factory, which the boost row gives its own normalizer and
description. Each row still passes its glyph as a function around its own `icon()` call with a
string literal, because a factory taking the name as an argument would make the rows invisible to
the check that proves every glyph is placed exactly once and every placement names a drawn glyph.
The core row draws `cores`, its own glyph.

`SteamPowerPresetRow` publishes `SteamPowerPresetState`: preset options, observed label, independent
AC/battery assignment IDs, scope, unset label and status. `ISteamPowerPresetBackend` owns assignment
policy. Its patch `steam-ui.power-preset` and kind `powerPreset` accept only `setAcPowerPreset` and
`setBatteryPowerPreset`. Each payload has exactly one `target`: a target ID or null to clear the
local assignment. An option published with `Selectable = false` names a state the user cannot pick,
such as a saved assignment that matches no preset: it is listed only in a dropdown whose current
value it is, and the page never sends it. The row does not check selectability itself; the host's
backend refuses any id it does not offer. The power-profile, hybrid-core and CPU boost dropdowns
treat the flag the same way. Empty options hide the controls. The C# tests cover routing,
cancellation forwarding and payload refusals; emitted tests cover both source selectors, clearing,
disabled state and malformed publications.

The shared row host draws its rows in the kit's groups (`renderSteamUiGroup`), each a block with a
subtle fill and border under a heading that carries the section's glyph. The sections are the
host's: `SteamQuickAccessLayoutSurface` publishes `SteamQuickAccessLayout` under
`steam-ui.quick-access-layout`, and each `SteamQuickAccessSection` names its id, title, glyph,
whether it folds and the row kinds drawn in it. Rows keep the toolkit's order within a section, and
a kind no section names is not drawn while a layout is published. Without a layout each tab draws
its rows in one untitled group. Performance draws `Performance` after Steam's battery line, then the
host's settings sections, then `PerformanceEnd`. Quick Settings draws `QuickSettings` before Valve's
common controls; the device controls draw their two groups, the kinds `charging` and `lighting`,
after Valve's sections, in whichever section of `QuickSettings` or `QuickSettingsEnd` names each.
Valve's own sections between are wrapped in `steam-ui-kit-valve`, which gives their `PanelSection`s
the same block look and the kit's heading. Steam's own FPS counter rows are hidden only when the
layout sets `HideValveFpsRows`, for a host whose own overlay replaces them. What remains of Valve's
Performance tree once they are hidden is the battery line, wrapped in `steam-ui-kit-battery`, which
draws it at one line's height: the row is found as the element with three children whose middle one,
the percentage, is not empty (the section around it has three too, two of them empty), so no hashed
class is named. RGB brightness stays visible; an Edit color toggle reveals the zone and HSV
controls. If Valve's toggle component is unavailable, the color editor is omitted while charging and
brightness remain usable. A group with no registered row is omitted. A group whose host rows all
render nothing, such as Power limits and Controller without a device, stays mounted with the kit's
`hidden` class so its rows keep their subscriptions. Rows report drawing through `drew` and not
drawing through `note`, and a change queues one re-render of the panel roots. Valve's own rows
report nothing and count as drawn. Each control retains the existing bridge and patch ownership.

A section folds when the layout says so, and one with an empty title is a plain block with no
heading. A folded group's heading reports what its rows hold: each row leaves a line through
`summarize` as it renders (the chosen profile, `60 fps cap`, `17 W sustained · 25 W boost`,
`Limit 80%`), and the heading joins the lines of the section's kinds. Rows stay mounted while
folded, so the line stays current. Every section starts folded. Folds are one mechanism for every
Quick Access tab, `createSteamFolds` in `gate-helpers.ts`: the host publishes the open sections' ids
as `SteamPanelFoldsState` under `steam-ui.panel-folds`, a heading sends `setFolded {id, folded}`,
and the fold is shown at once and held until the host's next publication agrees with it, so a host
that keeps folds has the last word and one without the module leaves them to last the session.
`SteamPanelFoldsSurface` declares no patch: the tabs' own gates draw the folds. A Performance or
Quick Settings section is named by its layout id, an Extensions tab item as `extensions:<item>`, a
switch's settings as `extensions:<item>:<key>`; the host keeps the ids as given.

Rows and section headers carry a glyph. `icons.ts` holds the drawings — the toolkit's own, on a
24x24 grid, filled with `currentColor` and cut with `fill-rule="evenodd"`, because the client's
artwork is Valve's and cannot be vendored. `createIconRenderer(react)` builds them with Steam's
React and caches one element per name and size, and the control runtime exposes it as
`controlRuntime.icon(name, size = 20)`. A row passes the result as Field's `icon` prop, which
`SliderField`, `ToggleField` and `DropDownField` all forward; sliders also pass
`iconLocation: "front"`, because `SliderField` otherwise places the glyph beside the track rather
than the label. A section heading takes the 18px glyph its layout section names through the kit
group's `icon`. An unknown name renders no glyph rather than failing the row, so a mistyped name
costs an icon and nothing else.

Every glyph is used exactly once. A panel like this is scanned by shape before it is read, so a
glyph on a header that reappears on a row inside it, or on two rows that do different things, says
those controls are the same control. Adding a row means drawing a shape, not borrowing one, and
`eng/check-power-profile.mjs` fails the build on a repeat.

Valve's own rows take no props, so `withIcon` renders one and clones what it returned. Only the
overlay-level row qualifies: it returns Valve's slider wrapper, which spreads every unrecognized
prop into `SliderField` and on into `Field`. The per-game toggle returns a Fragment, which keeps no
prop but `key`; the reset row is a button rather than a field; the profile header already draws the
game's capsule art.

The profile a preset row reports, rather than sets, uses Valve's `LabelField` — resolved from the
Field module, whose `#Field_MoreInfo_Action` token occurs once in the whole client bundle — with the
scope and status as its description. Like the toggle, it is outside `createControlRuntime`'s guard:
a client where it cannot be resolved loses that one line and keeps the assignments.

The Performance surface's module also mounts Valve's profile header and per-game toggle, reset
button, overlay-level selector and manual refresh-rate row. Which of them show anything is decided
entirely by which fields the published `SteamPerformanceState` carries, because Valve's wrappers
read availability out of that state. Its state, delta and overlay-level types follow Valve's
protobuf field names; the two-layer hiding rule, the external-display twins, the 769 "no game" id
and the limits-and-settings pairing are documented on the types.

A command that opens a page answers `SteamUiCommandResult.Route(route)`, the `{ route }` the
Extensions tab, the game context menu and the navigation panel follow. A payload of one field is
read with `SteamUiPayload.TryReadOnlyString`, `TryReadOnlyOptionalString`, `TryReadOnlyChoice` or
`TryReadOnlyBoolean`.

Every gate's payload is read with `SteamUiPayload` (exact object shape, string kinds, ranges), and a
malformed one is refused with a fixed reason before the backend runs. Its readers cover a non-blank
string, a string that may be empty (`TryReadString`), one that may be null for "clear"
(`TryReadNullableString`), an array of strings (`TryReadStrings`), booleans and integers.
`SteamUiBridgePatch` installs the bridge; register it in the same manager as dependent surfaces, but
do not rely on call order: the manager applies the bridge before every other patch and removes it
after them. Its probe asks only for webpack and React (`steam-ui-bridge-v1:webpack+react`); every
Quick Access row probes the TDP availability, TDP component, profile projection and
performance-actions modules itself, so a Steam build that moves one of them takes the rows and
nothing else. A row's probe count name must be a JavaScript identifier. Patch ids are `steam-ui.*`,
the markers the live client carries are `__steamUi*`, and both are public constants: a consumer's
kill-switch policy names patches by them and a probe from a separate CDP call reads the markers
back.

Power-limit state additionally carries unified and canSelectMode. The optional mode toggle sends
setUnifiedMode with exactly one boolean unified property. Consumers own persistence and paired
hardware dispatch. Unified presentation hides the independent boost slider while showing both
observed values in the TDP description. Default state retains the existing split presentation.

### Host-owned plugin surfaces

`SteamExtensionsTabSurface` publishes `SteamExtensionsTabState` under `steam-ui.extensions-tab`.
Each plugin item carries `Id`, `Name`, `Version`, `Status`, optional `Detail`, its actions,
primitive settings and the configuration revision; the gate renders every item. A revision that is
not a non-negative safe integer refuses the item at the publication boundary, since the configure
command would reject every change it offered. Activation sends `activate {id}`. A setting sends
exact `configure {id,key,value,revision}` to `ISteamExtensionsTabBackend`; booleans, finite numbers
and text are the only values accepted.

Each item is drawn as Steam's own `PanelSection`, titled with the item's name, with its detail,
actions and settings in `PanelSectionRow` rows. An action is Steam's `DialogButton`, and a setting
is drawn by `renderSteamSettingRow`, the same code and the same Steam fields a host's settings page
uses: a boolean is the `ToggleField`, text with choices the dropdown, a bounded number the slider, a
number with choices the slider with the choices naming its notches and the index as its value, text,
an unbounded number and a secret the `TextField` (a secret's box starts empty), a colour the swatch
and Edit button of the settings renderer's `color` kind, and an order the value field with Steam's
small move buttons, sent as the comma-joined list. A setting's `description` is the line under its
label. A value typed into a box belongs to the revision it was typed against and is sent when the
box is left: a newer published revision and a refused save both drop it, so the box never shows or
resends a value the host has replaced or rejected. The panel adds no heading of its own; Steam
titles the tab, which draws its own `extensions` glyph.

A setting's `choices` are the values sent back; `choiceLabels`, in the same order, is what they are
shown as, so a host whose values are ids never finds a choice again by its label.

A setting with a `parent` names a boolean setting on the same item and is drawn indented under it,
only while that switch is on, as the user last set it or as the host published it; the way CSSLoader
shows a theme's patches only for an enabled theme. A parent that is not a switch on the item hides
the setting, since nothing could open it. A switch's settings fold under a small heading of their
own ("3 settings"), through the same fold surface as the sections, under
`extensions:<item>:<switch key>`.

Every item is headed by a focusable row carrying its name, its detail line and a caret
(`sectionOpen`, `sectionClosed`) instead of the section's own title, because the title Steam draws
cannot take focus and a controller has to be able to land on the fold; it is drawn as a title, not a
button, so a folded section reads as a heading. Its rows are drawn only while it is open, and it
starts folded: the fold is the shared fold surface's, under `extensions:<item>`. An item's actions
share one wrapping row, so two short ones sit side by side, and a dropdown setting goes under its
label (`layout: "below"`), as CSSLoader draws a patch in the panel.

Every one of those pieces is required, as on every surface here: a client missing one refuses the
tab and names what is missing, rather than drawing an imitation. The probe counts the Quick Access
module, the fields module, the focusable and the panel layout module, and verification requires
`nativeComponentsResolved`. The Quick Access memo claim retains its original member snapshot, and
both discovery and subsequent probes recognize that snapshot rather than rejecting the installed
wrapper.

An `activate` answer carrying a `route` opens that page, the same contract the game context menu
follows, and the only way a host can navigate from this tab. The panel is closed first through
`closeSteamSideMenus`: this tab renders inside the Quick Access flyout, so navigating with it open
leaves the page behind the panel, which on a controller is indistinguishable from a dead button. The
route itself goes through the shared `navigateSteamRoute` bound, so a host that answers with
something unusable navigates nowhere rather than handing it to Steam's router. An answer without a
route, and a refusal, both navigate nowhere and leave the panel open.

`SteamGameContextMenuSurface` publishes `SteamGameContextMenuState` under
`steam-ui.game-context-menu`. It renders every `Id`/`Label` command and accepts exactly
`activate {appId,id}`, where the app ID is a positive uint32 and the ID is a non-blank string.
`ISteamGameContextMenuBackend` receives the selected game's ID, not a later current-page lookup. The
gate shares the `steam-ui.jsx-runtime` resource and intercepts creation of the private menu class
before its first render. It does not scan the visible DOM. Removal releases both the named element
interceptor and the class's render claim.

`SteamPowerMenuSurface` publishes `SteamPowerMenuState` under `steam-ui.power-menu` and revives
Steam's own Switch to Desktop in the Big Picture power menu. Valve draws that entry only under
gamescope and answers it with SteamOS's session service, which does nothing on Windows. The gate
shares `steam-ui.jsx-runtime` and recognizes the menu root, a module-private observer, by its Sleep
or Shutdown child as the root passes through the element interceptor; `#Quit_Shutdown` occurs once
in the client. While `Visible` is true it appends one section at the end, where Valve places the
entry: Valve's separator and a destructive item of the plain type the menu rendered, labelled with
Steam's localized `#SwitchToDesktop`. Selecting it sends `switchToDesktop` to
`ISteamPowerMenuBackend`; the host owns the switch and decides when a desktop exists to return to.
Removal releases the named element interceptor. `eng/check-power-menu.mjs` covers the emitted gate.

Both gates give their claims back before they forget they hold them, so a release that throws is
reported and stays retriable rather than leaving owned work live behind an `absent` answer. The
custom-page host does the same for its router claim and both patched fibers.

`SteamExtensionsTabTests` and `SteamGameContextMenuTests` cover the typed contracts.
`eng/check-extension-surfaces.mjs` covers the emitted asset's repeated probe/reclaim, native
activation, the route follow and its panel-close ordering, first-menu insertion, draft
reconciliation, a section's header and its fold through the shared fold surface, a switch's settings
folding under it, nested settings following their switch, the labelled slider, a failed release that
stays retriable, and removal. Offline checks do not establish live visual parity with Decky or prove
compatibility with a different Steam build.

### Theme stylesheets

`SteamThemeStyleSurface` publishes `SteamThemeState` under `steam-ui.theme-styles`: the
`SteamThemeStyle` blocks in cascade order, each an `Id`, its `Css`, a `Hash` of that text and the
`Targets` it is for. The vocabulary of a target is CSSLoader's own (`b1bc683`,
`css_browserhook.py`): a whole-title regular expression, `~text~` for a substring of the window's
URL, or `!name` for a class on the document's root elements. There is no command; a block exists
because the host published it.

CSSLoader attaches a debugger session to each of Steam's page targets and appends one `<style>` per
block to that document's head. Every one of those windows is rendered from SharedJSContext, so the
gate reaches the same documents from the one context the toolkit already holds, with no connection
per window, gathering them from two places: every popup `g_PopupManager` lists, and every document a
React portal in SharedJSContext's mounted trees renders into. Both are needed. On the Windows client
of 2026-09-28 the manager held the Big Picture window and its context menus only; Quick Access, the
main menu and the toasts existed to SharedJSContext as portal containers alone. The probe reads the
popup manager by the name Valve publishes it under and whether the context has a mounted React root,
and accepts either; it captures no webpack runtime and names no module. Verification is `installed`
and `resolved`.

A title target is tried against the window's own name as well as its document title. On the Deck the
two are the same string; on Windows the Big Picture window is named `SP BPM_uid0` while its title is
the localized product name (`Big-Picture-Modus` on a German client) and its URL carries none of the
markers CSSLoader's table names, so a host names Big Picture by its window name. The gate's
`windows()` lists every window it found with its name, title, URL and node count, for a host's
diagnostics.

On every publication whose blocks, hashes, targets or order changed, and for each window Steam
announces through the popup manager's created callback, the gate brings each popup's head in step
(it never polls: walking Steam's React tree every two seconds slowed every image Big Picture loads):
the blocks whose targets name that document, in the published order, as
`<style id="steam-ui-theme-<id>" class="steam-ui-theme-style" data-steam-ui-hash>` nodes. A head
already holding that list, block for block and hash for hash, is left alone; anything else is
rebuilt, so order stays part of what a theme means. Only nodes carrying the gate's class are ever
removed, and CSSLoader's own `css-loader-style` nodes are never touched, so the two can run side by
side. A target whose pattern does not compile matches nothing. Removal takes every owned node out of
every window and stops looking, which leaves Steam's own styling exactly as it was.

The toolkit installs what it is given and reads none of it. Loading a theme's files, translating its
class names for the running client build, resolving its patches and ordering its blocks are a
host's. `SteamThemeStyleTests` covers the probe, the wire shape and the module's revision-stamped
publication; `eng/check-theme-styles.mjs` covers the emitted gate's targeting by title, URL and
class, a window reached only through a portal, the Big Picture window matched by its name under a
localized title, the published order, an unchanged block kept, a changed block rebuilt, a window
opened later styled on the next pass, a broken pattern matching nothing, and removal leaving no node
behind. Whether Steam still publishes `g_PopupManager` under that name, and whether a theme written
for a given client build still styles it, are live questions.

## 16. The client layer

`Client/` reads and drives the running client through `SteamClient.*` and Steam's own stores. A host
composes one `SteamClient` over its transport and passes it to every caller; its `Apps`,
`Collections`, `InstallFolders`, `Library`, `Downloads`, `CurrentPage`, `StartupMovie` and
`RunningApps` are the objects below. These are one-shot calls, not patches: nothing is installed in
the page and nothing has to be removed, except the one resident observer described below.
`SteamClient.EvaluateAsync` runs a host's own repository-owned expression over the same transport
and never throws.

Every write reports one of four `SteamClientWriteOutcome`s: `NotSent` (nothing reached Steam),
`Unknown` (sent, but the answer was lost or unreadable, so the change may have run), `Rejected`
(Steam refused or threw) and `Applied`. A caller never retries `Unknown` automatically; it reports
it once. Reads return `SteamReadResult<T>` with the `SteamUiDispatch` of the read, the value, and an
error; a failed read never stands in for an empty or absent value. Every refusal uses one reply
shape, `{ok:false, err, result}`, with `result` carrying Steam's EResult when it threw one instead
of a message. A script whose first step changed something keeps the evidence on a failure too: a
collection created before a later step failed still answers its id, and a startup-movie set-aside
that failed after its first write still answers the choice Steam held.

Library writes (apps, shortcuts, collections and install folders) share the client's one write lane,
one at a time.

| Type                    | What it does                                                                                                                                                                                                                                                                                                                                                                                       |
| ----------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `SteamApps`             | reads one app's details (`RegisterForAppDetails`), creates and deletes non-Steam shortcuts, writes a title's launch options or a shortcut's Target and arguments, and sets or clears custom artwork. `NormalizeAppId` converts a stored signed id; `IsShortcutAppId` splits the two kinds.                                                                                                         |
| `SteamInstallFolders`   | adds, removes and relabels library folders through the write lane. Selects every registration at a path, never the first, and `NormalizePath` is the C# twin of the script's own normalizer. Statuses split `NotSent` from `Unknown`, and an add whose stale-registration purge or label failed is `Partial`.                                                                                      |
| `SteamDownloadActivity` | one snapshot of the download queue, with `IsActive` as the live-verified activity rule.                                                                                                                                                                                                                                                                                                            |
| `SteamLibraryData`      | collections, games and shortcuts, and the store tags in use, each as a typed read. App ids leave the page unsigned (`>>>0`) everywhere, so a shortcut id is never negative.                                                                                                                                                                                                                        |
| `SteamCollections`      | a host-owned user collection kept in step: found by the id the host recorded, never by name, created with its first apps, changed by the apps the host adds and takes back so the user's own additions stay, and deleted when left empty if asked. Shares the client's write lane.                                                                                                                 |
| `SteamStartupMovie`     | sets Steam's own startup movie choice aside (`startup_movie_id`, `startup_movie_local_path`, `startup_movie_shuffle`, written through the settings store's own setter) so an override under `/uioverrides/movies` plays, answers what it held, and puts that back only while Steam still plays its default. Both wait up to 5 s, polling every 250 ms, for a settings store that is still loading. |
| `SteamCurrentPage`      | which game page is in view: the focused React fiber, then the largest wide library image, then the library route.                                                                                                                                                                                                                                                                                  |
| `SteamRunningAppsProbe` | which apps Steam is running, kept current by Steam's own lifetime notifications.                                                                                                                                                                                                                                                                                                                   |

### Creating, reading and deleting non-Steam shortcuts

Every change `SteamApps` makes goes through the client's write lane, one at a time: two changes in
flight against a client mutating its own library store is how that store gets corrupted, and a
shortcut added by one caller while another diffs the library would make the other misread which
entry it created.

`AddShortcutAsync` asks the client for a new entry and confirms which entry it is, so no caller
reproduces Steam's own derivation or its own diff. The id is confirmed by what the client returned
and by a before-and-after diff of the library, and the diff is the authority. `Confirmed` is true
only when the library gained exactly one shortcut and, when Steam returned an id, that one; an
unconfirmed id may name another entry or none, and its `Error` says which case it was. When Steam
returns no id, the one entry the library gained is adopted only when its Target (compared without
case) and name are the ones asked for; anything else is left untouched and the add is `Unknown`.
Once Steam may have the entry the call runs to its answer whatever the cancellation token says.
Steam persists the entry to `shortcuts.vdf` immediately, as it does for every other write here.

The fields are then set on the entry the library gained, deliberately a second time. `AddShortcut`'s
positional contract is not one this library has verified across client builds, while
`SetShortcutName`, `SetShortcutExe`, `SetShortcutStartDir` and `SetShortcutLaunchOptions` are the
calls every shortcut manager relies on. The name is the one that bites: a client can ignore the name
it is passed and call the entry after its executable. The fields are read back until they match or
two seconds pass, and `Mismatch` names any that Steam still holds differently. Each setter is
guarded by its own `typeof` check, as `AddShortcut` itself is: a missing export is reported as a
refusal, never assumed present.

`ListShortcutsAsync` reads every shortcut in the library with its Target, start directory and
arguments in one evaluation, in batches of 32 detail reads. It is all or nothing: a shortcut whose
details Steam did not return fails the read, and its value is null, never a list with holes or an
empty list standing in for a read that failed.

`SetShortcutLaunchAsync(appId, target, launchArguments)` leaves the start directory alone, for a
caller that wraps what a shortcut runs;
`SetShortcutLaunchAsync(appId, target, startDirectory, launchArguments)` writes all three, for a
caller that owns the whole command.

The id crosses as a decimal string, because a shortcut id occupies the top half of the unsigned
32-bit range and reads back negative as a JSON number. `ParseAddShortcut` refuses anything that is
not a shortcut id, including a store id: that reply would not describe the entry just created, and
callers key their own records on the value.

`RemoveShortcutAsync` throws for an id outside the shortcut range rather than asking Steam. A store
title has no shortcut entry to delete, deleting a library entry is not something the call can undo,
and the guard belongs before the client is reached.

### Running apps

The probe installs one resident observer in SharedJSContext under `window.__steamUiRunningApps`. It
seeds the running set from the app store (`display_status` 4) and follows
`SteamClient.GameSessions.RegisterForAppLifetimeNotifications`; focus is never used to infer what is
running. Every change to the set advances `SourceGeneration`, so a game that stops and starts again
between two reads still reads as a change. The observer is published on the page only after Steam
returned a releasable registration, so a registration that throws leaves nothing behind and the next
read installs again; one left by an earlier host is a working observer of the same shape and is
reused. The probe has one reader; disposing its lease removes the observer, and a cleanup Steam
refuses is logged once.

A transport the host closed (`Dispatch == Closed`) is reported as reachable with no apps rather than
as a failure, so a consumer's own non-Steam detection keeps working while Steam integration is
switched off.

### Additions to native Settings pages

`SteamNativeSettingsSurface` owns patch `steam-ui.native-settings` and gate `nativeSettings`.
`SteamNativeSettingsState.Pages` contains unique `SteamNativeSettingsPage` identities `display`,
`power`, `audio` and `controller`, each with existing `SteamSettingsSection` and `SteamSettingsRow`
descriptors. `Revision` advances after observed values or descriptors change. Empty sections and
pages are omitted. A malformed wire publication retracts all additions; the injected gate also
accepts a JSON null as empty state. A C# `read` callback returning null, however, sends no
publication and leaves the last state in place. Publish `Pages = []` to clear additions, or disable
the patch to remove the hooks. Supported rows are boolean, choice, range, text, color, action and
note. Device availability and profile/GPU scope belong to the host.

The only command is `set { key, value }`: exactly two properties, a nonblank key of at most 1,024
characters and a boolean, number or string value. Strings are bounded to 4,096 characters; null,
arrays, objects and extra fields are refused before `ISteamNativeSettingsBackend.SetAsync`. The
owner validates the row against its current capabilities, selections and operation generation. An
action sends true. Shared drafts display backend refusals under their row. Sliders send only on
completion; color modal sliders stage changes and Save sends once. Hardware RGB rows publish
`ColorAlpha = false` to hide opacity. Cancel and B send no color command.

The gate resolves the native descriptor factory by the four authored localization tokens
`#Settings_Page_Display`, `#Settings_Page_Power`, `#Settings_Page_Audio` and
`#Settings_Page_Controller`. The native Settings root's provider is unique on `#Settings_Title`,
`SettingsModal` and `SettingsTitleBar`; its function is selected by `#Settings_Title`, `show-icon`
and the string `Settings`. No module id or export name is named. The shared `useMemo` transform
copies the four native descriptors, retaining Steam's original label, route, glyph and content, and
appends one subscribed host slot to each. An empty slot returns null but stays mounted, so
Controller capability arrival or removal updates the existing slot without navigating or remounting
the page. Only the Power descriptor's battery condition is revealed, and only while it has host rows
and the Display, Audio and Controller descriptors confirm Steam's services are ready. No platform or
battery value is changed.

The shared JSX transform reaches future Settings mounts. Mounted root fibers are adopted without
adding hooks and retain a durable original marker for bridge replacement. Discovery includes
SharedJSContext and the documents of known `g_PopupManager.GetPopups()` handles, deduplicating roots
before one traversal with a total 60,000-node budget and examining at most 256 popup handles. Host
Power presence changes request a native ancestor render to update the filtered sidebar; other
capability and value publications update only subscribed host slots. Remove withdraws both shared
transforms, retracts sections and restores exactly the native root functions. `status.claimed`
requires both transforms; `claimsRemaining` reports either lingering claim for removal verification.
`ownedRoots`, `pages`, `renderedPages`, `lastOutcome` and `lastError` distinguish installation from
a page actually rendering. `ownedRoots = 0` alone is inconclusive: future JSX mounts are not
recorded until a root discovery pass.

Offline inspection of the installed Windows Steam bundle on 2026-10-06 found one descriptor factory
among 2,835 module factories. Big Picture's native page ordering contains all four pages; Power is
hidden when Steam does not report a battery. This establishes shipped source structure, not live
mounting, navigation, focus or device writes. `SteamNativeSettingsSurfaceTests` covers
serialization, primitive command bounds and unique probe facts; `eng/check-native-settings.mjs`
covers native content retention, popup-document adoption, duplicate-document handling, an empty
Controller slot gaining rows, state retraction, dynamic Power visibility and shared-claim
restoration. The opaque color path is covered by `eng/check-settings-fields.mjs`.

### Host settings sections in native Quick Access

`SteamSettingsQuickAccessRow` mounts `SteamSettingsQuickAccessState.Pages` in Performance using the
shared native settings fields and UI kit groups. Each page is one folding group titled by the page;
its sections are plain inner groups. The host owns row keys, revision, capability availability and
command validation through `ISteamSettingsQuickAccessBackend.SetAsync`. Its only command is `set`
with `{key,value}`; arrays, objects, blank keys and extra fields are refused. A null C# reading
withholds publication; publish an empty `Pages` list to clear previously rendered sections.

The four-argument `Module(enabled, read, backend, revision)` overload accepts a cheap revision
callback. Once the current document has that revision, the bridge skips `read` and serialization on
publication rounds raised by other surfaces. Change the revision for every state change, including
an empty `Pages` list. Document replacement still republishes the same current state. The existing
three-argument overload, or a null revision callback, retains publication without a revision
shortcut.

`SteamAudioFormatState` publishes independent `ChannelOptions`/`CurrentChannels` and
`FormatOptions`/`CurrentFormat` alongside Spatial choices. Both playback selectors send the offered
complete format id to `setFormat`; the host supplies supported combinations and preserves encoding
where possible. Quick Settings groups those fields under Audio, separately from Display.

The audio row's `setFormat` and `setSpatial` commands accept exactly one nonblank `target` string,
bounded to 256 characters. Format IDs may contain colons, as the Windows host's complete format
identity does; they are not controller target identifiers. The backend still checks the current
endpoint and its offered choices before a write.

### Truthful library reads

`SteamLibraryData.ReadGamesAsync` returns `SteamLibraryReadResult`: confirmed games and
`Error = null` on success, including a valid empty array; a transport, JavaScript or schema failure
has an error. It preserves shortcut identities and validates entries before parsing/sorting.
`ReadCollectionsAsync` and `ReadStoreTagsAsync` are typed the same way, so a caller keeps its last
good answer instead of caching a failure as an empty library.
