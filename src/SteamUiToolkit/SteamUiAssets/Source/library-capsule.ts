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
