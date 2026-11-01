// Native library styling for host-owned titles that have no Steam app overview.

const SteamLibraryClassNames = [
    "LibraryItemBox",
    "Portrait",
    "Landscape",
    "PortraitImage",
    "LibraryItemBoxShine",
    "LibraryItemOverlayOuterArea",
    "LibraryItemOverlayInnerArea",
] as const;

/**
 * Resolves all native CSS classes needed for a host-rendered library capsule.
 * @param runtime Shared resolver; loading a matched factory may throw.
 * @returns Mapped capsule classes, or null when the complete class contract is unavailable.
 */
const resolveSteamLibraryClasses = (runtime) => {
    const factory = runtime.findUnique([...SteamLibraryClassTokens]);
    if (!factory) return null;
    const map = classMapOf(runtime(factory[0]));
    if (!map) return null;
    for (const name of SteamLibraryClassNames) {
        if (typeof map[name] !== "string" || !map[name]) return null;
    }
    return {
        box: map.LibraryItemBox,
        portrait: map.Portrait,
        landscape: map.Landscape,
        image: map.PortraitImage,
        shine: map.LibraryItemBoxShine,
        overlayOuter: map.LibraryItemOverlayOuterArea,
        overlayInner: map.LibraryItemOverlayInnerArea,
    };
};

// The shape of each Steam artwork type, as a CSS aspect ratio.
const SteamCapsuleAspects: Record<string, string> = {
    grid: "2 / 3",
    wide: "460 / 215",
    hero: "1920 / 620",
    logo: "16 / 9",
    icon: "1 / 1",
};

/**
 * Creates a reusable capsule component; retain its identity across renders to preserve focus.
 * @param ui Steam's resolved React and native control components.
 * @param classes Complete class map from resolveSteamLibraryClasses.
 * @returns A component accepting asset/image/placeholder/width/dimmed/overlay/caption/focus props.
 */
const createSteamCapsule = (ui, classes) => {
    const react = ui.react;
    return function SteamCapsule(props: any) {
        const asset = props.asset ?? "grid";
        const portrait = asset === "grid";
        const boxClass = `${classes.box} ${portrait ? classes.portrait : classes.landscape}`;
        const children: any[] = [];
        if (props.image) {
            children.push(
                react.createElement("img", {
                    key: "image",
                    className: classes.image,
                    src: props.image,
                    loading: "lazy",
                    draggable: false,
                    style: {
                        width: "100%",
                        height: "100%",
                        objectFit: asset === "logo" || asset === "icon" ? "contain" : "cover",
                        display: "block",
                    },
                }),
            );
        } else {
            children.push(
                react.createElement(
                    "div",
                    {
                        key: "placeholder",
                        style: {
                            position: "absolute",
                            inset: 0,
                            display: "flex",
                            alignItems: "center",
                            justifyContent: "center",
                            padding: "10px",
                            textAlign: "center",
                            fontSize: "14px",
                            opacity: 0.7,
                        },
                    },
                    props.placeholder ?? "",
                ),
            );
        }
        if (portrait) {
            children.push(
                react.createElement("div", {
                    key: "shine",
                    className: `${classes.shine} ${classes.portrait}`,
                }),
            );
        }
        children.push(
            react.createElement(
                "div",
                { key: "overlay", className: classes.overlayOuter },
                react.createElement("div", { className: classes.overlayInner }, props.overlay ?? null),
            ),
        );
        if (props.caption) {
            children.push(
                react.createElement(
                    "div",
                    {
                        key: "caption",
                        style: {
                            position: "absolute",
                            left: 0,
                            right: 0,
                            bottom: 0,
                            display: "flex",
                            alignItems: "center",
                            justifyContent: "center",
                            gap: "8px",
                            padding: "4px 6px",
                            fontSize: "12px",
                            background: "rgba(14, 20, 27, 0.82)",
                            pointerEvents: "none",
                        },
                    },
                    props.caption,
                ),
            );
        }
        return react.createElement(
            ui.focusable,
            {
                ...(props.focus ?? {}),
                className: boxClass,
                style: {
                    position: "relative",
                    width: `${props.width ?? 160}px`,
                    aspectRatio: SteamCapsuleAspects[asset] ?? SteamCapsuleAspects.grid,
                    height: "auto",
                    overflow: "hidden",
                    opacity: props.dimmed ? 0.5 : 1,
                    background: props.image ? undefined : "rgba(255, 255, 255, 0.06)",
                },
            },
            ...children,
        );
    };
};

// Steam's own artwork and game list for a host page, read from the stores Steam's library draws
// from. Each answers what Steam holds now and nothing when a store is not there; none of them
// fetches, and an image list is tried in order by `renderSteamUiArt`.

/**
 * Reads one app's overview from Steam's app store.
 * @param appId The Steam AppID or shortcut id.
 * @returns The overview, or null when Steam has none or its store is unavailable.
 */
const steamAppOverview = (appId: number) => {
    try {
        return (window as any).appStore?.GetAppOverviewByAppID(appId) ?? null;
    } catch {
        return null;
    }
};

/**
 * Lists one app's images of a kind the way Steam's game page tries them: a custom image first, then
 * Steam's cached and published copies.
 * @param appId The Steam AppID or shortcut id.
 * @param kind `hero` for the library hero, `header` for the wide header capsule.
 * @returns The image URLs, best first; empty when Steam has none or its stores are unavailable.
 */
const steamAppImages = (appId: number, kind: "hero" | "header"): string[] => {
    const overview = steamAppOverview(appId);
    if (!overview) return [];
    try {
        const store = (window as any).appDetailsStore;
        const images =
            kind === "hero"
                ? store?.GetHeroImages?.(overview)?.rgHeroImages
                : store?.GetHeaderImages?.(overview);
        return Array.isArray(images)
            ? images.filter((image) => typeof image === "string" && image.length > 0)
            : [];
    } catch {
        return [];
    }
};

/** One app's library hero art, best first. */
const steamAppHeroImages = (appId: number) => steamAppImages(appId, "hero");

/** One app's wide header capsule, best first. */
const steamAppHeaderImages = (appId: number) => steamAppImages(appId, "header");

/**
 * Reads the games in the Steam library, with the non-Steam shortcuts, by name: the same list the
 * toolkit's `SteamLibraryData.ReadGamesAsync` answers the host with.
 * @returns The games, or null when Steam's collection store is unavailable (not an empty library).
 */
const readSteamLibraryGames = (): { appId: number; name: string; shortcut: boolean }[] | null => {
    try {
        const store = (window as any).collectionStore;
        if (!store || typeof store.GetCollection !== "function") return null;
        const typed = store.GetCollection("type-games");
        const games = (typed && (typed.allApps || typed.visibleApps)) || [];
        const ids = new Set(games.map((app) => app.appid >>> 0));
        const every = store.allAppsCollection;
        const all = (every && (every.allApps || every.visibleApps)) || games;
        const seen = new Set<number>();
        const out: { appId: number; name: string; shortcut: boolean }[] = [];
        for (const app of all) {
            const appId = app.appid >>> 0;
            const shortcut =
                typeof app.BIsShortcut === "function" ? !!app.BIsShortcut() : appId >= 2147483648;
            if ((!ids.has(appId) && !shortcut) || seen.has(appId)) continue;
            seen.add(appId);
            out.push({ appId, name: app.display_name || String(appId), shortcut });
        }
        return out.sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: "base" }));
    } catch {
        return null;
    }
};
