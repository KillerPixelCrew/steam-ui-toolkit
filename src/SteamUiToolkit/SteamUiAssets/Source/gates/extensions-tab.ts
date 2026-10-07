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
  // The tab's identity in Steam's strip, a number like Valve's own (Notifications 0, Friends 3,
  // Settings 4, Perf 5, Help 6, Music 7): the strip's activeTab is compared to it. Clear of Valve's
  // and of decky-loader's 999 so the two can coexist.
  const ExtensionsTabId = 1010;
  const ExtensionsTabTitle = "Extensions";

  // How the tab is both drawn and selected, in two steps that are each necessary:
  //   - it is pushed into Valve's own tab array in place, as decky-loader does, which is what the
  //     strip and the content draw from;
  //   - the component rendering those two validates the store's active tab against a list it built
  //     itself and falls back to the first entry when ours is absent from it, so whenever Steam's
  //     store names our tab, the strip and the content are handed it as the active one directly.
  // The store is read by the names Valve gives it; null when it is not where Valve keeps it today.
  const activeQuickAccessTab = () => {
    try {
      return (
        (window as any).SteamUIStore?.ActiveWindowInstance?.MenuStore?.GetQuickAccessTab?.() ?? null
      );
    } catch {
      return null;
    }
  };
  const withOurTabActive = (element) =>
    (activeQuickAccessTab() === ExtensionsTabId ||
      pluginFrontendItems("tab").some((slot) => slot.tabKey === activeQuickAccessTab())) &&
    element.props.activeTab !== activeQuickAccessTab()
      ? react.cloneElement(element, { activeTab: activeQuickAccessTab() })
      : element;
  // Element depth within one render pass, reset at every wrapped component. Measured on the
  // 2026-09-24 client at nineteen component-typed levels from the component carrying
  // onFocusNavDeactivated to the element holding the tab list.
  const MaximumDescent = 32;

  // What the tab draws with, every piece Steam's own: the panel's PanelSection and PanelSectionRow,
  // its DialogButton, and the settings fields `renderSteamSettingRow` draws a setting with. All of
  // them are required. A client missing one refuses the tab, like every surface here, rather than
  // drawing an imitation that looks and navigates unlike the tabs beside it.
  const ExtensionsTabRequired = [
    "react",
    "focusable",
    "dialogButton",
    "toggleField",
    "dropdown",
    "sliderField",
    "textField",
    "smallButton",
    "valueField",
  ] as const;

  let runtime;
  let react;
  let ui: any = null;
  let panel: any = null;
  let icon: any = null;
  let memo: any = null;
  let installed = false;
  let unsubscribe: (() => void) | null = null;
  let unsubscribeFolds: (() => void) | null = null;
  let unsubscribePlugins: any = null;
  let nextPluginTabKey = 1100;
  const pluginTabLists = new Set<any[]>();
  let desired: { items: any[]; revision: number } = { items: [], revision: 0 };
  // The folds, shared with the Performance and Quick Settings groups: the host publishes the
  // sections the user opened, and every section and every switch's settings start folded.
  const folds = createSteamFolds();
  let openSections: Set<string> | null = null;
  let lastOutcome = "never rendered";
  let lastError = "";
  const descenderCache = new Map();
  const mounted = createMountedAdoption();
  // Valve's tab array the tab was last pushed into, so removal can take it out again.
  let insertedInto: any[] | null = null;

  // Only what the renderer needs to draw a row: the right types, and text where a label goes. No
  // counts and no lengths: the publication is delivered in parts however large it is, and a cap here
  // only ever threw real content away. A theme set with 160 settings made the whole Themes section
  // vanish. Anything malformed drops alone, never the item it sits in.
  const text = (value) => typeof value === "string" && value.length > 0;
  const optionalText = (value) =>
    value === undefined || value === null || typeof value === "string";
  const optionalFlag = (value) =>
    value === undefined || value === null || typeof value === "boolean";
  const textList = (value) =>
    Array.isArray(value) && value.every((entry) => typeof entry === "string");
  const validAction = (action) => action && text(action.id) && text(action.label);
  const validSetting = (setting) =>
    setting &&
    text(setting.key) &&
    text(setting.label) &&
    ["boolean", "number", "text", "secret", "order", "color"].includes(setting.kind) &&
    optionalText(setting.description) &&
    optionalText(setting.parent) &&
    optionalFlag(setting.highlight) &&
    (setting.choices === undefined || setting.choices === null || textList(setting.choices)) &&
    (setting.choiceLabels === undefined ||
      setting.choiceLabels === null ||
      (textList(setting.choiceLabels) &&
        Array.isArray(setting.choices) &&
        setting.choiceLabels.length === setting.choices.length));
  // An item keeps whatever of it can be drawn. Only one without an identity and a name is dropped,
  // or one with a revision the configure command refuses, since every row in it would be dead.
  const usableItem = (item) =>
    item &&
    text(item.id) &&
    typeof item.name === "string" &&
    typeof item.version === "string" &&
    typeof item.status === "string" &&
    Number.isSafeInteger(item.configurationRevision ?? 0) &&
    (item.configurationRevision ?? 0) >= 0;
  const drawable = (item) => ({
    ...item,
    actions: Array.isArray(item.actions) ? item.actions.filter(validAction) : [],
    settings: Array.isArray(item.settings) ? item.settings.filter(validSetting) : [],
    detail: typeof item.detail === "string" ? item.detail : undefined,
  });

  // An element whose own props carry the tab list, with our tab in it; null for any other element.
  // Steam's tab view is private, so the list is matched by content rather than by a path into the
  // tree. The strip and the content each carry the same array, so the second visit finds the tab
  // already present.
  const insertTab = (element, visible) => {
    const tabs = element.props?.tabs;
    if (!Array.isArray(tabs)) return null;
    if (!tabs.some((tab) => tab?.steamUiExtensionsTab || [4, 5, 6].includes(tab?.key))) return null;
    pluginTabLists.add(tabs);
    for (let index = tabs.length - 1; index >= 0; index--)
      if (tabs[index]?.steamUiPluginTab) tabs.splice(index, 1);
    for (const slot of pluginFrontendItems("tab")) {
      slot.tabKey ??= nextPluginTabKey++;
      tabs.push({
        key: slot.tabKey,
        title: react.createElement("div", null, slot.value.title),
        strTitle: slot.value.title,
        tab: slot.value.icon ?? icon("extensions", 22),
        steamUiPluginTab: slot.id,
        initialVisibility: !!visible,
        panel: slot.element(react, {}),
      });
    }
    const existing = tabs.filter((tab) => tab && tab.steamUiExtensionsTab === true);
    if (existing.length > 1) {
      lastOutcome = `tabs=${tabs.length} extensions=ambiguous`;
      return element;
    }
    if (existing.length === 0) {
      // Valve's tabs carry both: the element the header draws and the string it is named by. The
      // element is Valve's own title element with our text in it, so the tab's heading is drawn
      // the size and colour Valve's are, under a class this code never names; a client whose
      // title is not a plain element gets a plain div.
      const sample = tabs.find(
        (tab) => tab && react.isValidElement(tab.title) && typeof tab.title.type === "string",
      );
      tabs.push({
        key: ExtensionsTabId,
        title: sample
          ? react.cloneElement(sample.title, { key: undefined }, ExtensionsTabTitle)
          : react.createElement("div", null, ExtensionsTabTitle),
        strTitle: ExtensionsTabTitle,
        tab: icon("extensions", 22),
        steamUiExtensionsTab: true,
        initialVisibility: !!visible,
        panel: react.createElement(ExtensionsTabPanel, { key: "steam-ui.extensions-panel" }),
      });
      insertedInto = tabs;
    }
    lastOutcome = `tabs=${tabs.length} extensions=${existing.length ? "present" : "added"}`;
    return withOurTabActive(element);
  };

  // A published setting as the settings renderer's row, so a setting here is drawn by the same code,
  // and looks the same, as one on a host's settings page. Null for a setting no row can show.
  const settingRow = (item, setting) => {
    const key = `${item.id}:${setting.key}`;
    // A highlighted description is drawn in the kit's accent: "Update available", for one.
    const description =
      setting.description && setting.highlight
        ? react.createElement("span", { className: "steam-ui-kit-highlight" }, setting.description)
        : (setting.description ?? undefined);
    // A choice is sent back by its value and shown by its label, when the host gave one.
    const labels = Array.isArray(setting.choiceLabels) ? setting.choiceLabels : setting.choices;
    const choices = Array.isArray(setting.choices)
      ? setting.choices.map((choice, index) => ({
          value: choice,
          label: labels?.[index] ?? choice,
        }))
      : null;
    switch (setting.kind) {
      case "boolean":
        return {
          key,
          label: setting.label,
          description,
          kind: "boolean",
          checked: !!setting.booleanValue,
        };
      case "order": {
        if (!choices) return null;
        const saved = String(setting.textValue ?? "")
          .split(",")
          .filter((choice) => setting.choices.includes(choice));
        return {
          key,
          label: setting.label,
          description,
          kind: "order",
          choices,
          order: [...new Set([...saved, ...setting.choices])],
        };
      }
      case "number":
        // A number with choices is one of them by index: a slider stepping through the choices,
        // each named on its notch, the way CSSLoader draws a theme's slider patch.
        if (choices && choices.length > 1) {
          return {
            key,
            label: setting.label,
            description,
            kind: "range",
            number: setting.numberValue ?? 0,
            minimum: 0,
            maximum: choices.length - 1,
            labels,
          };
        }
        return Number.isFinite(setting.minimum) && Number.isFinite(setting.maximum)
          ? {
              key,
              label: setting.label,
              description,
              kind: "range",
              number: setting.numberValue ?? setting.minimum,
              minimum: setting.minimum,
              maximum: setting.maximum,
            }
          : {
              key,
              label: setting.label,
              description,
              kind: "text",
              text: String(setting.numberValue ?? ""),
            };
      case "secret":
        // A secret's current value is never published, so its box starts empty.
        return { key, label: setting.label, description, kind: "secret" };
      case "color":
        return {
          key,
          label: setting.label,
          description,
          kind: "color",
          text: setting.textValue ?? "",
        };
      default:
        return choices
          ? {
              key,
              label: setting.label,
              description,
              kind: "choice",
              choices,
              text: setting.textValue ?? "",
              // Below its label, as CSSLoader draws a patch: the panel is too narrow for a
              // dropdown beside one.
              layout: "below",
            }
          : { key, label: setting.label, description, kind: "text", text: setting.textValue ?? "" };
    }
  };

  // What a row's value means to the host: an order is sent as its comma-joined list, and a number
  // typed into a box as a number. Undefined when the value is not one the setting can take.
  const settingValue = (setting, value) => {
    if (setting.kind === "order") return Array.isArray(value) ? value.join(",") : undefined;
    if (setting.kind === "number") {
      const number = Number(value);
      return Number.isFinite(number) ? number : undefined;
    }
    return value;
  };

  function ExtensionsTabPanel() {
    const [, setRevision] = react.useState(0);
    // The settings page's draft keeping. A draft belongs to the value its row was published with,
    // so a newer configuration that changes the row replaces it, one that changes another row
    // keeps it, and a refused save drops it and says why on the row.
    const drafts = useSteamSettingDrafts(
      react,
      desired.items.map((item) => item.configurationRevision ?? 0).join(","),
    );
    const redraw = () => setRevision((value) => value + 1);
    react.useEffect(() => subscribe(patchId, redraw), []);
    const h = react.createElement;
    const items = desired.items;
    const activate = (id) => {
      void request(patchId, "activate", { id }).then(
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
    // The row renderer's change: a committed value is sent against the configuration it was made
    // against. A value the setting cannot take is refused here rather than sent to be refused.
    const change = (item, setting) =>
      drafts.change((_row, value) => {
        const sent = settingValue(setting, value);
        if (sent === undefined)
          return Promise.reject(new Error("Not a value this setting can take"));
        return request(patchId, "configure", {
          id: item.id,
          key: setting.key,
          value: sent,
          revision: item.configurationRevision ?? 0,
        });
      });
    const draftOf = (item, setting) => {
      const row = settingRow(item, setting);
      return row ? drafts.draft(row) : undefined;
    };
    const settingControl = (item, setting) => {
      const row = settingRow(item, setting);
      if (!row) return null;
      return renderSteamSettingRow(
        ui,
        drafts.row(row),
        drafts.draft(row),
        change(item, setting),
        () => {},
      );
    };
    // Whether a switch is on, as the user last set it or as the host published it.
    const switchOn = (item, setting) => {
      const draft = draftOf(item, setting);
      return draft !== undefined ? !!draft : !!setting.booleanValue;
    };
    const settingLine = (item, setting) => {
      const control = settingControl(item, setting);
      if (!control) return null;
      return h(
        panel.row,
        { key: `setting-${setting.key}` },
        setting.parent ? h("div", { className: "steam-ui-kit-nested" }, control) : control,
      );
    };
    const detailOf = (item) =>
      [item.version, item.status, item.detail].filter((part) => !!part).join(" · ");
    const isFolded = (id) => folds.isFolded(openSections, id);
    const foldHeading = (id, props) =>
      renderSteamUiHeader(ui, {
        ...props,
        collapsed: isFolded(id),
        onToggle: () => folds.setFolded(id, !isFolded(id), redraw),
      });
    // A section is headed by a focusable row rather than the section's own title: the title Steam
    // draws cannot take focus, and a controller has to be able to land on the fold. It is drawn as
    // a title with a caret, not as a button, so a folded section reads as a heading.
    const header = (item) =>
      h(
        panel.row,
        { key: "header" },
        foldHeading(`extensions:${item.id}`, { title: item.name, detail: detailOf(item) }),
      );
    // Actions in the kit's grid: two short labels side by side, a long one across the row, rather
    // than every action being a full-width bar of its own.
    const actionsRow = (item) =>
      (item.actions ?? []).length
        ? h(
            panel.row,
            { key: "actions" },
            renderSteamUiActions(
              ui,
              item.actions.map((action) => ({
                id: action.id,
                label: action.label,
                onClick: () => activate(action.id),
              })),
            ),
          )
        : null;
    // One line per setting, in the order published. A setting with a parent is drawn under that
    // switch, only while it is on, and a switch's settings fold under a small heading of their own
    // that names how many there are; a parent that is not a switch on the item hides the setting,
    // since nothing could open it.
    const settingLines = (item) => {
      const settings = item.settings ?? [];
      const children = new Map<string, any[]>();
      for (const setting of settings) {
        if (!setting.parent) continue;
        if (!children.has(setting.parent)) children.set(setting.parent, []);
        children.get(setting.parent)!.push(setting);
      }
      return settings.flatMap((setting) => {
        if (setting.parent) return [];
        const line = settingLine(item, setting);
        const under = setting.kind === "boolean" ? (children.get(setting.key) ?? []) : [];
        if (!under.length || !switchOn(item, setting)) return [line];
        const id = `extensions:${item.id}:${setting.key}`;
        const heading = h(
          panel.row,
          { key: `fold-${setting.key}` },
          h(
            "div",
            { className: "steam-ui-kit-nested" },
            foldHeading(id, {
              title: under.length === 1 ? "1 setting" : `${under.length} settings`,
              sub: true,
            }),
          ),
        );
        return [
          line,
          heading,
          ...(isFolded(id) ? [] : under.map((child) => settingLine(item, child))),
        ];
      });
    };
    // One PanelSection per extension, one PanelSectionRow per line in it, the way Valve's own tabs
    // and decky's plugin list lay theirs out, each drawn as a kit block so the sections read as
    // the groups on the Performance and Quick Settings tabs do. Steam titles the tab itself, so the
    // panel adds no heading of its own.
    const sections = items.map((item) =>
      h(
        panel.section,
        { key: item.id },
        header(item),
        actionsRow(item),
        ...(isFolded(`extensions:${item.id}`) ? [] : settingLines(item)),
      ),
    );
    return h(
      "div",
      { className: "steam-ui-extensions-tab steam-ui-kit-blocks" },
      steamUiKitStyle(react),
      sections.length
        ? sections
        : h(
            panel.section,
            { key: "empty" },
            h(panel.row, null, "No Steam UI extensions are installed."),
          ),
    );
  }

  const tabDescender = (type) =>
    function SteamUiExtensionsTabDescend(props) {
      return descend(type(props), 0, props?.visible);
    };
  // One traversal: the tab list stops it, a function component is entered through a wrapper that
  // keeps descending, and anything else — a context provider, a host element, the portal the
  // menu's body is drawn through — is descended through its children.
  const descend = (element, depth, visible) => {
    if (depth > MaximumDescent) return element;
    if (isPortal(element)) {
      return mapPortalChildren(react, element, (kid) => descend(kid, depth + 1, visible));
    }
    if (!react.isValidElement(element)) return element;
    return (
      insertTab(element, visible) ??
      descendInto(react, element, descenderCache, tabDescender) ??
      mapChildren(react, element, (kid) => descend(kid, depth + 1, visible))
    );
  };

  const resolve = () => {
    runtime = getWebpackRuntime("extensions-tab");
    ui = resolveSteamSettingsComponents(runtime);
    panel = resolveSteamPanelComponents(runtime);
    const missing: string[] = ExtensionsTabRequired.filter((name) => !ui?.[name]);
    if (!panel) missing.push("panel section and row");
    if (missing.length) {
      lastError = `Native Steam components unavailable: ${missing.join(", ")}`;
      ui = null;
      panel = null;
      return false;
    }
    react = ui.react;
    icon = createIconRenderer(react);
    const qam = runtime.findUnique([QamToken]);
    if (!qam) {
      lastError = "Quick Access module was not a unique match";
      return false;
    }
    const exports = runtime(qam[0]);
    // Through the gate's own claim, or a re-resolve while the claim is held finds no memo.
    const candidates = Object.keys(exports).filter((name) => {
      const value = exports[name];
      return (
        value &&
        typeof value === "object" &&
        sourceMatches(unclaimedValue(value.type, claimKeys), [QamToken])
      );
    });
    if (candidates.length !== 1) {
      lastError = `Quick Access memo was ${candidates.length ? "ambiguous" : "absent"}`;
      return false;
    }
    memo = exports[candidates[0]];
    return true;
  };

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
    // The claim reaches the next mount only, and the Quick Access view is mounted at boot and kept.
    mounted.adopt(memo, memo.type);
    unsubscribe = subscribe(patchId, (state) => {
      const items = Array.isArray(state?.items) ? state.items.filter(usableItem).map(drawable) : [];
      const next = { items, revision: Number.isSafeInteger(state?.revision) ? state.revision : 0 };
      // The wrapper reads `desired` from its closure, so a publication changes nothing React can
      // see on its own, and an unchanged one needs no render at all.
      if (!publicationChanged(desired, next)) return;
      desired = next;
      mounted.rerender();
    });
    // The folds arrive on their own publication and redraw the tab the same way.
    unsubscribeFolds = subscribe(SteamFoldsPatchId, (state) => {
      openSections = folds.normalize(state);
      mounted.rerender();
    });
    unsubscribePlugins = subscribePluginFrontends(() => {
      for (const tabs of pluginTabLists)
        for (let index = tabs.length - 1; index >= 0; index--)
          if (
            tabs[index]?.steamUiPluginTab &&
            !pluginFrontendItems("tab").some((slot) => slot.id === tabs[index].steamUiPluginTab)
          )
            tabs.splice(index, 1);
      mounted.rerender();
    });
    return { ok: true, installed: true, reclaimed: claim.reclaimed };
  };

  // Ownership is given up before the gate forgets it owns anything: a failed release otherwise
  // leaves the claim live while every later remove() answers `absent` and never retries it.
  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    const released = releaseMember(memo, "type", claimKeys);
    if (!released.ok) {
      lastError = released.error ?? "Extensions tab release failed";
      return { ok: false, error: lastError };
    }
    mounted.release(memo.type);
    // Out of Valve's array again: removal restores exactly what was displaced.
    if (insertedInto) {
      const at = insertedInto.findIndex((tab) => tab && tab.steamUiExtensionsTab === true);
      if (at >= 0) insertedInto.splice(at, 1);
      insertedInto = null;
    }
    installed = false;
    unsubscribe = endSubscription(unsubscribe);
    unsubscribeFolds = endSubscription(unsubscribeFolds);
    unsubscribePlugins = endSubscription(unsubscribePlugins);
    for (const tabs of pluginTabLists)
      for (let index = tabs.length - 1; index >= 0; index--)
        if (tabs[index]?.steamUiPluginTab) tabs.splice(index, 1);
    pluginTabLists.clear();
    desired = { items: [], revision: 0 };
    descenderCache.clear();
    lastOutcome = "removed";
    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    resolved: !!memo,
    nativeComponentsResolved: !!ui && !!panel,
    claimed: memberClaimed(memo, "type", claimKeys),
    items: desired.items.length,
    revision: desired.revision,
    // Whether the claim reached the views already on screen; a claim that adopted nothing is inert.
    mounted: mounted.status(),
    lastOutcome,
    lastError,
  });

  return { install, remove, status };
}

registerGate("extensionsTab", createExtensionsTab());
