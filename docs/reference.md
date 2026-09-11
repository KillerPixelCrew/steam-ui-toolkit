# SteamUiToolkit reference

The contract of `SteamUiToolkit`: the transport that owns one CDP connection to Steam's Chromium
front-end, the probe/apply/verify/remove patch lifecycle, the in-page bridge, the module contract,
the ownership primitives, the extension host, the prelude build and the surfaces. The XML
documentation on each member is the authoritative wording; this document reads the library as a
whole, in the order a consumer meets it. How WSGM uses it (Steam discovery, gating the transport on
Big Picture, which surfaces it registers) is on the WSGM side in `docs/steam-cef-system.md`.

| Fact                | Value                                                                                                       |
| ------------------- | ----------------------------------------------------------------------------------------------------------- |
| Package             | `SteamUiToolkit` 0.1.0, pre-1.0 on purpose                                                                  |
| Target framework    | `net10.0-windows`                                                                                           |
| Licence             | MIT                                                                                                         |
| Documentation gate  | every public member is documented; an undocumented one fails the build                                      |
| CI                  | build, tests, `npm ci`, `npm run prelude:claims` against the emitted prelude                                |
| Consumer supplies   | an `ISteamUiLog`, a `SteamUiInjectedAsset`, its `ISteamUiModule`s, and the Steam install directory          |

## 1. The shape

### Steam surface observations

`SteamNativeSurfaceCommands.ReplayAsync` replays `QuickAccess` or `Home` through the existing
window handler. Supply the process/app identity and CEF generations from the target observation;
zero/zero identifies the main window. Overlay targets must match exactly one gamepad overlay.
Missing, ambiguous, desktop-overlay and stale-generation targets are refused without fallback.
The result means the handler was invoked, not that its surface opened; Steam keeps its native
debounce and availability policy. Consumers observe the resulting surface separately and must
not retry an uncertain dispatch. On 2026-09-09, the compiled main-window Quick Access command
was observed opening and closing live Big Picture QAM (0 to 2 to 0). No live game-overlay dispatch
was exercised.

`Keyboard` uses the same exact target and generation checks. It closes side menus, enables dismissal
on Enter and shows the window keyboard, using Steam's native `/keyboard` route for game overlays.
It returns true for an already visible keyboard without toggling it. Missing keyboard methods refuse
the request before mutation. The installed main-window methods and route were inspected on 2026-09-09;
actual keyboard interaction remains a field check.

`SteamUiToolkit.Surfaces.SteamSideMenuObserver` reads the known window/menu stores through an
existing subscribed `ISteamUiTransport`. It does not create a transport or control input ownership.
Snapshots include CEF generations, the main window and at most 32 overlay windows identified by
process/application id. Missing stores, invalid identities, malformed menus and stale generations
produce unknown data. Consumers must also invalidate previously held snapshots when transport
generations change.

Register `SteamOverlayActivationPatch` through the normal patch manager lifecycle. It owns one
`RegisterForOverlayActivated` subscription, replaces only a recognized older observer, and ignores
late callbacks after cleanup. Events are bounded to 32 identities; overflow or malformed events
make activation unknown until reattachment. No synthetic closed event is supplied at startup.
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

Every Steam-shaped fact (a literal module id, a store's field names, a localization token, a row's
placement) lives in a surface (§15), never in the machinery. The bridge's vocabulary is derived
from whichever modules the consumer registers. A consumer's own fragments call `registerGate`, and
its patches reach them through `window[namespace].gate(name)`, exactly as the shipped surfaces do.

## 2. Public surface

### Transport

| Type                                                                          | Role                                                                                                                                   |
| ----------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------- |
| `SteamCef` (static)                                                           | `EnsureRemoteDebuggingEnabled(steamDirectory, enabled)`, `JsString`, and the pure gates `IsAllowedDebuggerUrl` and `IsSteamPortOwner`.        |
| `SteamUiEndpoint`                                                             | One validated target: `BrowserId`, `TargetId`, `Role`, `SocketUri`, `Type`, `Title`, `Url`.                                            |
| `ISteamUiEndpointDiscovery`                                                   | `DiscoverAsync(role, ct)` returning an endpoint or null. Public so a consumer can test above it.                                       |
| `ISteamUiCdpWire`, `ISteamUiCdpWireFactory`                                   | The framed message channel and its connector; the seam for testing generations, correlation and the patch lifecycle on a fake wire. |
| `SteamUiTargetRole`                                                           | `SharedJsContext`, `MainWindow`.                                                                                                       |
| `SteamUiTransportHealth`                                                      | `Idle`, `Connecting`, `Ready`, `Unavailable`, `Incompatible`, `Retrying`, `Disposed`.                                                  |
| `SteamUiGenerations`                                                          | `Browser`, `Target`, `Session`, `Frame`, `ExecutionContext`, `Document`.                                                               |
| `SteamUiTransportSnapshot`, `SteamUiEvaluationResult`, `SteamUiNotification`  | Sanitized state, evaluation result, bounded CDP notification.                                                                          |
| `ISteamUiTransport`                                                           | `NotificationReceived`, `GenerationChanged`, `SubscribeAsync`, `EvaluateAsync`, `SetRuntimeBindingAsync`, `GetSnapshots`, `SetEnabled`. |
| `PersistentSteamUiTransport`                                                  | The production implementation.                                                                                                         |
| `SteamUiTransportSession` (static)                                            | The session-wide master switch and the attach point for one-shot evaluation; `CefEvalResult` is its never-throwing result.            |

### Other groups

| Group      | Types                                                                                                                                                                                                                                                                                                                                                        |
| ---------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Patches    | `ISteamUiPatch`, `SteamUiPatchBounds`, `SteamUiPatchProbeResult`, `SteamUiPatchOperationResult`, `SteamUiPatchContext`, `SteamUiPatchState`, `SteamUiPatchSnapshot`, `SteamUiPatchManager`, `SteamUiPatchEvaluation`                                                                                                                                       |
| Bridge     | `SteamUiBridgeHost`, `SteamUiBridgeRequest`, `SteamUiBridgeAuthorizer`, `SteamUiBridgeAuthorizationResult`, `SteamUiBridgeIdentity`, `SteamUiInjectedAsset`                                                                                                                                                                                                 |
| Modules    | `ISteamUiModule`, `SteamUiModule`, `SteamUiModuleSet`, `SteamUiStatePublication`, `SteamUiCommandHandler`, `SteamUiCommandDelegate`, `SteamUiCommandResult`, `SteamUiModuleRuntime`                                                                                                                                                                         |
| Extensions | `SteamUiExtensionHost` (static), `SteamUiExtension`, `SteamUiExtensionManifest`, `SteamUiExtensionRejection`                                                                                                                                                                                                                                                 |
| Logging    | `ISteamUiLog { Info, Warn, Change(key, message, warning) }`, static `SteamUiLog` with a discarding default                                                                                                                                                                                                                                                   |
| Surfaces   | `SteamAudioSurface`, `SteamNetworkSurface`, `SteamBluetoothSurface`, `SteamBrightnessSurface`, `SteamPerformanceSurface`, `SteamPowerLimitSurface`, `SteamFrameLimitRow`, `SteamVariableRefreshRow`, `SteamResolutionRow`, `SteamAutoTdpRow`, `SteamControllerTargetRow`, `SteamDeviceControlsRow`, `SteamNavigationPanelSurface`, `SteamPageSurface`, `SteamStorageSurface`, `SteamLibraryBadgeSurface`, `SteamHomeCarouselSurface`, each with a state record and `ISteam*Backend` (§15) |
| Patch helpers | `SteamUiBridgePatch`, `SteamGatePatch`, `SteamQuickAccessRowPatch`; readers `SteamUiPayload`, `SteamPerformanceDeltaReader`, `SteamOverlayLevelWire`; `SteamUiProbeJs`, `SteamUiText`, `SteamSettingPersistence`                                                                                                                                         |
| Assets     | `SteamUiAssets/Source/types.ts`, `bridge.ts`, `ownership.ts`, `rpc.ts`, `icons.ts`, `gates/*.ts`, `components.ts`, `epilogue.ts`; built by `eng/build-prelude.mjs`, checked by `eng/check-ownership-claims.mjs`                                                                                                                                           |

