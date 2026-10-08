// Shared elements around Steam's native fields. Use Steam React/Focusable and render the kit
// stylesheet in each consuming document. CSS selectors preserve Valve layout and focus behavior.

const SteamUiKitStyles = `
.steam-ui-kit-page{margin-top:var(--basicui-header-height,40px);height:calc(100% - var(--basicui-header-height,40px));display:flex;flex-direction:column;background:var(--gpSystemDarkestGrey,#0e141b);color:#dcdedf}
.steam-ui-kit-pane{display:flex;flex-direction:column;gap:14px;padding:12px 4px 72px}
.steam-ui-kit-sheet{display:flex;flex-direction:column;gap:6px;width:100%;min-width:0;max-height:min(70vh,calc(100vh - 160px));overflow-y:auto;overflow-x:hidden;overscroll-behavior:contain;scrollbar-gutter:stable}
.steam-ui-kit-sheet>*{min-width:0;max-width:100%;box-sizing:border-box;flex-shrink:0}
.steam-ui-kit-sheet .DialogInput,.steam-ui-kit-sheet .DialogInputWrapper,.steam-ui-kit-sheet .DialogButton{min-width:0;max-width:100%;box-sizing:border-box}
.steam-ui-kit-sheet-note{font-size:14px;color:#8b929a;line-height:1.45;margin:0 0 12px}
.steam-ui-kit-sheet-error{color:#ff6d6d;font-size:14px}
.steam-ui-kit-sheet-actions{padding-top:18px}
.steam-ui-kit-page div[class*="gamepadtabbedpage_TabHeaderRowWrapper"]{background:#1b2838}
.steam-ui-kit-page-banner{margin:8px 48px 0}
.steam-ui-kit-page h3{margin:6px 0 0;font-size:15px;font-weight:700;color:#fff}
.steam-ui-kit-page p{margin:0;font-size:14px;line-height:1.5;color:#c6d4df;max-width:700px;white-space:pre-wrap}
.steam-ui-kit-detail{display:flex;gap:32px;padding:12px 4px 72px}
.steam-ui-kit-detail-main{flex:1;min-width:0;display:flex;flex-direction:column;gap:10px}
.steam-ui-kit-detail-aside{width:300px;flex:0 0 auto;display:flex;flex-direction:column;gap:14px}
.steam-ui-kit-detail-heading{display:flex;align-items:baseline;gap:12px}
.steam-ui-kit-detail-heading h2{margin:0;font-size:30px;font-weight:700;color:#fff}
.steam-ui-kit-detail-heading span{font-size:16px;font-weight:700;color:#fff}
.steam-ui-kit-header{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:8px 10px;margin:0 -10px;border-radius:2px;outline:2px solid transparent}
.steam-ui-kit-header.gpfocus,.steam-ui-kit-header:hover{background:rgba(255,255,255,.08)}
.steam-ui-kit-header.plain:hover{background:transparent}
.steam-ui-kit-header.sub{padding:6px 10px}
.steam-ui-kit-header-icon{display:flex;flex:0 0 auto;color:rgba(255,255,255,.8)}
.steam-ui-kit-header-icon svg{width:18px;height:18px}
.steam-ui-kit-header-text{min-width:0;flex:1 1 auto}
.steam-ui-kit-header-title{font-size:12px;font-weight:700;letter-spacing:.09em;text-transform:uppercase;color:rgba(255,255,255,.6);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.steam-ui-kit-header.open .steam-ui-kit-header-title{color:#fff}
.steam-ui-kit-header.sub .steam-ui-kit-header-title{font-size:13px;font-weight:600;letter-spacing:0;text-transform:none;color:rgba(255,255,255,.75)}
.steam-ui-kit-header.sub.open .steam-ui-kit-header-title{color:#fff}
.steam-ui-kit-header-detail{font-size:12px;color:rgba(255,255,255,.55);margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.steam-ui-kit-header-caret{flex:0 0 auto;color:rgba(255,255,255,.7);display:flex}
.steam-ui-kit-group,.steam-ui-kit-blocks > div:not(:empty),.steam-ui-kit-valve > div:not(:empty){border-radius:4px;background:rgba(255,255,255,.045);border:1px solid rgba(255,255,255,.07);padding:4px 12px 8px!important;margin:0 12px 10px!important;box-sizing:border-box}
.steam-ui-kit-group.plain{padding:8px 12px!important}
.steam-ui-kit-group.hidden{display:none}
.steam-ui-kit-more{display:flex;justify-content:center;padding:8px 0 24px}
.steam-ui-kit-more .DialogButton{width:50%!important}
.steam-ui-kit-group.closed .steam-ui-kit-group-body{display:none}
.steam-ui-kit-valve > div:not(:empty){padding-top:8px!important}
.steam-ui-kit-valve > div:not(:empty) > div:first-child{font-size:12px!important;font-weight:700;letter-spacing:.09em;text-transform:uppercase;color:rgba(255,255,255,.6)!important;padding:4px 0 8px!important}
.steam-ui-kit-group-body > div > :first-child,.steam-ui-kit-blocks > div > div > :first-child,.steam-ui-kit-valve > div > div > :first-child{--field-negative-horizontal-margin:0px}
.steam-ui-kit-group-body div,.steam-ui-kit-blocks div,.steam-ui-kit-valve div,.steam-ui-kit-battery div{min-width:0!important}
.steam-ui-kit-group-body button.DialogButton,.steam-ui-kit-blocks button.DialogButton,.steam-ui-kit-valve button.DialogButton{min-width:0!important;box-sizing:border-box!important}
.steam-ui-kit-battery{width:calc(100% - 32px);box-sizing:border-box;margin:0 16px 6px;padding-bottom:8px;border-bottom:1px solid rgba(255,255,255,.08);--field-negative-horizontal-margin:0px}
.steam-ui-kit-battery > *{margin-inline:0!important;padding-inline:0!important;width:100%!important}
.steam-ui-kit-battery div:has(> :nth-child(2):nth-last-child(2):not(:empty)){display:flex;flex-wrap:nowrap;align-items:center;gap:8px;height:24px!important}
.steam-ui-kit-battery div:has(> :nth-child(2):nth-last-child(2):not(:empty)) > :nth-child(1){width:24px!important;height:24px!important;margin:0!important;transform:scale(.6);transform-origin:center}
.steam-ui-kit-battery div:has(> :nth-child(2):nth-last-child(2):not(:empty)) > :nth-child(1) > div{height:24px!important;align-items:center}
.steam-ui-kit-battery div:has(> :nth-child(2):nth-last-child(2):not(:empty)) > :nth-child(2){font-size:14px!important;font-weight:600;height:auto!important;line-height:24px}
.steam-ui-kit-battery div:has(> :nth-child(2):nth-last-child(2):not(:empty)) > :nth-child(3){margin-left:auto!important;height:auto!important;flex-direction:row!important;align-items:baseline;gap:6px}
.steam-ui-kit-battery div:has(> :nth-child(2):nth-last-child(2):not(:empty)) > :nth-child(3) > :first-child{font-size:13px!important;font-weight:600;height:auto!important}
.steam-ui-kit-battery div:has(> :nth-child(2):nth-last-child(2):not(:empty)) > :nth-child(3) > :last-child{font-size:10px!important;height:auto!important}
.steam-ui-kit-actions{display:grid;grid-template-columns:1fr 1fr;gap:8px}
.steam-ui-kit-actions button.DialogButton{display:block!important;width:auto!important;min-width:0!important;height:36px!important;line-height:36px!important;padding:0 10px!important;box-sizing:border-box!important;font-size:13px;text-align:center;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.steam-ui-kit-actions button.DialogButton:last-child:nth-child(odd){grid-column:1 / -1}
.steam-ui-kit-actions .DialogButton.steam-ui-kit-wide{grid-column:1 / -1}
.steam-ui-kit-nested{margin-left:2px;padding-left:12px;border-left:2px solid rgba(255,255,255,.12);box-sizing:border-box}
.steam-ui-kit-nested .DialogToggle_Label{font-size:14px}
.steam-ui-kit-nested .DialogToggle_Description{font-size:12px}
.steam-ui-kit-highlight{color:#fca904}
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
.steam-ui-kit-box p{margin:0;line-height:1.45}
.steam-ui-kit-box,.steam-ui-kit-group,.steam-ui-kit-group-body,.steam-ui-kit-pane,.steam-ui-kit-detail-aside{min-width:0;max-width:100%;box-sizing:border-box}
.steam-ui-kit-box>*{min-width:0;max-width:100%;box-sizing:border-box}
.steam-ui-kit-box .DialogButton,.steam-ui-kit-sheet .DialogButton{min-width:0!important;max-width:100%;box-sizing:border-box!important;white-space:normal;overflow-wrap:anywhere}
.steam-ui-kit-choice{display:flex;flex-direction:column;gap:6px;min-width:0;max-width:100%;width:100%;box-sizing:border-box}
.steam-ui-kit-choice-label{font-size:14px;color:#dcdedf}
.steam-ui-kit-choice .DialogDropDown{min-width:0!important;max-width:100%!important;width:100%!important;box-sizing:border-box!important;margin:0}
.steam-ui-kit-choice div{min-width:0!important;max-width:100%!important;box-sizing:border-box!important}
.steam-ui-kit-choice .DialogButton{width:100%!important;min-width:0!important;max-width:100%!important;box-sizing:border-box!important}
.steam-ui-kit-choice .DialogDropDown_CurrentDisplay{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.steam-ui-kit-choice>div{width:100%!important;min-width:0;max-width:100%;box-sizing:border-box;--field-negative-horizontal-margin:0px}
.steam-ui-kit-select-row{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:4px 12px;width:100%;min-width:0;text-align:left;box-sizing:border-box}
.steam-ui-kit-select-row-title{min-width:0;overflow-wrap:anywhere}
.steam-ui-kit-select-row-detail{grid-column:1 / -1;min-width:0;font-size:13px;color:#8b929a;white-space:normal;overflow-wrap:anywhere}
.steam-ui-kit-picker{display:flex;flex-direction:column;gap:12px;min-width:0;width:min(900px,100%);max-width:100%;box-sizing:border-box}
.steam-ui-kit-picker-body{display:grid;grid-template-columns:minmax(140px,1fr) minmax(0,2fr);gap:16px;min-height:0;height:min(360px,48vh)}
.steam-ui-kit-picker-places,.steam-ui-kit-picker-files{min-width:0;min-height:0;overflow-y:auto;overflow-x:hidden;display:flex;flex-direction:column;gap:6px;background:rgba(255,255,255,.045);border:1px solid rgba(255,255,255,.12);border-radius:4px;padding:8px;box-sizing:border-box}
.steam-ui-kit-picker-heading{padding:4px 8px 8px;border-bottom:1px solid #3d4450;font-size:12px;font-weight:600;color:#8b929a;text-transform:uppercase;letter-spacing:.06em}
.steam-ui-kit-picker .steam-ui-kit-select-row{padding:4px 0;border-bottom:1px solid rgba(255,255,255,.14)}
.steam-ui-kit-picker .DialogButton{min-width:0!important;max-width:100%;width:100%;box-sizing:border-box!important;white-space:normal;margin:0}
.steam-ui-kit-picker-path{font-size:14px;color:#b8bcbf;overflow-wrap:anywhere}
.steam-ui-kit-picker-actions{display:flex;gap:8px;justify-content:flex-end;flex-wrap:wrap}
.steam-ui-kit-picker-actions .DialogButton{width:auto}
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
.steam-ui-kit-hero.video{aspect-ratio:16 / 9}
.steam-ui-kit-hero video{width:100%;height:100%;object-fit:contain;display:block}
.steam-ui-kit-hero.video img{object-fit:contain}
.steam-ui-kit-modal-body{display:flex;flex-direction:column;gap:12px}
.steam-ui-kit-modal-body p{margin:0}
.steam-ui-kit-modal-actions{display:flex;justify-content:flex-end;gap:8px;margin-top:8px}
`;

