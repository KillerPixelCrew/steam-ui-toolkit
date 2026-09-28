// The UI kit: the elements a host draws around Steam's own fields.
//
// Steam ships a toggle, a dropdown, a slider, a text field, a button and a modal, and a page uses
// those wherever one fits, resolved from Steam's own modules. It ships nothing for the rest of what
// a page is made of: a section heading that folds, a row of actions, a labelled control, a note, a
// swatch, a card in a grid. Those are drawn here, once, from plain elements and one stylesheet, in
// the vocabulary of Steam's own panels — its greys, its 2px radius, its focus outline — so a host's
// page and its Quick Access tab look like one thing and like the panels beside them.
//
// Every element takes `ui`, the components resolved for the page, and answers React elements built
// with Steam's React, so Steam's navigation treats them as its own. Focus is Steam's Focusable, and
// the `gpfocus` class it sets on the focused element is what the stylesheet lights up.
//
// The stylesheet is rendered by whichever root uses the kit (`steamUiKitStyle`), so it lands in the
// document the root is drawn into: the Quick Access popup, a page's window, a modal. Class names
// are prefixed `steam-ui-kit-` and the rules are flat, so a host can add to them without fighting
// specificity.

const SteamUiKitStyles = `
.steam-ui-kit-header{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:8px 10px;margin:0 -10px;border-radius:2px;outline:2px solid transparent}
.steam-ui-kit-header.gpfocus,.steam-ui-kit-header:hover{background:rgba(255,255,255,.08)}
.steam-ui-kit-header.plain:hover{background:transparent}
.steam-ui-kit-header-icon{display:flex;flex:0 0 auto;color:rgba(255,255,255,.8)}
.steam-ui-kit-header-icon svg{width:18px;height:18px}
.steam-ui-kit-header-text{min-width:0;flex:1 1 auto}
.steam-ui-kit-header-title{font-size:12px;font-weight:700;letter-spacing:.09em;text-transform:uppercase;color:rgba(255,255,255,.6);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.steam-ui-kit-header.open .steam-ui-kit-header-title{color:#fff}
.steam-ui-kit-header-detail{font-size:12px;color:rgba(255,255,255,.55);margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.steam-ui-kit-header-caret{flex:0 0 auto;color:rgba(255,255,255,.7);display:flex}
.steam-ui-kit-actions{display:grid;grid-template-columns:1fr 1fr;gap:8px}
.steam-ui-kit-actions button.DialogButton{width:auto!important;min-width:0!important;height:36px!important;padding:0 10px!important;box-sizing:border-box!important;font-size:13px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;display:flex;align-items:center;justify-content:center;gap:8px}
.steam-ui-kit-actions .DialogButton.steam-ui-kit-wide{grid-column:1 / -1}
.steam-ui-kit-actions .DialogButton svg{width:16px;height:16px;flex:0 0 auto}
.steam-ui-kit-labelled{display:flex;flex-direction:column;gap:6px;padding:4px 0}
.steam-ui-kit-label{font-size:13px;color:rgba(255,255,255,.7)}
.steam-ui-kit-labelled .DialogDropDown{width:100%}
.steam-ui-kit-nested{margin-left:2px;padding-left:12px;border-left:2px solid rgba(255,255,255,.12);box-sizing:border-box}
.steam-ui-kit-nested .DialogToggle_Label{font-size:14px}
.steam-ui-kit-nested .DialogToggle_Description{font-size:12px}
.steam-ui-kit-group{border-radius:4px;background:rgba(255,255,255,.045);border:1px solid rgba(255,255,255,.07);padding:4px 12px 8px;margin:0 12px 10px;box-sizing:border-box}
.steam-ui-kit-group.plain{padding:8px 12px}
.steam-ui-kit-group.hidden{display:none}
.steam-ui-kit-group.closed .steam-ui-kit-group-body{display:none}
.steam-ui-kit-blocks > div:not(:empty){border-radius:4px;background:rgba(255,255,255,.045);border:1px solid rgba(255,255,255,.07);padding:4px 12px 8px!important;margin:0 12px 10px!important;box-sizing:border-box}
.steam-ui-kit-valve > div:not(:empty){border-radius:4px;background:rgba(255,255,255,.045);border:1px solid rgba(255,255,255,.07);padding:8px 12px 8px!important;margin:0 12px 10px!important;box-sizing:border-box}
.steam-ui-kit-valve > div:not(:empty) > div:first-child{font-size:12px!important;font-weight:700;letter-spacing:.09em;text-transform:uppercase;color:rgba(255,255,255,.6)!important;padding:4px 0 8px!important}
.steam-ui-kit-group-body > div > :first-child,.steam-ui-kit-blocks > div > div > :first-child,.steam-ui-kit-valve > div > div > :first-child{--field-negative-horizontal-margin:0px}
.steam-ui-kit-group-body div,.steam-ui-kit-blocks div,.steam-ui-kit-valve div,.steam-ui-kit-battery div{min-width:0!important}
.steam-ui-kit-group-body button.DialogButton,.steam-ui-kit-blocks button.DialogButton,.steam-ui-kit-valve button.DialogButton{min-width:0!important;box-sizing:border-box!important}
.steam-ui-kit-battery{width:calc(100% - 32px);box-sizing:border-box;margin:0 16px 6px;padding-bottom:8px;border-bottom:1px solid rgba(255,255,255,.08);--field-negative-horizontal-margin:0px}
.steam-ui-kit-battery > *{margin-inline:0!important;padding-inline:0!important;width:100%!important}
.steam-ui-kit-battery div:has(> :nth-child(3):last-child){display:flex;flex-wrap:nowrap;align-items:center;gap:8px;height:24px!important}
.steam-ui-kit-battery div:has(> :nth-child(3):last-child) > :nth-child(1){width:24px!important;height:24px!important;margin:0!important;transform:scale(.6);transform-origin:center}
.steam-ui-kit-battery div:has(> :nth-child(3):last-child) > :nth-child(1) > div{height:24px!important;align-items:center}
.steam-ui-kit-battery div:has(> :nth-child(3):last-child) > :nth-child(2){font-size:14px!important;font-weight:600;height:auto!important;line-height:24px}
.steam-ui-kit-battery div:has(> :nth-child(3):last-child) > :nth-child(3){margin-left:auto!important;height:auto!important;flex-direction:row!important;align-items:baseline;gap:6px}
.steam-ui-kit-battery div:has(> :nth-child(3):last-child) > :nth-child(3) > :first-child{font-size:13px!important;font-weight:600;height:auto!important}
.steam-ui-kit-battery div:has(> :nth-child(3):last-child) > :nth-child(3) > :last-child{font-size:10px!important;height:auto!important}
.steam-ui-kit-note{display:flex;align-items:center;gap:8px;font-size:12px;color:rgba(255,255,255,.5);padding:6px 0}
.steam-ui-kit-note svg{width:14px;height:14px;flex:0 0 auto}
.steam-ui-kit-highlight{color:#fca904}
.steam-ui-kit-marker{position:absolute;top:0;right:0;width:20px;height:20px;background:linear-gradient(45deg,transparent 49%,#fca904 50%);pointer-events:none}
.steam-ui-kit-swatch{width:20px;height:20px;border-radius:3px;border:1px solid rgba(255,255,255,.3);flex:0 0 auto}
.steam-ui-kit-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:14px}
.steam-ui-kit-card{display:flex;flex-direction:column;border-radius:4px;overflow:hidden;background:#ACB2C924;outline:2px solid transparent;transition:outline-color 150ms,background 150ms}
.steam-ui-kit-card.gpfocus,.steam-ui-kit-card:hover{background:#ACB2C947;outline-color:#fff}
.steam-ui-kit-card-shot{position:relative;aspect-ratio:16 / 10;background:#10151c;overflow:hidden}
.steam-ui-kit-card-shot img{width:100%;height:100%;object-fit:cover;display:block}
.steam-ui-kit-card-stats{position:absolute;left:0;right:0;bottom:0;display:flex;gap:12px;padding:6px 8px;font-size:12px;color:#fff;background:linear-gradient(180deg,transparent,rgba(0,0,0,.75))}
.steam-ui-kit-card-stats span{display:inline-flex;align-items:center;gap:4px;min-width:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.steam-ui-kit-card-stats svg{width:13px;height:13px;flex:0 0 auto}
.steam-ui-kit-badge{position:absolute;top:6px;right:6px;padding:2px 8px;border-radius:12px;font-size:11px;font-weight:700;background:#5cb85c;color:#000}
.steam-ui-kit-badge.warn{background:#fca904}
.steam-ui-kit-card-title{font-size:15px;font-weight:600;color:#fff;padding:8px 10px 0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.steam-ui-kit-card-meta{font-size:11px;color:rgba(255,255,255,.55);padding:2px 10px}
.steam-ui-kit-card-meta:last-child{padding-bottom:10px}
.steam-ui-kit-empty{padding:12px;text-align:center;color:#b8bcbf;font-size:14px}
.steam-ui-kit-empty.error{color:#ff6d6d}
.steam-ui-kit-banner{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:10px 14px;border-radius:2px;background:rgba(26,159,255,.18);font-size:14px;color:#dcdedf}
.steam-ui-kit-banner.error{background:rgba(194,70,62,.25)}
.steam-ui-kit-toolbar{display:flex;align-items:flex-end;gap:12px;flex-wrap:nowrap}
.steam-ui-kit-toolbar .DialogButton{width:auto;min-width:auto;height:40px;padding:0 16px;white-space:nowrap}
.steam-ui-kit-tool{display:flex;flex-direction:column;flex:0 0 auto}
.steam-ui-kit-tool .DialogLabel{font-size:12px;margin-bottom:4px}
.steam-ui-kit-tool .DialogDropDown_CurrentDisplay{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.steam-ui-kit-tool.grow{flex:1 1 auto;min-width:160px}
.steam-ui-kit-tool.grow .DialogInputLabelGroup,.steam-ui-kit-tool.grow .DialogInput_Wrapper{margin:0}
.steam-ui-kit-chips{display:flex;flex-wrap:wrap;gap:8px}
.steam-ui-kit-chips .DialogButton{width:auto;min-width:auto;height:32px;padding:0 12px;font-size:13px}
.steam-ui-kit-box{background:rgba(27,40,56,.9);border-radius:4px;padding:16px;display:flex;flex-direction:column;gap:10px}
.steam-ui-kit-box-title{display:flex;align-items:center;gap:6px;font-size:16px;font-weight:600;color:#fff}
.steam-ui-kit-box-title svg{width:18px;height:18px}
.steam-ui-kit-muted{color:rgb(124,142,163);font-size:13px}
.steam-ui-kit-gallery{display:flex;gap:12px}
.steam-ui-kit-thumbs{display:flex;flex-direction:column;gap:8px}
.steam-ui-kit-thumb{width:96px;aspect-ratio:16 / 10;border-radius:3px;overflow:hidden;opacity:.6;outline:2px solid transparent}
.steam-ui-kit-thumb.current,.steam-ui-kit-thumb.gpfocus{opacity:1;outline-color:#fff}
.steam-ui-kit-thumb img{width:100%;height:100%;object-fit:cover;display:block}
.steam-ui-kit-hero{position:relative;width:556px;max-width:100%;aspect-ratio:16 / 10;border-radius:4px;overflow:hidden;background:#10151c}
.steam-ui-kit-hero img{width:100%;height:100%;object-fit:cover;display:block}
.steam-ui-kit-hero-empty{display:flex;align-items:center;justify-content:center;height:100%;color:#8b929a}
.steam-ui-kit-hero-count{position:absolute;right:10px;bottom:10px;padding:3px 8px;border-radius:2px;background:rgba(0,0,0,.7);font-size:12px;color:#fff}
.steam-ui-kit-modal-body{display:flex;flex-direction:column;gap:12px}
.steam-ui-kit-modal-body p{margin:0}
.steam-ui-kit-modal-actions{display:flex;justify-content:flex-end;gap:8px;margin-top:8px}
`;

