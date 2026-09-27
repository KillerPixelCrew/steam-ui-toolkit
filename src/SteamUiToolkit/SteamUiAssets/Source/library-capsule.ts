// A library capsule drawn exactly as Steam's library draws one.
//
// Steam's own capsule component takes an app overview from its stores, so it can only draw games
// Steam already has. A host page that shows titles Steam does not know yet - an importer's review,
// say - builds the same element from Steam's library class map instead: the item box with its
// portrait or landscape shape, the image class, the shine and the overlay areas. The focus ring,
// the grow-on-focus animation and the shine are Steam's CSS for those classes, not this file's.
//
// Mapped from the installed client on 2026-09-27: the library item module's class map carries
// LibraryItemBox, Portrait, Landscape, PortraitImage, LibraryItemBoxShine and the two overlay
// areas; its gamepad capsule composes LibraryItemBox with Portrait or Landscape, then the image,
// then the shine, then LibraryItemOverlayOuterArea around LibraryItemOverlayInnerArea.

// The same three tokens the library badge finds this map by.
const SteamLibraryClassTokens = [
    'ControllerSupportIcon:"',
    'LibraryItemIcons:"',
    'LibraryItemBox:"',
] as const;

// Every class the capsule uses. A map that lost one of them is not the map this was written
// against, so the capsule is unavailable rather than half-styled.
const SteamLibraryClassNames = [
    "LibraryItemBox",
    "Portrait",
    "Landscape",
    "PortraitImage",
    "LibraryItemBoxShine",
    "LibraryItemOverlayOuterArea",
    "LibraryItemOverlayInnerArea",
] as const;

// Resolves Steam's library class map, or null when this client's differs.
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

// Builds the capsule component over resolved Steam components and classes. Create it once per
// resolution and keep it: a component made on every render is a new type each time, and React
// would remount the grid and drop the controller's focus.
//
// Props:
//   asset        grid, wide, hero, logo or icon: the shape
//   image        the URL to show, or empty for the placeholder
//   placeholder  what to write in its place when there is no image
//   width        the capsule's width in pixels
//   dimmed       drawn faded, for an item that is left out
//   overlay      elements for the overlay area: badges, a selection mark
//   caption      an element for the bottom edge, such as which image of how many
//   focus        props for Steam's Focusable: onActivate, onSecondaryButton, action descriptions
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
