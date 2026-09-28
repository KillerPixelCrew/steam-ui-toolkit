// Theme stylesheets in every Steam window, the way CSSLoader delivers them.
//
// CSSLoader (b1bc683, css_browserhook.py) opens a CDP session to each of Steam's page targets and
// appends one <style> per block to that document's head, choosing the documents a block is for by
// the target's title, its URL or the classes on its root elements. Every one of those windows is a
// popup Steam opens from SharedJSContext and keeps in g_PopupManager, so their documents are
// reachable from here without a connection per window: one gate, one publication, every window.
//
// The host publishes the blocks and the targets each is for; the gate keeps every popup's head in
// step with that, and with the popups Steam opens or navigates after the publication, which is
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
  const MaximumStyles = 256;
  const MaximumCssLength = 4 * 1024 * 1024;
  const MaximumTargets = 32;
  const MaximumTargetLength = 256;
  // How often the popups are read again for one Steam opened or navigated since the last pass.
  // CSSLoader checks every three seconds; a pass here is a few property reads per popup.
  const ReconcileMilliseconds = 2000;

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
  let popupsSeen = 0;
  let documentsStyled = 0;
  let nodesInstalled = 0;
  // Compiled title patterns, once each: a pattern that does not compile matches nothing.
  const patterns = new Map<string, RegExp | null>();

  const validStyle = (style) =>
    !!style &&
    typeof style.id === "string" &&
    /^[A-Za-z0-9_.:-]{1,96}$/u.test(style.id) &&
    typeof style.css === "string" &&
    style.css.length <= MaximumCssLength &&
    typeof style.hash === "string" &&
    style.hash.length > 0 &&
    style.hash.length <= 64 &&
    Array.isArray(style.targets) &&
    style.targets.length > 0 &&
    style.targets.length <= MaximumTargets &&
    style.targets.every(
      (target) => typeof target === "string" && target.length > 0 && target.length <= MaximumTargetLength,
    );

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

  // Every popup with a document: the window it renders into and what CSSLoader compares a target
  // against. A popup whose window is gone, or not yet open, is skipped this pass and read again on
  // the next.
  const popupDocuments = () => {
    const documents: { doc: any; title: string; url: string; classes: string[] }[] = [];
    const manager = popupManager();
    if (!manager) return documents;
    let popups: any[] = [];
    try {
      popups = Array.from(manager.GetPopups() ?? []);
    } catch {
      return documents;
    }
    popupsSeen = popups.length;
    for (const popup of popups) {
      try {
        const win = popup?.m_popup;
        const doc = win?.document;
        if (!doc || !doc.head) continue;
        const classes = [
          ...Array.from(doc.documentElement?.classList ?? []),
          ...Array.from(doc.body?.classList ?? []),
          ...Array.from(doc.head?.classList ?? []),
        ].map(String);
        documents.push({
          doc,
          title: String(doc.title ?? ""),
          url: String(win.location?.href ?? ""),
          classes,
        });
      } catch {
        // A popup mid-navigation can refuse every read; it is looked at again next pass.
      }
    }
    return documents;
  };

  // CSSLoader's compare(): `~text~` is a URL substring, `!name` a class on the document's root
  // elements, anything else a whole-title regular expression.
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
    return !!pattern && pattern.test(facts.title);
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
  const reconcileDocument = (facts) => {
    const wanted = desired.styles.filter((style) =>
      style.targets.some((target) => matchesTarget(target, facts)),
    );
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
    try {
      let styled = 0;
      let nodes = 0;
      for (const facts of popupDocuments()) {
        const count = reconcileDocument(facts);
        if (count > 0) styled++;
        nodes += count;
      }
      documentsStyled = styled;
      nodesInstalled = nodes;
      lastOutcome = `popups=${popupsSeen} documents=${styled} nodes=${nodes} styles=${desired.styles.length}`;
      lastError = "";
    } catch (error) {
      lastError = "theme reconciliation failed: " + String(error);
    }
  };

  const clearAll = () => {
    let removed = 0;
    for (const facts of popupDocuments()) {
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
    if (!popupManager()) {
      lastError = "Steam's popup manager was not found";
      return { ok: false, error: lastError };
    }
    installed = true;
    lastError = "";
    unsubscribe = subscribe(patchId, (state) => {
      const styles = Array.isArray(state?.styles)
        ? state.styles.filter(validStyle).slice(0, MaximumStyles)
        : [];
      const revision = Number.isSafeInteger(state?.revision) ? state.revision : 0;
      const signature = signatureOf(styles);
      if (signature === desired.signature && revision === desired.revision) return;
      desired = { styles, signature, revision };
      reconcile();
    });
    // Popups Steam opens or navigates later have empty heads until this looks again.
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

  const status = () => ({
    ok: true,
    installed,
    resolved: !!popupManager(),
    popups: popupsSeen,
    documents: documentsStyled,
    nodes: nodesInstalled,
    styles: desired.styles.length,
    revision: desired.revision,
    lastOutcome,
    lastError,
  });

  return { install, remove, status };
}

registerGate("themeStyles", createThemeStyles());