// One stylesheet element and one icon renderer per React, so a root re-rendering on every host
// publication hands React the same style element and the same caret elements each time rather
// than fresh ones to diff.
const steamUiKitStyles = new WeakMap();
const steamUiKitIcons = new WeakMap();
/**
 * Returns the cached kit stylesheet element; render it once in each consuming document root.
 * @param react Steam's React instance used by the consuming root.
 * @returns The style element shared by callers using this React instance.
 */
const steamUiKitStyle = (react) => {
    let element = steamUiKitStyles.get(react);
    if (!element) {
        element = react.createElement("style", { key: "steam-ui-kit" }, SteamUiKitStyles);
        steamUiKitStyles.set(react, element);
    }
    return element;
};
const steamUiKitIcon = (react) => {
    let icon = steamUiKitIcons.get(react);
    if (!icon) {
        icon = createIconRenderer(react);
        steamUiKitIcons.set(react, icon);
    }
    return icon;
};

/**
 * Fits a native dropdown to a box, toolbar or table cell without a second settings-row label.
 * @param ui Resolved native controls; a full field with below layout is the fallback.
 * @param props Accessible label, optional visible label and native option/selection/change props.
 * @returns A contained dropdown; its popup remains owned by Steam.
 */
const renderSteamUiChoice = (ui, props: {
    label: string; showLabel?: boolean; rgOptions: any[]; selectedOption: any;
    disabled?: boolean; onChange: (option: any) => void;
}) => {
    const h = ui.react.createElement;
    const bare = !!ui.dropdownControl;
    return h("div", {className: "steam-ui-kit-choice", "aria-label": props.label},
        bare && props.showLabel !== false ? h("div", {className: "steam-ui-kit-choice-label"}, props.label) : null,
        h(ui.dropdownControl ?? ui.dropdown, {
            label: bare || props.showLabel !== false ? props.label : "",
            menuLabel: props.label,
            layout: "below", rgOptions: props.rgOptions, selectedOption: props.selectedOption,
            disabled: !!props.disabled, onChange: props.onChange, "aria-label": props.label
        }));
};