`SteamUiLog` is a settable static rather than a constructor parameter because there is one sink per
process. `Change` is the poll-loop primitive: a line is written once per transition of its key, and
suppressed repeats are counted rather than dropped. The TypeScript ships as source in the package so
a consumer can compile it together with its own fragments; `dist/steam-ui.js` is the complete asset
for a consumer with none.

## 3. Discovery and the port gate

### The opt-in flag

`SteamCef.EnsureRemoteDebuggingEnabled(steamDirectory, enabled)` creates an empty
`.cef-enable-remote-debugging` file in Steam's directory when it is missing and logs
`Steam CEF remote-debugging enabled (<path>).`. It writes nothing when the explicit configured switch is
off or the directory is null. It never deletes an existing flag: the file is shared with other
tools, and the library cannot know who created it. The flag takes effect on Steam's next cold start.
The transport's temporary readiness hold does not control this write.

### Port ownership

Before any HTTP probe, discovery verifies that TCP port 8080 is owned by Steam. `NativeTcp` reads
`iphlpapi!GetExtendedTcpTable` directly (address family 2, owner-PID listener table class 3,
24-byte rows with the address at offset 4, the port at 8, the PID at 20), retrying three times on
`ERROR_INSUFFICIENT_BUFFER`. netstat is not used because its state column is localized, so a literal
match on `LISTENING` fails closed on a non-English machine. An unreadable table returns null, not an
empty list.

`SteamCef.IsSteamPortOwner` sorts candidates loopback-first so a `127.0.0.1` squatter cannot hide
behind Steam's wildcard row, skips rows whose process has exited, accepts `steamwebhelper` and
`steam`, and reports one of four reasons:

| Reason                                                                     | Meaning                              |
| -------------------------------------------------------------------------- | ------------------------------------ |
| the TCP listener table was unavailable                                     | the owner could not be verified      |
| nothing is listening on port 8080                                          | Steam is not up, or the flag is absent |
| port 8080 is owned by `<name>` (pid n), not Steam                          | decisive refusal                     |
| n listener(s) on port 8080 could not be attributed to a running process    | stale rows only                      |

A refusal logs `Change("steam.ui.discovery", "Steam UI discovery for <role> refused: <reason>.")`
as a warning.

### HTTP discovery

`PersistentSteamUiTransport(requireMainWindow: true)` requires exactly one validated MainWindow in
the same target list before attaching to any role. Login popups, foreign websocket URLs and
ambiguous main windows do not satisfy this condition. This is an attachment gate; it does not
disconnect an established session when its window is minimized or hidden. The parameterless
constructor preserves the default discovery behavior. Hosts still own game-mode transition policy.

### Module resolution

`SteamUiModuleResolver.CreateExpression(scope)` embeds `module-resolver.ts`, kept valid JavaScript,
as a standalone expression. The same source is compiled into the bridge. The returned function
accepts a literal string id and refuses a missing factory before invoking webpack. `resolve(tokens)`
loads exports only for a unique source match; `count(tokens)` and `findUnique(tokens)` inspect
source without invoking factories. `findUnique` returns an id/source pair or null. Invalid
fingerprints, absent/ambiguous resolution and load failures throw diagnostic errors. Fingerprints
have 1 to 16 nonempty tokens of at most 512 characters; discovery accepts at most 32,768 factories.
A resolver does not repeat a factory call that threw through that resolver. It exposes no raw
registry or loader. This is not a sandbox for arbitrary page JavaScript, nor proof that a factory's
dependencies have initialized; hosts must enforce startup readiness as well.

Probes and gates share this resolver. The network surface instead reads Steam's published
`window.SystemNetworkStore`, so inspecting availability cannot construct the singleton early.
`eng/check-startup.mjs` exercises both the standalone source and emitted asset against the loader
failure shape that leaves empty exports cached after a missing-factory call. Native-component
installation catches discovery and dependency-resolution exceptions before installing the React
hook or registering a row. It returns `ok: false` and records the refusal in `status().lastError`.
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

| Role              | Match                                                                                                                                                   |
| ----------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `SharedJsContext` | `type == "page"`, title `SharedJSContext`, URL under `https://steamloopback.host/`. Headless: stores, webpack modules, React.                            |
| `MainWindow`      | `type == "page"`, URL starting `about:blank?` containing `createflags` and `minwidth`, and not containing `browserviewpopup` or `openerid`. The Big Picture window. |

The main window is matched by its creation URL, not its title, because the title is localized
("Big-Picture-Modus" on a German client) and the navigated document address matches nothing.

## 4. The CDP connection

`SteamUiWebSocketWireFactory` opens a `ClientWebSocket` with a 20 s keep-alive. The wire
accumulates text frames in 16 KiB chunks until end-of-message, treats a close frame as null,
refuses a non-text frame, and caps a response at 8 MiB. There is deliberately no cap on what is
sent: a 96 KiB expression cap once rejected the glyph stylesheet and the Steam Input page silently
kept Valve's artwork. Disposal sends a normal close with a 500 ms budget.

`SteamUiCdpConnection` correlates JSON-RPC by integer id with at most 32 outstanding requests, a
256-slot notification channel and a 1 MiB cap on notification parameters. `EvaluateAsync` sends
`Runtime.evaluate` with `awaitPromise`, `returnByValue` and `userGesture` all true. An
`exceptionDetails` becomes `InvalidDataException("Steam UI JavaScript exception: …")` bounded to
2048 characters; a string value is returned as-is, other kinds as raw JSON, no value as null. Each
request requires a timeout in `(0, 30 s]`. A send failure cancels the whole connection.

Inbound faults that end the connection: a non-object message, an invalid id, an `error` member, a
reply with neither `result` nor `error`, a notification without a method, oversized parameters, or
a full notification queue. An orphan response is only logged, three times at most. Teardown drains
the notification pump with a 1 s budget, fails every pending request with the failure or
`IOException("Steam UI CDP channel closed.")`, disposes the wire and invokes the closed callback.
A notification handler that throws is logged and does not poison the channel.

## 5. `PersistentSteamUiTransport`

One `TargetChannel` per role. `SubscribeAsync` increments the subscriber count and starts a
reconnect loop when enabled and none is live; releasing the last subscriber bumps the ownership
generation, cancels the loop, disposes the connection and sets `Idle`.

### Reconnection

The loop connects, marks `Retrying` with the failure on error, waits for the connection's
completion, then sleeps 1 s, 4 s, 16 s, 30 s (clamped). Connecting runs discovery (`Unavailable`
with `Steam UI <role> target is absent.` when it returns null), enables `Runtime`, `Page` and `DOM`
with 5 s each, and only then publishes the connection, so an in-place document replacement is
observable from the first moment a channel claims to be ready. Ownership is re-checked before
publishing and again after the domains are enabled; a connection that completes after its owner
left logs `Steam UI <role> connection completed after its owner left; discarding it.` and throws
`OperationCanceledException`.