// The kit's stylesheet, rendered once by a root so it lands in that root's document.
const steamUiKitStyle = (react) => react.createElement("style", { key: "steam-ui-kit" }, SteamUiKitStyles);

// A section heading: a glyph, the title and its detail line, and, when it folds, a caret that says
// which way. A folding header is Steam's Focusable, because Steam's own section title cannot take
// focus and a controller has to be able to land on the fold; one that does not fold is a plain
// heading, drawn the same so a fixed section and a folding one read as siblings.
const renderSteamUiHeader = (
  ui,
  props: {
    title: string;
    icon?: any;
    detail?: string;
    collapsed?: boolean;
    onToggle?: () => void;
    caret?: any;
  },
) => {
  const h = ui.react.createElement;
  const folds = typeof props.onToggle === "function";
  const collapsed = folds && !!props.collapsed;
  // The kit's own caret unless the caller brought one; a heading that does not fold has none.
  const icon = createIconRenderer(ui.react);
  const caret =
    props.caret ?? (folds ? (collapsed ? icon("sectionClosed", 18) : icon("sectionOpen", 18)) : null);
  const children = [
    props.icon ? h("div", { className: "steam-ui-kit-header-icon" }, props.icon) : null,
    h(
      "div",
      { className: "steam-ui-kit-header-text" },
      h("div", { className: "steam-ui-kit-header-title" }, props.title),
      props.detail ? h("div", { className: "steam-ui-kit-header-detail" }, props.detail) : null,
    ),
    caret ? h("div", { className: "steam-ui-kit-header-caret" }, caret) : null,
  ].filter((child) => child !== null);
  const className = `steam-ui-kit-header${collapsed ? "" : " open"}${folds ? "" : " plain"}`;
  return folds
    ? h(
        ui.focusable ?? "div",
        {
          className,
          onActivate: props.onToggle,
          onOKActionDescription: collapsed ? "Expand" : "Collapse",
        },
        ...children,
      )
    : h("div", { className }, ...children);
};