/**
 * Arranges a selectable item as a title, trailing status and separate supporting line.
 * @param ui Resolved native Steam button.
 * @param props Stable key, label/detail/status, selected state and one activation callback.
 * @returns One native focus target with structured content instead of concatenated button text.
 */
const renderSteamUiSelectRow = (ui, props: {
    key: string; title: string; detail?: string; status?: any; selected?: boolean;
    onClick: () => void;
}) => {
    const h = ui.react.createElement;
    return h(ui.dialogButton, {key:props.key, "aria-pressed":!!props.selected, onClick:props.onClick},
        h("div", {className:"steam-ui-kit-select-row"},
            h("span", {className:"steam-ui-kit-select-row-title"}, props.title),
            props.status ?? null,
            props.detail ? h("span", {className:"steam-ui-kit-select-row-detail"}, props.detail) : null));
};

/**
 * Creates a section heading with optional controller-accessible folding.
 * @param ui Steam's resolved React and native control components.
 * @param props Title, optional icon/detail, fold state and toggle callback; sub selects nested styling.
 * @returns A heading element; without onToggle it is not a focusable fold control.
 */
const renderSteamUiHeader = (
    ui,
    props: {
        title: string;
        icon?: any;
        detail?: string;
        collapsed?: boolean;
        onToggle?: () => void;
        sub?: boolean;
    },
) => {
    const h = ui.react.createElement;
    const folds = typeof props.onToggle === "function";
    const collapsed = folds && !!props.collapsed;
    const icon = steamUiKitIcon(ui.react);
    const children = [
        props.icon ? h("div", { className: "steam-ui-kit-header-icon" }, props.icon) : null,
        h(
            "div",
            { className: "steam-ui-kit-header-text" },
            h("div", { className: "steam-ui-kit-header-title" }, props.title),
            props.detail
                ? h("div", { className: "steam-ui-kit-header-detail" }, props.detail)
                : null,
        ),
        folds
            ? h(
                  "div",
                  { className: "steam-ui-kit-header-caret" },
                  collapsed ? icon("sectionClosed", 18) : icon("sectionOpen", 18),
              )
            : null,
    ].filter((child) => child !== null);
    const className = `steam-ui-kit-header${collapsed ? "" : " open"}${folds ? "" : " plain"}${props.sub ? " sub" : ""}`;
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

/**
 * Creates a section whose hidden or folded descendants remain mounted but cannot receive focus.
 * @param ui Steam's resolved React and native control components.
 * @param props Section identity, optional title/icon/detail and fold/visibility controls.
 * @param children Rows retained while the section is folded.
 * @returns The grouped React element; the caller owns fold state.
 */
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
    // Steam's Focusable where the client has one, so navigation can be switched off for what is not
    // drawn; a plain element otherwise, where there is no gamepad navigation to switch off.
    const box = (unreachable: boolean, boxProps: any, ...kids) =>
        ui.focusable
            ? h(ui.focusable, { ...boxProps, childFocusDisabled: unreachable }, ...kids)
            : h("div", boxProps, ...kids);
    return box(
        !!props.hidden,
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
        box(collapsed, { className: "steam-ui-kit-group-body" }, ...children),
    );
};

