// The flows a host page is made of, built from the kit: a collection to browse, an item's detail
// with the action that acquires it, the list a user manages, and a section added to one of Steam's
// own Settings pages. Each takes the page's resolved `ui` and the host's data and callbacks; the
// host keeps its labels, its rules and its backend.

/**
 * The one browse vocabulary a page and its host share: the tab, the query, the next page, an
 * item's detail and its closing, and the dismissal of a notice. `SteamUiModuleBuilder.BrowseCommands`
 * declares the same names on the host.
 */
const SteamUiBrowse = Object.freeze({
    setTab: "setTab",
    browse: "browse",
    more: "more",
    open: "open",
    closeDetail: "closeDetail",
    dismiss: "dismiss",
});

// Where each collection was left: the item that had focus and how far its scroller was scrolled, by
// the collection's id. A collection unmounts when the user opens an item, and finds both again when
// the user comes back.
const steamUiCollectionMemory = new Map<string, { key: string; scroll: number }>();
// Bounded: a page has a handful of collections, and an id is never more than a page and a tab.
const SteamUiCollectionMemoryLimit = 64;

// The nearest ancestor that scrolls, bounded so a detached node cannot walk far.
const steamUiScroller = (node: any) => {
    for (let at = node?.parentElement, depth = 0; at && depth < 32; at = at.parentElement, depth++) {
        try {
            const overflow = at.ownerDocument?.defaultView?.getComputedStyle?.(at)?.overflowY ?? "";
            if (at.scrollHeight > at.clientHeight && /auto|scroll/u.test(overflow)) return at;
        } catch {
            return null;
        }
    }
    return null;
};

function SteamUiCollection(props: any) {
    const ui = props.ui;
    const react = ui.react;
    const h = react.createElement;
    const search = props.search ?? null;
    const [text, setText] = react.useState(search?.value ?? "");
    const [remembered] = react.useState(() => steamUiCollectionMemory.get(props.id) ?? null);
    const node = react.useRef(null);
    const focused = react.useRef(remembered?.key ?? "");
    // A published query replaces what is typed only when it changed; the draft survives every other
    // publication.
    react.useEffect(() => setText(search?.value ?? ""), [search?.value]);
    // The first look is the collection's own: nothing is fetched until it is first shown.
    react.useEffect(() => {
        props.onFirstShow?.();
    }, []);
    react.useEffect(() => {
        const scroller = steamUiScroller(node.current);
        if (scroller && remembered?.scroll) scroller.scrollTop = remembered.scroll;
        return () => {
            const leaving = steamUiScroller(node.current);
            if (steamUiCollectionMemory.size >= SteamUiCollectionMemoryLimit && !steamUiCollectionMemory.has(props.id)) {
                steamUiCollectionMemory.delete(steamUiCollectionMemory.keys().next().value as string);
            }
            steamUiCollectionMemory.set(props.id, { key: focused.current, scroll: leaving?.scrollTop ?? 0 });
        };
    }, []);

    const submit = () => {
        if (search && text !== (search.value ?? "")) search.onSubmit(text);
    };
    const items: any[] = props.items ?? [];
    const drawn = items.map((item, index) => {
        const key = String(props.itemKey(item));
        const element = props.render(item, index);
        if (!react.isValidElement(element)) return element;
        const onFocus = element.props?.onFocus;
        return react.cloneElement(element, {
            key,
            // The item focus comes back to; the remembered one takes focus when the collection returns.
            onFocus: (event) => {
                focused.current = key;
                onFocus?.(event);
            },
            ...(remembered?.key === key ? { autoFocus: true } : {}),
        });
    });
    const tools = [
        ...(props.choices ?? []).map((choice) =>
            renderSteamUiTool(
                ui,
                choice.label,
                renderSteamDropdown(ui, {
                    label: choice.label,
                    rgOptions: choice.options,
                    selectedOption: choice.selected,
                    disabled: choice.disabled,
                    onChange: (option) => {
                        if (option && option.data !== choice.selected) choice.onChange(option.data);
                    },
                }),
            ),
        ),
        search
            ? renderSteamUiTool(
                  ui,
                  null,
                  h(ui.textField, {
                      label: search.label ?? "Search",
                      value: text,
                      onChange: (event) => setText(event?.target?.value ?? ""),
                      onBlur: submit,
                      onKeyDown: (event) => {
                          if (event?.key === "Enter") submit();
                      },
                  }),
                  true,
              )
            : null,
        ...(props.actions ?? []).map((action) =>
            h(
                action.primary ? (ui.dialogButtonPrimary ?? ui.dialogButton) : ui.dialogButton,
                {
                    key: action.id,
                    disabled: !!action.disabled,
                    onClick: () => {
                        if (!action.disabled) runSteamUiAction(ui, action);
                    },
                },
                action.label,
            ),
        ),
    ].filter(Boolean);

    const loading = !!props.loading;
    const error = props.error ? String(props.error) : "";
    let empty: any = null;
    if (!items.length && !loading && !error) {
        empty =
            props.filtered && props.noMatch
                ? renderSteamUiEmpty(ui, { kind: "noMatch", ...props.noMatch })
                : renderSteamUiEmpty(ui, { kind: "empty", ...props.empty });
    }
    const more = props.more;
    return h(
        "div",
        { ref: node, className: "steam-ui-kit-collection" },
        props.header ?? null,
        tools.length ? renderSteamUiToolbar(ui, ...tools) : null,
        renderSteamUiStatus(ui, props.status ?? []),
        error
            ? renderSteamUiEmpty(ui, {
                  kind: "error",
                  text: error,
                  action: props.onRetry ? { label: "Try again", onClick: props.onRetry } : null,
              })
            : null,
        drawn.length
            ? props.layout === "list"
                ? h(ui.focusable, { className: "steam-ui-kit-list", "flow-children": "column" }, ...drawn)
                : renderSteamUiGrid(ui, drawn)
            : null,
        loading ? renderSteamUiEmpty(ui, { kind: "loading", text: props.loadingText ?? "Loading…" }) : null,
        empty,
        more && !loading
            ? renderSteamUiMore(ui, {
                  label: more.label ?? (more.total ? `Load More (${items.length} of ${more.total})` : "Load More"),
                  disabled: !!more.disabled,
                  onClick: more.onMore,
              })
            : null,
    );
}

