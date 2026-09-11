// A library badge on every library tile: the name of the Steam library that holds the game, drawn
// beside Valve's own Steam Input badge in the tile's icon row.
//
// Mapped against the September 2026 client beta on 2026-09-11. One module carries the library tile
// and everything it draws:
//
//   TK    an exported React.memo (mobx observer): the app tile, props { app, bFeatured, context, … }
//         rendered by Home's carousel, the library grid and three other callers, all through the
//         export, and keyed in Steam's focus tree as `appportrait_<appid>`
//     b.Z  Focusable, navKey "appportrait_<appid>"
//       d.z  hover wrapper
//         div.LibraryItemOverlayOuterArea > div.LibraryItemOverlayInnerArea > div.LibraryBottomItems
//           div.LibraryItemIcons     the icon row: justify-content space-between
//             Kt                     exported: the Steam Input badge, props { overview }   <- anchor
//
// `Kt` is exported but the tile calls it by its module-local name, so claiming the badge export
// changes nothing the tile draws. The claim is on the tile memo's `type` instead, and the badge is
// found in what the tile RENDERS by element type — identity with the export, never a class name or
// a minified name — and replaced by a row of two: this badge, then Valve's. The tile is drawn
// through the export by every caller, so one claim reaches the carousel and the grid alike, and
// the badge inherits the row's own visibility: Valve shows the icon row on the focused tile only,
// and this badge appears and disappears with it.
//
// The badge names the library and says whether the game is installed, by colour: green when the
// game is installed, grey when it is not — which is what a card being disconnected amounts to. The
// text is the library's name alone, as the maintainer chose. A game that no published library
// holds is on the internal library by definition and is labelled with the published internal name
// while it is installed; one that is not installed anywhere gets no badge, because there is no
// library to name.
//
// Big Art Mode is Steam's own `library_home_big_art` client setting, read from the settings store
// the Home component itself reads it from. The badge is tile-relative and does not care, but a
// consumer may: the gate reports the mode to the host through `homeLayout` when it first resolves
// and whenever a tile render sees it change, and carries it in `status` for verification.
// The host's published libraries, read once for every gate that names them: indexed by app id, with
// the label for a game no listed library holds. Bounded; a malformed entry is skipped rather than
// failing the whole reading.
const readLibraryBadgeState = (state) => {
  const MaximumLibraries = 64;
  const MaximumAppIds = 4096;
  const MaximumNameLength = 64;
  const libraries = new Map<number, { name: string; connected: boolean }>();
  const published = Array.isArray(state?.libraries) ? state.libraries : [];
  let ids = 0;
  let count = 0;
  for (const entry of published.slice(0, MaximumLibraries)) {
    if (!entry || typeof entry.name !== "string" || !Array.isArray(entry.appIds)) continue;
    count++;
    const library = {
      name: entry.name.slice(0, MaximumNameLength),
      connected: entry.connected === true,
    };
    for (const appid of entry.appIds) {
      if (typeof appid !== "number" || !Number.isInteger(appid) || appid <= 0) continue;
      if (ids >= MaximumAppIds) break;
      libraries.set(appid, library);
      ids++;
    }
  }
  const internalLabel =
    typeof state?.internalLabel === "string" && state.internalLabel
      ? state.internalLabel.slice(0, MaximumNameLength)
      : "Internal";
  return { libraries, internalLabel, count };
};

// The library to name for one app overview, or null when there is none. Steam's own installed flag is
// the authority on installed; a disconnected card's games are not installed by Steam's reckoning. The
// published connection stands in only where the overview cannot say. A game that no published library
// holds is on the internal library while it is installed, and one installed nowhere has no library.
const libraryForOverview = (overview, reading: ReturnType<typeof readLibraryBadgeState>) => {
  const appid = typeof overview?.appid === "number" ? overview.appid : null;
  if (appid === null) return null;
  const library = reading.libraries.get(appid);
  const installed =
    typeof overview.installed === "boolean" ? overview.installed : (library?.connected ?? false);
  if (!library && !installed) return null;
  return { name: library ? library.name : reading.internalLabel, installed };
};

