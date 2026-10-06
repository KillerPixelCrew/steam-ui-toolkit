// @fragment plugin-frontends
const pluginFrontendEntries = new Map<string, any>();
const pluginFrontendSlots = new Map<string, any>();
const pluginFrontendListeners = new Set<() => void>();
const pluginFrontendChanged = () => {
  for (const listener of pluginFrontendListeners) listener();
};
const subscribePluginFrontends = (listener: () => void) => {
  pluginFrontendListeners.add(listener);
  return () => pluginFrontendListeners.delete(listener);
};
const pluginFrontendItems = (kind: string) =>
  [...pluginFrontendSlots.values()].filter(
    (slot) => slot.kind === kind && !slot.entry.failed && !slot.entry.closed,
  );
const pluginFrontendElements = (kind: string, react: any, props: any = {}) =>
  pluginFrontendItems(kind).map((slot) => slot.element(react, props));

function createPluginFrontends() {
  const documents = () => {
    const docs = new Set<Document>([document]);
    for (const popup of (window as any).g_PopupManager?.GetPopups?.() ?? []) {
      if (popup?.m_popup?.document) docs.add(popup.m_popup.document);
    }
    return [...docs];
  };
  const reactRuntime = () => resolveReact(getWebpackRuntime("plugin-frontends"));
  const report = (entry: any, module: string, error: unknown) => {
    if (entry.failed || entry.closed) return;
    const reason = String((error as any)?.stack ?? error).slice(0, 4096);
    // Shut down every sibling synchronously before reporting to the host.
    for (const sibling of pluginFrontendEntries.values()) {
      if (sibling.owner !== entry.owner) continue;
      sibling.failed = `${module}: ${reason}`;
      void cleanup(sibling);
    }
    void request(entry.id, "failure", { module, reason }).catch(() => {});
  };
  const guard = (entry: any, module: string, callback: any) =>
    function (this: any, ...args: any[]) {
      if (entry.failed || entry.closed) return;
      try {
        const result = callback.apply(this, args);
        if (result && typeof result.then === "function") {
          return Promise.resolve(result).catch((error) => {
            report(entry, module, error);
          });
        }
        return result;
      } catch (error) {
        report(entry, module, error);
        return undefined;
      }
    };
  const cleanup = async (entry: any) => {
    entry.stopDocuments?.();
    entry.stopDocuments = null;
    for (const [key, slot] of pluginFrontendSlots)
      if (slot.entry === entry) pluginFrontendSlots.delete(key);
    pluginFrontendChanged();
    let failure = "";
    while (entry.cleanups.length) {
      try {
        const work = entry.cleanups.pop()();
        if (work && typeof work.then === "function") {
          let timer: any;
          try {
            await Promise.race([
              work,
              new Promise((_, reject) => {
                timer = setTimeout(() => reject(new Error("module teardown timed out")), 2000);
              }),
            ]);
          } finally {
            clearTimeout(timer);
          }
        }
      } catch (error) {
        failure ||= String(error);
      }
    }
    if (failure) entry.cleanupFailure = failure;
  };
  const boundary = (react: any, entry: any, module: string, render: any) => {
    class ModuleBoundary extends react.Component {
      state = { failed: false };
      stop: any;
      componentDidMount() {
        this.stop = subscribePluginFrontends(guard(entry, module, () => this.forceUpdate()));
      }
      componentWillUnmount() {
        this.stop?.();
      }
      static getDerivedStateFromError() {
        return { failed: true };
      }
      componentDidCatch(error: unknown) {
        report(entry, module, error);
      }
      render() {
        return this.state.failed || entry.failed || entry.closed ? null : this.props.children;
      }
    }
    const Render = guard(entry, module, (props: any) => render(react, props));
    return (props: any) =>
      react.createElement(ModuleBoundary, null, react.createElement(Render, props));
  };
  const ready = async (entry: any, module: string, check: () => any) => {
    const until = Date.now() + 5000;
    let last: any;
    while (!entry.closed && !entry.failed) {
      last = check();
      if (last === true || last?.ok === true) return;
      if (Date.now() >= until)
        throw new Error(`${module}: readiness failed: ${last?.error ?? "gate never ready"}`);
      await new Promise((resolve) => setTimeout(resolve, 50));
    }
    throw new Error(`${module}: module stopped during readiness`);
  };
  const load = async (spec: any) => {
    const prior = pluginFrontendEntries.get(spec.id);
    if (prior) return status(spec.id);
    const entry: any = {
      ...spec,
      failed: "",
      closed: false,
      cleanups: [],
      pending: [],
      verifiers: [],
      contributionIds: new Set(),
      styles: new Set(),
      ready: false,
    };
    pluginFrontendEntries.set(spec.id, entry);
    const sourceUrl = `steam-ui-plugin://${encodeURIComponent(spec.owner)}/${encodeURIComponent(spec.module)}.js`;
    const knownWindows = new Set<any>();
    const asyncError = (event: any) => {
      const origin = `${event.filename ?? ""}\n${event.error?.stack ?? ""}\n${event.reason?.stack ?? ""}`;
      if (!origin.includes(sourceUrl)) return;
      event.preventDefault?.();
      report(entry, spec.module, event.error ?? event.reason ?? event.message);
    };
    const refreshDocuments = () => {
      if (entry.closed || entry.failed) return;
      for (const doc of documents()) {
        const target = doc.defaultView;
        if (target && !knownWindows.has(target)) {
          knownWindows.add(target);
          target.addEventListener("error", asyncError);
          target.addEventListener("unhandledrejection", asyncError);
        }
        if (entry.style && ![...entry.styles].some((style: any) => style.ownerDocument === doc)) {
          const style = doc.createElement("style");
          style.dataset.steamUiPlugin = spec.id;
          style.textContent = entry.style;
          (doc.head ?? doc.documentElement).appendChild(style);
          entry.styles.add(style);
        }
      }
    };
    refreshDocuments();
    const interval = setInterval(refreshDocuments, 500);
    entry.stopDocuments = () => {
      clearInterval(interval);
      for (const target of knownWindows) {
        target.removeEventListener("error", asyncError);
        target.removeEventListener("unhandledrejection", asyncError);
      }
      for (const style of entry.styles) style.remove();
      entry.styles.clear();
    };
    const register = (
      kind: string,
      id: string,
      value: any,
      render: any,
      gate: string,
      gateKind?: string,
    ) => {
      const module = `${spec.module}/${id}`;
      const key = `${spec.id}/${id}`;
      if (!id || entry.contributionIds.has(id))
        throw new Error(`Invalid or duplicate module: ${module}`);
      entry.contributionIds.add(id);
      if (kind === "page" && (typeof value?.path !== "string" || !value.path.startsWith("/")))
        throw new Error(`${module}: page path must be absolute`);
      if (render != null && typeof render !== "function")
        throw new Error(`${module}: renderer must be a function`);
      const work = (async () => {
        await ready(entry, module, () => (bridge.gate(gate) as any)?.install(gateKind));
        const react = reactRuntime();
        if (!react) throw new Error("Steam React runtime unavailable");
        const Component = render ? boundary(react, entry, module, render) : null;
        const slot = {
          kind,
          id: key,
          entry,
          value: { ...value, id: key },
          element: (_react: any, props: any) =>
            Component ? react.createElement(Component, { ...props, key }) : null,
        };
        pluginFrontendSlots.set(key, slot);
        entry.verifiers.push({
          module,
          check: () => {
            const state: any = (bridge.gate(gate) as any)?.status(gateKind);
            return state?.installed === true || state?.registered === true;
          },
        });
        if (kind === "page") {
          steamPageRenderers.set(key, (_react, page) => slot.element(react, { page }));
          entry.cleanups.push(() => steamPageRenderers.delete(key));
        }
        entry.cleanups.push(() => {
          pluginFrontendSlots.delete(key);
          pluginFrontendChanged();
        });
        pluginFrontendChanged();
        return () => {
          pluginFrontendSlots.delete(key);
          pluginFrontendChanged();
        };
      })().catch((error) => {
        report(entry, module, error);
      });
      entry.pending.push(work);
      return work;
    };
    const api: any = {
      id: spec.id,
      module: spec.module,
      react: reactRuntime(),
      bridge,
      // The plugin is trusted session code. These are convenience primitives, not permissions.
      resolveModules: getWebpackRuntime,
      resolveComponents: () =>
        resolveSteamSettingsComponents(getWebpackRuntime("plugin-frontends")),
      uiKit: { section: renderSteamUiGroup, header: renderSteamUiHeader, note: renderSteamUiEmpty },
      guard: (callback: any) => guard(entry, spec.module, callback),
      onDispose: (callback: any) => entry.cleanups.push(callback),
      call: (method: string, payload: any = null) =>
        request(spec.id, "invoke", { method, payload }),
      subscribe: (callback: any) => {
        const stop = subscribe(spec.id, guard(entry, spec.module, callback));
        entry.cleanups.push(stop);
        return stop;
      },
      ready: (check: any) => ready(entry, spec.module, check),
      registerPage: (id: string, page: any, render: any) =>
        register("page", id, page, render, "pages"),
      registerMenuEntry: (id: string, item: any) =>
        register(
          "menu",
          id,
          {
            ...item,
            frontendAction: item.onActivate
              ? guard(entry, `${spec.module}/${id}`, item.onActivate)
              : undefined,
          },
          null,
          "navigationPanel",
        ),
      registerQuickAccessTab: (id: string, tab: any, render: any) =>
        register("tab", id, tab, render, "extensionsTab"),
      registerQuickAccessRow: (id: string, placement: string, render: any) =>
        register(
          placement === "quickSettings" ? "quickSettings" : "perf",
          id,
          {},
          render,
          "nativeComponents",
          "settingsSections",
        ),
      registerLibraryAddition: (id: string, render: any) =>
        register("library", id, {}, render, "libraryBadge"),
      registerGamePageAddition: (id: string, render: any) =>
        register("gamePage", id, {}, render, "libraryDetails"),
      addStyle: (css: string) => {
        entry.style = `${entry.style ?? ""}\n${css}`;
        for (const style of entry.styles) style.textContent = entry.style;
        refreshDocuments();
      },
      registerPatch: (id: string, patch: any) => {
        const module = `${spec.module}/${id}`;
        const work = (async () => {
          await ready(entry, module, () => patch.probe());
          entry.cleanups.push(() => patch.remove());
          await patch.apply();
          await ready(entry, module, () => patch.verify());
          entry.verifiers.push({ module, check: () => patch.verify() });
        })().catch((error) => report(entry, module, error));
        entry.pending.push(work);
        return work;
      },
      setTimeout: (callback: any, milliseconds: number) => {
        const timer = setTimeout(guard(entry, spec.module, callback), milliseconds);
        entry.cleanups.push(() => clearTimeout(timer));
        return timer;
      },
      setInterval: (callback: any, milliseconds: number) => {
        const timer = setInterval(guard(entry, spec.module, callback), milliseconds);
        entry.cleanups.push(() => clearInterval(timer));
        return timer;
      },
      addEventListener: (target: any, type: string, callback: any, options?: any) => {
        const wrapped = guard(entry, spec.module, callback);
        target.addEventListener(type, wrapped, options);
        entry.cleanups.push(() => target.removeEventListener(type, wrapped, options));
      },
    };
    try {
      const execute = new Function("api", `${spec.script}\n//# sourceURL=${sourceUrl}`);
      const dispose = await execute(api);
      if (typeof dispose === "function") entry.cleanups.push(dispose);
      await Promise.all(entry.pending);
      if (entry.failed || entry.closed) {
        await cleanup(entry);
        return status(spec.id);
      }
      entry.ready = true;
      return { ok: true };
    } catch (error) {
      report(entry, spec.module, error);
      return status(spec.id);
    }
  };
  const status = async (id: string) => {
    const entry = pluginFrontendEntries.get(id);
    if (entry?.ready && !entry.failed && !entry.closed) {
      for (const verifier of entry.verifiers) {
        try {
          const result = await verifier.check();
          if (result !== true && result?.ok !== true) {
            report(entry, verifier.module, "module no longer passes verification");
            break;
          }
        } catch (error) {
          report(entry, verifier.module, error);
          break;
        }
      }
    }
    return {
      ok: !!entry?.ready && !entry.failed && !entry.closed,
      error: entry?.failed || (entry ? "frontend not ready" : "frontend absent"),
    };
  };
  const unload = async (id: string) => {
    const entry = pluginFrontendEntries.get(id);
    if (!entry) return { ok: true };
    entry.closed = true;
    await cleanup(entry);
    if (entry.cleanupFailure) return { ok: false, error: entry.cleanupFailure };
    pluginFrontendEntries.delete(id);
    return { ok: true };
  };
  return {
    probe: () => ({ ok: !!reactRuntime() }),
    load,
    status,
    unload,
    remove: () => {
      for (const id of pluginFrontendEntries.keys()) void unload(id);
      return { ok: true };
    },
  };
}
registerGate("pluginFrontends", createPluginFrontends());
