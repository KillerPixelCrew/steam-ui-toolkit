// Host-owned per-game commands in Steam's library and gear context menu.
//
// The component already knows which app opened its menu. This gate only wraps that render method,
// reuses the exact item type Steam emitted, and sends a bounded app id plus host command identity.
// It never offers package JavaScript or React nodes a handle to Steam's private menu objects.
function createGameContextMenu() {
  const patchId = "steam-ui.game-context-menu";
  const renderClaimKeys = {
    marker: "__steamUiGameContextMenuRenderClaimed",
    original: "__steamUiGameContextMenuRenderOriginal",
  } as const;
  const MenuTokens = ["GetTargetApps", "BuildManageSubmenu", "GetPrimaryActionMenuItem"];
  const MaximumItems = 32;
  const MaximumDepth = 10;

  let runtime;
  let react;
  let menuComponent: any = null;
  let jsxRuntime;
  let installed = false;
  let unsubscribe: (() => void) | null = null;
  let desired: { items: any[]; revision: number } = { items: [], revision: 0 };
  let lastOutcome = "never rendered";
  let lastError = "";

  const validItem = (item) =>
    item &&
    typeof item.id === "string" &&
    item.id.length > 0 &&
    item.id.length <= 96 &&
    typeof item.label === "string" &&
    item.label.length > 0 &&
    item.label.length <= 160;

  const appIdFor = (instance) => {
    try {
      const apps = instance?.GetTargetApps?.();
      const appId = Array.isArray(apps) && apps.length === 1 ? apps[0]?.appid : null;
      return Number.isSafeInteger(appId) && appId > 0 ? appId : null;
    } catch (error) {
      lastError = "Game menu app lookup failed: " + String(error);
      return null;
    }
  };

  // Item components are module-private. A menu already rendered at least one when it reached this
  // wrapper, so borrow its actual type rather than resolving unrelated exports by a CSS name.
  const findItemType = (element, depth = 0) => {
    if (depth > MaximumDepth || !react.isValidElement(element)) return null;
    if (typeof element.props?.onSelected === "function" && element.type) return element.type;
    for (const child of react.Children.toArray(element.props?.children)) {
      const found = findItemType(child, depth + 1);
      if (found) return found;
    }
    return null;
  };

  const containsPropertiesAction = (element, depth = 0) => {
    if (depth > MaximumDepth || !react.isValidElement(element)) return false;
    if (
      typeof element.props?.onSelected === "function" &&
      String(element.props.onSelected).includes("AppProperties")
    ) {
      return true;
    }
    return react.Children.toArray(element.props?.children).some((child) =>
      containsPropertiesAction(child, depth + 1),
    );
  };

  const insertItems = (root, appId) => {
    if (!react.isValidElement(root) || desired.items.length === 0) return root;
    const itemType = findItemType(root);
    if (!itemType) {
      lastOutcome = "menu item type absent";
      return root;
    }
    const children = react.Children.toArray(root.props?.children);
    if (children.length === 0) {
      lastOutcome = "menu root had no children";
      return root;
    }
    const ownItems = desired.items.map((item) =>
      react.createElement(
        itemType,
        {
          key: `steam-ui-game-context-menu-${item.id}`,
          onSelected: () => {
            void request(
              patchId,
              "activate",
              { appId, id: item.id },
              nextActionGeneration(patchId),
            ).then(
              (answer: any) => {
                if (answer?.route) navigateSteamRoute(answer.route);
              },
              () => {
                // A refusal stays host-authoritative and must not make Steam's menu fail.
              },
            );
          },
        },
        item.label,
      ),
    );
    const beforeProperties = children.findIndex((child) => containsPropertiesAction(child));
    const index = beforeProperties >= 0 ? beforeProperties : children.length;
    children.splice(index, 0, ...ownItems);
    lastOutcome = `app=${appId} commands=${ownItems.length} ${beforeProperties >= 0 ? "before-properties" : "appended"}`;
    return react.cloneElement(root, undefined, children);
  };

  const resolve = () => {
    runtime = getWebpackRuntime("game-context-menu");
    react = resolveReact(runtime);
    if (!react) {
      lastError = "React runtime was not a unique match";
      return false;
    }
    const menu = runtime.findUnique(MenuTokens);
    if (!menu) {
      lastError = "Game context menu module was not a unique match";
      return false;
    }
    jsxRuntime = runtime.resolve([...JsxRuntimeTokens]);
    if (!jsxRuntime) {
      lastError = "JSX runtime was not a unique match";
      return false;
    }
    return true;
  };

  const claimMenuRender = (candidate) => {
    if (
      !candidate ||
      !candidate.prototype ||
      typeof candidate.prototype.render !== "function" ||
      menuComponent === candidate
    ) {
      return;
    }
    if (menuComponent) {
      lastError = "Game context menu component changed while claimed";
      return;
    }
    const claim = claimMember(candidate.prototype, "render", renderClaimKeys, (original: any) => {
      if (typeof original !== "function") return original;
      return function SteamUiGameContextMenuRender(this: any, ...args: any[]) {
        const root = original.apply(this, args);
        const appId = appIdFor(this);
        return appId === null ? root : insertItems(root, appId);
      };
    });
    if (!claim.ok) {
      lastError = "Game context menu render claim failed: " + claim.error;
      return;
    }
    menuComponent = candidate;
    lastError = "";
  };

  // SharedJSContext owns React but has no visible DOM. Observe the existing shared JSX claim:
  // the private class passes through it before its first render, so that same opening gets rows.
  const captureMenu = (_create, candidate) => {
    if (menuComponent || typeof candidate !== "function") return;
    const prototype = candidate.prototype;
    if (
      prototype &&
      typeof prototype.render === "function" &&
      MenuTokens.every((name) => typeof prototype[name] === "function")
    ) {
      claimMenuRender(candidate);
    }
  };

  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
    if (
      !attemptResolution(
        resolve,
        (error) => (lastError = "Game context menu resolution failed: " + String(error)),
      )
    ) {
      return { ok: false, error: lastError };
    }
    const claim = interceptElements(jsxRuntime, patchId, captureMenu);
    if (!claim.ok) {
      lastError = claim.error ?? "Game context menu JSX interception failed";
      return { ok: false, error: lastError };
    }
    installed = true;
    lastError = "";
    unsubscribe = subscribe(patchId, (state) => {
      const items = Array.isArray(state?.items)
        ? state.items.filter(validItem).slice(0, MaximumItems)
        : [];
      desired = { items, revision: Number.isSafeInteger(state?.revision) ? state.revision : 0 };
    });
    return { ok: true, installed: true, observing: true };
  };

  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    installed = false;
    unsubscribe = endSubscription(unsubscribe);
    const releasedElements = releaseElements(jsxRuntime, patchId);
    desired = { items: [], revision: 0 };
    const releasedRender = releaseMember(menuComponent?.prototype, "render", renderClaimKeys);
    menuComponent = null;
    if (!releasedRender.ok || !releasedElements.ok) {
      lastError =
        releasedRender.error ?? releasedElements.error ?? "Game context menu release failed";
      return { ok: false, error: lastError };
    }
    lastOutcome = "removed";
    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    resolved: !!runtime && !!react,
    observing: installed && elementsIntercepted(jsxRuntime, patchId),
    menuClaimed: memberClaimed(menuComponent?.prototype, "render", renderClaimKeys),
    items: desired.items.length,
    revision: desired.revision,
    lastOutcome,
    lastError,
  });

  return { install, remove, status };
}

registerGate("gameContextMenu", createGameContextMenu());