function createLibraryBadge() {
  const patchId = "steam-ui.library-badge";
  const claimKeys = {
    marker: "__steamUiLibraryBadgeClaimed",
    original: "__steamUiLibraryBadgeOriginal",
  } as const;

  // The module that owns the tile. `appportrait_` is the tile's own focus key and occurs in exactly
  // one module; `ControllerSupportIcon` also names the stylesheet module, so the pair is what is
  // unique. Neither is a localized string or a generated class.
  const TileTokens = ["ControllerSupportIcon", "appportrait_"] as const;
  // The settings store: the one module with the store class's own getter and deferred-settings set.
  const SettingsTokens = ["get clientSettings()", "m_setDeferredSettings"] as const;
  // The tile stylesheet's class map: Valve's own names for the icon row and the Steam Input badge,
  // mapped to whatever hashes this build emitted. Read by name, so the hashes are never written
  // down here. The badge's visibility comes from Valve's rules on the badge class — hidden until
  // the tile is focused or hovered — and the row wearing that class inherits them.
  const ClassMapTokens = ['ControllerSupportIcon:"', 'LibraryItemIcons:"', 'LibraryItemBox:"'] as const;
  const BigArtSetting = "library_home_big_art";

  const MaximumDescent = 12;
  const MaximumChildren = 64;

  let runtime;
  let react;
  let tile: any = null;
  let badge: any = null;
  let settings: any = null;
  let classes: { row: string; icon: string } | null = null;
  let installed = false;
  let lastError = "";
  let unsubscribe: (() => void) | null = null;
  let reportedBigArt: boolean | null = null;

  // The host's published libraries, replaced whole on each publication and indexed by app id.
  let reading = readLibraryBadgeState(null);

  // What the last tile render actually did, because a claimed tile can render exactly what Valve
  // shipped when the badge anchor is not in its tree.
  let lastOutcome = "never rendered";
  let placed = 0;
  let unanchored = 0;

  const tileCache = new Map();

  const readBigArt = () => {
    try {
      const value = settings?.clientSettings?.[BigArtSetting];
      return typeof value === "boolean" ? value : null;
    } catch {
      return null;
    }
  };

  // Tells the host once per change, never once per render: the setting is read on every tile
  // render, which is how a toggle is noticed without a subscription into Valve's store.
  const reportBigArt = () => {
    const current = readBigArt();
    if (current === null || current === reportedBigArt) return;
    reportedBigArt = current;
    request(patchId, "homeLayout", { bigArt: current }).catch(() => {});
  };

  const badgeStyle = (installedNow) => ({
    display: "inline-block",
    maxWidth: "180px",
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
    padding: "2px 8px",
    borderRadius: "4px",
    fontSize: "13px",
    lineHeight: "17px",
    fontWeight: 600,
    letterSpacing: "0.2px",
    color: "#f2f4f5",
    background: installedNow ? "rgba(76, 160, 54, 0.92)" : "rgba(110, 115, 120, 0.85)",
  });

  // The badge for one tile, or null when there is no library to name.
  const renderBadge = (overview) => {
    // Grey when not installed, which is exactly what a disconnected card amounts to.
    const library = libraryForOverview(overview, reading);
    if (!library) return null;
    return react.createElement(
      "span",
      {
        key: "steam-ui-library-badge",
        className: "steam-ui-library-badge",
        style: badgeStyle(library.installed),
        "aria-label": library.name,
      },
      library.name,
    );
  };

  // Replaces Valve's badge element with a right-aligned row of ours and Valve's. The icon row is
  // `space-between` with Valve's badge pushed to its end by an auto margin, so a bare sibling would
  // land at the row's far left; one flex box holding both keeps this badge immediately left of the
  // icon wherever the row puts it.
  //
  // The box wears two of Valve's own classes. The badge class carries the visibility rule — opacity
  // zero until the tile is focused or hovered — and the end-of-row margin, so the pair appears and
  // disappears with Valve's icon instead of sitting on every tile; its size, padding and pill
  // background are overridden inline because they are drawn for a 34-pixel glyph. The row class
  // keeps Valve's icon a direct child of a row, which is what its pill background is written
  // against. Without the class map the box is plain and always visible, and status says so.
  const withBadge = (element) => {
    const ours = renderBadge(element.props?.overview);
    if (!ours) return element;
    const style: Record<string, unknown> = {
      display: "flex",
      alignItems: "center",
      gap: "8px",
      marginInlineStart: "auto",
    };
    let className: string | undefined;
    if (classes) {
      className = `${classes.row} ${classes.icon}`;
      Object.assign(style, {
        justifyContent: "flex-end",
        width: "auto",
        maxWidth: "none",
        maxHeight: "none",
        padding: 0,
        borderRadius: 0,
        backgroundColor: "transparent",
      });
    }
    return react.createElement(
      "div",
      { key: "steam-ui-library-badge-row", className, style },
      ours,
      element,
    );
  };

  // Descends the rendered tree by props alone. The tile's whole icon row is host elements and
  // fragments below the Focusable, so nothing has to be rendered to reach the anchor; function
  // components on the way are left untouched, which keeps every identity Valve's reconciler holds.
  const decorate = (element, depth) => {
    if (depth > MaximumDescent || !react.isValidElement(element)) return element;
    if (element.type === badge) {
      placed++;
      return withBadge(element);
    }
    const kids = react.Children.toArray(element.props?.children);
    if (!kids.length || kids.length > MaximumChildren) return element;
    let changed = false;
    const next: unknown[] = [];
    for (const kid of kids) {
      const replacement = decorate(kid, depth + 1);
      changed ||= replacement !== kid;
      next.push(replacement);
    }
    return changed ? react.cloneElement(element, {}, ...next) : element;
  };

  // Wraps the tile's observer so its OUTPUT can be changed. Cached against the original: a fresh
  // identity on every claim would remount every tile React reconciles.
  const wrapTile = (original) => {
    let wrapped = tileCache.get(original);
    if (wrapped) return wrapped;
    wrapped = function SteamUiLibraryTile(this: unknown, props, secondArgument) {
      const tree = original.call(this, props, secondArgument);
      reportBigArt();
      const before = placed;
      const result = decorate(tree, 0);
      if (placed === before) unanchored++;
      lastOutcome = `placed=${placed} unanchored=${unanchored} libraries=${reading.count} apps=${reading.libraries.size}`;
      return result;
    };
    tileCache.set(original, wrapped);
    return wrapped;
  };

  const resolve = () => {
    runtime = getWebpackRuntime("library-badge");
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

    const tileFactory = runtime.findUnique([...TileTokens]);
    if (!tileFactory) {
      lastError = "library tile module was not a unique match";
      return false;
    }
    const exports = runtime(tileFactory[0]);

    // The tile is the module's one memo export; the badge is the one function export that draws
    // the controller-support icon. Both are chosen by what they are, never by their minified names.
    const memoType = Symbol.for("react.memo");
    const tiles = Object.keys(exports).filter((name) => {
      const value = exports[name];
      return value && typeof value === "object" && value.$$typeof === memoType;
    });
    if (tiles.length !== 1) {
      lastError = `library tile export was ${tiles.length ? "ambiguous" : "absent"}`;
      return false;
    }
    const badges = Object.keys(exports).filter((name) => {
      const value = exports[name];
      return typeof value === "function" && String(value).includes(TileTokens[0]);
    });
    if (badges.length !== 1) {
      lastError = `Steam Input badge export was ${badges.length ? "ambiguous" : "absent"}`;
      return false;
    }
    tile = exports[tiles[0]];
    badge = exports[badges[0]];

    // The class map is wanted, not required: without it the badge still draws, on every tile
    // rather than the focused one, and `status.classesResolved` says so. Read as the tile reads
    // it — the module's export, unwrapped if it is an ES default.
    classes = null;
    const classMapFactory = runtime.findUnique([...ClassMapTokens]);
    if (classMapFactory) {
      const exported = runtime(classMapFactory[0]);
      const map = exported && exported.__esModule ? exported.default : exported;
      const row = map?.LibraryItemIcons;
      const icon = map?.ControllerSupportIcon;
      if (typeof row === "string" && row && typeof icon === "string" && icon) {
        classes = { row, icon };
      }
    }

    // The settings store is wanted, not required: without it the badge still draws and
    // `status.bigArt` says null rather than guessing.
    settings = null;
    const settingsFactory = runtime.findUnique([...SettingsTokens]);
    if (settingsFactory) {
      const stores = runtime(settingsFactory[0]);
      const candidates = Object.keys(stores).filter((name) => {
        const value = stores[name];
        return value && typeof value === "object" && typeof value.clientSettings === "object";
      });
      if (candidates.length === 1) settings = stores[candidates[0]];
    }
    return true;
  };

  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
    try {
      if (!resolve()) return { ok: false, error: lastError };
    } catch (error) {
      lastError = "library badge resolution failed: " + String(error);
      return { ok: false, error: lastError };
    }

    // Every caller draws the tile through the same memo, so claiming its `type` reaches the
    // carousel and the grid without patching a single caller.
    const claim = claimMember(tile, "type", claimKeys, (original: any) => {
      if (typeof original !== "function") return original;
      return wrapTile(original);
    });
    if (!claim.ok) {
      lastError = claim.error;
      return { ok: false, error: lastError };
    }

    installed = true;
    lastError = "";
    reportedBigArt = null;
    reportBigArt();
    unsubscribe = subscribe(patchId, (state) => {
      // Nothing re-renders the tiles on its own: the claim is on the type, so the next render of
      // each tile — focus moving, the grid scrolling, Home rebuilding — draws the new map.
      reading = readLibraryBadgeState(state);
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
    reading = readLibraryBadgeState(null);
    tileCache.clear();
    const released = releaseMember(tile, "type", claimKeys);
    if (!released.ok) {
      lastError = released.error ?? "library badge release failed";
      return { ok: false, error: lastError };
    }
    lastOutcome = "removed";
    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    resolved: !!tile && !!badge,
    claimed: memberClaimed(tile, "type", claimKeys),
    settingsResolved: !!settings,
    classesResolved: !!classes,
    bigArt: readBigArt(),
    libraries: reading.count,
    apps: reading.libraries.size,
    lastOutcome,
    lastError,
  });

  return { install, remove, status };
}