// A block of a panel: a heading over its rows, with a subtle fill and border so the blocks beside
// each other read as groups. With `onToggle` the heading folds the body away; the body stays
// mounted while folded, so rows keep their subscriptions and what a folded block's detail line
// reports stays current. `hidden` takes the whole block out of layout, still mounted. Without a
// title the block is a plain box around its rows. A root whose blocks are Steam's own PanelSections
// gives them the same look with the `steam-ui-kit-blocks` class, and `steam-ui-kit-valve` also
// restyles Valve's section titles to the kit's heading. Inside every block the field bleed Steam's
// panel rows give their fields (`--field-negative-horizontal-margin`, 16px, so a field can run to
// the panel's edge) is set to zero: a block has a border, and a field runs to that. The Quick
// Access menu also gives a field's control container a 270px minimum width and its buttons a
// 160px one, from an id-scoped rule, so both are lifted with `!important`: a block's content is
// narrower than Valve's panel column, and a fixed minimum is what pushed dropdowns past the border.
const renderSteamUiGroup = (
  ui,
  props: {
    key?: string;
    title?: string | null;
    icon?: any;
    detail?: string;
    collapsed?: boolean;
    onToggle?: () => void;
    hidden?: boolean;
  },
  ...children
) => {
  const h = ui.react.createElement;
  const folds = typeof props.onToggle === "function";
  const collapsed = folds && !!props.collapsed;
  const className = [
    "steam-ui-kit-group",
    props.title ? "" : "plain",
    collapsed ? "closed" : "",
    props.hidden ? "hidden" : "",
  ]
    .filter((name) => name)
    .join(" ");
  return h(
    "div",
    { key: props.key, className },
    props.title
      ? renderSteamUiHeader(ui, {
          title: props.title,
          icon: props.icon,
          detail: props.detail,
          collapsed,
          onToggle: props.onToggle,
        })
      : null,
    h("div", { className: "steam-ui-kit-group-body" }, ...children),
  );
};

