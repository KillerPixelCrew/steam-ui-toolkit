// Glyphs for the Quick Access rows and section headers the component host mounts.
//
// Valve draws every Quick Access icon as inline SVG that carries no size of its own: the shapes are
// filled with `currentColor` so they inherit the row's colour, and the panel's own CSS decides
// how big they are — `.FieldIcon svg` is 20px tall next to a label, and `.PanelSectionTitle > svg`
// is 18px. Field takes one through its `icon` prop, which SliderField, ToggleField and DropDownField
// all forward, so a row needs nothing but an element here.
//
// These are the toolkit's own drawings on a 24x24 grid, not copies of the client's artwork. Valve's
// icons live in the Steam bundle under Valve's terms; a library that ships under its own license
// cannot vendor them, and matching the drawing convention — solid shapes, `currentColor`, holes cut
// with `fill-rule="evenodd"` — is what makes a new glyph sit beside a Valve one without looking
// borrowed or bolted on.
//
// EVERY GLYPH IS USED EXACTLY ONCE. People navigate a panel like this by shape before they read the
// label, so a glyph that appears on a header and again on a row inside it, or on two rows that do
// different things, is worse than no glyph at all: it tells the eye two controls are the same when
// they are not. Adding a row means drawing a shape, never borrowing one.
//
// A shape is a tag and its attributes, in React's camelCase spelling because these are handed
// straight to Steam's own createElement. Composing an icon from rects and circles where the geometry
// allows keeps the path data short enough to read, which is the same reason Valve does it.
type SteamUiIconShape = readonly [string, Readonly<Record<string, string | number | boolean>>];

