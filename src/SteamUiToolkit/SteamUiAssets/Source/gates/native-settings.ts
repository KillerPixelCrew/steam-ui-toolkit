// Append host fields to Steam's native Big Picture Settings pages. Offline read on 2026-10-06:
// one factory builds the Display/Power/Audio/Controller descriptor map through useMemo, then filters
// it into the native sidebar. Big Picture's own ordering already contains all four. Only Power's
// descriptor is hidden when Steam believes there is no battery. The shared memo claim changes that
// one descriptor while host sections exist; it never changes Steam's platform or battery identity.
function createNativeSettings() {
  const patchId = "steam-ui.native-settings";
  const RootOriginal = "__steamUiNativeSettingsRootOriginal";
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
  const RowKinds = new Set(["boolean", "choice", "range", "text", "color", "action", "note"]);

  let ui: any = null;
  let react: any = null;
  let jsxRuntime: any = null;
  let nativeRoot: any = null;
  let installed = false;
  let lastError = "";
  let lastOutcome = "never rendered";
  let unsubscribe: (() => void) | null = null;
  let state: any = { pages: [], revision: 0 };
  let mapCache = new WeakMap<object, any>();
  const local = createLocalStore();
  const roots = new Map<any, boolean>();
  const rendered = new Set<string>();

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
            row.kind === "choice" &&
            (!Array.isArray(row.choices) ||
              row.choices.some(
                (choice) => typeof choice?.value !== "string" || typeof choice.label !== "string",
              ))
          )
            return null;
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
    if (!installed || !current || current.disabled)
      return Promise.reject(new Error("This setting is unavailable."));
    return request(patchId, "set", { key: row.key, value }, nextActionGeneration(patchId));
  };

  function SteamUiNativeSettingsSections({ id }) {
    react.useSyncExternalStore(local.subscribe, local.revision);
    const drafts = useSteamSettingDrafts(react, state.revision);
    const change = drafts.change(send);
    const action = (row) => change(row, true);
    const page = installed ? state.pages.find((candidate) => candidate.id === id) : null;
    if (!page) return null;
    rendered.add(id);
    lastOutcome = `rendered ${id}: ${page.sections.length} section(s)`;
    return react.createElement(
      react.Fragment,
      null,
      steamUiKitStyle(react),
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
  const transformDescriptors = (value) => {
    if (!installed || !value || Array.isArray(value) || typeof value !== "object") return value;
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
    let next = value;
    for (const page of state.pages) {
      const key = NativePages[page.id];
      const original = value[key];
      if (next === value) next = { ...value };
      next[key] = {
        ...original,
        // The peers share Power's native services-initialized gate. Reveal only the battery
        // condition, never a page whose native services are still unavailable.
        visible:
          page.id === "power"
            ? value.Display.visible && value.Audio.visible && value.Controller.visible
            : original.visible,
        content: react.createElement(
          react.Fragment,
          null,
          original.content,
          react.createElement(SteamUiNativeSettingsSections, {
            id: page.id,
            key: `steam-ui-native-${page.id}`,
          }),
        ),
      };
    }
    mapCache.set(value, next);
    lastOutcome = `augmented ${state.pages.length} native page(s)`;
    return next;
  };

  // Same hooks as the original. A mounted root may be adopted without changing its hook order.
  function SteamUiNativeSettingsRoot(props) {
    return nativeRoot(props);
  }

  // Only settings topology changes need the native root to render. Values update inside the
  // separate subscribed sections. A root already mounted when the gate installs is adopted, and
  // the shared JSX transform covers future mounts. Durable original markers reclaim a previous
  // bridge's adopted roots without stacking wrappers.
  const refreshRoots = (remove = false) => {
    for (const [fiber, hadParent] of roots) {
      if (hadParent && !fiberAttached(fiber)) roots.delete(fiber);
    }
    walkFibers(reactRootFibers(), MaximumMountedNodes, (fiber) => {
      const type = fiber.type;
      if (
        type !== nativeRoot &&
        type !== SteamUiNativeSettingsRoot &&
        type?.[RootOriginal] !== nativeRoot
      )
        return;
      roots.set(fiber, fiberAttached(fiber));
    });
    for (const [fiber] of roots) {
      if (remove && fiber.type !== SteamUiNativeSettingsRoot) continue;
      retargetFiber(fiber, remove ? nativeRoot : SteamUiNativeSettingsRoot);
      invalidateFiberProps(fiber);
      requestRender(fiber);
    }
    if (remove) roots.clear();
  };

  const resolve = () => {
    const runtime = getWebpackRuntime("native-settings");
    if (!runtime.findUnique([...DescriptorTokens])) {
      lastError = "native Settings descriptors were not a unique match";
      return false;
    }
    ui = resolveSteamSettingsComponents(runtime);
    react = ui?.react;
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
      !ui ||
      required.some((key) => !ui[key]) ||
      typeof react.useSyncExternalStore !== "function"
    ) {
      lastError = "native Settings fields or subscription hooks are unavailable";
      return false;
    }
    nativeRoot = runtime.exported([...RootTokens], (value) =>
      sourceMatches(value, ["#Settings_Title", "show-icon", '"Settings"']),
    );
    jsxRuntime = runtime.resolve([...JsxRuntimeTokens]);
    if (
      !nativeRoot ||
      typeof jsxRuntime?.jsx !== "function" ||
      typeof jsxRuntime?.jsxs !== "function"
    ) {
      lastError = "native Settings root or JSX runtime was not a unique match";
      return false;
    }
    Object.defineProperty(SteamUiNativeSettingsRoot, RootOriginal, {
      value: nativeRoot,
      configurable: true,
    });
    return true;
  };

  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
    if (
      !attemptResolution(resolve, (error) => {
        lastError = "native Settings resolution failed: " + String(error);
      })
    )
      return { ok: false, error: lastError };
    const memo = interceptMemo(react, patchId, transformDescriptors);
    if (!memo.ok) return { ok: false, error: memo.error };
    const elements = interceptElements(jsxRuntime, patchId, (create, type, props, key) =>
      type === nativeRoot ? create(SteamUiNativeSettingsRoot, props, key) : undefined,
    );
    if (!elements.ok) {
      releaseMemo(react, patchId);
      return { ok: false, error: elements.error };
    }
    installed = true;
    lastError = "";
    unsubscribe = subscribe(patchId, (published) => {
      const next = normalize(published);
      if (!next) {
        lastError = "native Settings publication is invalid";
        state = { pages: [], revision: state.revision };
        mapCache = new WeakMap<object, any>();
        rendered.clear();
        refreshRoots();
        local.changed();
        lastOutcome = "invalid publication retracted native additions";
        return;
      }
      lastError = "";
      if (!publicationChanged(state, next)) return;
      const topologyChanged =
        JSON.stringify(state.pages.map((page) => page.id)) !==
        JSON.stringify(next.pages.map((page) => page.id));
      state = next;
      if (topologyChanged) {
        mapCache = new WeakMap<object, any>();
        rendered.clear();
        refreshRoots();
      }
      local.changed();
    });
    refreshRoots();
    return { ok: true, installed: true, adopted: roots.size };
  };

  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    const elements = releaseElements(jsxRuntime, patchId);
    if (!elements.ok) return { ok: false, error: elements.error };
    const memo = releaseMemo(react, patchId);
    if (!memo.ok) return { ok: false, error: memo.error };
    installed = false;
    unsubscribe = endSubscription(unsubscribe);
    state = { pages: [], revision: 0 };
    local.changed();
    refreshRoots(true);
    mapCache = new WeakMap<object, any>();
    rendered.clear();
    lastOutcome = "removed";
    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    resolved: !!ui && !!nativeRoot && !!jsxRuntime,
    claimed: memoIntercepted(react, patchId) && elementsIntercepted(jsxRuntime, patchId),
    claimsRemaining: memoIntercepted(react, patchId) || elementsIntercepted(jsxRuntime, patchId),
    ownedRoots: [...roots.keys()].filter((fiber) => fiber.type === SteamUiNativeSettingsRoot)
      .length,
    pages: state.pages.map((page) => page.id),
    renderedPages: [...rendered],
    lastOutcome,
    lastError,
  });

  return { install, remove, status };
}

registerGate("nativeSettings", createNativeSettings());