// Actions in a two-column grid: two short labels sit side by side, a long one takes the row. Each
// is Steam's DialogButton, so it navigates and lights up as Steam's do.
const renderSteamUiActions = (
  ui,
  actions: { id: string; label: string; onClick: () => void; icon?: any; wide?: boolean }[],
) => {
  const h = ui.react.createElement;
  return h(
    ui.focusable,
    { "flow-children": "row", className: "steam-ui-kit-actions" },
    ...actions.map((action) =>
      h(
        ui.dialogButton,
        {
          key: action.id,
          className: action.wide ?? action.label.length > 18 ? "steam-ui-kit-wide" : undefined,
          onClick: action.onClick,
        },
        action.icon ?? null,
        action.label,
      ),
    ),
  );
};

// A small label above a control, the way CSSLoader lays out a patch's dropdown in the panel.
const renderSteamUiLabelled = (ui, label: string, control) => {
  const h = ui.react.createElement;
  return h(
    "div",
    { className: "steam-ui-kit-labelled" },
    h("div", { className: "steam-ui-kit-label" }, label),
    control,
  );
};

// A quiet line under a list, with an optional glyph: "1 theme is hidden."
const renderSteamUiNote = (ui, text: string, glyph?: any) => {
  const h = ui.react.createElement;
  return h("div", { className: "steam-ui-kit-note" }, glyph ?? null, h("span", null, text));
};