/**
 * Creates a controller-navigable grid of native action buttons.
 * @param ui Steam's resolved React and native control components.
 * @param actions Stable ids, visible labels and activation callbacks in display order.
 * @returns A two-column grid; long labels span both columns.
 */
const renderSteamUiActions = (
    ui,
    actions: {
        id: string;
        label: string;
        onClick: () => void;
        primary?: boolean;
        disabled?: boolean;
    }[],
) => {
    const h = ui.react.createElement;
    return h(
        ui.focusable,
        { "flow-children": "row", className: "steam-ui-kit-actions" },
        ...actions.map((action) =>
            h(
                action.primary ? (ui.dialogButtonPrimary ?? ui.dialogButton) : ui.dialogButton,
                {
                    key: action.id,
                    className: action.label.length > 18 ? "steam-ui-kit-wide" : undefined,
                    onClick: action.onClick,
                    disabled: !!action.disabled,
                },
                action.label,
            ),
        ),
    );
};

// A scrollable Steam modal body. The host supplies its fields and action handlers; the kit owns
// the common spacing, error line and action row instead of each page carrying another sheet.
const renderSteamUiSheet = (
    ui,
    props: {
        className?: string;
        note?: string;
        error?: string;
        actions?: {
            id: string;
            label: string;
            onClick: () => void;
            primary?: boolean;
            disabled?: boolean;
        }[];
    },
    ...children
) => {
    const h = ui.react.createElement;
    return h(
        ui.focusable ?? "div",
        {
            className: ["steam-ui-kit-sheet", props.className].filter(Boolean).join(" "),
            "flow-children": "column",
        },
        steamUiKitStyle(ui.react),
        props.note ? h("p", { className: "steam-ui-kit-sheet-note" }, props.note) : null,
        ...children,
        props.error ? h("p", { className: "steam-ui-kit-sheet-error" }, props.error) : null,
        props.actions?.length
            ? h(
                  "div",
                  { className: "steam-ui-kit-sheet-actions" },
                  renderSteamUiActions(ui, props.actions),
              )
            : null,
    );
};

