/**
 * Contributes host settings pages to Steam using reversible member and render claims.
 * @returns The defineGate gate; remove must release it before its bridge is replaced.
 */

function createNativeSettings() {
  const patchId = "steam-ui.native-settings";
  // The durable keys of the adopted Settings root. The original key is the one earlier bridges wrote,
  // so a root a previous bridge adopted is reclaimed rather than wrapped twice.
  const RootKeys = {
    marker: "__steamUiNativeSettingsRootClaimed",
    original: "__steamUiNativeSettingsRootOriginal",
  } as const;
  const DescriptorTokens = [
    "#Settings_Page_Display",
    "#Settings_Page_Power",
    "#Settings_Page_Audio",
    "#Settings_Page_Controller",
  ] as const;
  const RootTokens = ["#Settings_Title", "SettingsModal", "SettingsTitleBar"] as const;
  const NativePages = {
    display: "Display",
    power: "Power",
    audio: "Audio",
    controller: "Controller",
  };
  const RowKinds = new Set(["boolean", "choice", "range", "text", "color", "action", "note", "resolution-list"]);

  let ui: any = null;
  let react: any = null;
  let jsxRuntime: any = null;
  let nativeRoot: any = null;
  let publicationError = "";
  let lastOutcome = "never rendered";
  let state: any = { pages: [], revision: 0 };
  let mapCache = new WeakMap<object, any>();
  const local = createLocalStore();
  const rendered = new Set<string>();
  // The adopted Settings roots: every root mounted at install, and every one Steam mounts later
  // through the JSX transform, carries this claim's marked wrapper.
  let component: ClaimedComponent | null = null;
  const installed = () => gate.installed();

  // Bounded, whole-state validation: duplicate page/row identities are unsafe and an unsupported
  // row must not become a control that sends a value the host did not describe.
  const normalize = (value) => {
    if (value === null) return { pages: [], revision: 0 };
    if (
      !Array.isArray(value?.pages) ||
      value.pages.length > 4 ||
      !Number.isSafeInteger(value.revision)
    )
      return null;
    const pageIds = new Set();
    const rowIds = new Set();
    for (const page of value.pages) {
      if (
        !Object.hasOwn(NativePages, page?.id) ||
        pageIds.has(page.id) ||
        !Array.isArray(page.sections)
      )
        return null;
      if (page.sections.length > 64) return null;
      pageIds.add(page.id);
      for (const section of page.sections) {
        if (!Array.isArray(section?.rows) || section.rows.length > 256) return null;
        for (const row of section.rows) {
          if (
            typeof row?.key !== "string" ||
            !row.key.trim() ||
            row.key.length > 1024 ||
            rowIds.has(row.key) ||
            typeof row.label !== "string" ||
            !RowKinds.has(row.kind)
          )
            return null;
          if (
            (row.kind === "choice" || row.kind === "resolution-list") &&
            (!Array.isArray(row.choices) ||
              row.choices.some(
                (choice) => typeof choice?.value !== "string" || typeof choice.label !== "string",
              ))
          )
            return null;
          if (row.kind === "resolution-list") {
            const editor = row.resolutionEditor;
            const validDimension = (value) => Number.isSafeInteger(value) && value >= 100 && value <= 15999;
            if (!editor || row.choices.length > 512 || !Array.isArray(editor.removable)
                || (editor.restoreAvailable != null && typeof editor.restoreAvailable !== "boolean")
                || editor.removable.some((value) => !row.choices.some((item) => item.value === value))
                || !((editor.nativeWidth == null && editor.nativeHeight == null)
                    || (validDimension(editor.nativeWidth) && validDimension(editor.nativeHeight)))) return null;
          }
          if (
            row.kind === "range" &&
            (!Number.isFinite(row.number) ||
              !Number.isFinite(row.minimum) ||
              !Number.isFinite(row.maximum) ||
              !Number.isFinite(row.step) ||
              row.step <= 0 ||
              row.minimum > row.maximum)
          )
            return null;
          rowIds.add(row.key);
        }
      }
    }
    return {
      pages: value.pages.filter((page) => page.sections.some((section) => section.rows.length)),
      revision: value.revision,
    };
  };

  const send = (row, value) => {
    const current = state.pages
      .flatMap((page) => page.sections.flatMap((section) => section.rows))
      .find((candidate) => candidate.key === row.key);
    if (!installed() || !current || current.disabled)
      return Promise.reject(new Error("This setting is unavailable."));
    return request(patchId, "set", { key: row.key, value });
  };

  function SteamUiNativeSettingsSections({ id }) {
    react.useSyncExternalStore(local.subscribe, local.revision);
    react.useEffect(() => {
      if (id !== "controller") return;
      return () => {
        // Stop is an idempotent host action even after its rows retract. Reload/disconnect also
        // stop through the host and the output owner, and every preview has a bounded timer.
        void request(patchId, "set", { key: "rumble.stop", value: true }).catch(() => {});
      };
    }, [id]);
    const drafts = useSteamSettingDrafts(react, state.revision);
    const change = drafts.change(send);
    const action = (row) => change(row, true);
    const page = installed() ? state.pages.find((candidate) => candidate.id === id) : null;
    if (!page) return null;
    rendered.add(id);
    lastOutcome = `rendered ${id}: ${page.sections.length} section(s)`;
    return react.createElement(
      react.Fragment,
      null,
      ...page.sections
        .filter((section) => section.rows.length)
        .map((section, index) =>
          react.createElement(
            ui.settingsSection,
            {
              key: `steam-ui-native-${id}-${section.id ?? index}`,
              label: section.title ?? undefined,
            },
            ...section.rows.map((row) =>
              renderSteamSettingRow(ui, drafts.row(row), drafts.draft(row), change, action),
            ),
          ),
        ),
    );
  }

  // Match Steam's semantic descriptor keys and the native element handles already in its map.
  // No native component is replaced: the route, label, glyph and content remain Steam's own.
  // Each native page renders its own scrolling DialogBody. In the desktop client that body is a
  // flex:1, overflow:auto child of a fixed-height column, so host sections placed beside it took
  // their full height and squeezed Steam's page into the remainder. The wrapper becomes the page's
  // one DialogBody instead, and the kit stylesheet lets Steam's body inside it keep its full height
  // (Big Picture's dialog bodies never scroll, so it lays out as before there).
  const transformDescriptors = (value) => {
    if (!installed() || !value || Array.isArray(value) || typeof value !== "object") return value;
    if (
      !Object.values(NativePages).every((key) => {
        const page = value[key];
        return (
          page &&
          typeof page.route === "string" &&
          page.route.startsWith("/") &&
          typeof page.visible === "boolean" &&
          react.isValidElement(page.content)
        );
      })
    )
      return value;
    const cached = mapCache.get(value);
    if (cached) return cached;
    const next = { ...value };
    const hostPower = state.pages.some((page) => page.id === "power");
    for (const [id, key] of Object.entries(NativePages)) {
      const original = value[key];
      next[key] = {
        ...original,
        // The peers share Power's native services-initialized gate. Reveal only the battery
        // condition, never a page whose native services are still unavailable.
        visible:
          id === "power" && hostPower
            ? original.visible ||
              (value.Display.visible && value.Audio.visible && value.Controller.visible)
            : original.visible,
        content: react.createElement(
          "div",
          { className: "DialogBody steam-ui-kit-settings-body" },
          original.content,
          react.createElement(SteamUiNativeSettingsSections, {
            id,
            key: `steam-ui-native-${id}`,
          }),
          steamUiKitStyle(react),
        ),
      };
    }
    mapCache.set(value, next);
    lastOutcome = `mounted four native slots; ${state.pages.length} page(s) published`;
    return next;
  };

  // Only host Power presence needs the native root to render. Other capability changes update the
  // always-mounted slots, including a Controller slot that was previously empty. Values update inside the
  // separate subscribed sections. A root already mounted when the gate installs is adopted, and
  // the shared JSX transform covers future mounts. Durable original markers reclaim a previous
  // bridge's adopted roots without stacking wrappers.
  const nativeReactRoots = () => {
    const documents = new Set<any>();
    if (typeof document !== "undefined") documents.add(document);
    // The same popup documents the theme gate reads.
    for (const popup of steamPopupDocuments().slice(0, 256)) documents.add(popup.document);
    const found = new Set<any>();
    for (const doc of documents) {
      try {
        for (const fiber of reactRootFibers(doc)) found.add(fiber);
      } catch {}
    }
    return [...found];
  };

  // Pure: the four descriptors, the Settings root, React, the JSX runtime, the fields and Steam's
  // Panel must each be one match, and every field the sections draw with must resolve. A client
  // without any of them is incompatible; nothing here waits on Settings being opened.
  const find = (runtime) => {
    const facts = {
      descriptors: runtime.count([...DescriptorTokens]),
      root: runtime.count([...RootTokens]),
      react: runtime.count([...ReactTokens]),
      jsx: runtime.count([...JsxRuntimeTokens]),
      fields: runtime.count([...FieldTokens]),
      focusable: runtime.count([...NativeFocusableTokens]),
    };
    const ambiguous = Object.keys(facts).filter((name) => facts[name] !== 1);
    if (ambiguous.length) {
      return gateIncompatible(
        `native Settings modules were not a unique match: ${ambiguous.join(", ")}`,
        facts,
      );
    }
    const resolved = resolveSteamSettingsComponents(runtime);
    const required = [
      "react",
      "settingsSection",
      "toggleField",
      "dropdown",
      "sliderField",
      "textField",
      "valueField",
      "dialogButton",
      "smallButton",
      "showModal",
      "modalRoot",
      "confirmModal",
    ];
    if (
      !resolved ||
      required.some((key) => !resolved[key]) ||
      typeof resolved.react.useSyncExternalStore !== "function"
    ) {
      return gateIncompatible("native Settings fields or subscription hooks are unavailable", facts);
    }
    const root = runtime.exported([...RootTokens], (value) =>
      sourceMatches(value, ["#Settings_Title", "show-icon", '"Settings"']),
    );
    const jsx = runtime.resolve([...JsxRuntimeTokens]);
    if (!root || typeof jsx?.jsx !== "function" || typeof jsx?.jsxs !== "function") {
      return gateIncompatible("native Settings root or JSX runtime was not a unique match", facts);
    }
    return gateReady({ ui: resolved, nativeRoot: root, jsxRuntime: jsx }, facts);
  };

  const gate = defineGate({
    name: "native-settings",
    find,
    bind: (handles, ledger) => {
      ui = handles.ui;
      react = ui.react;
      nativeRoot = handles.nativeRoot;
      jsxRuntime = handles.jsxRuntime;
      const memo = ledger.memo(react, patchId, transformDescriptors);
      if (!memo.ok) return memo.error ?? "React useMemo could not be intercepted";
      // A root already mounted is adopted; the JSX transform covers future mounts with the same
      // marked wrapper, so release hands back every root running it. The wrapper keeps the original's
      // hooks: it renders the original and nothing else.
      const claimed = ledger.component({
        keys: RootKeys,
        match: (_fiber, type) => type === nativeRoot,
        wrap: (original) =>
          function SteamUiNativeSettingsRoot(props) {
            return original(props);
          },
        roots: nativeReactRoots,
      });
      if (!claimed.ok) return claimed.error;
      component = claimed;
      const elements = ledger.elements(jsxRuntime, patchId, (create, type, props, key) =>
        type === nativeRoot ? create(claimed.wrapperFor(nativeRoot), props, key) : undefined,
      );
      if (!elements.ok) return elements.error ?? "the JSX runtime could not be intercepted";
      ledger.subscribe(patchId, (published) => {
        const next = normalize(published);
        if (!next) {
          publicationError = "native Settings publication is invalid";
          const hadPower = state.pages.some((page) => page.id === "power");
          state = { pages: [], revision: state.revision };
          rendered.clear();
          if (hadPower) {
            mapCache = new WeakMap<object, any>();
            component?.rerender(true);
          }
          local.changed();
          lastOutcome = "invalid publication retracted native additions";
          return;
        }
        publicationError = "";
        if (!publicationChanged(state, next)) return;
        const powerPresenceChanged =
          state.pages.some((page) => page.id === "power") !==
          next.pages.some((page) => page.id === "power");
        state = next;
        if (powerPresenceChanged) {
          mapCache = new WeakMap<object, any>();
          rendered.clear();
          component?.rerender(true);
        }
        local.changed();
      });
      // A mounted section draws nothing from the moment the gate goes, before its roots are handed back.
      ledger.cleanup("published pages", () => {
        state = { pages: [], revision: 0 };
        local.changed();
      });
      return { adopted: claimed.status().adopted };
    },
    unbind: () => {
      component = null;
      state = { pages: [], revision: 0 };
      local.changed();
      mapCache = new WeakMap<object, any>();
      rendered.clear();
      publicationError = "";
      lastOutcome = "removed";
    },
    status: () => {
      const roots = component?.status() ?? { adopted: 0, scheduled: false, stale: 0 };
      return {
        claimed: memoIntercepted(react, patchId) && elementsIntercepted(jsxRuntime, patchId),
        claimsRemaining: memoIntercepted(react, patchId) || elementsIntercepted(jsxRuntime, patchId),
        ownedRoots: roots.adopted - roots.stale,
        mounted: roots,
        pages: state.pages.map((page) => page.id),
        renderedPages: [...rendered],
        publicationError,
        lastOutcome,
      };
    },
  });
  return gate;
}

registerGate("nativeSettings", createNativeSettings());