// A colour as a small square.
const renderSteamUiSwatch = (react, color: string) =>
  react.createElement("div", { className: "steam-ui-kit-swatch", style: { background: color } });

// A card in a grid: a 16:10 image with a stats strip over its foot, a badge in its corner, a title
// and up to a few meta lines. Focusable and activatable as one thing.
const renderSteamUiCard = (
  ui,
  props: {
    key?: string;
    image?: string | null;
    stats?: { glyph?: any; text: string }[];
    badge?: { text: string; warn?: boolean } | null;
    title: string;
    meta?: string[];
    onActivate: () => void;
    activateDescription?: string;
  },
) => {
  const h = ui.react.createElement;
  return h(
    ui.focusable,
    {
      key: props.key,
      className: "steam-ui-kit-card",
      onActivate: props.onActivate,
      onOKActionDescription: props.activateDescription ?? "Open",
    },
    h(
      "div",
      { className: "steam-ui-kit-card-shot" },
      props.image ? h("img", { src: props.image, alt: "", loading: "lazy" }) : null,
      props.stats?.length
        ? h(
            "div",
            { className: "steam-ui-kit-card-stats" },
            ...props.stats.map((stat, index) => h("span", { key: index }, stat.glyph ?? null, stat.text)),
          )
        : null,
      props.badge
        ? h("div", { className: `steam-ui-kit-badge${props.badge.warn ? " warn" : ""}` }, props.badge.text)
        : null,
    ),
    h("div", { className: "steam-ui-kit-card-title" }, props.title),
    ...(props.meta ?? []).map((line, index) => h("div", { key: index, className: "steam-ui-kit-card-meta" }, line)),
  );
};

// A grid of cards.
const renderSteamUiGrid = (ui, cards: any[]) =>
  ui.react.createElement(ui.focusable, { className: "steam-ui-kit-grid", "flow-children": "grid" }, ...cards);

// What a list shows when it has nothing, or why it could not be filled.
const renderSteamUiEmpty = (react, text: string, error = false) =>
  react.createElement("div", { className: `steam-ui-kit-empty${error ? " error" : ""}` }, text);

// A line the user should read, with a way to dismiss it: a notice, or an error in red.
const renderSteamUiBanner = (ui, props: { text: string; error?: boolean; onDismiss: () => void }) => {
  const h = ui.react.createElement;
  return h(
    "div",
    { className: `steam-ui-kit-banner${props.error ? " error" : ""}` },
    h("span", null, props.text),
    h(ui.smallButton ?? ui.dialogButton, { onClick: props.onDismiss }, "Dismiss"),
  );
};

// A toolbar of controls: dropdowns, a search box and buttons in one focusable row. A tool is
// `renderSteamUiTool`, which labels a control the way the store's filter row labels its own;
// `grow` lets a search box take what is left.
const renderSteamUiToolbar = (ui, ...tools) =>
  ui.react.createElement(ui.focusable, { className: "steam-ui-kit-toolbar", "flow-children": "row" }, ...tools);
const renderSteamUiTool = (ui, label: string | null, control, grow = false) => {
  const h = ui.react.createElement;
  return h(
    "div",
    { className: `steam-ui-kit-tool${grow ? " grow" : ""}` },
    label ? h("span", { className: "DialogLabel" }, label) : null,
    control,
  );
};

// Small buttons in a wrapping row: a theme's targets, a filter's values.
const renderSteamUiChips = (ui, chips: { label: string; onClick: () => void; description?: string }[]) => {
  const h = ui.react.createElement;
  return h(
    ui.focusable,
    { "flow-children": "row", className: "steam-ui-kit-chips" },
    ...chips.map((chip) =>
      h(
        ui.dialogButton,
        { key: chip.label, onClick: chip.onClick, onOKActionDescription: chip.description },
        chip.label,
      ),
    ),
  );
};