### Generations

| Event                                                                     | Advances                                        |
| ------------------------------------------------------------------------- | ----------------------------------------------- |
| New browser id on connect                                                 | Browser, Target, Frame, ExecutionContext, Document |
| New target id on connect                                                  | Target, Frame, ExecutionContext, Document       |
| Every attachment                                                          | Session                                         |
| `Page.frameNavigated`                                                     | Frame, Document                                 |
| `Runtime.executionContextCreated`                                         | ExecutionContext                                |
| `Runtime.executionContextDestroyed`, `Runtime.executionContextsCleared`   | ExecutionContext, Document                      |
| `DOM.documentUpdated`                                                     | Document                                        |

`NotificationReceived` fires for every notification; `GenerationChanged` only when a generation
changed. Both go through bounded drop-oldest channels (256 and 64) and handler exceptions are
logged. A Steam restart is detected through nothing more than this: the socket closes, the loop
backs off, discovery refuses while the port is closed, and the reconnect brings a new browser id
that advances every generation, which invalidates every patch and the bridge.

### Evaluation

`EvaluateAsync(role, expression, timeout, ct)` validates the timeout before connecting, returns
`Unavailable("Steam CEF integration disabled in settings.")` when disabled, takes a temporary
subscription for the call, and maps failures:

| Caught                                                     | Result                                              | Health         |
| ---------------------------------------------------------- | --------------------------------------------------- | -------------- |
| caller cancellation                                        | `Unavailable("Steam UI evaluation was cancelled.")` | unchanged      |
| deadline                                                   | `Unavailable("Steam UI evaluation timed out.")`     | unchanged      |
| `InvalidDataException` (CDP error, JS exception, framing)  | `Reachable = true` with `Error`                     | `Incompatible` |
| anything else                                              | `Unavailable(message)`                              | `Retrying`     |

The `Reachable` distinction matters: Steam answered, so a caller must not diagnose a renamed API as
a closed client. A later success restores `Ready`.

`SetRuntimeBindingAsync` throws rather than returning: `InvalidOperationException` when disabled,
`IOException("Steam UI target is unavailable.")` without a connection. It issues
`Runtime.addBinding` or `Runtime.removeBinding`.

### The session statics

`SteamUiTransportSession.Attach(transport)` publishes one transport for one-shot callers and throws
`A Steam UI transport is already attached.` for a different instance, because two transports would
mean two connections with independent generations. `SetEnabled(bool)` is the master switch: false
bumps ownership, cancels reconnects, closes connections and retains subscriber intent; true restarts
reconnects for channels with subscribers. `EvaluateAsync` targets `SharedJsContext` and
`EvaluateOnVisibleWindowAsync` targets `MainWindow`. Both never throw and answer with
`CefEvalResult { Reachable, Value, Error }`, using one of:

| Error                                             |
| ------------------------------------------------- |
| `Steam CEF integration disabled in settings.`     |
| `Steam UI transport is not active.`               |
| `Steam CEF evaluation cancelled.`                 |
| `Timed out talking to Steam's debug port.`        |

## 6. Patch lifecycle

### Declaring a patch