/**
 * Creates a collection to browse: a toolbar of choices, a search that submits when the field is
 * left (or on Enter) and actions, a status stack, the items as a grid of cards or a list of rows,
 * and what it says while loading, when it failed, when it has nothing and when nothing matched,
 * with Load More while the host has more.
 *
 * The collection remembers, by `id`, which item had focus and how far it was scrolled when it
 * unmounted (an item opened, a tab changed) and returns to both. A search being typed survives every
 * publication that does not change the published query. `onFirstShow` runs once when the
 * collection first appears, which is where a page fetches what it shows.
 * @param ui The page's resolved components.
 * @param props The collection: its retention id, items, their keys and how each is drawn, the
 * layout, toolbar, status, states and paging.
 * @returns The collection element.
 */
const renderSteamUiCollection = (ui, props: SteamUiCollectionProps) =>
    ui.react.createElement(SteamUiCollection, { ...props, key: props.key ?? props.id, ui });

/**
 * The label of the action that acquires an item: Install, Update while an older version is held,
 * Reinstall while the current one is.
 * @param installed Whether the user holds the item.
 * @param current Whether what they hold is the latest version.
 * @returns The label.
 */
const steamUiAcquireLabel = (installed: boolean, current: boolean) =>
    !installed ? "Install" : current ? "Reinstall" : "Update";

/**
 * Creates the box that acquires an item: what it does, a line of facts, the primary button and a
 * note on where the item goes. While busy the button says so and does nothing.
 * @param ui Steam's resolved React and buttons.
 * @param props The box title, facts, button label, busy and disabled state, the action and a note.
 * @returns The box element.
 */