// A box with a bold title line and whatever follows: the action column of a detail view.
const renderSteamUiBox = (react, title, ...children) =>
  react.createElement(
    "div",
    { className: "steam-ui-kit-box" },
    title ? react.createElement("div", { className: "steam-ui-kit-box-title" }, title) : null,
    ...children,
  );

// A gallery: one large image and, with more than one, a column of thumbnails that pick it and a
// counter over its corner.
const renderSteamUiGallery = (
  ui,
  props: { images: string[]; index: number; onSelect: (index: number) => void; empty?: string },
) => {
  const h = ui.react.createElement;
  const images = props.images ?? [];
  const index = Math.min(Math.max(0, props.index), Math.max(0, images.length - 1));
  const shown = images[index];
  return h(
    "div",
    { className: "steam-ui-kit-gallery" },
    images.length > 1
      ? h(
          ui.focusable,
          { className: "steam-ui-kit-thumbs", "flow-children": "column" },
          ...images.map((url, at) =>
            h(
              ui.focusable,
              {
                key: url,
                className: `steam-ui-kit-thumb${at === index ? " current" : ""}`,
                onActivate: () => props.onSelect(at),
                onFocus: () => props.onSelect(at),
              },
              h("img", { src: url, alt: "" }),
            ),
          ),
        )
      : null,
    h(
      "div",
      { className: "steam-ui-kit-hero" },
      shown
        ? h("img", { src: shown, alt: "" })
        : h("div", { className: "steam-ui-kit-hero-empty" }, props.empty ?? "No image"),
      images.length > 1 ? h("div", { className: "steam-ui-kit-hero-count" }, `${index + 1}/${images.length}`) : null,
    ),
  );
};

// Asks before something is done: a sentence and two buttons in Steam's modal. Cancel and B send
// nothing.
const showSteamUiConfirm = (
  ui,
  props: { title: string; text: string; confirmLabel: string; onConfirm: () => void },
) => {
  const h = ui.react.createElement;
  return showSteamModal(ui, {
    title: props.title,
    className: "steam-ui-kit-modal",
    render: (close) =>
      h(
        "div",
        { className: "steam-ui-kit-modal-body" },
        h("p", null, props.text),
        h(
          ui.focusable,
          { "flow-children": "row", className: "steam-ui-kit-modal-actions" },
          h(ui.dialogButton, { onClick: close }, "Cancel"),
          h(
            ui.dialogButtonPrimary ?? ui.dialogButton,
            {
              onClick: () => {
                props.onConfirm();
                close();
              },
            },
            props.confirmLabel,
          ),
        ),
      ),
  });
};

// Asks for a line of text: a sentence, Steam's text field and two buttons. An empty answer is not
// sent.
function SteamUiPromptBody(props: any) {
  const ui = props.ui;
  const react = ui.react;
  const h = react.createElement;
  const [value, setValue] = react.useState(props.initial ?? "");
  return h(
    "div",
    { className: "steam-ui-kit-modal-body" },
    props.text ? h("p", null, props.text) : null,
    h(ui.textField, {
      label: props.label,
      value,
      onChange: (event) => setValue(event?.target?.value ?? ""),
    }),
    h(
      ui.focusable,
      { "flow-children": "row", className: "steam-ui-kit-modal-actions" },
      h(ui.dialogButton, { onClick: props.close }, "Cancel"),
      h(
        ui.dialogButtonPrimary ?? ui.dialogButton,
        {
          onClick: () => {
            if (!String(value).trim()) return;
            props.onConfirm(String(value).trim());
            props.close();
          },
        },
        props.confirmLabel,
      ),
    ),
  );
}
const showSteamUiPrompt = (
  ui,
  props: {
    title: string;
    text?: string;
    label: string;
    initial?: string;
    confirmLabel: string;
    onConfirm: (value: string) => void;
  },
) =>
  showSteamModal(ui, {
    title: props.title,
    className: "steam-ui-kit-modal",
    render: (close) => ui.react.createElement(SteamUiPromptBody, { ...props, ui, close }),
  });