// A controlled list of text values: one Steam text field and remove action per row, plus add and
// optional reset. The caller owns the draft and validation, including any application limits.
const renderSteamUiStringList = (
    ui,
    props: {
        values: readonly string[];
        onChange: (values: string[]) => void;
        label?: (index: number) => string;
        addLabel?: string;
        removeLabel?: (index: number) => string;
        resetLabel?: string;
    },
) => {
    const h = ui.react.createElement;
    return h(
        ui.focusable ?? "div",
        { "flow-children": "column" },
        ...props.values.map((value, index) =>
            h(
                ui.focusable ?? "div",
                { key: index, "flow-children": "column" },
                h(ui.textField, {
                    label: props.label?.(index) ?? `Value ${index + 1}`,
                    value,
                    onChange: (event) =>
                        props.onChange(
                            props.values.map((item, position) =>
                                position === index ? (event?.target?.value ?? "") : item,
                            ),
                        ),
                }),
                h(
                    ui.dialogButton,
                    {
                        onClick: () =>
                            props.onChange(
                                props.values.filter((_item, position) => position !== index),
                            ),
                    },
                    props.removeLabel?.(index) ?? `Remove value ${index + 1}`,
                ),
            ),
        ),
        h(
            ui.dialogButton,
            { onClick: () => props.onChange([...props.values, ""]) },
            props.addLabel ?? "Add value",
        ),
        props.resetLabel
            ? h(ui.dialogButton, { onClick: () => props.onChange([]) }, props.resetLabel)
            : null,
    );
};

/**
 * Creates the next-page control for a bounded list of rendered items.
 * @param ui Steam's resolved React and native control components.
 * @param props Optional label/disabled state and the callback requesting more items.
 * @returns A centered native button; it does not fetch or append items itself.
 */
const renderSteamUiMore = (
    ui,
    props: { label?: string; onClick: () => void; disabled?: boolean },
) => {
    const h = ui.react.createElement;
    return h(
        "div",
        { className: "steam-ui-kit-more" },
        h(
            ui.dialogButton,
            { onClick: props.onClick, disabled: !!props.disabled },
            props.label ?? "Load More",
        ),
    );
};

/**
 * Creates a page column that acquires controller focus when mounted.
 * @param ui Steam's resolved React and native control components.
 * @param props Optional React key and additional class name.
 * @param children Toolbar, list or other page content in navigation order.
 * @returns The focusable pane, or a plain container when Focusable is unavailable.
 */