registerGate("libraryBadge", createLibraryBadge());

// The library as a stat on a game's own page, after Last Played and Play Time.
//
// Mapped from the Stable client (UI build of 2026-09-06) and the September 2026 beta on 2026-09-11,
// whose app-details module is the same in both:
//
//   PlayBar                  exported mobx observer class
//     StatusAndStats         exported mobx observer class
//       stats section        module-local mobx observer class, rendering
//         div.GameStatsSection   claim content, cloud status, install size, Last Played,
//                                Play Time or time left, achievements, controller support
//
// Every one of those pins a non-writable render on each instance, so no claim on a type or a
// prototype holds. The row passes through the JSX runtime when Steam creates it, and that is where
// this adds to it (interceptElements in ownership.ts): the div whose class is the play bar class
// map's `GameStatsSection` gets one more child. The stat is Valve's markup for Last Played, built
// from the same class map, so it takes the row's type, spacing and narrow-window rules, and its
// label is Steam's own `#Settings_Page_Library`, localized. The app is the overview the row's own
// children are given.
//
// The data is the library badge's publication, read by the same rules: a game on a library that is
// not attached shows its library dimmed, and one installed nowhere has no stat. The row draws with the
// page, so a new publication shows the next time the page renders.
function createLibraryDetails() {
  const publicationId = "steam-ui.library-badge";
  const TransformName = "libraryDetails";
  const ReactTokens = ["react.transitional.element", "useState", "cloneElement", "createElement"] as const;
  const RuntimeTokens = ["react.transitional.element", ".jsx", ".jsxs"] as const;
  const ClassMapTokens = ['GameStatsSection:"', 'PlayBarDetailLabel:"', 'LastPlayedInfo:"'] as const;
  const LocalizationTokens = [
    "Attempting to localize token",
    "Unable to find localization token",
    "LocalizeString",
  ] as const;
  const RequiredClasses = ["GameStatsSection", "GameStat", "GameStatRight", "PlayBarLabel", "PlayBarDetailLabel"];
  const LabelToken = "#Settings_Page_Library";
  const StatKey = "steam-ui-library-details";
  const MaximumChildren = 32;

  let runtime;
  let react: any = null;
  let jsxRuntime: any = null;
  let localize: ((token: string) => unknown) | null = null;
  let classes: { section: string; stat: string; right: string; label: string; value: string } | null =
    null;
  let installed = false;
  let lastError = "";
  let lastOutcome = "never rendered";
  let placed = 0;
  let without = 0;
  let unsubscribe: (() => void) | null = null;
  let reading = readLibraryBadgeState(null);

  const label = () => {
    try {
      const text = localize?.(LabelToken);
      if (typeof text === "string" && text && text !== LabelToken) return text;
    } catch {
      // The English word stands in for a localizer that did not resolve or answer.
    }
    return "Library";
  };

  const overviewIn = (children: unknown[]) => {
    for (const child of children) {
      const overview = (child as any)?.props?.overview;
      if (overview && typeof overview.appid === "number") return overview;
    }
    return null;
  };

  const renderStat = (library: { name: string; installed: boolean }) =>
    react.createElement(
      "div",
      { key: StatKey, className: classes!.stat },
      react.createElement(
        "div",
        { className: classes!.right },
        react.createElement("div", { className: classes!.label }, label()),
        react.createElement(
          "div",
          { className: classes!.value, style: library.installed ? undefined : { opacity: 0.55 } },
          library.name,
        ),
      ),
    );

  const transform = (create, type, props, key) => {
    if (type !== "div" || !installed || !classes || props?.className !== classes.section) return undefined;
    const children = Array.isArray(props.children) ? props.children : [props.children];
    if (children.length > MaximumChildren || children.some((child) => child?.key === StatKey)) {
      return undefined;
    }
    const library = libraryForOverview(overviewIn(children), reading);
    if (!library) {
      without++;
    } else {
      placed++;
    }
    lastOutcome = `placed=${placed} without=${without} libraries=${reading.count} apps=${reading.libraries.size}`;
    if (!library) return undefined;
    return create(type, { ...props, children: [...children, renderStat(library)] }, key);
  };

  const resolve = () => {
    runtime = getWebpackRuntime("library-details");
    react = runtime.resolve([...ReactTokens]);
    jsxRuntime = runtime.resolve([...RuntimeTokens]);
    if (typeof jsxRuntime?.jsx !== "function" || typeof jsxRuntime?.jsxs !== "function") {
      lastError = "JSX runtime lacks jsx or jsxs";
      return false;
    }
    // Valve's names for the play bar's classes, mapped to whatever this build emitted. Read by
    // name, never written down.
    const exported = runtime.resolve([...ClassMapTokens]);
    const map = exported && exported.__esModule ? exported.default : exported;
    if (!map || RequiredClasses.some((name) => typeof map[name] !== "string" || !map[name])) {
      lastError = "the play bar class map lacks a stat class";
      return false;
    }
    const join = (...names: string[]) =>
      names.map((name) => map[name]).filter((value) => typeof value === "string" && value).join(" ");
    classes = {
      section: map.GameStatsSection,
      stat: join("GameStat", "LastPlayed"),
      right: join("GameStatRight"),
      label: join("PlayBarLabel"),
      value: join("PlayBarDetailLabel", "LastPlayedInfo"),
    };

    // Wanted, not required: without it the label is the English word.
    localize = null;
    const localization = runtime.findUnique([...LocalizationTokens]);
    if (localization) {
      const exports = runtime(localization[0]);
      const candidates = new Set(
        Object.values(exports).filter((value) => {
          if (typeof value !== "function") return false;
          const source = String(value);
          return (
            !source.startsWith("class") &&
            source.includes(".LocalizeString(") &&
            source.includes("void 0") &&
            !source.includes("!0)") &&
            !source.includes("!=null") &&
            !source.includes("createElement")
          );
        }),
      );
      if (candidates.size === 1) localize = [...candidates][0] as (token: string) => unknown;
    }
    return true;
  };

  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
    try {
      if (!resolve()) return { ok: false, error: lastError };
    } catch (error) {
      lastError = "library details resolution failed: " + String(error);
      return { ok: false, error: lastError };
    }

    installed = true;
    const claim = interceptElements(jsxRuntime, TransformName, transform);
    if (!claim.ok) {
      installed = false;
      lastError = claim.error ?? "the JSX runtime could not be intercepted";
      return { ok: false, error: lastError };
    }
    lastError = "";
    unsubscribe = subscribe(publicationId, (state) => {
      reading = readLibraryBadgeState(state);
    });
    return { ok: true, installed: true };
  };

  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    installed = false;
    if (unsubscribe) {
      unsubscribe();
      unsubscribe = null;
    }
    reading = readLibraryBadgeState(null);
    const released = releaseElements(jsxRuntime, TransformName);
    if (!released.ok) {
      lastError = released.error ?? "library details release failed";
      return { ok: false, error: lastError };
    }
    lastOutcome = "removed";
    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    resolved: !!react && !!jsxRuntime && !!classes,
    claimed: elementsIntercepted(jsxRuntime, TransformName),
    localized: !!localize,
    libraries: reading.count,
    apps: reading.libraries.size,
    lastOutcome,
    lastError,
  });

  return { install, remove, status };
}

registerGate("libraryDetails", createLibraryDetails());
