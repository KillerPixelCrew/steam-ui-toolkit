---
name: steam-ui-toolkit
description:
  Implement or review SteamUiToolkit transport, bridge, patch lifecycle and reusable Steam surfaces.
  Use for toolkit mechanics; consumer feature policy stays outside this repository.
---

# SteamUiToolkit contributions

Resolve aliases to this owning repository. Start with AGENTS.md, README.md and docs/reference.md;
public XML docs own API wording. Follow the repository map and existing typed surfaces and UI kit.
Keep consumer feature policy outside transport, bridge and patch mechanics.

Preserve generation identity, bridge admission, replay protection and ownership of injected state.
Patch success means Verified, not only applied or endpoint reachable. Failure after native/bridge
dispatch remains uncertain; do not silently replay a user command during reconnect. Reconcile
controlled UI state without losing focus or turning stale observations into new user intent. Review
acquire, navigation/back, generation replacement, reconnect, remove and failed cleanup.

Follow owning formatter settings. C# uses expanded braces, var locals, named literal arguments,
explicit construction types where needed, no multiline trailing commas and public XML docs.
TypeScript uses existing helpers and native Steam controls. Extend the shared UI kit instead of
adding consumer-specific HTML; edit source fragments, never generated dist output.

Before any attended CEF connection, establish from this run's Steam logs that Steam and Big Picture
fully started; a port/window is insufficient. Review permission does not authorize live mutation.
Separate asset checks, compilation and fixtures from screenshots/input acceptance in live Steam.
Consumers must validate their generated claims and supported Steam channels under their own budget.