const renderSteamUiAcquire = (
    ui,
    props: {
        title: string;
        meta?: string | null;
        label: string;
        busy?: boolean;
        busyLabel?: string;
        disabled?: boolean;
        onAcquire: () => void;
        note?: string | null;
    },
) => {
    const h = ui.react.createElement;
    return renderSteamUiBox(
        ui.react,
        props.title,
        props.meta ? h("div", { className: "steam-ui-kit-muted" }, props.meta) : null,
        h(
            ui.dialogButtonPrimary ?? ui.dialogButton,
            {
                disabled: !!props.busy || !!props.disabled,
                onClick: () => {
                    if (!props.busy && !props.disabled) props.onAcquire();
                },
            },
            props.busy ? (props.busyLabel ?? "Working…") : props.label,
        ),
        props.note ? h("div", { className: "steam-ui-kit-muted" }, props.note) : null,
    );
};

/**
 * Creates an item's detail: its media, heading, byline and description (or that it is loading, or
 * why it failed), the host's sections, the acquire box, further boxes and a removal that asks
 * first, with Back on B and at the foot of the aside.
 * @param ui The page's resolved components.
 * @param props The item's parts, its acquire box, removal and Back.
 * @returns The detail element.
 */
const renderSteamUiItem = (
    ui,
    props: {
        title: string;
        badge?: string | null;
        media?: any;
        byline?: string | null;
        description?: string | null;
        loading?: boolean;
        error?: string | null;
        noDescription?: string;
        sections?: ({ title: string; content: any } | null | false)[];
        acquire?: Parameters<typeof renderSteamUiAcquire>[1] | null;
        aside?: any[];
        remove?: {
            label: string;
            title: string;
            text: string;
            confirmLabel?: string;
            disabled?: boolean;
            onRemove: () => void;
        } | null;
        onBack: () => void;
        showBack?: boolean;
    },
) => {
    const h = ui.react.createElement;
    const description = props.loading
        ? "Loading…"
        : props.error
          ? props.error
          : props.description || (props.noDescription ?? "No description provided.");
    const remove = props.remove;
    return renderSteamUiDetail(ui, {
        title: props.title,
        badge: props.badge ?? undefined,
        media: props.media ?? null,
        main: [
            props.byline ? h("div", { className: "steam-ui-kit-item-byline" }, props.byline) : null,
            props.description !== undefined || props.loading || props.error
                ? h(ui.react.Fragment, null,
                      h("h3", null, "Description"),
                      h(
                          "p",
                          {
                              className: props.error ? "steam-ui-kit-manage-error" : props.description ? "" : "steam-ui-kit-muted",
                              role: props.error ? "alert" : undefined,
                          },
                          description,
                      ),
                  )
                : null,
            ...(props.sections ?? [])
                .filter((section): section is { title: string; content: any } => !!section)
                .map((section) =>
                    h(ui.react.Fragment, { key: section.title }, h("h3", null, section.title), section.content),
                ),
        ],
        aside: [
            props.acquire ? renderSteamUiAcquire(ui, props.acquire) : null,
            ...(props.aside ?? []),
            remove
                ? h(
                      ui.dialogButton,
                      {
                          disabled: !!remove.disabled,
                          onClick: () =>
                              showSteamUiConfirm(ui, {
                                  title: remove.title,
                                  text: remove.text,
                                  confirmLabel: remove.confirmLabel ?? "Remove",
                                  onConfirm: remove.onRemove,
                              }),
                      },
                      remove.label,
                  )
                : null,
        ],
        onBack: props.onBack,
        showBack: props.showBack,
    });
};

/**
 * Creates the list a user manages: actions to add (a file-pick action opens Steam's picker), then
 * the items as rows with a title, a supporting line and a status, the selected one marked and an
 * unusable one saying why on its row, or the empty state when there are none.
 * @param ui The page's resolved components.
 * @param props The section title, items, their keys, labels, detail, status, error and selection,
 * what opening one does, the add actions and the empty state.
 * @returns The list element.
 */
