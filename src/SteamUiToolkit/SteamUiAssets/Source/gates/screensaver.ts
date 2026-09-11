// Big Picture's Screensaver settings, with the host's timeout rows beside Steam's own screensaver
// timeout.
//
// Mapped from the September 2026 client beta's shipped bundle on 2026-09-11:
//
//   Settings page list          the Settings root's hook builds it with React.useMemo, one entry per
//                               page: { visible, title, icon, route, content }
//     /settings/customization   content is a module-local page returning a list of sections
//       Screensaver section     module-local; draws "#Settings_Customization_Screensaver" and calls
//                               Screensaver.ForceScreensaver for its preview button. Its last row is
//                               Steam's "When idle, start screensaver after", which writes the
//                               system_idle_screensaver_ac_sec client setting
//
// Steam keeps per-source idle settings on its Power page, and shows that page only on a machine it
// believes has a battery or under gamescope; everywhere else the Screensaver section carries the one
// plugged-in timeout. The report therefore carries both values and whether Steam believes there is a
// battery, and the host decides which of its own timeouts each one bounds.
//
// Nothing of Steam's is restyled or rebuilt. The page list passes through the one shared useMemo
// claim (ownership.ts); there the customization page is replaced by a wrapper that renders it and
// swaps the Screensaver section for a wrapper that renders the section with the host's rows appended.
// Both wrappers are cached by the component they wrap, so React keeps one stable type per original,
// and both render exactly what Steam shipped once the gate is removed.
//
// The host owns what the rows offer: which timeouts, their observed values, and only the choices the
// screensaver timeout allows. This half reads Steam's settings inside Steam's own mobx observer, so a
// change made on the page re-renders the rows and reaches the host at once.
function createScreensaverSettings() {
  const patchId = "steam-ui.screensaver";
  const MemoName = "screensaverSettings";

  const ReactTokens = ["react.transitional.element", "useState", "cloneElement", "createElement"] as const;
  const FieldTokens = ["DialogSlider_Container", "DropDownField", "SliderField"] as const;
  const DropdownMarkers = ["contextMenuPositionOptions", "childrenContainerWidth", "menuLabel"] as const;
  const SettingsTokens = ["get clientSettings()", "m_setDeferredSettings"] as const;
  const ObserverTokens = ["mobx-react-lite requires React with Hooks support"] as const;
  const RouteTokens = ["GameAPIOSK:", "/gameapiosk"] as const;
  const SectionTokens = ['"#Settings_Customization_Screensaver"', "ForceScreensaver"] as const;
  const PluggedInSetting = "system_idle_screensaver_ac_sec";
  const BatterySetting = "system_idle_screensaver_battery_sec";

  const MaximumRows = 4;
  const MaximumOptions = 16;
  const MaximumSeconds = 604800;
  const MaximumPages = 128;
  // Steam's settings can arrive after the gate installs. The first report is retried on this
  // bounded schedule rather than waiting for someone to open the page.
  const ReportAttempts = 60;
  const ReportIntervalMilliseconds = 2000;

  type Row = {
    id: string;
    label: string;
    description: string;
    seconds: number;
    options: { data: number; label: string }[];
    available: boolean;
  };

  let runtime;
  let react: any = null;
  let dropdown: any = null;
  let settings: any = null;
  let useObserver: any = null;
  let route = "";
  let installed = false;
  let lastError = "";
  let lastOutcome = "never rendered";
  let lastReport = "";
  let unsubscribe: (() => void) | null = null;
  let reportTimer: ReturnType<typeof setTimeout> | null = null;

  // The host's rows, replaced whole on each publication.
  let rows: Row[] = [];
  let revision = 0;
  const pending = new Set<string>();
  const listeners = new Set<() => void>();
  const pageCache = new Map();
  const sectionCache = new Map();
  const listCache = new WeakMap<object, unknown[]>();

  const notify = () => {
    revision += 1;
    for (const listener of [...listeners]) {
      try {
        listener();
      } catch {}
    }
  };
  const subscribeLocal = (listener) => {
    listeners.add(listener);
    return () => listeners.delete(listener);
  };
  const readRevision = () => revision;

  const text = (value, limit) => (typeof value === "string" ? value.slice(0, limit) : "");
  const seconds = (value) =>
    Number.isInteger(value) && value >= 0 && value <= MaximumSeconds ? value : null;

  // Validated rather than trusted: a malformed option list renders a dropdown whose entries select
  // nothing. A state that fails is dropped whole and the outcome says so.
  const normalize = (value): Row[] | null => {
    if (!value || typeof value !== "object" || !Array.isArray(value.rows)) return null;
    if (value.rows.length > MaximumRows) return null;
    const ids = new Set<string>();
    const next: Row[] = [];
    for (const row of value.rows) {
      if (!row || typeof row !== "object") return null;
      const id = text(row.id, 32);
      const current = seconds(row.seconds);
      if (
        !/^[a-z][a-z0-9-]{0,31}$/u.test(id) ||
        ids.has(id) ||
        current === null ||
        !Array.isArray(row.options) ||
        row.options.length > MaximumOptions
      )
        return null;
      const options: { data: number; label: string }[] = [];
      for (const option of row.options) {
        const optionSeconds = seconds(option?.seconds);
        const label = text(option?.label, 64);
        if (optionSeconds === null || !label) return null;
        options.push({ data: optionSeconds, label });
      }
      ids.add(id);
      next.push({
        id,
        label: text(row.label, 240),
        description: text(row.description, 240),
        seconds: current,
        options,
        available: row.available === true,
      });
    }
    return next;
  };

  // Steam's own screensaver timeouts from its client settings, and whether Steam believes the
  // machine has a battery (the test its Power page is shown on). Null until the settings arrive.
  const readScreensaver = () => {
    const values = settings?.clientSettings;
    const pluggedIn = seconds(values?.[PluggedInSetting]);
    if (pluggedIn === null) return null;
    return {
      acSeconds: pluggedIn,
      batterySeconds: seconds(values?.[BatterySetting]),
      battery: (window as any).SystemPowerStore?.batteryState?.bHasBattery === true,
    };
  };

  // Once per change, and again whenever the page opens, because that is when the host's reading of
  // its own timeouts is worth refreshing.
  const report = (reading, force) => {
    if (!installed || !reading) return false;
    const signature = JSON.stringify(reading);
    if (!force && signature === lastReport) return true;
    lastReport = signature;
    request(patchId, "report", reading).catch((error) => {
      lastError = "screensaver report failed: " + String(error);
    });
    return true;
  };

  const reportWhenReady = (attempt) => {
    reportTimer = null;
    if (!installed || report(readScreensaver(), false) || attempt >= ReportAttempts) return;
    reportTimer = setTimeout(() => reportWhenReady(attempt + 1), ReportIntervalMilliseconds);
  };

  const select = (row: Row, value) => {
    const chosen = seconds(value);
    if (!installed || chosen === null || chosen === row.seconds || pending.has(row.id)) return;
    pending.add(row.id);
    notify();
    request(patchId, "setTimeout", { row: row.id, seconds: chosen })
      .catch((error) => {
        lastError = "timeout change failed: " + String(error);
      })
      .finally(() => {
        pending.delete(row.id);
        notify();
      });
  };

  function SteamUiScreensaverTimeouts() {
    react.useSyncExternalStore(subscribeLocal, readRevision);
    const reading = useObserver
      ? useObserver(readScreensaver, "SteamUiScreensaverTimeouts")
      : readScreensaver();
    const signature = reading ? JSON.stringify(reading) : "";
    react.useEffect(() => {
      report(readScreensaver(), true);
    }, []);
    react.useEffect(() => {
      report(readScreensaver(), false);
    }, [signature]);
    if (!installed) return null;
    if (!rows.length) {
      lastOutcome = "no rows published";
      return null;
    }
    lastOutcome = `rendered ${rows.length} row(s)`;
    return react.createElement(
      react.Fragment,
      null,
      ...rows.map((row) =>
        react.createElement(dropdown, {
          key: `steam-ui-timeout-${row.id}`,
          label: row.label,
          description: row.description || undefined,
          rgOptions: row.options,
          selectedOption: row.seconds,
          disabled: !row.available || pending.has(row.id),
          controlled: true,
          onChange: (option) => select(row, option?.data),
        }),
      ),
    );
  }

  const childrenOf = (element) => {
    const children = element.props?.children;
    return Array.isArray(children) ? children : children === undefined ? [] : [children];
  };
  const keyed = (element) =>
    element.key === null ? element.props : { ...element.props, key: element.key };

  const isSection = (type) => {
    if (typeof type !== "function") return false;
    const source = String(type);
    return SectionTokens.every((token) => source.includes(token));
  };

  const sectionFor = (original) => {
    let wrapped = sectionCache.get(original);
    if (wrapped) return wrapped;
    wrapped = function SteamUiScreensaverSection(props) {
      const section = original(props);
      if (!installed || !react.isValidElement(section)) return section;
      return react.cloneElement(
        section,
        undefined,
        ...childrenOf(section),
        react.createElement(SteamUiScreensaverTimeouts, { key: "steam-ui-screensaver-timeouts" }),
      );
    };
    sectionCache.set(original, wrapped);
    return wrapped;
  };

  const pageFor = (original) => {
    let wrapped = pageCache.get(original);
    if (wrapped) return wrapped;
    wrapped = function SteamUiCustomizationPage(props) {
      const tree = original(props);
      if (!installed || !react.isValidElement(tree)) return tree;
      let found = 0;
      const children = childrenOf(tree).map((child) => {
        if (!react.isValidElement(child) || !isSection(child.type)) return child;
        found += 1;
        return react.createElement(sectionFor(child.type), keyed(child));
      });
      if (found !== 1) {
        lastOutcome = found
          ? "the screensaver section was not unique on the page"
          : "the screensaver section was not found on the page";
        return tree;
      }
      return react.cloneElement(tree, undefined, ...children);
    };
    pageCache.set(original, wrapped);
    return wrapped;
  };

  // The page list, with the customization page's content wrapped. The same input list always maps
  // to the same output list, so memo consumers downstream see a stable identity.
  const transformPages = (value) => {
    if (!installed || !Array.isArray(value) || !value.length || value.length > MaximumPages) return value;
    const first = value[0];
    if (!first || typeof first !== "object" || !("route" in first) || !("content" in first)) return value;
    const cached = listCache.get(value);
    if (cached) return cached;
    let index = -1;
    for (let at = 0; at < value.length; at++) {
      const item = value[at];
      if (item && typeof item === "object" && item.route === route && react.isValidElement(item.content)) {
        if (index >= 0) return value;
        index = at;
      }
    }
    if (index < 0 || typeof value[index].content.type !== "function") return value;
    const item = value[index];
    const next = value.slice();
    next[index] = {
      ...item,
      content: react.createElement(pageFor(item.content.type), keyed(item.content)),
    };
    listCache.set(value, next);
    return next;
  };

  const resolve = () => {
    runtime = getWebpackRuntime("screensaver-settings");
    react = runtime.resolve([...ReactTokens]);
    if (typeof react?.useSyncExternalStore !== "function" || typeof react?.useEffect !== "function") {
      lastError = "React runtime lacks useSyncExternalStore or useEffect";
      return false;
    }
    const fields = runtime.resolve([...FieldTokens]);
    const dropdowns = new Set(
      Object.values(fields).filter(
        (value) =>
          typeof value === "function" && DropdownMarkers.every((token) => String(value).includes(token)),
      ),
    );
    if (dropdowns.size !== 1) {
      lastError = "the dropdown field was not a unique match";
      return false;
    }
    dropdown = [...dropdowns][0];
    const pages = runtime.exported(
      [...RouteTokens],
      (value) => typeof value?.Settings?.Customization === "function",
    ) as any;
    route = pages.Settings.Customization();
    if (typeof route !== "string" || !route.startsWith("/")) {
      lastError = "the customization settings route is unavailable";
      return false;
    }
    if (!runtime.findUnique([...SectionTokens])) {
      lastError = "the screensaver section module was not a unique match";
      return false;
    }
    settings = runtime.exported(
      [...SettingsTokens],
      (value) => !!value && typeof value === "object" && typeof value.clientSettings === "object",
    );

    // Wanted, not required: without it the rows still follow the host, and a change to Steam's
    // timeout reaches the host on the section's next render. `status.tracking` says which.
    useObserver = null;
    const observer = runtime.findUnique([...ObserverTokens]);
    if (observer) {
      const exports = runtime(observer[0]);
      const hooks = Object.keys(exports).filter((name) => {
        const value = exports[name];
        return typeof value === "function" && value.length === 2 && String(value).includes('"observed"');
      });
      if (hooks.length === 1) useObserver = exports[hooks[0]];
    }
    return true;
  };

  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
    try {
      if (!resolve()) return { ok: false, error: lastError };
    } catch (error) {
      lastError = "screensaver settings resolution failed: " + String(error);
      return { ok: false, error: lastError };
    }

    installed = true;
    const intercepted = interceptMemo(react, MemoName, transformPages);
    if (!intercepted.ok) {
      installed = false;
      lastError = intercepted.error ?? "React useMemo could not be intercepted";
      return { ok: false, error: lastError };
    }
    lastError = "";
    unsubscribe = subscribe(patchId, (published) => {
      const next = normalize(published);
      if (!next) {
        lastOutcome = "state received but rejected by validation";
        return;
      }
      rows = next;
      notify();
    });
    reportWhenReady(0);
    return { ok: true, installed: true };
  };

  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    installed = false;
    if (unsubscribe) {
      unsubscribe();
      unsubscribe = null;
    }
    if (reportTimer) {
      clearTimeout(reportTimer);
      reportTimer = null;
    }
    // An open page re-renders without the rows; its wrappers pass Steam's own tree through.
    rows = [];
    pending.clear();
    lastReport = "";
    notify();
    const released = releaseMemo(react, MemoName);
    if (!released.ok) {
      lastError = released.error ?? "React useMemo could not be released";
      return { ok: false, error: lastError };
    }
    pageCache.clear();
    sectionCache.clear();
    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    resolved: !!react && !!dropdown && !!route,
    claimed: memoIntercepted(react, MemoName),
    route,
    settings: !!settings,
    tracking: !!useObserver,
    rows: rows.length,
    lastOutcome,
    lastReport,
    lastError,
  });

  return { install, remove, status };
}

registerGate("screensaver", createScreensaverSettings());
