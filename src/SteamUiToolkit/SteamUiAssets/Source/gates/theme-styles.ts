// Theme stylesheets in every Steam window, the way CSSLoader delivers them.
//
// CSSLoader (b1bc683, css_browserhook.py) opens a CDP session to each of Steam's page targets and
// appends one <style> per block to that document's head, choosing the documents a block is for by
// the target's title, its URL or the classes on its root elements. Every one of those windows is
// rendered from SharedJSContext, so their documents are reachable from here without a connection
// per window: one gate, one publication, every window.
//
// Where the windows are, measured on a Windows client on 2026-09-28: g_PopupManager holds the Big
// Picture window and its context menus, and NOT the Quick Access, main-menu and toast windows.
// Those exist to SharedJSContext only as the containers of React portals, which is how Steam draws
// into them. So the documents are gathered from both: every popup the manager lists, and every
// document a portal in SharedJSContext's mounted trees renders into.
//
// What identifies a window, measured the same day: the window's own name, "SP BPM_uid0",
// "QuickAccess_uid17", "MainMenu_uid17", "notificationtoasts_uid17", "contextmenu_13_uid0". The
// document title is that name for the popups but the LOCALIZED product name for the Big Picture
// window ("Big-Picture-Modus" on a German client), and its URL carries none of the markers
// CSSLoader's table names. A title target is therefore tested against the name as well as the
// title, and the host's alias table names the Big Picture window by its name.
//
// The host publishes the blocks and the targets each is for; the gate keeps every window's head in
// step with that, and with the windows Steam opens or navigates after the publication, which is
// what CSSLoader's force_reinject and health check exist for. Nothing here reads the CSS: a theme
// is the host's to load, translate and order, and this gate installs what it is given.
function createThemeStyles() {
  const patchId = "steam-ui.theme-styles";
  // Every node this gate appends carries the class, and only nodes with it are ever removed.
  // CSSLoader's own is `css-loader-style`, which it bulk-removes; a different class is what lets
  // the two run beside each other.
  const OwnedClass = "steam-ui-theme-style";
  const IdPrefix = "steam-ui-theme-";
  const HashKey = "steamUiHash";
  // How often the windows are read again for one Steam opened or navigated since the last pass.
  // CSSLoader checks every three seconds; a pass here is a bounded walk and a few reads per window.
  const ReconcileMilliseconds = 2000;
  // React's HostPortal fiber tag, the one whose stateNode carries the container it renders into.
  const HostPortalTag = 4;

  let installed = false;
  let unsubscribe: (() => void) | null = null;
  let timer: any = null;
  let desired: { styles: any[]; signature: string; revision: number } = {
    styles: [],
    signature: "",
    revision: 0,
  };
  let lastOutcome = "never reconciled";
  let lastError = "";
  let windowsSeen = 0;
  let documentsStyled = 0;
  let nodesInstalled = 0;
  // Compiled title patterns, once each: a pattern that does not compile matches nothing.
  const patterns = new Map<string, RegExp | null>();

  // Types only, and an id a node can carry. However many themes are on and however large their CSS,
  // every one is installed.
  const validStyle = (style) =>
    !!style &&
    typeof style.id === "string" &&
    /^[A-Za-z0-9_.:-]+$/u.test(style.id) &&
    typeof style.css === "string" &&
    typeof style.hash === "string" &&
    style.hash.length > 0 &&
    Array.isArray(style.targets) &&
    style.targets.length > 0 &&
    style.targets.every((target) => typeof target === "string" && target.length > 0);

  // Steam's popup manager, by the name Valve publishes it under; null when it is not where Valve
  // keeps it today.
  const popupManager = () => {
    try {
      const manager = (window as any).g_PopupManager;
      return manager && typeof manager.GetPopups === "function" ? manager : null;
    } catch {
      return null;
    }
  };

  // What a target is compared against, read fresh each pass because a window navigates.
  const factsOf = (doc, name: string) => {
    const win = doc.defaultView;
    const classes = [
      ...Array.from(doc.documentElement?.classList ?? []),
      ...Array.from(doc.body?.classList ?? []),
      ...Array.from(doc.head?.classList ?? []),
    ].map(String);
    return {
      doc,
      name: String(name || win?.name || ""),
      title: String(doc.title ?? ""),
      url: String(win?.location?.href ?? doc.location?.href ?? ""),
      classes,
    };
  };

  // Every window Steam is rendering into, each document once: the popups the manager lists, then
  // the containers of every portal in the mounted trees. SharedJSContext's own document is not a
  // window anyone looks at and is left out.
  const steamDocuments = () => {
    const found = new Map<any, any>();
    const consider = (doc, name = "") => {
      try {
        if (!doc || doc === document || !doc.head || found.has(doc)) return;
        found.set(doc, factsOf(doc, name));
      } catch {
        // A window mid-navigation can refuse every read; it is looked at again next pass.
      }
    };
    const manager = popupManager();
    if (manager) {
      let popups: any[] = [];
      try {
        popups = Array.from(manager.GetPopups() ?? []);
      } catch {
        popups = [];
      }
      for (const popup of popups) {
        try {
          consider(popup?.m_popup?.document, String(popup?.m_strName ?? ""));
        } catch {
          // As above.
        }
      }
    }
    walkFibers(reactRootFibers(), MaximumMountedNodes, (fiber) => {
      if (fiber.tag !== HostPortalTag) return false;
      const container = fiber.stateNode?.containerInfo;
      if (!container) return false;
      consider(container.nodeType === 9 ? container : container.ownerDocument);
      return false;
    });
    windowsSeen = found.size;
    return [...found.values()];
  };

  // CSSLoader's compare(): `~text~` is a URL substring, `!name` a class on the document's root
  // elements, anything else a whole-title regular expression. The expression is also tried against
  // the window's name, which is what the title is on the Deck and what stays stable on Windows.
  const matchesTarget = (target: string, facts) => {
    if (target.length > 2 && target.startsWith("~") && target.endsWith("~")) {
      return facts.url.includes(target.slice(1, -1));
    }
    if (target.startsWith("!")) {
      return facts.classes.includes(target.slice(1));
    }
    let pattern = patterns.get(target);
    if (pattern === undefined) {
      try {
        pattern = new RegExp(`^(${target})$`, "u");
      } catch {
        pattern = null;
      }
      patterns.set(target, pattern);
    }
    return !!pattern && (pattern.test(facts.title) || (facts.name.length > 0 && pattern.test(facts.name)));
  };

  const ownedNodes = (doc) => {
    try {
      return Array.from(doc.head.querySelectorAll("style." + OwnedClass));
    } catch {
      return [];
    }
  };

  // One document brought in step with the publication: the blocks whose targets name it, in the
  // order published, each exactly once. A head already holding that list, block for block and hash
  // for hash, is left alone; anything else is rebuilt, because order is part of what a theme means.
  // What a document wants is a function of the publication and the document's facts, both of
  // which rarely change between the 2 s passes, so the match is kept per document until either does.
  const wantedByDocument = new WeakMap();
  const wantedFor = (facts) => {
    const key = `${desired.signature}\u0000${facts.name}\u0000${facts.title}\u0000${facts.url}\u0000${facts.classes.join(" ")}`;
    const cached = wantedByDocument.get(facts.doc);
    if (cached && cached.key === key) return cached.wanted;
    const wanted = desired.styles.filter((style) =>
      style.targets.some((target) => matchesTarget(target, facts)),
    );
    wantedByDocument.set(facts.doc, { key, wanted });
    return wanted;
  };
  const reconcileDocument = (facts) => {
    const wanted = wantedFor(facts);
    const owned = ownedNodes(facts.doc);
    const same =
      owned.length === wanted.length &&
      owned.every(
        (node: any, index) =>
          node.id === IdPrefix + wanted[index].id && node.dataset?.[HashKey] === wanted[index].hash,
      );
    if (!same) {
      for (const node of owned) (node as any).remove();
      for (const style of wanted) {
        const node = facts.doc.createElement("style");
        node.id = IdPrefix + style.id;
        node.className = OwnedClass;
        node.dataset[HashKey] = style.hash;
        node.textContent = style.css;
        facts.doc.head.append(node);
      }
    }
    return wanted.length;
  };

  const reconcile = () => {
    if (!installed) return;
    // With nothing published and nothing installed there is no window to bring in step, and the
    // walk over every mounted fiber that finds the windows is not worth a 2 s tick.
    if (desired.styles.length === 0 && nodesInstalled === 0) {
      lastOutcome = "idle: no styles";
      return;
    }
    try {
      let styled = 0;
      let nodes = 0;
      for (const facts of steamDocuments()) {
        const count = reconcileDocument(facts);
        if (count > 0) styled++;
        nodes += count;
      }
      documentsStyled = styled;
      nodesInstalled = nodes;
      lastOutcome = `windows=${windowsSeen} documents=${styled} nodes=${nodes} styles=${desired.styles.length}`;
      lastError = "";
    } catch (error) {
      lastError = "theme reconciliation failed: " + String(error);
    }
  };

  const clearAll = () => {
    let removed = 0;
    for (const facts of steamDocuments()) {
      for (const node of ownedNodes(facts.doc)) {
        try {
          (node as any).remove();
          removed++;
        } catch {
          // A node whose document is gone has nothing left to remove.
        }
      }
    }
    documentsStyled = 0;
    nodesInstalled = 0;
    return removed;
  };

  // What a publication changes, without stringifying megabytes of CSS on every round: a block's
  // identity and hash, its targets, and the order.
  const signatureOf = (styles) =>
    styles.map((style) => `${style.id}#${style.hash}@${style.targets.join("|")}`).join(";");

  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
    if (!popupManager() && reactRootFibers().length === 0) {
      lastError = "neither Steam's popup manager nor a mounted React tree was found";
      return { ok: false, error: lastError };
    }
    installed = true;
    lastError = "";
    unsubscribe = subscribe(patchId, (state) => {
      const styles = Array.isArray(state?.styles)
        ? state.styles.filter(validStyle)
        : [];
      const revision = Number.isSafeInteger(state?.revision) ? state.revision : 0;
      const signature = signatureOf(styles);
      if (signature === desired.signature && revision === desired.revision) return;
      desired = { styles, signature, revision };
      reconcile();
    });
    // Windows Steam opens or navigates later have empty heads until this looks again.
    timer = setInterval(reconcile, ReconcileMilliseconds);
    reconcile();
    return { ok: true, installed: true };
  };

  // Every owned node in every window goes before the gate forgets it holds anything, so a window
  // keeps no theme once the host has retracted it, and Steam's own styling is what remains.
  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    if (timer !== null) {
      clearInterval(timer);
      timer = null;
    }
    unsubscribe = endSubscription(unsubscribe);
    const removed = clearAll();
    desired = { styles: [], signature: "", revision: 0 };
    installed = false;
    lastOutcome = `removed ${removed}`;
    return { ok: true, removed: true, nodes: removed };
  };

  // The windows and what they are matched by, for a host's diagnostics.
  const windows = () =>
    steamDocuments().map((facts) => ({
      name: facts.name,
      title: facts.title,
      url: facts.url,
      nodes: ownedNodes(facts.doc).length,
    }));

  const status = () => ({
    ok: true,
    installed,
    resolved: !!popupManager() || reactRootFibers().length > 0,
    windows: windowsSeen,
    documents: documentsStyled,
    nodes: nodesInstalled,
    styles: desired.styles.length,
    revision: desired.revision,
    lastOutcome,
    lastError,
  });

  return { install, remove, status, windows };
}

registerGate("themeStyles", createThemeStyles());