const renderSteamUiManage = (
    ui,
    props: {
        key?: string;
        title?: string | null;
        items: readonly any[];
        itemKey: (item: any) => string;
        label: (item: any) => string;
        detail?: (item: any) => string | null | undefined;
        status?: (item: any) => any;
        error?: (item: any) => string | null | undefined;
        selected?: string | null;
        onOpen: (item: any) => void;
        add?: SteamUiAction[];
        empty: { text: string; title?: string; action?: { label: string; onClick: () => void } | null };
    },
) => {
    const h = ui.react.createElement;
    const items = props.items ?? [];
    return h(
        ui.react.Fragment,
        { key: props.key },
        props.add?.length ? renderSteamUiActions(ui, props.add, { compact: true }) : null,
        items.length
            ? h(
                  ui.settingsSection ?? "div",
                  ui.settingsSection ? { label: props.title ?? undefined } : null,
                  ...items.map((item) => {
                      const key = String(props.itemKey(item));
                      const error = props.error?.(item) ?? null;
                      return renderSteamUiSelectRow(ui, {
                          key,
                          title: props.label(item),
                          detail: error ?? props.detail?.(item) ?? undefined,
                          error: !!error,
                          status: props.status?.(item) ?? null,
                          selected: props.selected === key,
                          onClick: () => props.onOpen(item),
                      });
                  }),
              )
            : renderSteamUiEmpty(ui, { kind: "empty", ...props.empty }),
    );
};

// The sections hosts add to Steam's own Settings pages, all through one transform on the shared
// useMemo claim: the Settings page list is wrapped once, whichever gates add to it, so two gates
// never wrap each other's wrapper and the claim is held while any section is attached.
const SteamSettingsSectionsMemo = "steam-ui.settings-sections";
const steamSettingsSectionHost = {
    react: null as any,
    attached: [] as SteamSettingsSection[],
    changed: createLocalStore(),
    lists: new WeakMap<object, unknown[]>(),
    pages: new Map<any, any>(),
    wrapped: new WeakMap<any, any>(),
    sections: new Map<string, any>(),
};

const steamSettingsChildren = (element) => {
    const children = element.props?.children;
    return Array.isArray(children) ? children : children === undefined ? [] : [children];
};

// Whether a child is the Valve section a section is placed by: its component, or the original under
// a wrapper of this transform's, carries every token.
const steamSettingsSectionMatches = (react, child, tokens: readonly string[]) => {
    if (!react.isValidElement(child) || typeof child.type !== "function") return false;
    const original = steamSettingsSectionHost.wrapped.get(child.type) ?? child.type;
    const source = String(original);
    return tokens.every((token) => source.includes(token));
};

// A Valve section with sections appended inside it, one wrapper per original and section key.
const steamSettingsInto = (react, original, section: SteamSettingsSection) => {
    const cacheKey = section.definition.key;
    let byOriginal = steamSettingsSectionHost.sections.get(cacheKey);
    if (!byOriginal) {
        byOriginal = new Map();
        steamSettingsSectionHost.sections.set(cacheKey, byOriginal);
    }
    let wrapped = byOriginal.get(original);
    if (wrapped) return wrapped;
    wrapped = function SteamUiSettingsSectionHost(props) {
        const tree = original(props);
        if (!steamSettingsSectionHost.attached.includes(section) || !react.isValidElement(tree)) return tree;
        return react.cloneElement(
            tree,
            undefined,
            ...steamSettingsChildren(tree),
            react.createElement(section.definition.render, { key: section.definition.key }),
        );
    };
    steamSettingsSectionHost.wrapped.set(wrapped, original);
    byOriginal.set(original, wrapped);
    return wrapped;
};