const renderSteamUiPane = (ui, props: { key?: string; className?: string }, ...children) => {
    const h = ui.react.createElement;
    const className = ["steam-ui-kit-pane", props.className].filter(Boolean).join(" ");
    return ui.focusable
        ? h(
              ui.focusable,
              { key: props.key, className, autoFocus: true, "flow-children": "column" },
              ...children,
          )
        : h("div", { key: props.key, className }, ...children);
};

/**
 * Creates a nested page level that handles Back before Steam leaves the route.
 * @param ui Steam's resolved React and native control components.
 * @param props Optional class and callback returning to the previous level.
 * @param children Content of the nested level.
 * @returns A focusable level with initial focus and the supplied Back action.
 */
const renderSteamUiLevel = (ui, props: { className?: string; onBack?: () => void }, ...children) =>
    ui.react.createElement(
        ui.focusable,
        {
            className: props.className,
            autoFocus: true,
            onCancelButton: props.onBack,
            onCancelActionDescription: "Back",
        },
        ...children,
    );

/**
 * Creates a noninteractive color preview.
 * @param react Steam's React instance used by the consuming root.
 * @param color CSS color used as the swatch background.
 * @returns The swatch element.
 */
const renderSteamUiSwatch = (react, color: string) =>
    react.createElement("div", { className: "steam-ui-kit-swatch", style: { background: color } });

/**
 * Creates one activatable gallery card using Steam controller focus.
 * @param ui Steam's resolved React and native control components.
 * @param props Optional image/stats/badge, title, metadata and activation callback.
 * @returns A single focus target containing the card content.
 */
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
    },
) => {
    const h = ui.react.createElement;
    return h(
        ui.focusable,
        {
            key: props.key,
            className: "steam-ui-kit-card",
            onActivate: props.onActivate,
            onOKActionDescription: "Open",
        },
        h(
            "div",
            { className: "steam-ui-kit-card-shot" },
            props.image ? h("img", { src: props.image, alt: "", loading: "lazy" }) : null,
            props.stats?.length
                ? h(
                      "div",
                      { className: "steam-ui-kit-card-stats" },
                      ...props.stats.map((stat, index) =>
                          h("span", { key: index }, stat.glyph ?? null, stat.text),
                      ),
                  )
                : null,
            props.badge
                ? h(
                      "div",
                      { className: `steam-ui-kit-badge${props.badge.warn ? " warn" : ""}` },
                      props.badge.text,
                  )
                : null,
        ),
        h("div", { className: "steam-ui-kit-card-title" }, props.title),
        ...(props.meta ?? []).map((line, index) =>
            h("div", { key: index, className: "steam-ui-kit-card-meta" }, line),
        ),
    );
};

/**
 * Arranges rendered cards in a controller-navigable grid.
 * @param ui Steam's resolved React and native control components.
 * @param cards Already-created card elements in display order.
 * @returns The grid element; callers bound the number of mounted cards.
 */
const renderSteamUiGrid = (ui, cards: any[]) =>
    ui.react.createElement(
        ui.focusable,
        { className: "steam-ui-kit-grid", "flow-children": "grid" },
        ...cards,
    );

/**
 * Creates an empty-list explanation or error message.
 * @param react Steam's React instance used by the consuming root.
 * @param text Visible reason or empty-state text.
 * @param error Whether to use error styling.
 * @returns The message element.
 */
const renderSteamUiEmpty = (react, text: string, error = false) =>
    react.createElement("div", { className: `steam-ui-kit-empty${error ? " error" : ""}` }, text);

/**
 * Creates a dismissible notice or error.
 * @param ui Steam's resolved React and native control components.
 * @param props Message, error flag and callback that clears the owning state.
 * @returns The banner; dismissal is delegated to the caller.
 */
const renderSteamUiBanner = (
    ui,
    props: { text: string; error?: boolean; onDismiss: () => void },
) => {
    const h = ui.react.createElement;
    return h(
        "div",
        { className: `steam-ui-kit-banner${props.error ? " error" : ""}` },
        h("span", null, props.text),
        h(ui.smallButton ?? ui.dialogButton, { onClick: props.onDismiss }, "Dismiss"),
    );
};