| `ISteamUiPatch` member   | Contract                                                                                                                                                               |
| ------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Id`, `Version` (> 0)    | Stable identity; the log key is `steam.ui.patch.<id>`.                                                                                                                 |
| `TargetRole`             | Which target the phases evaluate on.                                                                                                                                   |
| `ResourceKey`            | Patches sharing a key serialize on one gate.                                                                                                                           |
| `Bounds`                 | `SteamUiPatchBounds(OperationTimeout ≤ 30 s, MaximumExpressionCharacters > 0, MaximumDiagnosticCharacters 1…65536)`; default 8 s, 96 KiB, 2048.                        |
| `ProbeAsync`             | Read-only. Returns `SteamUiPatchProbeResult(TargetPresent, Compatible, Unique, Fingerprint, Diagnostic)`. The fingerprint is a semantic identity, never a module id alone. |
| `ApplyAsync`             | Touches only resources the patch owns.                                                                                                                                 |
| `VerifyAsync`            | Proves the applied work is functional.                                                                                                                                 |
| `RemoveAsync`            | Removes and verifies removal of only the patch's own work.                                                                                                             |

`SteamUiPatchContext.EvaluateAsync` enforces the expression bound (`Patch expression exceeded its
declared bound.`) and passes the operation timeout to the transport. Registration refuses missing
identity, version, resource or bounds and duplicate ids. Patches are kept sorted by id; there are
no declared dependencies between patches, and mutual exclusion comes from the resource key.

### States

`Unknown`, `AbsentTarget`, `Incompatible`, `Applying`, `Applied`, `Verified`, `Degraded`,
`Disabled`, `RemoveFailed`, `Retrying`.

### One synchronization pass, per patch

1. Kill switch off (global or per patch): remove unless already `Disabled`; end in `Disabled` or
   `RemoveFailed` (`Patch removal timed out.` on timeout); release the transport subscription.
2. Take a subscription lazily and read the snapshot. If the generations differ from those the patch
   was applied under, bump the patch's epoch and move `Applying`/`Applied`/`Verified` to `Retrying`
   with `Steam UI generation changed; reapply required.`. This catches a snapshot observed before
   its event arrives.
3. Probe under its own phase timeout. Exception: `Degraded`. Target absent: `AbsentTarget`. Not
   compatible, not unique or no fingerprint: if the patch was applied, retract it (`Incompatible`,
   or `RemoveFailed` when removal also failed); otherwise `Incompatible`.
4. A `Verified` patch whose fingerprint is unchanged only re-verifies; success keeps `Verified`
   without reapplying.
5. `Applying`: apply; failure is `Degraded` with the diagnostic.
6. `Applied`: verify; success is `Verified`.
7. Verify failure: `Steam UI patch <id> applied but did not verify; removing it: …`, then remove.
   An applied-but-unverified mutation is never left in the client.
8. A phase timeout is `Retrying` with `Patch operation timed out.`; any other exception is
   `Degraded`.

Every phase gets its own cancellation source: one budget across probe, apply and verify once
cancelled an in-budget apply with nothing wrong.

### Generation events and kill switches

`OnGenerationChanged` compares the published snapshot's generations against the event for patches
on that role; on a difference it bumps the epoch, cancels the active phase and moves live states to
`Retrying`. Every state write goes through an epoch check so a stale phase cannot publish a result
for a replaced document.

`SetGlobalEnabled` and `SetPatchEnabled` flip the flag, cancel an active phase when disabling, and
queue a synchronization on the thread pool so a settings change flipping several switches does not
run a pass inline per switch. The `…Async` variants await the pass; use them when shutdown, a
settings confirmation or an emergency kill switch must know cleanup finished. `DisposeAsync` turns
the global switch off and removes every patch under its own timeout.

Every transition logs
`Change("steam.ui.patch.<id>", "Steam UI patch <id> v<n>: <State> — <failure>")`, as a warning
unless the state is `Applying`, `Applied`, `Verified` or `Disabled`.

### `SteamUiPatchEvaluation`

`EvaluateOutcomeAsync` parses the page's `JSON.stringify({ok, error})`: unreachable is a failure
with the transport's error or the fallback; `ok: true` succeeds; otherwise the page's error, the
bounded raw value, or the fallback. `IsSuccessful(value)` treats an unparseable value as failure,
never as an optimistic success; the overload with flag names additionally requires each named
boolean true. `IsOne` demands exactly one structural match, because a second match means the Steam
build has two candidate components and the patch cannot tell which it would modify.

## 7. Modules and the runtime

A module here is a surface: the patches that install it, the state it publishes, and the commands
it answers. It is not a Steam webpack module.

| Type                                                              | Contract                                                                                                                                                                                                                                                                                                    |
| ----------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `SteamUiStatePublication(PatchId, Read, Enabled)`                 | `Read` returning null publishes nothing that round, keeping "momentarily unavailable" distinct from zero.                                                                                                                                                                                                  |
| `SteamUiCommandHandler(PatchId, Command, Handle)`                 | `Handle` returns `SteamUiCommandResult(Succeeded, Error, Payload)`; `Error` is never null on failure. `Refused` carries `The requested semantic service is not active.`                                                                                                                                     |
| `SteamUiModuleSet(modules)`                                       | Flattens once. Throws on a duplicate module id, a patch registered by two modules (naming the second), or a `(patchId, command)` answered twice. `AllowedCommands` maps every patch id to its commands; a publication-only patch appears with an empty list because subscriptions are guarded by the same vocabulary. |
| `SteamUiModuleRuntime(bridge, modules, commandsEnabled, publishEnabled)` | Runs both directions.                                                                                                                                                                                                                                                                                |

Runtime behaviour: a `cancel` request cancels the in-flight source by sequence; duplicate sequences
are ignored; commands are `Refused` when disabled or unhandled; a handler exception becomes a
failure with its message. Every failure logs
`Change("steam.ui.request.<patch>.<command>", "Steam UI request <patch>/<command> did nothing: <error>")`;
an undelivered response logs `steam.ui.response.<patch>.<command>`. Publications are coalesced into
one pending round, skipped while publishing is disabled or the bridge is not ready, and one failing
publication does not block the next (`steam.ui.publication.<id>`). `CancelAllInflight` is the
generation-replacement path.

### Webpack modules

The toolkit touches webpack in exactly two places: `getWebpackRuntime(scope)` captures the runtime
by pushing an empty chunk and never evaluates an unknown module, and `rpc.ts` names the one literal
module it needs. The same constraint binds consumers: never iterate the module registry
constructing exports; name literal ids and inspect factory or prototype source. Enumerating and
calling everything once restarted a machine and signed Steam out.

## 8. The bridge

### Identity and configuration

`SteamUiBridgeIdentity.Namespace = "__steamUi_v1_28d7c54a"`,
`BindingName = "__steamUiBridge_v1_7b24d11c"`. `SteamUiBridgeHost.SchemaVersion = 1`,
`MaximumPayloadCharacters = 16 KiB`, `OperationTimeout = 5 s`, a 64-slot request channel.

`BootstrapAsync` installs the binding, reads the snapshot after the install so a generation raised
by it is the baseline, substitutes the configuration JSON for the literal
`__STEAM_UI_CONFIGURATION_JSON__` in the asset, evaluates it, and is ready only when the reply is
`ok: true`, the reply's generations equal the snapshot's, and no generation epoch changed meanwhile.

Configuration fields: `version`, `namespace`, `binding`, `assetHash`, `contextGeneration`,
`documentGeneration`, `maximumPending` (32), `timeoutMilliseconds` (5000), `allowed` (patch id to
commands). `assetHash` is load-bearing: neither context nor document generation changes on a
consumer update, so without it a new build kept running the previous build's script until Steam
restarted.

### The injected side (`bridge.ts`)

| Member                                                  | Behaviour                                                                                                                                                                                                                                                                                                                                                                       |
| ------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| reuse check                                             | If `window[namespace]` exists with equal `version`, `assetHash`, `contextGeneration`, `documentGeneration` and a `gate` function, return `{ok:true, reused:true}` before any fragment runs. Otherwise a prior bridge is unwound: its known gates get `remove()`, then `dispose("generation replaced")`.                                                                          |
| `request(patchId, command, payload, actionGeneration?)` | Rejects `command not allowlisted` and `bridge busy` (≥ `maximumPending`). Allocates a positive action generation when the caller passes none or zero, because the host rejects zero and several gates once passed exactly that. Sends the envelope through `window[binding](JSON.stringify(...))`; on timeout sends a `cancel` envelope and rejects `Steam UI bridge request timed out`. |
| `subscribe(patchId, callback)`                          | Throws `subscription not allowlisted` unless the patch id is a key of `allowed`; replays the latest state.                                                                                                                                                                                                                                                                      |
| `deliver(envelope)`                                     | Accepts only `response` and `state` envelopes whose version and generations match; a response resolves or rejects the pending promise by sequence and patch/command; a state is stored and fanned out.                                                                                                                                                                          |
| `dispose(reason)`                                       | Calls `remove?.()` then `dispose?.()` on every registered gate, rejects pending requests, clears maps.                                                                                                                                                                                                                                                                          |
| `gate(name)`                                            | Returns null for an unknown gate so a failed fragment reads as "gate absent".                                                                                                                                                                                                                                                                                                   |
| `registerGate(name, gate)`                              | What consumer fragments call.                                                                                                                                                                                                                                                                                                                                                   |

Subscriber exceptions are isolated during cached replay as well as later delivery. A throwing
callback cannot prevent `subscribe` from returning its cleanup handle or stop another subscriber.
The emitted `eng/check-startup.mjs` fixtures exercise both paths across the built-in module IDs.

`SteamPowerLimitSurface` publishes independent sustained (PL1) and boost (PL2) ranges with
availability, minimum/maximum/step watts, observed watts, progress and status. Its `powerLimit`
control uses Valve slider primitives, following hardware readback after profile or external edits.
Only a completed slider edit sends `setPrimaryLimit` or `setBoostLimit` with exactly `{watts}`
(an integer from 1 through 200). The host validates the current descriptor and performs readback.
The sliders share a pending-command guard, show refusals and never retry automatically. Mounting,
publication and profile changes issue no writes. The former SteamOS Manager overlay and settings
watcher are removed; Steam's persisted TDP setting has no authority over this surface.
`eng/check-power-profile.mjs` exercises profile updates, drag echoes, independent commands,
unavailable readback, bounds, pending commands and failures using the emitted controls.

The bridge object is frozen and defined on `window` as non-enumerable, non-writable, configurable.
`installResult` is assigned, not returned; `epilogue.ts` returns it after every fragment ran,
because a return in `bridge.ts` once published a bridge with an empty registry while the bootstrap
patch still verified.

### Host-side authorization

`SteamUiBridgeAuthorizer.Authorize` rejects, in order:

| Rejection                                   | Rule                                                        |
| ------------------------------------------- | ----------------------------------------------------------- |
| `schema version mismatch`                   | `version` must equal `SchemaVersion`                        |
| `message type is not allowlisted`           | only `request` and `cancel`                                 |
| `patch command is not allowlisted`          | `(patchId, command)` must be in `allowed`                   |
| `sequence or action generation is invalid`  | both must be positive                                       |
| `payload exceeded its limit`                | 16 KiB                                                      |
| `stale bridge generation`                   | generations must match the current snapshot                 |
| `cancel references an unknown request`      | a `cancel` sequence above the last accepted one             |
| `request sequence was replayed`             | sequences are monotonic                                     |
| `action generation was replayed`            | action generations are monotonic                            |

The authorizer resets on every generation change. Only `Runtime.bindingCalled` notifications from
`SharedJsContext` with matching generations, the binding name and a string payload of at most
16 KiB are accepted, deserialized with a camelCase source-generated context (PascalCase once
rejected every command). Rejections log `Change("steam.ui.bridge.rejected", …)` with the first 200
characters of the payload.

`RespondAsync` and `PublishStateAsync` require readiness and matching generations, then evaluate
`b.deliver(JSON.parse("..."))` and accept only a structured `{ok:true}`. Response envelopes carry
`version`, `type: "response"`, `patchId`, `command`, `sequence`, both generations, `ok`, `payload`,
`error` (truncated to 1024); state envelopes carry `type: "state"`, `patchId`, both generations and
`payload`. A `SharedJsContext` generation change drops readiness and resets the authorizer.
`RemoveAsync` removes the binding, evaluates `b.dispose('Steam UI removed'); delete window[k]`, and
logs any incomplete step. Disposal waits 2 s for an in-progress bootstrap and 1 s for the request
pump.

## 9. Ownership (`ownership.ts`)

The three ways to change the client, and what removal owes:

| API                   | Primitives                                                                | Removal owes                                       |
| --------------------- | ------------------------------------------------------------------------- | -------------------------------------------------- |
| Feed a data construct | `supplyNamespace`, `withdrawNamespace`                                    | delete it                                          |
| Answer an RPC         | `claimMember`, `releaseMember`, `memberClaimed`, plus `rpc.ts`            | restore what was displaced                         |
| Reveal what is gated  | `claimValue`, `releaseValue`, `claimAccessor`, `releaseAccessor`          | restore the original; never the platform constant  |

Three invariants: a claim must recognise its own work, must hand back exactly what was there, and
both facts must survive a separate CDP evaluation, which is why markers are string-keyed
non-enumerable fields rather than Symbols. Every claim writes `keys.marker = true` and
`keys.original = { kind: "steam-ui-property-snapshot-v1", hadOwn, descriptor, value }`; the caller
supplies the key names so a renamed key cannot orphan a marker a previous build left.

| Primitive                                            | Behaviour                                                                                                                                                                                                                                                                                                                       |
| ---------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `claimValue(host, field, keys, next, absent)`        | Refuses `claim target unavailable` when the field is not in the host and `already set by the client` when the unmarked value already equals `next` (restoring later would hand back an invented value). Writes through an accessor's setter and reads back; throws `claim target is a read-only accessor`; rolls back field, marker and original on any failure. |
| `releaseValue`                                       | Restores through the setter, by redefining the saved descriptor, or by deleting so an inherited value shows through, then deletes both keys. Releasing an unclaimed field succeeds.                                                                                                                                             |
| `claimMember(host, member, keys, replacement(original))` | Puts the marker on the replacement, which may be an object or a function; a `typeof === "object"` check once let an overlaid method outlive its own removal. A reclaim passes the underlying original to the factory so wrappers never stack.                                                                                |
| `supplyNamespace(host, name, marker, factory)`       | Refuses a real backend (`<name> already exists`), reclaims its own orphan (a namespace on `SteamClient` outlives the bridge that dies with the context), and defines non-writable rather than assigning, because assignment throws against a previous bridge's definition under strict mode. `withdrawNamespace` deletes only a marked one. |
| `claimAccessor(host, property, keys, getter)`        | Refuses a non-configurable property and marks the replacement getter with the whole original descriptor; `releaseAccessor` redefines it.                                                                                                                                                                                       |

The accessor rule in `claimValue` comes from a MobX crash in the Quick Access Menu.

`eng/check-ownership-claims.mjs` slices the ownership primitives out of the emitted prelude,
evaluates them with `new Function`, and runs more than thirty claim, reclaim, release, stand-aside
and lost-original scenarios. It runs in CI; reintroducing the function-type defect fails four
checks.

`rpc.ts` supplies `transportReply(body)` (the `{BSuccess, BFailed, GetEResult: 1, Body().toObject()}`
shape a Steam transport RPC answer takes) and `invalidateQuery(queryKey)`, which resolves the one
literal query-client module and calls `invalidateQueries`, swallowing every failure.

## 10. The extension host

An extension is a module discovered from a package instead of compiled in: same patch lifecycle,
same ownership rules, same clean removal. It is not a sandbox: injected script has the same reach
as the consumer's gates, and the checks are about identity and collision only. The host reads and
validates; it loads no assembly and executes nothing. The returned script is text until the
consumer builds it into the injected asset.

| Manifest field (`extension.steam-ui.json`) | Rule                                                                                                          |
| ------------------------------------------ | ------------------------------------------------------------------------------------------------------------- |
| `id`                                       | 1–96 characters of `[a-z0-9._-]`, no leading or trailing separator                                            |
| `name`, `version`                          | free text                                                                                                     |
| `apiVersion`                               | must equal `SteamUiExtensionHost.ApiVersion` (1) exactly                                                      |
| `script`                                   | relative path that stays inside the package, exists, is at most 256 KiB of strict UTF-8 measured before reading |
| `patches`                                  | safe, distinct ids each prefixed `<id>.`                                                                      |

`Discover(root)` returns every package in deterministic order, loaded or refused with a
`SteamUiExtensionRejection` (`UnreadableManifest`, `InvalidManifest`, `ApiVersionMismatch`,
`UnreadableScript`, `UnscopedPatch`, `Conflict`) and a detail, named by directory when the manifest
could not be read, so "my extension does nothing" always has a reason. Conflicts are resolved on the
complete claim set, so a rejected extension does not reserve claims that would make a later valid
one look conflicting. Log keys: `steam.ui.extensions.root`, `steam.ui.extension.<id>`.

## 11. The prelude build and the composition contract

`eng/build-prelude.mjs` concatenates `types.ts`, `bridge.ts`, `ownership.ts`, `rpc.ts`, `icons.ts`,
appends the IIFE close only for the compile, type-checks with TypeScript 7 under a strict, ES2022,
type-stripping-only configuration, and emits `dist/prelude.js` from the `// @steam-ui-bundle-start`
marker onward with the IIFE left open. `types.ts` sits above the marker so it types the compile and
ships nothing. Compiling the prelude alone is what proves it stands on its own: it stopped compiling
the moment the bridge still named a consumer's gates.

