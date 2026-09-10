// Steam's left slideout navigation panel, as an extension surface.
//
// The panel is module-private. Mapped against the live client on 2026-09-10:
//
//   v_            an exported React.memo, the VR-aware outer wrapper
//     fe          navID "MainNavMenuContainer", role "application"
//       c.g       nav context
//         Ie      the panel root, props { loggedIn, menuOpen }   <- local, not exported
//           d.Z   role "menu", aria-label #MainMenu_Title, flow-children "column"
//             Ae  one route entry, props { route, active, label, icon, onGamepadFocus }
//
// `Ie` builds its list from `ve(loggedIn)` and maps it to entry elements keyed by the descriptor's
// own `key`. Neither `Ie` nor `ve` is exported, and `ve` calls hooks — calling the module's own
// exported list builder from outside a render throws React error #321, which is how that was
// established rather than assumed. So both reading the entries and changing them have to happen
// during a render, and one wrapper serves both.
//
// The claim is on the exported memo's `type`, which is the only public handle on the panel. From
// there the descent reaches `Ie` by rendering: a component's children do not exist until React
// renders it, so a walk over props.children alone arrives nowhere. That is the same mechanism
// `hideNativeRows` in components.ts already uses, pointed at a different target.
//
// Entries are identified by `route` and by their React key, never by index or by a generated class
// name. Both come from Valve's own descriptor and are stable across builds and languages; the
// rendered labels are localized and the class names are content hashes, so neither is an anchor.
function createNavigationPanel() {
  const patchId = "steam-ui.navigation-panel";
  const claimKeys = {
    marker: "__steamUiNavigationPanelClaimed",
    original: "__steamUiNavigationPanelOriginal",
  } as const;

  // The two tokens that identify the panel root. `#MainMenu_Title` occurs in exactly one module of
  // the 2581 the client loads, and `RunnningAppSeparator` — Valve's own typo — occurs in three, so
  // the pair is unique where neither is alone. Deliberately not the localized title: that changes
  // with the user's language, and this has to match on a client running in any of them.
  const PanelRootTokens = ["#MainMenu_Title", "RunnningAppSeparator"] as const;
  const OuterToken = "MainNavMenuContainer";

  // A panel with more entries than this is not the panel this was written against, and cloning an
  // unbounded child list on every render is not something a navigation menu should ever ask for.
  const MaximumEntries = 64;
  const MaximumDescent = 12;

  let runtime;
  let react;
  let icon;
  let memo = null;
  let installed = false;
  let lastError = "";
  let unsubscribe: (() => void) | null = null;

  // What the last render actually saw and did. Everything else can report success while the panel
  // shows exactly what Valve shipped, because insertion depends on the tree Steam rendered.
  let observed: { key: string; route: string | null; label: string }[] = [];
  let lastOutcome = "never rendered";

  // The host's desired additions and hidden entries, replaced whole on each publication.
  let desired: {
    items: {
      id: string;
      label: string;
      icon?: string;
      route?: string;
      before?: string;
      after?: string;
      position?: "start" | "end";
    }[];
    hidden: string[];
  } = { items: [], hidden: [] };

  const descendCache = new Map();
  const panelCache = new Map();

  const textOf = (value) => {
    if (typeof value === "string") return value;
    if (value && typeof value === "object" && typeof value.props?.children === "string") {
      return value.props.children;
    }
    return "";
  };

  // A rendered entry's identity. `route` is the descriptor's own destination and the anchor an
  // "insert after Library" is written against; the React key is Valve's descriptor key and is what
  // survives when an entry has no route at all, such as the power button.
  const identify = (element) => {
    const route = typeof element?.props?.route === "string" ? element.props.route : null;
    const key = typeof element?.key === "string" ? element.key.replace(/^\.\$/u, "") : "";
    return { key, route, label: textOf(element?.props?.label) };
  };

  const matchesAnchor = (element, anchor) => {
    if (typeof anchor !== "string" || !anchor) return false;
    const identity = identify(element);
    return identity.route === anchor || identity.key === anchor;
  };

  // One added entry. Rendered as Valve's own row would be if it could take arbitrary props: a
  // menuitem div carrying the same role and accessible name, so the panel's keyboard and controller
  // flow treats it as one of its own. It deliberately does not reuse Valve's route entry component —
  // that one resolves its own active state from the router, and an entry pointing at a toolkit
  // consumer's surface has no route in Steam's router to resolve.
  const renderItem = (item) =>
    react.createElement(
      "div",
      {
        key: `steam-ui-nav-${item.id}`,
        role: "menuitem",
        "aria-label": item.label,
        onClick: () => {
          request(patchId, "activate", { id: item.id }).catch(() => {});
        },
      },
      item.icon && icon ? icon(item.icon) : null,
      react.createElement("span", null, item.label),
    );

  // Applies the host's list to the panel's own children.
  //
  // Order of operations matters and is fixed: hide first, then insert. Anchoring an insertion to an
  // entry that was just hidden would otherwise place it against something the user cannot see, and
  // "after Library" would silently become "at the end" depending on an unrelated setting.
  const applyEntries = (children) => {
    const kept: unknown[] = [];
    observed = [];
    let hidden = 0;
    for (const child of children) {
      const identity = identify(child);
      if (react.isValidElement(child) && (identity.route || identity.key)) {
        observed.push(identity);
        if (
          desired.hidden.includes(identity.route ?? "") ||
          desired.hidden.includes(identity.key)
        ) {
          hidden++;
          continue;
        }
      }
      kept.push(child);
    }

    const pending = desired.items.slice(0, MaximumEntries);
    const placed = new Set<string>();
    const result: unknown[] = [];
    for (const item of pending) {
      if (item.position === "start") {
        result.push(renderItem(item));
        placed.add(item.id);
      }
    }

    for (const child of kept) {
      for (const item of pending) {
        if (!placed.has(item.id) && matchesAnchor(child, item.before)) {
          result.push(renderItem(item));
          placed.add(item.id);
        }
      }
      result.push(child);
      for (const item of pending) {
        if (!placed.has(item.id) && matchesAnchor(child, item.after)) {
          result.push(renderItem(item));
          placed.add(item.id);
        }
      }
    }

    // Anything left over goes at the end, including an entry whose anchor is not in this panel.
    // Dropping it would be the silent-control failure the guidance forbids: the caller asked for a
    // row and would have no way to tell that Steam simply does not have the item it named.
    let orphaned = 0;
    for (const item of pending) {
      if (placed.has(item.id)) continue;
      if (item.before || item.after) orphaned++;
      result.push(renderItem(item));
    }

    lastOutcome = `entries=${observed.length} hidden=${hidden} added=${pending.length} orphaned=${orphaned}`;
    return result;
  };

  // Wraps the panel root so its OUTPUT can be changed. Cached against the original, because a fresh
  // component identity on every render would remount the whole menu each time React reconciles it.
  const wrapPanelRoot = (original) => {
    let wrapped = panelCache.get(original);
    if (wrapped) return wrapped;
    wrapped = function SteamUiNavigationPanel(props) {
      const tree = original(props);
      if (!react.isValidElement(tree)) return tree;
      const children = react.Children.toArray(tree.props?.children);
      if (!children.length || children.length > MaximumEntries) {
        lastOutcome = `panel had ${children.length} children; left alone`;
        return tree;
      }
      return react.cloneElement(tree, {}, ...applyEntries(children));
    };
    panelCache.set(original, wrapped);
    return wrapped;
  };

  const isPanelRoot = (type) => {
    if (typeof type !== "function") return false;
    const source = String(type);
    return PanelRootTokens.every((token) => source.includes(token));
  };

  // Descends the rendered tree to the panel root. Function components on the way down are replaced
  // by wrappers that render the original and keep descending, because their children do not exist
  // until they render. Class components, memo and forwardRef objects are left alone: they cannot be
  // called directly, and wrapping them would change identity for refs.
  const descend = (element, depth) => {
    if (depth > MaximumDescent || !react.isValidElement(element)) return element;
    const type: any = element.type;
    if (isPanelRoot(type)) {
      return react.createElement(
        wrapPanelRoot(type),
        element.key === null ? element.props : { ...element.props, key: element.key },
      );
    }

    if (typeof type === "function" && !type.prototype?.isReactComponent) {
      let wrapper = descendCache.get(type);
      if (!wrapper) {
        wrapper = function SteamUiNavigationDescend(props) {
          return descend(type(props), 0);
        };
        descendCache.set(type, wrapper);
      }
      return react.createElement(
        wrapper,
        element.key === null ? element.props : { ...element.props, key: element.key },
      );
    }

    const kids = react.Children.toArray(element.props?.children);
    if (!kids.length) return element;
    let changed = false;
    const next: unknown[] = [];
    for (const kid of kids) {
      const replacement = descend(kid, depth + 1);
      changed ||= replacement !== kid;
      next.push(replacement);
    }
    return changed ? react.cloneElement(element, {}, ...next) : element;
  };

  const resolve = () => {
    runtime = getWebpackRuntime("navigation-panel");
    const reactFactory = runtime.findUnique([
      "react.transitional.element",
      "useState",
      "cloneElement",
      "createElement",
    ]);
    if (!reactFactory) {
      lastError = "React runtime was not a unique match";
      return false;
    }
    react = runtime(reactFactory[0]);
    icon = createIconRenderer(react);

    const menuFactory = runtime.findUnique([PanelRootTokens[0], OuterToken]);
    if (!menuFactory) {
      lastError = "main menu module was not a unique match";
      return false;
    }

    // The one export whose memo renders the outer container. Selected by what its component draws,
    // never by its minified export name: those are right for today's build and nothing more.
    const exports = runtime(menuFactory[0]);
    const candidates = Object.keys(exports).filter((name) => {
      const value = exports[name];
      return (
        value &&
        typeof value === "object" &&
        typeof value.type === "function" &&
        String(value.type).includes(OuterToken)
      );
    });
    if (candidates.length !== 1) {
      lastError = `main menu export was ${candidates.length ? "ambiguous" : "absent"}`;
      return false;
    }

    memo = exports[candidates[0]];
    return true;
  };

  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
    try {
      if (!resolve()) return { ok: false, error: lastError };
    } catch (error) {
      lastError = "navigation panel resolution failed: " + String(error);
      return { ok: false, error: lastError };
    }

    // The memo object is the public handle, and every consumer holds the same one, so claiming its
    // `type` reaches the panel wherever it is rendered without patching a single caller.
    const claim = claimMember(memo, "type", claimKeys, (original: any) => {
      if (typeof original !== "function") return original;
      return function SteamUiNavigationRoot(props) {
        return descend(original(props), 0);
      };
    });
    if (!claim.ok) {
      lastError = claim.error;
      return { ok: false, error: lastError };
    }

    installed = true;
    lastError = "";
    unsubscribe = subscribe(patchId, (state) => {
      const items = Array.isArray(state?.items) ? state.items : [];
      const hidden = Array.isArray(state?.hidden) ? state.hidden : [];
      desired = {
        items: items
          .filter((item) => item && typeof item.id === "string" && typeof item.label === "string")
          .slice(0, MaximumEntries),
        hidden: hidden.filter((value) => typeof value === "string").slice(0, MaximumEntries),
      };
      // Nothing re-renders the menu on its own, so a change published while it is closed shows the
      // next time Steam draws it. That is the whole of the reapply story: the claim is on the type,
      // so every future render already runs through it.
    });
    return { ok: true, installed: true, reclaimed: claim.reclaimed };
  };

  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    installed = false;
    if (unsubscribe) {
      unsubscribe();
      unsubscribe = null;
    }

    desired = { items: [], hidden: [] };
    descendCache.clear();
    panelCache.clear();
    const released = releaseMember(memo, "type", claimKeys);
    if (!released.ok) {
      lastError = released.error ?? "navigation panel release failed";
      return { ok: false, error: lastError };
    }

    lastOutcome = "removed";
    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    resolved: !!memo,
    claimed: memberClaimed(memo, "type", claimKeys),
    // Everything above can be true while the panel shows exactly what Valve shipped, because
    // insertion depends on the tree Steam rendered. This is the part that says what happened.
    entries: observed,
    items: desired.items.length,
    hidden: desired.hidden.length,
    lastOutcome,
    lastError,
  });

  return { install, remove, status };
}

registerGate("navigationPanel", createNavigationPanel());