/**
 * Groups existing controls in a horizontal controller-navigation row.
 * @param ui Steam's resolved React and native control components.
 * @param tools Rendered toolbar items, normally created with renderSteamUiTool.
 * @returns The focusable toolbar element.
 */
const renderSteamUiToolbar = (ui, ...tools) =>
    ui.react.createElement(
        ui.focusable,
        { className: "steam-ui-kit-toolbar", "flow-children": "row" },
        ...tools,
    );
/**
 * Labels a control within a toolbar.
 * @param ui Steam's resolved React and native control components.
 * @param label Visible label, or null to omit it.
 * @param control Rendered native control.
 * @param grow Whether this item occupies remaining toolbar space.
 * @returns The labelled toolbar item.
 */
const renderSteamUiTool = (ui, label: string | null, control, grow = false) => {
    const h = ui.react.createElement;
    return h(
        "div",
        { className: `steam-ui-kit-tool${grow ? " grow" : ""}` },
        label ? h("span", { className: "DialogLabel" }, label) : null,
        control,
    );
};

/**
 * Creates a wrapping row of native buttons.
 * @param ui Steam's resolved React and native control components.
 * @param chips Labels, click handlers and optional controller action descriptions.
 * @returns The controller-navigable chip row.
 */
const renderSteamUiChips = (
    ui,
    chips: { label: string; onClick: () => void; description?: string }[],
) => {
    const h = ui.react.createElement;
    return h(
        ui.focusable,
        { "flow-children": "row", className: "steam-ui-kit-chips" },
        ...chips.map((chip, index) =>
            h(
                ui.dialogButton,
                {
                    key: `${index}:${chip.label}`,
                    onClick: chip.onClick,
                    onOKActionDescription: chip.description,
                },
                chip.label,
            ),
        ),
    );
};

/**
 * Groups detail content under an optional heading.
 * @param react Steam's React instance used by the consuming root.
 * @param title Heading content; a false-like value omits the heading.
 * @param children Content rendered below the heading.
 * @returns The detail box element.
 */
const renderSteamUiBox = (react, title, ...children) =>
    react.createElement(
        "div",
        { className: "steam-ui-kit-box" },
        title ? react.createElement("div", { className: "steam-ui-kit-box-title" }, title) : null,
        ...children,
    );

/**
 * Creates a selected image with controller-selectable thumbnails.
 * @param ui Steam's resolved React and native control components.
 * @param props Image URLs, selected index, selection callback and optional empty text.
 * @returns The gallery; the displayed index is clamped without changing caller state.
 */
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
                              key: `${at}:${url}`,
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
            images.length > 1
                ? h(
                      "div",
                      { className: "steam-ui-kit-hero-count" },
                      `${index + 1}/${images.length}`,
                  )
                : null,
        ),
    );
};

/**
 * Creates a muted looping preview, a poster fallback or empty-state text.
 * @param react Steam's React instance used by the consuming root.
 * @param props Optional media URL, poster URL and empty-state message.
 * @returns The preview element; unmounting removes its video element.
 */
const renderSteamUiVideo = (
    react,
    props: { src?: string | null; poster?: string | null; empty?: string },
) =>
    react.createElement(
        "div",
        { className: "steam-ui-kit-hero video" },
        props.src
            ? react.createElement("video", {
                  src: props.src,
                  poster: props.poster ?? undefined,
                  autoPlay: true,
                  loop: true,
                  muted: true,
                  playsInline: true,
              })
            : props.poster
              ? react.createElement("img", { src: props.poster, alt: "" })
              : react.createElement(
                    "div",
                    { className: "steam-ui-kit-hero-empty" },
                    props.empty ?? "No preview",
                ),
    );

/**
 * Shared SVG path data for store-page metadata glyphs.
 */
const SteamUiGlyphs = Object.freeze({
    download: "M11 3h2v9.2l3.6-3.6 1.4 1.4-6 6-6-6 1.4-1.4L11 12.2zM4 19h16v2H4z",
    star: "M12 2.5l2.9 6 6.6.9-4.8 4.6 1.2 6.5L12 17.4 6.1 20.5l1.2-6.5L2.5 9.4l6.6-.9z",
    heart: "M12 21s-7-4.6-9.3-9.1C1 8.5 3.2 5 6.7 5c2 0 3.4 1 4.3 2.3C12 6 13.4 5 15.3 5c3.5 0 5.7 3.5 4 6.9C19 16.4 12 21 12 21z",
    target: "M12 3a9 9 0 1 1 0 18 9 9 0 0 1 0-18zm0 2a7 7 0 1 0 0 14 7 7 0 0 0 0-14zm0 3a4 4 0 1 1 0 8 4 4 0 0 1 0-8zm0 2a2 2 0 1 0 0 4 2 2 0 0 0 0-4z",
});
/**
 * Creates an SVG from the kit glyph vocabulary.
 * @param react Steam's React instance used by the consuming root.
 * @param name Key from SteamUiGlyphs.
 * @returns The rendered glyph.
 */