A consumer composes one script:

```text
(() => { "use strict"; let installResult; const config = __STEAM_UI_CONFIGURATION_JSON__;
  …bridge.ts…            reuse check, request/subscribe/deliver/dispose, registerGate, window[ns]
  …ownership.ts, rpc.ts, icons.ts…
  …consumer fragments…   hoisted function create…() + top-level registerGate(name, create…())
  …epilogue.ts…          return installResult;
})();
```

The host replaces the placeholder with the configuration, evaluates the whole thing in one
`Runtime.evaluate`, and passes the SHA-256 of the source as `assetHash`.

## 12. Rules

- Every patch carries an ownership marker and accepts "already ours"; a probe that requires the
  pre-patch condition its own apply invalidates tears itself down on every poll.
- Removal restores exactly what was displaced, read from the object rather than from the closure
  that installed it.
- Reveal the surface, never the platform: overriding a store getter is allowed; setting Steam's
  "is this SteamOS" constant is not.
- Never iterate the webpack module registry constructing exports.
- Every refusal is logged with its reason, because the injected side has nowhere to put an error.
- Every patch fails open to Valve behaviour, and a successful patch must not invalidate its own next
  probe.

| Gate                     | Example                          | Allowed response           |
| ------------------------ | -------------------------------- | -------------------------- |
| Absent JS namespace      | `SteamClient.System.Perf`        | supply it                  |
| Absent RPC response      | a manager's `GetState`           | supply it                  |
| RPC stub with no backend | a service whose methods refuse   | replace the stub's methods |
| Deck-only store getter   | `networkManagementAvailable`     | override that one getter   |
| Global platform constant | `TS.IS_STEAMOS`                  | never                      |