// A Settings page's content with its sections: in front of the Valve section each names, inside it,
// or last. The wrapper redraws when a section attaches or detaches.
const steamSettingsPageFor = (react, original, route: string) => {
    let wrapped = steamSettingsSectionHost.pages.get(original);
    if (wrapped) return wrapped;
    wrapped = function SteamUiSettingsPage(props) {
        react.useSyncExternalStore(
            steamSettingsSectionHost.changed.subscribe,
            steamSettingsSectionHost.changed.revision,
            steamSettingsSectionHost.changed.revision,
        );
        const tree = original(props);
        const sections = steamSettingsSectionHost.attached.filter((section) => section.route === route);
        if (!sections.length || !react.isValidElement(tree)) return tree;
        let children = steamSettingsChildren(tree);
        for (const section of sections) {
            const definition = section.definition;
            if (definition.into) {
                const at = children
                    .map((child, index) => (steamSettingsSectionMatches(react, child, definition.into ?? []) ? index : -1))
                    .filter((index) => index >= 0);
                if (at.length !== 1) {
                    section.outcome = at.length
                        ? "the section it goes into was not unique on the page"
                        : "the section it goes into was not found on the page";
                    continue;
                }
                const target = children[at[0]];
                children = children.slice();
                children[at[0]] = react.createElement(steamSettingsInto(react, target.type, section), keyed(target));
                section.outcome = "rendered inside its section";
                continue;
            }
            if (children.some((child) => react.isValidElement(child) && child.key === definition.key)) continue;
            const slot = react.createElement(definition.render, { key: definition.key });
            const at = definition.before
                ? children.findIndex((child) => steamSettingsSectionMatches(react, child, definition.before ?? []))
                : -1;
            if (definition.before && at < 0) {
                section.outcome = "the section it goes in front of was not found; it is last";
            } else {
                section.outcome = "rendered";
            }
            children = at < 0 ? [...children, slot] : [...children.slice(0, at), slot, ...children.slice(at)];
        }
        return react.cloneElement(tree, undefined, ...children);
    };
    steamSettingsSectionHost.pages.set(original, wrapped);
    steamSettingsSectionHost.wrapped.set(wrapped, original);
    return wrapped;
};

// The Settings page list, with each page that has sections wrapped. The same input list maps to
// the same output list until a section attaches or detaches, so memo consumers see a stable identity.
const transformSteamSettingsPages = (value) => {
    const host = steamSettingsSectionHost;
    const react = host.react;
    if (!react || !host.attached.length || !Array.isArray(value) || !value.length) return value;
    const first = value[0];
    if (!first || typeof first !== "object" || !("route" in first) || !("content" in first)) return value;
    const cached = host.lists.get(value);
    if (cached) return cached;
    const routes = new Set(host.attached.map((section) => section.route));
    const seen = new Map<string, number>();
    for (const item of value) {
        if (item && typeof item === "object" && routes.has(item.route)) {
            seen.set(item.route, (seen.get(item.route) ?? 0) + 1);
        }
    }
    let next: unknown[] | null = null;
    value.forEach((item, index) => {
        if (!item || typeof item !== "object" || !routes.has(item.route) || seen.get(item.route) !== 1) return;
        const content = item.content;
        if (!react.isValidElement(content) || typeof content.type !== "function") return;
        if (host.wrapped.has(content.type)) return;
        next ??= value.slice();
        next[index] = {
            ...item,
            content: react.createElement(steamSettingsPageFor(react, content.type, item.route), keyed(content)),
        };
    });
    const result = next ?? value;
    host.lists.set(value, result);
    return result;
};

/**
 * Finds a Steam Settings page's route through Steam's route table, by the page's name there.
 * @param runtime The shared module resolver.
 * @param page The page's name in `Settings`, such as "Customization".
 * @returns The route, or why there is none on this client.
 */
const findSteamSettingsRoute = (runtime, page: string): { route: string } | { incompatible: string } => {
    let route: unknown = "";
    try {
        const pages = runtime.exported(
            [...RouteTableTokens],
            (value) => typeof value?.Settings?.[page] === "function",
        ) as any;
        route = pages.Settings[page]();
    } catch (error) {
        return { incompatible: `the route table was not a unique match: ${String(error)}` };
    }
    if (typeof route !== "string" || !route.startsWith("/")) {
        return { incompatible: `the ${page} settings route is unavailable` };
    }
    return { route };
};

/**
 * Declares a section a gate adds to one of Steam's own Settings pages: drawn in front of the Valve
 * section `before` names, appended inside the Valve section `into` names, or last on the page.
 *
 * Every section shares one transform on the shared useMemo claim. A gate finds the page's route in
 * its pure `find` with `find(runtime)` and attaches the section in its bind with
 * `attach(ledger, react, route)`, which puts the detach on the gate's ledger; the claim is taken by
 * the first section attached and released with the last. `render` is a component, so it can use
 * hooks; it is drawn with its key and no props.
 * @param definition The page by its name in Steam's route table, the section's unique key, where it
 * goes by the tokens its Valve neighbour's source carries, and its component.
 * @returns The section: its pure `find`, `attach`, whether it is `attached()` and its `status()`.
 */