const renderSteamUiGlyph = (react, name: keyof typeof SteamUiGlyphs) =>
    renderSteamGlyph(react, SteamUiGlyphs[name]);

/**
 * Component names a tabbed page must resolve before installation.
 */
const SteamUiTabbedPageRequired = Object.freeze([
    "react",
    "focusable",
    "toggleField",
    "dropdown",
    "sliderField",
    "textField",
    "dialogButton",
    "dialogButtonPrimary",
    "smallButton",
    "valueField",
    "settingsSection",
    "tabs",
    "modalRoot",
    "showModal",
]);

/**
 * Creates a native tabbed host page and mounts only its active content.
 * @param ui Steam's resolved React and native control components.
 * @param props Page identity/label, optional CSS/banner, tabs, active id and tab callbacks.
 * @returns The page with kit styles; an unknown active id selects the first tab.
 */
const renderSteamUiTabbedPage = (
    ui,
    props: {
        id: string;
        label: string;
        style?: string;
        tabs: { id: string; title: string }[];
        active: string;
        onTab: (tab: string) => void;
        content: (tab: string) => any;
        banner?: { text: string; error?: boolean; onDismiss: () => void } | null;
    },
) => {
    const h = ui.react.createElement;
    const active = props.tabs.some((tab) => tab.id === props.active)
        ? props.active
        : (props.tabs[0]?.id ?? "");
    return h(
        "div",
        { id: props.id, className: "steam-ui-kit-page", "aria-label": props.label },
        steamUiKitStyle(ui.react),
        props.style ? h("style", null, props.style) : null,
        props.banner?.text
            ? h(
                  "div",
                  { className: "steam-ui-kit-page-banner" },
                  renderSteamUiBanner(ui, props.banner),
              )
            : null,
        h(ui.tabs, {
            autoFocusContents: true,
            activeTab: active,
            onShowTab: props.onTab,
            tabs: props.tabs.map((tab) => ({
                id: tab.id,
                title: tab.title,
                content: tab.id === active ? props.content(tab.id) : null,
            })),
        }),
    );
};

/**
 * Creates an item detail view with its own controller Back action.
 * @param ui Steam's resolved React and native control components.
 * @param props Heading/badge/media, primary and aside content, and the Back callback.
 * @returns The focused detail layout.
 */
const renderSteamUiDetail = (
    ui,
    props: {
        title: string;
        badge?: string;
        media?: any;
        main: any[];
        aside: any[];
        onBack: () => void;
    },
) => {
    const h = ui.react.createElement;
    return h(
        ui.focusable,
        {
            className: "steam-ui-kit-detail",
            autoFocus: true,
            onCancelButton: props.onBack,
            onCancelActionDescription: "Back",
        },
        h(
            "div",
            { className: "steam-ui-kit-detail-main" },
            props.media ?? null,
            h(
                "div",
                { className: "steam-ui-kit-detail-heading" },
                h("h2", null, props.title),
                props.badge ? h("span", null, props.badge) : null,
            ),
            ...props.main,
        ),
        h(
            "div",
            { className: "steam-ui-kit-detail-aside" },
            ...props.aside,
            h(ui.dialogButton, { onClick: props.onBack }, "Back"),
        ),
    );
};

/**
 * Opens a native confirmation; Cancel and Back invoke no action.
 * @param ui Steam's resolved React and native control components.
 * @param props Title, explanation, confirm label and callback invoked on confirmation.
 * @returns Whether the modal could be shown; a thrown confirmation callback is not swallowed.
 */
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
/**
 * Opens a native text prompt that submits only a trimmed nonempty value.
 * @param ui Steam's resolved React and native control components.
 * @param props Title, optional explanation/initial value, input label and confirmation callback.
 * @returns Whether the modal could be shown; cancellation submits nothing.
 */
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