## 13. Constants

| Constant                                                          | Value                                                     |
| ----------------------------------------------------------------- | --------------------------------------------------------- |
| Debug port, flag file                                             | 8080, `.cef-enable-remote-debugging`                      |
| Accepted port owners                                              | `steamwebhelper`, `steam`                                 |
| Discovery timeout, response cap                                   | 5 s, 1 MiB                                                |
| WebSocket keep-alive, receive chunk, max response, close budget   | 20 s, 16 KiB, 8 MiB, 500 ms                               |
| Outstanding requests, notification queue, notification params     | 32, 256, 1 MiB                                            |
| Per-request timeout bound                                         | (0, 30 s]                                                 |
| Diagnostic bounds                                                 | 2048 characters                                           |
| Reconnect backoff                                                 | 1, 4, 16, 30 s                                            |
| Domain enable timeout                                             | 5 s each                                                  |
| Transport event channels                                          | 256 notifications, 64 generations, drop oldest            |
| Patch bounds default                                              | 8 s, 96 KiB, 2048                                         |
| Fingerprint bound                                                 | 512                                                       |
| Bridge schema, payload cap, operation timeout, request channel    | 1, 16 KiB, 5 s, 64                                        |
| Injected `maximumPending`, `timeoutMilliseconds`                  | 32, 5000                                                  |
| Bridge namespace, binding                                         | `__steamUi_v1_28d7c54a`, `__steamUiBridge_v1_7b24d11c`    |
| Configuration placeholder, bundle marker                          | `__STEAM_UI_CONFIGURATION_JSON__`, `// @steam-ui-bundle-start` |
| Property snapshot kind                                            | `steam-ui-property-snapshot-v1`                           |
| Extension API version, script cap, identifier                     | 1, 256 KiB, ≤ 96 of `[a-z0-9._-]`                         |

## 14. Tests

| Suite                                              | Locks                                                                                                                                                                                    |
| -------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `TransportTests`                                   | CDP connection (orphan ids, malformed frames, cancellation, slow and throwing handlers); persistent transport (domains before publication, generation advances, one-shot leases, discarded late connections, master switch, health restoration, backoff, invalid deadlines) |
| `SteamUiPatchManagerTests`                         | kill switches, retraction of an incompatible or unverified patch, re-verification without reapplying, generation epoch guards                                                             |
| `SteamUiBridgeHostTests`, `SteamUiBridgeWireTests` | replay, malformed and oversized notifications, generation replacement, structured acknowledgements, disposal, the real camelCase envelope captured from a live client                     |
| `SteamUiExtensionHostTests`                        | every rejection reason and conflict rule                                                                                                                                                 |
| `SteamUiModuleTests`, `SteamUiModuleRuntimeTests`  | module set rules, publication isolation                                                                                                                                                  |
| `SteamUiTargetMatchingTests`                       | the two role matchers against real URLs                                                                                                                                                  |
| `NativeTcpTests`                                   | the table decoder, the URL gate, the four port-owner reasons                                                                                                                             |
| `SteamSurfaceModuleTests`                          | each surface's `Commands` against its module's vocabulary, each refusal reason against its payload                                                                                       |
| `SteamNavigationPanelTests`                        | the panel probe's separate structural facts, selection by what an export draws rather than by its minified name, already-claimed compatibility, the published wire shape                 |
| `eng/check-navigation-panel.mjs`                   | the emitted gate against an inert React fixture: descent to the panel root, anchoring by route and by descriptor key, orphan reporting, hiding before insertion, activation, exact restoration, reinstall |
| `SteamPageTests`, `eng/check-pages.mjs`            | the page probe's separate facts and its rendered-tree search; the emitted gate's route-list discovery by content, an addition losing to Steam's own route and an override winning, path validation, exact restoration, reinstall |
| `SteamStorageTests`, `eng/check-storage.mjs`       | the storage probe's service and transport facts and every action having a command; the emitted gate's availability answer, Steam's own state field names, action forwarding, unrelated service traffic passing through with its arguments and receiver, and restoration putting Valve's method back |
| `SteamHomeCarouselTests`, `eng/check-home-carousel.mjs` | the Home probe's separate facts, finding Home by content rather than name, already-claimed compatibility, the published wire shape, the exact report payload; the emitted gate finding Home through the route list, replacing `games` for the carousel and the background, clearing the whole-list overscan, the documented order, disconnected games leaving, uninstalled games greyed, no rebuild or report without a change, the fallback to Steam's list, bounded publications, a mounted wrapper passing through after removal, exact restoration, reinstall |
| `SteamLibraryBadgeTests`, `eng/check-library-badge.mjs` | the badge probe's separate structural facts, selection of the tile and the badge by what they are rather than by name, the published wire shape, the exact layout payload; the emitted gate placing the badge left of Valve's in one row, naming the library or the internal label, green for installed and grey otherwise, no badge for a game installed nowhere, an anchorless tile left untouched, Big Art reported once per change, exact restoration, reinstall |

## 15. Surfaces

A surface is one Valve feature the Windows client ships inert, revived end to end: the injected
gate that supplies or reveals it, the C# patch that probes, applies, verifies and removes it, the
typed state a consumer feeds, and the backend interface a consumer implements. Every literal module
id, store field name, localization token and row placement lives here, in `Surfaces/` and
`SteamUiAssets/Source/gates/` plus `components.ts`, so a consumer never reads the client's bundle.

Each surface class has the same four members:

| Member                               | Meaning                                                                                       |
| ------------------------------------ | --------------------------------------------------------------------------------------------- |
| `PatchId`                            | The id its state is published under and its commands are addressed to.                        |
| `Commands`                           | The exact command vocabulary its injected side sends; what the module puts on the bridge.     |
| `Patch` (rows: also `*Row` patches)  | The `ISteamUiPatch`(es) that install it.                                                      |
| `Module(enabled, read, backend, id)` | One `ISteamUiModule` from a publication gate, a state reading and a backend.                  |

`read` returns the state or null; null publishes nothing that round, which keeps "momentarily
unavailable" distinct from a zero. `Serialize(state)` on each surface emits the exact wire payload,
for fixtures and diagnostics.