const registerSteamSettingsSection = (definition: {
    page: string;
    key: string;
    before?: readonly string[];
    into?: readonly string[];
    render: (props: any) => any;
}) => {
    const section: SteamSettingsSection = { definition, route: "", outcome: "not attached" };
    const host = steamSettingsSectionHost;
    const attached = () => host.attached.includes(section);
    const detach = () => {
        if (!attached()) return { ok: true };
        if (host.attached.length === 1) {
            const released = releaseMemo(host.react, SteamSettingsSectionsMemo);
            if (!released.ok) return released;
            host.react = null;
            host.pages.clear();
            host.sections.clear();
        }
        host.attached = host.attached.filter((entry) => entry !== section);
        host.lists = new WeakMap();
        section.outcome = "detached";
        host.changed.changed();
        return { ok: true };
    };
    return {
        /** The page's route, or why this client has none; pure. */
        find: (runtime) => findSteamSettingsRoute(runtime, definition.page),
        /**
         * Adds the section to its page and its detach to the ledger.
         * @returns A refusal, or nothing once attached.
         */
        attach: (ledger: GateLedger, react, route: string): string | void => {
            if (attached()) return;
            if (host.react && host.react !== react) return "another React holds the Settings sections";
            if (!host.attached.length) {
                const intercepted = interceptMemo(react, SteamSettingsSectionsMemo, transformSteamSettingsPages);
                if (!intercepted.ok) return intercepted.error ?? "React useMemo could not be intercepted";
                host.react = react;
            }
            section.route = route;
            section.outcome = "attached, not rendered yet";
            host.attached = [...host.attached, section];
            host.lists = new WeakMap();
            host.changed.changed();
            ledger.add(`settings section ${definition.key}`, detach, () => attached() && memoIntercepted(react, SteamSettingsSectionsMemo));
        },
        attached,
        status: () => ({ page: definition.page, route: section.route, attached: attached(), lastOutcome: section.outcome }),
    };
};

/** One attached Settings section. */
type SteamSettingsSection = {
    definition: Parameters<typeof registerSteamSettingsSection>[0];
    route: string;
    outcome: string;
};

/** What a collection is made of; see `renderSteamUiCollection`. */
type SteamUiCollectionProps = {
    /** Where focus and scroll are remembered: unique per page and tab. */
    id: string;
    key?: string;
    items: readonly any[];
    itemKey: (item: any) => string;
    /** A card for a grid, a row for a list; the collection keys it and tracks its focus. */
    render: (item: any, index: number) => any;
    layout?: "grid" | "list";
    /** The search: the published query and what submitting a changed one does. */
    search?: { value: string; label?: string; onSubmit: (text: string) => void } | null;
    /** Dropdowns in the toolbar, before the search. */
    choices?: {
        label: string;
        options: { data: any; label: any }[];
        selected: any;
        disabled?: boolean;
        onChange: (data: any) => void;
    }[];
    /** Buttons after the search; a `pick` action opens Steam's file picker. */
    actions?: SteamUiAction[];
    /** Content above the toolbar, such as the settings row a collection is chosen with. */
    header?: any;
    status?: SteamUiStatusLine[];
    loading?: boolean;
    loadingText?: string;
    error?: string | null;
    onRetry?: (() => void) | null;
    empty: { text: string; title?: string; action?: { label: string; onClick: () => void } | null };
    /** What it says when a search or filter left nothing; `filtered` says one is in effect. */
    noMatch?: { text: string; title?: string; action?: { label: string; onClick: () => void } | null };
    filtered?: boolean;
    /** Load More while the host has more; the label counts what is shown against `total`. */
    more?: { onMore: () => void; total?: number; label?: string; disabled?: boolean } | null;
    onFirstShow?: (() => void) | null;
};