const SteamUiIconShapes: Readonly<Record<string, readonly SteamUiIconShape[]>> = Object.freeze({
  // -- Profile scope --------------------------------------------------------------------------

  // An ID card: the question the section answers is whose settings these are, not what they do.
  profile: [
    [
      "path",
      {
        d: "M3 5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5Zm2 0v14h14V5H5Z",
        fillRule: "evenodd",
      },
    ],
    ["circle", { cx: 9.5, cy: 10, r: 2.3 }],
    ["path", { d: "M6 17.2a3.5 3.5 0 0 1 7 0v.3H6v-.3Z" }],
    ["path", { d: "M14.5 8.4h4v1.8h-4V8.4Zm0 3.6h4v1.8h-4V12Z" }],
  ],

  // -- Power profiles -------------------------------------------------------------------------

  // Two faders for the Power profiles header: a profile is a set position, not a power source.
  sliders: [
    ["rect", { x: 3, y: 6.4, width: 18, height: 2.2, rx: 1.1 }],
    ["rect", { x: 6.6, y: 4.2, width: 3.4, height: 6.6, rx: 1.3 }],
    ["rect", { x: 3, y: 15.4, width: 18, height: 2.2, rx: 1.1 }],
    ["rect", { x: 14, y: 13.2, width: 3.4, height: 6.6, rx: 1.3 }],
  ],
  // The Windows power plan, drawn as the symbol Windows itself puts on one.
  power: [
    [
      "path",
      {
        d: "M5.87 7.86A8 8 0 1 0 18.13 7.86",
        fill: "none",
        stroke: "currentColor",
        strokeWidth: 2.4,
        strokeLinecap: "round",
      },
    ],
    ["rect", { x: 10.8, y: 2.6, width: 2.4, height: 8.6, rx: 1.2 }],
  ],
  plug: [
    ["path", { d: "M8.5 2h2v5h-2V2Zm5 0h2v5h-2V2Z" }],
    ["path", { d: "M6 8h12v4a6 6 0 0 1-5 5.92V22h-2v-4.08A6 6 0 0 1 6 12V8Z" }],
  ],
  battery: [
    [
      "path",
      {
        d: "M3 7a2 2 0 0 1 2-2h11a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7Zm2 0v10h11V7H5Z",
        fillRule: "evenodd",
      },
    ],
    ["rect", { x: 19.5, y: 10, width: 2, height: 4, rx: 1 }],
    // A part-full cell rather than a solid one: at 20px a fill that reaches the casing closes the
    // gap between them and the whole glyph reads as a rounded block.
    ["rect", { x: 7, y: 9, width: 5, height: 6, rx: 0.8 }],
  ],

  // -- Charging -------------------------------------------------------------------------------

  // The same casing as `battery` with a bolt in it, because the Charging section and the On battery
  // assignment are related but not the same thing, and the eye should read both at once.
  batteryCharging: [
    [
      "path",
      {
        d: "M3 7a2 2 0 0 1 2-2h11a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7Zm2 0v10h11V7H5Z",
        fillRule: "evenodd",
      },
    ],
    ["rect", { x: 19.5, y: 10, width: 2, height: 4, rx: 1 }],
    ["path", { d: "M11.6 7.8 7.4 12.9h2.6l-.6 3.9 4.2-5.1h-2.6l.6-3.9Z" }],
  ],
  // The charge limit is a percentage and nothing else, so it is drawn as one.
  percent: [
    ["path", { d: "M16.4 2.6 18.4 3.7 7.6 21.4 5.6 20.3 16.4 2.6Z" }],
    [
      "path",
      { d: "M7.6 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8Zm0 2a2 2 0 1 1 0 4 2 2 0 0 1 0-4Z", fillRule: "evenodd" },
    ],
    [
      "path",
      {
        d: "M16.4 13a4 4 0 1 0 0 8 4 4 0 0 0 0-8Zm0 2a2 2 0 1 1 0 4 2 2 0 0 1 0-4Z",
        fillRule: "evenodd",
      },
    ],
  ],

  // -- Display and frame rate -----------------------------------------------------------------

  display: [
    [
      "path",
      {
        d: "M2 5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5Zm2 0v10h16V5H4Z",
        fillRule: "evenodd",
      },
    ],
    ["path", { d: "M10 17h4v2h3v2H7v-2h3v-2Z" }],
  ],
  // Corner brackets for the resolution row: the mode is the size of the frame, not the panel.
  aspect: [
    [
      "path",
      {
        d: "M3 3h7v2.4H5.4V10H3V3Zm11 0h7v7h-2.4V5.4H14V3ZM3 14h2.4v4.6H10V21H3v-7Zm15.6 0H21v7h-7v-2.4h4.6V14Z",
      },
    ],
  ],
  // A stopwatch heads the Performance display section, where every row is about frame timing rather
  // than the panel itself.
  timer: [
    ["rect", { x: 9.6, y: 1.4, width: 4.8, height: 2.6, rx: 1 }],
    ["rect", { x: 11, y: 3.6, width: 2, height: 2.6 }],
    [
      "path",
      {
        d: "M12 5.8a8.2 8.2 0 1 0 0 16.4 8.2 8.2 0 0 0 0-16.4Zm0 2a6.2 6.2 0 1 1 0 12.4 6.2 6.2 0 0 1 0-12.4Z",
        fillRule: "evenodd",
      },
    ],
    ["rect", { x: 11.1, y: 8.6, width: 1.8, height: 5.4 }],
    ["rect", { x: 11.1, y: 13.1, width: 6, height: 1.8 }],
  ],
  // Rising bars for the frame-rate row: the same shape reads as a cap while one is set and as the
  // refresh rate once the cap is off, which is exactly what that one slider does.
  frameRate: [
    ["rect", { x: 3, y: 13, width: 4, height: 8, rx: 1.2 }],
    ["rect", { x: 10, y: 8, width: 4, height: 13, rx: 1.2 }],
    ["rect", { x: 17, y: 3, width: 4, height: 18, rx: 1.2 }],
  ],
  // Variable refresh: an uneven trace rather than a steady one. Stroked, not filled — a 2px line is
  // the only honest way to draw a waveform at this size.
  pulse: [
    [
      "path",
      {
        d: "M2.6 12h3.1l2.5-6.4 4 13 2.8-6.6h6.4",
        fill: "none",
        stroke: "currentColor",
        strokeWidth: 2.2,
        strokeLinecap: "round",
        strokeLinejoin: "round",
      },
    ],
  ],

  // -- Power limits ---------------------------------------------------------------------------

  // A dial with a needle for the header: the section is where the ceiling is set, and the rows
  // under it are the two watt figures themselves.
  gauge: [
    [
      "path",
      { d: "M2.82 12.54A9.5 9.5 0 0 1 21.18 12.54L18.47 13.27A6.7 6.7 0 0 0 5.53 13.27L2.82 12.54Z" },
    ],
    ["path", { d: "M16.3 8.9 13.5 16 10.5 14 16.3 8.9Z" }],
    ["circle", { cx: 12, cy: 15, r: 2.2 }],
  ],
  bolt: [["path", { d: "M13.6 2 4 13.6h5.6L8.4 22 18 10.4h-5.6L13.6 2Z" }]],
  // Boost sits above sustained on the same axis, so it is the sustained bolt's idea pointed upward
  // rather than a second unrelated glyph.
  boost: [
    ["path", { d: "M12 3 4 11l2.2 2.2L12 7.4l5.8 5.8L20 11 12 3Z" }],
    ["path", { d: "M12 11 4 19l2.2 2.2L12 15.4l5.8 5.8L20 19 12 11Z" }],
  ],
  auto: [
    ["path", { d: "M10 3.5c1 3.5 1.5 4 5 5-3.5 1-4 1.5-5 5-1-3.5-1.5-4-5-5 3.5-1 4-1.5 5-5Z" }],
    [
      "path",
      { d: "M17.4 12.4c.8 3 1.2 3.4 4.2 4.2-3 .8-3.4 1.2-4.2 4.2-.8-3-1.2-3.4-4.2-4.2 3-.8 3.4-1.2 4.2-4.2Z" },
    ],
  ],

  // -- Controller -----------------------------------------------------------------------------

  // Deliberately taller than a bare stadium would be: a 2:1 body leaves the pad and the two face
  // buttons too small to tell apart once the row draws it at 20px.
  controller: [
    [
      "path",
      {
        d: "M7 5.5h10a6.5 6.5 0 0 1 0 13H7a6.5 6.5 0 0 1 0-13Zm0 2.2a4.3 4.3 0 0 0 0 8.6h10a4.3 4.3 0 0 0 0-8.6H7Z",
        fillRule: "evenodd",
      },
    ],
    ["path", { d: "M6.3 9.4h1.5V11h1.6v1.5H7.8v1.6H6.3v-1.6H4.7V11h1.6V9.4Z" }],
    ["circle", { cx: 16.2, cy: 10.6, r: 1.4 }],
    ["circle", { cx: 18.4, cy: 13, r: 1.4 }],
  ],
  // The target row chooses which controller the game is shown, so it is an exchange rather than a
  // second gamepad under a gamepad header.
  swap: [
    ["path", { d: "M3 8.4h12.5V5.6L21 9.5l-5.5 3.9v-2.8H3V8.4Z" }],
    ["path", { d: "M21 15.4H8.5v-2.8L3 16.5l5.5 3.9v-2.8H21v-2.2Z" }],
  ],

  // -- Reset ----------------------------------------------------------------------------------

  reset: [
    ["path", { d: "M12 3a9 9 0 1 0 8.5 6.1l-1.9.6A7 7 0 1 1 12 5V3Z" }],
    // The head has to clear the band it grows out of, or the whole glyph reads as a plain broken
    // ring with a thick spot at the top.
    ["path", { d: "M13.2.6 7.8 4l5.4 3.4V.6Z" }],
  ],

  // -- RGB lighting ---------------------------------------------------------------------------

  // Three overlapping rings head the section: the additive triad the lighting hardware mixes.
  colors: [
    [
      "path",
      {
        d: "M12 2.6a5.2 5.2 0 1 0 0 10.4 5.2 5.2 0 0 0 0-10.4Zm0 2.2a3 3 0 1 1 0 6 3 3 0 0 1 0-6Z",
        fillRule: "evenodd",
      },
    ],
    [
      "path",
      {
        d: "M7.6 11a5.2 5.2 0 1 0 0 10.4 5.2 5.2 0 0 0 0-10.4Zm0 2.2a3 3 0 1 1 0 6 3 3 0 0 1 0-6Z",
        fillRule: "evenodd",
      },
    ],
    [
      "path",
      {
        d: "M16.4 11a5.2 5.2 0 1 0 0 10.4 5.2 5.2 0 0 0 0-10.4Zm0 2.2a3 3 0 1 1 0 6 3 3 0 0 1 0-6Z",
        fillRule: "evenodd",
      },
    ],
  ],
  // The LEDs' own brightness is a lamp, kept apart from the value slider inside the colour editor.
  bulb: [
    ["circle", { cx: 12, cy: 9.4, r: 6.2 }],
    ["rect", { x: 8.4, y: 13.4, width: 7.2, height: 3.6 }],
    ["rect", { x: 8.8, y: 17.6, width: 6.4, height: 1.9, rx: 0.95 }],
    ["rect", { x: 9.8, y: 20.1, width: 4.4, height: 1.9, rx: 0.95 }],
  ],
  // Edit color opens the editor, so the row is the act of editing rather than a second colour wheel.
  pencil: [
    ["path", { d: "M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25Z" }],
    [
      "path",
      {
        d: "M20.71 7.04a1 1 0 0 0 0-1.41l-2.34-2.34a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83Z",
      },
    ],
  ],
  zones: [
    ["rect", { x: 3.4, y: 3.4, width: 7.6, height: 7.6, rx: 1.6 }],
    ["rect", { x: 13, y: 3.4, width: 7.6, height: 7.6, rx: 1.6 }],
    ["rect", { x: 3.4, y: 13, width: 7.6, height: 7.6, rx: 1.6 }],
    ["rect", { x: 13, y: 13, width: 7.6, height: 7.6, rx: 1.6 }],
  ],
  // Hue is the whole spectrum in one control, which is a rainbow and not a single swatch.
  rainbow: [
    [
      "path",
      {
        d: "M3.9 18.6a8.1 8.1 0 0 1 16.2 0M8.4 18.6a3.6 3.6 0 0 1 7.2 0",
        fill: "none",
        stroke: "currentColor",
        strokeWidth: 2.6,
        strokeLinecap: "round",
      },
    ],
  ],
  // Saturation: how much colour there is, which is a drop of it.
  droplet: [
    ["path", { d: "M12 2.4c4 4.7 6.4 8 6.4 11.1a6.4 6.4 0 0 1-12.8 0c0-3.1 2.4-6.4 6.4-11.1Z" }],
  ],
  // The colour's own value, drawn as the light-to-dark split it actually moves.
  contrast: [
    [
      "path",
      {
        d: "M12 2.6a9.4 9.4 0 1 0 0 18.8 9.4 9.4 0 0 0 0-18.8Zm0 2.2a7.2 7.2 0 1 1 0 14.4 7.2 7.2 0 0 1 0-14.4Z",
        fillRule: "evenodd",
      },
    ],
    ["path", { d: "M12 4.8a7.2 7.2 0 0 1 0 14.4V4.8Z" }],
  ],
});

// Builds icons with Steam's own React, and caches the result: a React element is immutable, so one
// per name and size can be handed to every render of every row rather than rebuilt on each pass.
// An unknown name returns null, which is what Field, PanelSection and the section header below all
// treat as "no icon" — a mistyped name loses a glyph, never a row.
const createIconRenderer = (react) => {
  const cache = new Map();
  return (name, size = 20) => {
    if (typeof name !== "string" || !Object.hasOwn(SteamUiIconShapes, name)) return null;
    const key = `${name}:${size}`;
    const cached = cache.get(key);
    if (cached) return cached;
    const element = react.createElement(
      "svg",
      {
        xmlns: "http://www.w3.org/2000/svg",
        viewBox: "0 0 24 24",
        width: size,
        height: size,
        fill: "currentColor",
        // Decorative in every place it is used: the row's own label is the accessible name, and a
        // second announcement of it would only make the panel noisier to listen to.
        "aria-hidden": true,
        focusable: false,
      },
      ...SteamUiIconShapes[name].map(([tag, attributes], index) =>
        react.createElement(tag, { key: index, ...attributes }),
      ),
    );
    cache.set(key, element);
    return element;
  };
};