| Surface                    | Valve feature                                 | Gate kind                                                             | State                        | Backend answers                                                                            |
| -------------------------- | --------------------------------------------- | --------------------------------------------------------------------- | ---------------------------- | ------------------------------------------------------------------------------------------ |
| `SteamAudioSurface`        | audio page and Quick Settings audio           | supplies `SteamClient.System.Audio`, feeds the running store          | `SteamAudioState`            | default device, volume                                                                     |
| `SteamNetworkSurface`      | Internet page and header Wi-Fi indicator      | overrides `networkManagementAvailable`, feeds the network store       | `SteamNetworkState`          | scan start/stop                                                                            |
| `SteamBluetoothSurface`    | Bluetooth page and panel                      | replaces the service stub's methods, invalidates the query            | `SteamBluetoothState`        | discovery, pair, connect, disconnect, forget; trusted and wake-allowed accepted by default |
| `SteamBrightnessSurface`   | brightness slider                             | reveals the flag, claims `SetBrightness`, feeds the observable        | `SteamBrightnessState`       | set brightness                                                                             |
| `SteamPerformanceSurface`  | Performance tab and its Valve rows            | supplies `SteamClient.System.Perf`, writes the store, decodes deltas  | `SteamPerformanceState`      | apply a `SteamPerformanceDelta`                                                            |
| `SteamPowerLimitSurface` | Sustained and boost power sliders | Valve field primitives driven by hardware readback | `SteamPowerLimitState` | set PL1 or PL2 independently |
| `SteamFrameLimitRow`       | unified frame-limit row                       | row on Valve's slider and toggle                                      | `SteamFrameLimitState`       | frame cap, refresh rate                                                                    |
| `SteamVariableRefreshRow`  | VRR switch                                    | row on Valve's toggle                                                 | `SteamVariableRefreshState`  | VRR on/off                                                                                 |
| `SteamResolutionRow`       | resolution dropdown (Quick Settings)          | row on Valve's dropdown                                               | `SteamResolutionState`       | apply a mode                                                                               |
| `SteamAutoTdpRow`          | automatic power-limit switch                  | row on Valve's toggle                                                 | `SteamAutoTdpState`          | setting on/off                                                                             |
| `SteamControllerTargetRow` | controller-target dropdown                    | row on Valve's dropdown                                               | `SteamControllerTargetState` | choose a target                                                                            |
| `SteamDeviceControlsRow`   | charge limit, lighting brightness and colour  | rows on Valve's slider and dropdown                                   | `SteamDeviceControlsState`   | three writes                                                                               |
| `SteamNavigationPanelSurface` | left slideout navigation panel             | claims the exported memo's `type`, reaches the panel root by rendering | `SteamNavigationPanelState`  | activate an added entry                                                                    |
| `SteamPageSurface`         | custom pages in Steam's router                | claims the router memo's `type`, inserts routes into the route list   | `SteamPageState`             | none: a page is declared, not commanded                                                    |
| `SteamStorageSurface`      | SteamOS storage management pages              | claims `SendMsg` on the service transport, answers `StorageDeviceManager.*` | `SteamStorageState`     | adopt, unmount, eject, format, trim                                                        |
| `SteamLibraryBadgeSurface` | a library badge on every library tile         | claims the tile memo's `type`, replaces the Steam Input badge element with a row of two | `SteamLibraryBadgeState` | hears the Home layout (Big Art Mode) report                                          |
| `SteamHomeCarouselSurface` | Big Picture Home's carousel                    | claims Home's memo `type`, replaces the carousel's `games` array and bounds its overscan | `SteamHomeCarouselState` | hears what the carousel holds after each rebuild                                   |

### SteamOS storage management

Big Picture ships a complete storage UI — drives, volumes, format, adopt, eject, trim — that never
appears on Windows. The whole surface hangs off one question: its hooks ask
`StorageDeviceManager.IsServiceAvailable#1` over the WebUI service transport, and every other query
is `enabled:` on that answer. The Windows client has no service behind it, so the answer never
arrives and the pages stay inert. Nothing is hidden by a SteamOS check; it is simply unanswered.

The claim is `SendMsg` on the live transport instance. It is defined on the transport prototype as
writable and configurable and the instance carries no own property, so the claim is an own property
that removal deletes, leaving Valve's method showing through untouched.

**That one method carries every service call Steam makes**, which sets the rule for the whole gate:
the name prefix is checked first and nothing else happens on the pass-through path — same arguments,
same receiver, same return. The harness asserts exactly that, and asserts that after removal a
storage message goes to Valve like any other.

The message vocabulary is read from the client's own generated classes rather than guessed:

| Class | Fields |
| --- | --- |
| `CStorageDeviceManagerDrive` | `id`, `is_formattable`, `is_unformatted` |
| `CStorageDeviceManagerBlockDevice` | `block_device_id`, `drive_id`, `mount_paths`, `has_steam_library` |
| `CStorageDeviceManagerState` | `drives`, `block_devices`, `is_adopt_supported`, `is_unmount_supported`, `is_trim_supported`, `is_trim_running` |

Responses are duck-typed to the two things Steam's callers ask of them — `BSuccess()` and `Body()` —
rather than built as protobuf messages. The wire format is the client's business, and mirroring it
would mean owning a second copy of it.

Every action is the host's. The injected half performs no storage operation at all, which is what
keeps one Windows implementation behind both Steam's pages and WSGM's own surfaces instead of two
that can disagree. An empty published state is still answered: "no removable drives" is a truthful
answer and the page renders it, where refusing to answer leaves Steam's spinner up forever.

### The library badge

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
glyph-sized geometry overridden inline: the badge class for the fade and the end-of-row margin,
the row class so Valve's icon stays a direct child of a row, which its pill background is written
against. No hash is written down anywhere. Without the map the box is plain and always visible,
and `status.classesResolved` says so.

The walk is over props alone: the tile's whole icon row is host elements and fragments below its
Focusable root, so nothing has to be rendered to reach the anchor, and function components on the
way keep every identity Valve's reconciler holds. It is bounded at twelve levels and sixty-four
children per level. A tile whose tree has no anchor — a music album, a tile with compat icons
hidden — renders exactly what Valve shipped and is counted in `lastOutcome` as unanchored.

The badge text is the library's name alone, and its colour is Steam's own installed flag on the
app overview: green installed, grey not, which is what a disconnected card amounts to. The
published `connected` stands in only for an overview that cannot say. A game no published library
holds is on the internal library by definition and is labelled with `internalLabel` while it is
installed; one installed nowhere gets no badge, because there is no library to name. The
publication is bounded at 64 libraries, 4096 app ids and 64-character names.

Big Art Mode is `library_home_big_art`, a client setting the Home component reads through a
settings hook. The gate resolves the settings store by its own class body — the module carrying
`get clientSettings()` and `m_setDeferredSettings` — and the one export carrying `clientSettings`.
It reads the flag on every tile render and sends `homeLayout { bigArt }` once when it first
resolves and once per change, so a host learns of a toggle without a subscription into Valve's
store; `status.bigArt` carries the current reading and `null` when the store did not resolve. The
store is wanted, not required: the badge is tile-relative and draws the same in either layout.

Mapped against the September 2026 client beta on 2026-09-11: `appportrait_` occurs in exactly one
of the 2622 loaded modules, that module has exactly one memo export and exactly one function
export whose source draws the controller-support icon, five modules render the tile through the
export, and the memo's `type` is a writable and configurable own property. The probe checks each
of those separately and accepts a tile this gate already claimed.

### The Home carousel

Home's carousel draws one array of app ids, passed as `games` to both the carousel and the hero
background behind it. Steam builds that array in a module-local hook from four collections
(`local-played`, `recent-purchased`, `local-install`, `recent`) and caps it at 20. Home, the carousel
and the hook are all module-local, so the handle is the page element under the `/library/home` route
— the route list found by content — and the gate claims that Home memo's `type`. Big Picture's router
does not render under SharedJSContext's `#root`, where the page gate finds the desktop router: the
Big Picture window is a popup with a `popup_target` root of its own. The gate searches that window
first, through `SteamUIStore.WindowStore.GamepadUIMainWindowInstance`, then every popup in
`g_PopupManager`, then `#root`. The first live probe, on 2026-09-11, searched `#root` alone and found
the Home module, the stores and the observer hook but no Home. In what Home renders it finds the carousel memo by its
source (`#Showcase_RecentGames`, `RecentGamesContainer`) and replaces it with a memo of its own over
the same inner function and comparison. In what the carousel renders it replaces `games` on the two
elements that take it, told apart by shape: the background takes `refOnItemFocus`, the carousel
`onItemFocus`.

