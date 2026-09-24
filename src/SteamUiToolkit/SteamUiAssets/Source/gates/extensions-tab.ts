// The Quick Access Extensions tab.
//
// Decky demonstrates that a tab object is data added to the QAM's tab list, but this gate owns the
// narrow operation rather than exposing Decky's raw patch helpers to package code. The tab body is
// entirely host-rendered from a typed publication, so extensions cannot inject a React tree into a
// shared Steam surface.
function createExtensionsTab() {
  const patchId = "steam-ui.extensions-tab";
  const claimKeys = {
    marker: "__steamUiExtensionsTabClaimed",
    original: "__steamUiExtensionsTabOriginal",
  } as const;
  const QamToken = "QuickAccessMenuBrowserView";
  const MaximumItems = 64;
  // Element depth within one render pass, reset at every wrapped component. Measured on the
  // 2026-09-24 client: from the component carrying onFocusNavDeactivated to the element holding
  // the tab list is nineteen component-typed levels behind context providers and host elements,
  // so twelve stopped short of it.
  const MaximumDescent = 32;

  let runtime;
  let react;
  let focusable;
  let memo: any = null;
  let installed = false;
  let unsubscribe: (() => void) | null = null;
  let desired: { items: any[]; revision: number } = { items: [], revision: 0 };
  let lastOutcome = "never rendered";
  let lastError = "";
  const descenderCache = new Map();

  const validAction = (action) =>
    action &&
    typeof action.id === "string" &&
    action.id.length > 0 &&
    action.id.length <= 96 &&
    typeof action.label === "string" &&
    action.label.length > 0 &&
    action.label.length <= 160;
  const validSetting = (setting) =>
    setting &&
    typeof setting.key === "string" &&
    setting.key.length > 0 &&
    setting.key.length <= 128 &&
    typeof setting.label === "string" &&
    setting.label.length > 0 &&
    setting.label.length <= 128 &&
    ["boolean", "number", "text", "secret", "order"].includes(setting.kind) &&
    (setting.choices === undefined ||
      setting.choices === null ||
      (Array.isArray(setting.choices) &&
        setting.choices.length <= 64 &&
        setting.choices.every((choice) => typeof choice === "string" && choice.length <= 4096)));
  const validItem = (item) =>
    item &&
    typeof item.id === "string" &&
    item.id.length > 0 &&
    typeof item.name === "string" &&
    typeof item.version === "string" &&
    typeof item.status === "string" &&
    (item.actions === undefined ||
      item.actions === null ||
      (Array.isArray(item.actions) &&
        item.actions.length <= 64 &&
        item.actions.every(validAction))) &&
    (item.settings === undefined ||
      item.settings === null ||
      (Array.isArray(item.settings) &&
        item.settings.length <= 128 &&
        item.settings.every(validSetting))) &&
    Number.isSafeInteger(item.configurationRevision ?? 0) &&
    (item.configurationRevision ?? 0) >= 0 &&
    (item.detail === undefined || item.detail === null || typeof item.detail === "string");

  // Steam's QAM tab view is private. This bounded traversal finds the first element whose own
  // props carry the tab list, matching what the live client renders rather than indexing its tree.
  const replaceTabs = (element, depth, visible) => {
    if (depth > MaximumDescent || !react.isValidElement(element)) return element;
    const tabs = element.props?.tabs;
    if (Array.isArray(tabs)) {
      const existing = tabs.filter((tab) => tab && tab.steamUiExtensionsTab === true);
      if (existing.length === 1) {
        lastOutcome = `tabs=${tabs.length} extensions=present`;
        return element;
      }
      if (existing.length > 1) {
        lastOutcome = `tabs=${tabs.length} extensions=ambiguous`;
        return element;
      }
      const tab = {
        key: "steam-ui.extensions-tab",
        title: null,
        tab: createIconRenderer(react)("plug", 22),
        steamUiExtensionsTab: true,
        initialVisibility: !!visible,
        panel: react.createElement(ExtensionsTabPanel, { key: "steam-ui.extensions-panel" }),
      };
      lastOutcome = `tabs=${tabs.length} extensions=added`;
      return react.cloneElement(element, { tabs: [...tabs, tab] });
    }
    return mapChildren(react, element, (child) => replaceTabs(child, depth + 1, visible));
  };

  function ExtensionsTabPanel() {
    const [, setRevision] = react.useState(0);
    const [drafts, setDrafts] = react.useState({});
    react.useEffect(() => subscribe(patchId, () => setRevision((value) => value + 1)), []);
    const items = desired.items;
    const activate = (id) => {
      void request(patchId, "activate", { id }, nextActionGeneration(patchId)).then(
        (answer: any) => {
          // An action may answer with a page to open. The panel is closed first: this tab is
          // rendered inside the Quick Access flyout, so navigating with it open leaves the page
          // behind the panel, which on a controller is indistinguishable from a dead button.
          if (answer?.route && closeSteamSideMenus()) navigateSteamRoute(answer.route);
        },
        () => {
          // The host's refusal is already logged and the row remains truthful on the next state
          // publication. A rejected click must not tear down the whole Quick Access panel.
        },
      );
    };
    // A typed draft belongs to the publication it was typed against. Dropping it when the host
    // answers with a new configuration revision, and when the change is refused, is what stops the
    // box from showing and resending a value the host has already replaced or rejected.
    const dropDraft = (draftKey) =>
      setDrafts((previous) => {
        if (!(draftKey in previous)) return previous;
        const next = { ...previous };
        delete next[draftKey];
        return next;
      });
    const configure = (item, setting, value) => {
      const draftKey = `${item.id}:${setting.key}`;
      void request(
        patchId,
        "configure",
        { id: item.id, key: setting.key, value, revision: item.configurationRevision ?? 0 },
        nextActionGeneration(patchId),
      ).catch(() => dropDraft(draftKey));
    };
    const settingControl = (item, setting) => {
      const draftKey = `${item.id}:${setting.key}`;
      if (setting.kind === "boolean") {
        return react.createElement(
          focusable,
          {
            key: setting.key,
            focusable: true,
            navKey: `steam-ui-extension-setting-${draftKey}`,
            onActivate: () => configure(item, setting, !setting.booleanValue),
            style: {
              display: "flex",
              justifyContent: "space-between",
              width: "100%",
              padding: "10px 12px",
              margin: "3px 0",
              color: "inherit",
              background: "rgba(255,255,255,.05)",
              border: 0,
              borderRadius: "3px",
            },
          },
          react.createElement("span", null, setting.label),
          react.createElement("strong", null, setting.booleanValue ? "On" : "Off"),
        );
      }
      if (setting.kind === "order" && Array.isArray(setting.choices)) {
        const saved = String(setting.textValue ?? "")
          .split(",")
          .filter((choice) => setting.choices.includes(choice));
        const ordered = [
          ...new Set([...saved, ...setting.choices]),
        ];
        const move = (index, delta) => {
          const target = index + delta;
          if (target < 0 || target >= ordered.length) return;
          const next = [...ordered];
          [next[index], next[target]] = [next[target], next[index]];
          configure(item, setting, next.join(","));
        };
        return react.createElement(
          "div",
          { key: setting.key, style: { display: "grid", gap: "4px", padding: "8px 0" } },
          react.createElement("div", { style: { opacity: 0.8 } }, setting.label),
          ...ordered.map((choice, choiceIndex) =>
            react.createElement(
              "div",
              {
                key: choice,
                style: {
                  display: "grid",
                  gridTemplateColumns: "1fr auto auto",
                  alignItems: "center",
                  gap: "4px",
                  padding: "5px 8px",
                  background: "rgba(255,255,255,.05)",
                },
              },
              react.createElement("span", null, choice),
              react.createElement(
                focusable,
                {
                  focusable: true,
                  navKey: `steam-ui-extension-order-up-${draftKey}-${choiceIndex}`,
                  onActivate: () => move(choiceIndex, -1),
                  style: { padding: "7px 10px", color: "inherit", background: "transparent" },
                },
                "↑",
              ),
              react.createElement(
                focusable,
                {
                  focusable: true,
                  navKey: `steam-ui-extension-order-down-${draftKey}-${choiceIndex}`,
                  onActivate: () => move(choiceIndex, 1),
                  style: { padding: "7px 10px", color: "inherit", background: "transparent" },
                },
                "↓",
              ),
            ),
          ),
        );
      }
      if (Array.isArray(setting.choices)) {
        return react.createElement(
          "div",
          {
            key: setting.key,
            style: { display: "grid", gap: "4px", padding: "8px 0" },
          },
          react.createElement("div", { style: { opacity: 0.8 } }, setting.label),
          ...setting.choices.map((choice, choiceIndex) =>
            react.createElement(
              focusable,
              {
                key: choice,
                focusable: true,
                navKey: `steam-ui-extension-choice-${draftKey}-${choiceIndex}`,
                onActivate: () => configure(item, setting, choice),
                style: {
                  display: "flex",
                  justifyContent: "space-between",
                  width: "100%",
                  padding: "9px 12px",
                  color: "inherit",
                  background: "rgba(255,255,255,.05)",
                  border: 0,
                  borderRadius: "3px",
                },
              },
              react.createElement("span", null, choice),
              react.createElement("strong", null, setting.textValue === choice ? "Selected" : ""),
            ),
          ),
        );
      }
      const revision = item.configurationRevision ?? 0;
      const draft = drafts[draftKey];
      const current =
        draft && draft.revision === revision
          ? draft.value
          : (setting.textValue ?? setting.numberValue ?? "");
      return react.createElement(
        "div",
        {
          key: setting.key,
          style: { display: "grid", gap: "6px", padding: "8px 0" },
        },
        react.createElement("label", null, setting.label),
        react.createElement("input", {
          type:
            setting.kind === "secret" ? "password" : setting.kind === "number" ? "number" : "text",
          value: current,
          min: setting.minimum,
          max: setting.maximum,
          onChange: (event) =>
            setDrafts((previous) => ({
              ...previous,
              [draftKey]: { value: event.currentTarget.value, revision },
            })),
          style: {
            padding: "8px 10px",
            color: "inherit",
            background: "rgba(0,0,0,.3)",
            border: "1px solid rgba(255,255,255,.25)",
          },
        }),
        react.createElement(
          focusable,
          {
            focusable: true,
            navKey: `steam-ui-extension-save-${draftKey}`,
            onActivate: () =>
              configure(
                item,
                setting,
                setting.kind === "number" ? Number(current) : String(current),
              ),
            style: {
              padding: "8px 12px",
              color: "inherit",
              background: "#1a9fff",
              border: 0,
              borderRadius: "3px",
            },
          },
          "Save",
        ),
      );
    };
    const rows = items.map((item) =>
      react.createElement(
        "section",
        {
          key: item.id,
          style: {
            display: "block",
            width: "100%",
            textAlign: "left",
            padding: "12px 16px",
            margin: "4px 0",
            color: "inherit",
            background: "rgba(255,255,255,.06)",
            border: "0",
            borderRadius: "3px",
          },
        },
        react.createElement("div", { style: { fontWeight: 700 } }, item.name),
        react.createElement(
          "div",
          { style: { fontSize: "0.8em", opacity: 0.75 } },
          [item.version, item.status, item.detail].filter((part) => !!part).join(" · "),
        ),
        ...(item.actions ?? []).map((action) =>
          react.createElement(
            focusable,
            {
              key: action.id,
              focusable: true,
              navKey: `steam-ui-extension-action-${action.id}`,
              onActivate: () => activate(action.id),
              style: {
                width: "100%",
                marginTop: "8px",
                padding: "9px 12px",
                color: "inherit",
                background: "rgba(255,255,255,.1)",
                border: 0,
                borderRadius: "3px",
                textAlign: "left",
              },
            },
            action.label,
          ),
        ),
        ...(item.settings ?? []).map((setting) => settingControl(item, setting)),
      ),
    );
    return react.createElement(
      "div",
      { className: "steam-ui-extensions-tab", style: { padding: "16px" } },
      react.createElement("h2", null, "Extensions"),
      rows.length
        ? rows
        : react.createElement(
            "div",
            { style: { opacity: 0.7 } },
            "No Steam UI extensions are installed.",
          ),
    );
  }

  const tabDescender = (type) =>
    function SteamUiExtensionsTabDescend(props) {
      return descend(type(props), 0, props?.visible);
    };
  const descend = (element, depth, visible) => {
    if (depth > MaximumDescent) return element;
    // The menu's body is drawn through a portal into the popup window; see mapPortalChildren.
    if (isPortal(element)) {
      return mapPortalChildren(react, element, (kid) => descend(kid, depth + 1, visible));
    }
    if (!react.isValidElement(element)) return element;
    const replaced = replaceTabs(element, depth, visible);
    if (replaced !== element) return replaced;
    // A render whose root is not a plain function component — a context provider, a host div — is
    // descended through its children, the way the navigation panel already does. Stopping at such
    // a root left the descender one level deep on the 2026-09-24 client, where the tab list sits
    // twenty-three component levels down behind alternating providers and function components,
    // so the tab was never inserted while every status flag read true.
    return (
      descendInto(react, element, descenderCache, tabDescender) ??
      mapChildren(react, element, (kid) => descend(kid, depth + 1, visible))
    );
  };

  const resolve = () => {
    runtime = getWebpackRuntime("extensions-tab");
    react = resolveReact(runtime);
    if (!react) {
      lastError = "React runtime was not a unique match";
      return false;
    }
    focusable = resolveNativeFocusable(runtime);
    if (!focusable) {
      lastError = "Native Steam focusable control was not a unique match";
      return false;
    }
    const qam = runtime.findUnique([QamToken]);
    if (!qam) {
      lastError = "Quick Access module was not a unique match";
      return false;
    }
    const exports = runtime(qam[0]);
    const candidates = Object.keys(exports).filter((name) => {
      const value = exports[name];
      const stored =
        value?.type?.[claimKeys.marker] === true ? value.type[claimKeys.original] : value?.type;
      const original = stored?.kind === "steam-ui-property-snapshot-v1" ? stored.value : stored;
      return (
        value &&
        typeof value === "object" &&
        typeof original === "function" &&
        String(original).includes(QamToken)
      );
    });
    if (candidates.length !== 1) {
      lastError = `Quick Access memo was ${candidates.length ? "ambiguous" : "absent"}`;
      return false;
    }
    memo = exports[candidates[0]];
    return true;
  };

  // What the last install's adoption of already-mounted Quick Access views reached; see install().
  let lastAdoption: { adopted: number; scheduled: boolean } = { adopted: 0, scheduled: false };

  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
    if (
      !attemptResolution(
        resolve,
        (error) => (lastError = "Extensions tab resolution failed: " + String(error)),
      )
    ) {
      return { ok: false, error: lastError };
    }
    const claim = claimMember(memo, "type", claimKeys, (original: any) => {
      if (typeof original !== "function") return original;
      return function SteamUiExtensionsTabRoot(props) {
        return descend(original(props), 0, props?.visible);
      };
    });
    if (!claim.ok) {
      lastError = claim.error;
      return { ok: false, error: lastError };
    }
    installed = true;
    lastError = "";
    // The claim reaches the next mount only, and the Quick Access view is mounted at boot and kept,
    // so without this the tab never appeared: status said claimed, lastOutcome said never rendered,
    // and opening the menu drew Steam's own cached function (2026-09-24). Adoption swaps the mounted
    // instances over and defeats the memo bail-out; see adoptMountedType.
    lastAdoption = adoptMountedType(reactRootFibers(), memo, memo.type, MaximumMountedNodes);
    unsubscribe = subscribe(patchId, (state) => {
      const items = Array.isArray(state?.items)
        ? state.items.filter(validItem).slice(0, MaximumItems)
        : [];
      desired = { items, revision: Number.isSafeInteger(state?.revision) ? state.revision : 0 };
      // The wrapper reads `desired` from its closure, so a publication changes nothing React can
      // see. Ask the mounted views to draw again, or a tab published after install waits for the
      // next navigation.
      renderMountedType(reactRootFibers(), memo, memo.type, MaximumMountedNodes);
    });
    return { ok: true, installed: true, reclaimed: claim.reclaimed };
  };

  // Ownership is given up before the gate forgets it owns anything: a failed release otherwise
  // leaves the claim live while every later remove() answers `absent` and never retries it.
  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    // Read before the release hands `type` back, so the adopted instances can be matched by it.
    const wrapper = memo?.type;
    const released = releaseMember(memo, "type", claimKeys);
    if (!released.ok) {
      lastError = released.error ?? "Extensions tab release failed";
      return { ok: false, error: lastError };
    }
    // Every mounted view this install adopted, handed back to what the claim displaced.
    releaseMountedType(reactRootFibers(), memo, wrapper, memo.type, MaximumMountedNodes);
    lastAdoption = { adopted: 0, scheduled: false };
    installed = false;
    unsubscribe = endSubscription(unsubscribe);
    desired = { items: [], revision: 0 };
    descenderCache.clear();
    lastOutcome = "removed";
    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    resolved: !!memo,
    nativeFocusableResolved: !!focusable,
    claimed: memberClaimed(memo, "type", claimKeys),
    items: desired.items.length,
    revision: desired.revision,
    // Whether the claim reached the views already on screen, and whether one is still drawing
    // Steam's own. A claim that adopted nothing is inert until Steam mounts a new view.
    mounted: {
      ...lastAdoption,
      stale: staleFibers(reactRootFibers(), memo, MaximumMountedNodes),
    },
    lastOutcome,
    lastError,
  });

  return { install, remove, status };
}

registerGate("extensionsTab", createExtensionsTab());
