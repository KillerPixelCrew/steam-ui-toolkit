# Plugin frontends

`SteamPluginFrontendSurface.Module(id, owner, module, script, style, enabled, invoke, failed, read)`
declares one independently removable frontend patch, an optional JSON state publication, and the
closed bridge commands `invoke` and `failure`. `owner` is shared by all modules in one package
instance; `id` must be unique for its current host registration. The host supplies bundle bytes,
enablement, backend and whole-owner failure policy. The toolkit does not review content or sandbox
it. It resets pause-on-exceptions and disables CDP Debugger before making transport ready.

JavaScript runs as a function body with an `api` argument. Return an optional disposer, or a promise
resolving to one. The bundle has a distinct `steam-ui-plugin://<owner>/<module>.js` source URL.
Every registered contribution owns its readiness, rendering and teardown; readiness has a
five-second budget and the frontend patch's phases have a fifteen-second budget.

| API                                                                               | Contract                                                                                                     |
| --------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------ |
| `react`, `bridge`, `resolveModules`, `resolveComponents()`, `uiKit`               | Steam runtime and toolkit primitives, without a security restriction                                         |
| `registerPage(id, {path, title, override?}, render)`                              | Register a routed page using Steam's back-stack Route; `render(react, {page})` supplies arbitrary components |
| `registerMenuEntry(id, {label, route?, onActivate?, before?, after?, position?})` | Native main-menu route or guarded action                                                                     |
| `registerQuickAccessTab(id, {title, icon?}, render)`                              | Independent native QAM tab and arbitrary component body                                                      |
| `registerQuickAccessRow(id, placement, render)`                                   | Row in `perf` or `quickSettings`; Performance rows precede Reset to Default                                  |
| `registerLibraryAddition(id, render)`                                             | Library tile addition with its `overview`                                                                    |
| `registerGamePageAddition(id, render)`                                            | Game-page statistics addition with its `overview`                                                            |
| `registerPatch(id, {probe, apply, verify, remove})`                               | Arbitrary plugin patch with independent readiness, ongoing verification and cleanup                          |
| `addStyle(css)`                                                                   | Unrestricted CSS, loaded into current and newly discovered Steam popup documents                             |
| `call(method, payload)`                                                           | JSON request/response through the generation-bound optional backend                                          |
| `subscribe(callback)`                                                             | Current frontend state, with synchronous cached replay and a returned unsubscribe                            |
| `guard(callback)`, `ready(check)`                                                 | Callback failure containment and bounded readiness                                                           |
| `onDispose(callback)`                                                             | Register cleanup for plugin-owned external side effects                                                      |
| `setTimeout`, `setInterval`, `addEventListener`                                   | Guarded callbacks and automatic cleanup                                                                      |

Registration methods return promises and are also awaited by bundle loading. Their ids are unique
within a bundle. Render callbacks execute inside a per-module React error boundary. Window `error`
and `unhandledrejection` events are attributed through each bundle's source URL; an event without an
attributable owner disables nobody. Host backend/publication errors use the same owner policy.

On the first failure, every sibling is marked failed and its contributions retract before the host
is notified. CSS/error listeners are removed before arbitrary teardown, and asynchronous teardown
has a two-second budget per callback. Only the host's explicit reload admits a replacement; no
failed bundle retries itself. Other owners and host surfaces remain registered. Hosts must remove
the owning modules on stop, replacement, opt-out and shutdown, and drain backend calls before
unloading package code.

An endless JavaScript loop or renderer crash is outside an in-page wrapper's reach. The host must
provide a startup recovery option that omits plugin frontends while retaining its own UI. There is
no Decky compatibility layer or implied restriction on what a working plugin can do.

Regression sources: `SteamPluginFrontendSurfaceTests`, transport domain-order coverage and
`eng/check-plugin-frontends.mjs` for emitted owner isolation and renderer teardown.