The carousel is a react-virtualized grid whose overscan defaults to 3 columns. Home passes
`overscan: games.length`, which mounts every tile; harmless at 20, a memory flood at a library. The
gate renders the carousel's own function component and clears that prop so the component default
applies, which is what the Play Next carousel on the same page already gets.

The order is decided in the gate because its inputs — every installed and owned game with its
timestamps — do not fit the bridge's 16 KiB payload. The host decides which games are excluded and
whether uninstalled games appear:

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

### Custom pages

Steam's router renders its routes as the children of its own switch, so registering a page is a
list operation on props rather than DOM work — unlike the navigation panel, whose entries do not
exist until its root renders.

Three facts decide the API, and all three were measured against the live client on 2026-09-10:

- **The switch takes the first matching child.** So inserting ahead of Steam's routes overrides one
  and inserting behind them adds one. `SteamPage.Override` is that distinction, and it defaults to
  adding, because shadowing a client route is not something a caller should get by accident.
- **The `Route` must be Steam's own.** It comes from the module carrying `router-backstack`, whose
  single matching export registers the match with Steam's back stack. React-router's `Route` renders
  the same content and silently loses back-navigation, which is the failure this would otherwise
  ship with and nobody would notice until they pressed B.
- **The router memo is not an export.** It is built locally inside its module — every export of that
  module was inspected and none carries it — so the handle comes from SharedJSContext's own React
  root, which is the tree every Steam window renders from. The walk is bounded and matches on
  component source; on the reference client it finds the router in 659 visited nodes.

The route list is found by content: the array holding a route for a path every client has. Decky's
gamepad path indexes `children.props.children[0].props.children` instead, which is the kind of
selector that breaks on a client update with no diagnostic; its own desktop path searches by
`/library/home`, and that is the half worth following.

A page whose path is relative, or is `/`, is dropped rather than registered. A catch-all route
inserted ahead of Steam's own would black out the client.

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

`SteamBrightnessState` carries confirmed `Percent` and a monotonic `Revision`. A successful
`setBrightness` response returns serialized brightness readback in its payload. Use the same
revision sequence for responses and publications. The gate holds confirmed state separately from
pending requests, rejects old revisions and suppresses observable-update feedback into the setter.
Failures keep the last confirmed level and expose `lastError`; they never retry automatically.
`eng/check-brightness.mjs` exercises focused-slider echoes, stale readback, overlapping requests,
failures and reinstall against the emitted JavaScript without a live Steam session.

Semantic slider completion also suppresses an unchanged observed value. Programmatic refresh and
command acknowledgments cannot become new user writes. `eng/check-slider-readback.mjs` checks the
emitted echo hook with an inert React fixture.

`SteamPowerProfileRow` adds a dropdown on Performance through patch `steam-ui.power-profile`, kind
`powerProfile`, and command `setPowerProfile`. Payloads are exactly `{ target: "id" }`, validated
with `TryReadTarget`. `SteamPowerProfileState` carries up to 64 unique id/label options, observed
`Current`, `Available` and `StatusText`. Unknown current ids select nothing. Labels are bounded to
240 characters by the shared text normalizer. Unavailable state stays
visible but disabled; selection is also disabled while its request is pending. The host owns
validation, OS writes, persistence and readback. `SteamPowerProfileTests` covers serialization and
module vocabulary; `eng/check-power-profile.mjs` checks the emitted dropdown, rejected choices,
malformed states and Performance placement with inert React/bridge fixtures.

`SteamHybridCoreRow` adds a second dropdown on Performance through patch `steam-ui.hybrid-cores`,
kind `hybridCores`, and command `setHybridCores`. `SteamHybridCoreState` is the same shape as the
power-profile state and reuses `SteamPowerProfileOption`, because it is the same control: host-named
choices, the one observed, and a status line. The host owns what a choice means, whether the machine
supports any, and the OS write. An empty `Current` is the honest answer for a machine set to
something the host does not offer, and selects nothing.

The two rows are written out separately rather than sharing one factory. Each control's glyph is
read from the string literal at its own `icon()` call, so a factory taking the name as an argument
would make both rows invisible to the check that proves every glyph is placed exactly once and every
placement names a drawn glyph. The core row draws `cores`, its own glyph.

`SteamPowerPresetRow` publishes `SteamPowerPresetState`: preset options, observed label, independent
AC/battery assignment IDs, scope, unset label and status. `ISteamPowerPresetBackend` owns assignment
policy. Its patch `steam-ui.power-preset` and kind `powerPreset` accept only `setAcPowerPreset` and
`setBatteryPowerPreset`. Each payload has exactly one `target`: a bounded ID or null to clear the
local assignment. A host may publish a `custom` option for a saved Custom assignment on AC or
battery. It appears only in the source dropdown already assigned Custom and is read-only; the
bridge still rejects it as a command target. Empty options hide the controls. The
C# tests cover routing, cancellation forwarding and payload refusals; emitted tests cover both
source selectors, clearing, disabled state and malformed publications.

The shared row host uses Valve's titled `PanelSection` containers to group Performance controls by
profile scope, power profiles, display/frame rate, power limits, controller and reset. Quick
Settings places its Display section before Valve's common controls, with Charging and RGB lighting
sections after them. RGB brightness stays visible; an Edit color toggle reveals the zone and HSV controls.
If Valve's toggle component is unavailable, the color editor is omitted while charging and brightness remain usable.
Empty groups are omitted. Each control retains the existing bridge and patch
ownership.

Rows and section headers carry a glyph. `icons.ts` holds the drawings — the toolkit's own, on a
24x24 grid, filled with `currentColor` and cut with `fill-rule="evenodd"`, because the client's
artwork is Valve's and cannot be vendored. `createIconRenderer(react)` builds them with Steam's
React and caches one element per name and size, and the control runtime exposes it as
`controlRuntime.icon(name, size = 20)`. A row passes the result as Field's `icon` prop, which
`SliderField`, `ToggleField` and `DropDownField` all forward; sliders also pass
`iconLocation: "front"`, because `SliderField` otherwise places the glyph beside the track rather
than the label. A section header is composed by `sectionTitle`, which pairs the 18px glyph named in
`SectionIcons` with the header text — `PanelSection` renders whatever `title` is, so an element is
as valid there as a string. An unknown name renders no glyph rather than failing the row, so a
mistyped name costs an icon and nothing else.

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

Every gate's payload is read with `SteamUiPayload` (exact object shape, bounded strings, ranges),
and a malformed one is refused with a fixed reason before the backend runs. `SteamUiBridgePatch`
installs the bridge; register it in the same manager as dependent surfaces, but do not rely on call
order because the manager synchronizes patches by stable id and retries unmet conditions. Every row
shares the resource key `steam-ui.performance-root` so the mounted set serializes. Patch ids and resource keys are
`steam-ui.*`, the markers the live client carries are `__steamUi*`, and both are public constants:
a consumer's kill-switch policy names patches by them and a probe from a separate CDP call reads
the markers back.

Power-limit state additionally carries unified and canSelectMode. The optional mode toggle sends
setUnifiedMode with exactly one boolean unified property. Consumers own persistence and paired
hardware dispatch. Unified presentation hides the independent boost slider while showing both
observed values in the TDP description. Default state retains the existing split presentation.
