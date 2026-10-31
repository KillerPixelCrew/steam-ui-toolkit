/**
 * Projects the host screensaver setting into the matched Steam settings surface.
 * @returns The defineGate gate; remove must release it before its bridge is replaced.
 */

function createScreensaverSettings() {
    const patchId = "steam-ui.screensaver";

    const SectionTokens = ['"#Settings_Customization_Screensaver"', "ForceScreensaver"] as const;
    const PluggedInSetting = "system_idle_screensaver_ac_sec";
    const BatterySetting = "system_idle_screensaver_battery_sec";

    // Steam's settings can arrive after the gate installs. The first report is retried on this
    // bounded schedule rather than waiting for someone to open the page.
    const ReportAttempts = 60;
    const ReportIntervalMilliseconds = 2000;

    type Row = {
        id: string;
        label: string;
        description: string;
        seconds: number;
        options: { data: number; label: string }[];
        available: boolean;
    };

    let react: any = null;
    let dropdown: any = null;
    let settings: any = null;
    let useObserver: any = null;
    let route = "";
    // Why the last report or timeout change failed; the gate's own lastError is its lifecycle's.
    let commandError = "";
    let lastOutcome = "never rendered";
    let lastReport = "";
    let reportTimer: ReturnType<typeof setTimeout> | null = null;
    const installed = () => gate.installed();
    const content = createScreensaverContent(() => settings?.clientSettings);
    const inputActivity = createScreensaverActivity(
        () => settings?.clientSettings,
        () => (window as any).SystemPowerStore?.batteryState?.bHasBattery === true,
        (active) => content.active(active),
    );

    // The host's rows, replaced whole on each publication.
    let rows: Row[] = [];
    const pending = new Set<string>();
    const local = createLocalStore();

    const text = (value) => (typeof value === "string" ? value : "");
    const seconds = (value) => (Number.isInteger(value) && value >= 0 ? value : null);

    // Validated rather than trusted: a malformed option list renders a dropdown whose entries select
    // nothing. A state that fails is dropped whole and the outcome says so.
    const normalize = (value): Row[] | null => {
        if (!value || typeof value !== "object" || !Array.isArray(value.rows)) return null;
        const ids = new Set<string>();
        const next: Row[] = [];
        for (const row of value.rows) {
            if (!row || typeof row !== "object") return null;
            const id = text(row.id);
            const current = seconds(row.seconds);
            if (
                !/^[a-z][a-z0-9-]*$/u.test(id) ||
                ids.has(id) ||
                current === null ||
                !Array.isArray(row.options)
            )
                return null;
            const options: { data: number; label: string }[] = [];
            for (const option of row.options) {
                const optionSeconds = seconds(option?.seconds);
                const label = text(option?.label);
                if (optionSeconds === null || !label) return null;
                options.push({ data: optionSeconds, label });
            }
            ids.add(id);
            next.push({
                id,
                label: text(row.label),
                description: text(row.description),
                seconds: current,
                options,
                available: row.available === true,
            });
        }
        return next;
    };

    // Steam's own screensaver timeouts from its client settings, and whether Steam believes the
    // machine has a battery (the test its Power page is shown on). Null until the settings arrive.
    const readScreensaver = () => {
        const values = settings?.clientSettings;
        const pluggedIn = seconds(values?.[PluggedInSetting]);
        if (pluggedIn === null) return null;
        return {
            acSeconds: pluggedIn,
            batterySeconds: seconds(values?.[BatterySetting]),
            battery: (window as any).SystemPowerStore?.batteryState?.bHasBattery === true,
        };
    };

    // Once per change, and again whenever the page opens, because that is when the host's reading of
    // its own timeouts is worth refreshing.
    const report = (reading, force) => {
        if (!installed() || !reading) return false;
        const signature = JSON.stringify(reading);
        if (!force && signature === lastReport) return true;
        lastReport = signature;
        request(patchId, "report", reading).catch((error) => {
            commandError = "screensaver report failed: " + String(error);
        });
        return true;
    };

    const reportWhenReady = (attempt) => {
        reportTimer = null;
        if (!installed() || report(readScreensaver(), false) || attempt >= ReportAttempts) return;
        reportTimer = setTimeout(() => reportWhenReady(attempt + 1), ReportIntervalMilliseconds);
    };

    const select = (row: Row, value) => {
        const chosen = seconds(value);
        if (!installed() || chosen === null || chosen === row.seconds || pending.has(row.id)) return;
        pending.add(row.id);
        local.changed();
        request(patchId, "setTimeout", { row: row.id, seconds: chosen })
            .catch((error) => {
                commandError = "timeout change failed: " + String(error);
            })
            .finally(() => {
                pending.delete(row.id);
                local.changed();
            });
    };

    function SteamUiScreensaverTimeouts() {
        react.useSyncExternalStore(local.subscribe, local.revision);
        // The custom sources are drawn here too, so a change to their catalog redraws the rows.
        react.useSyncExternalStore(content.catalog.subscribe, content.catalog.revision);
        const reading = useObserver
            ? useObserver(readScreensaver, "SteamUiScreensaverTimeouts")
            : readScreensaver();
        const signature = reading ? JSON.stringify(reading) : "";
        react.useEffect(() => {
            report(readScreensaver(), true);
        }, []);
        react.useEffect(() => {
            report(readScreensaver(), false);
        }, [signature]);
        if (!installed()) return null;
        const custom = content.render();
        if (!rows.length && !custom) {
            lastOutcome = "no rows published";
            return null;
        }
        lastOutcome = `rendered ${rows.length} row(s)`;
        return react.createElement(
            react.Fragment,
            null,
            ...rows.map((row) =>
                react.createElement(dropdown, {
                    key: `steam-ui-timeout-${row.id}`,
                    label: row.label,
                    description: row.description || undefined,
                    rgOptions: row.options,
                    selectedOption: row.seconds,
                    disabled: !row.available || pending.has(row.id),
                    controlled: true,
                    onChange: (option) => select(row, option?.data),
                }),
            ),
            ...(custom ? [custom] : []),
        );
    }

    // The rows go inside Valve's Screensaver section on the Customization page, through the
    // Settings sections every gate shares.
    const section = registerSteamSettingsSection({
        page: "Customization",
        key: "steam-ui-screensaver-timeouts",
        into: SectionTokens,
        render: SteamUiScreensaverTimeouts,
    });

    // Pure: React with the hooks the rows use, Valve's dropdown field, the customization route from
    // Steam's route table, the one Screensaver section module and the client settings store. A
    // client without one of them is incompatible; a settings store not constructed yet is not ready.
    // The observer hook is reported but not required, and the shared useMemo claim the Settings
    // sections hold is never examined here, so a claim this or another gate holds stays compatible.
    const find = (runtime) => {
        const fields = resolveSteamFieldComponents(runtime);
        const resolvedReact = fields?.react ?? null;
        const tracking = findUseObserver(runtime);
        const facts: Record<string, unknown> = {
            react: resolvedReact ? 1 : 0,
            fields: fields?.dropdown ? 1 : 0,
            section: runtime.findUnique([...SectionTokens]) ? 1 : 0,
            route: "",
            settings: false,
            observer: tracking ? 1 : 0,
        };
        if (!resolvedReact) return gateIncompatible("React unavailable", facts);
        if (
            typeof resolvedReact.useSyncExternalStore !== "function" ||
            typeof resolvedReact.useEffect !== "function"
        ) {
            return gateIncompatible("React runtime lacks useSyncExternalStore or useEffect", facts);
        }
        if (!fields?.dropdown) return gateIncompatible("the dropdown field was not a unique match", facts);
        const page = section.find(runtime);
        if ("incompatible" in page) return gateIncompatible(page.incompatible, facts);
        const customization = page.route;
        facts.route = customization;
        if (!facts.section) {
            return gateIncompatible("the screensaver section module was not a unique match", facts);
        }
        if (!runtime.findUnique([...SettingsTokens])) {
            return gateIncompatible("the client settings module was not a unique match", facts);
        }
        let store: any = null;
        try {
            store = runtime.exported(
                [...SettingsTokens],
                (value) =>
                    !!value && typeof value === "object" && typeof value.clientSettings === "object",
            );
        } catch (error) {
            return gateNotReady(`the client settings store is not constructed yet: ${String(error)}`, facts);
        }
        facts.settings = true;
        return gateReady(
            {
                runtime,
                react: resolvedReact,
                dropdown: fields.dropdown,
                route: customization,
                settings: store,
                // Wanted, not required: without it the rows still follow the host, and a change to
                // Steam's timeout reaches the host on the section's next render.
                useObserver: tracking,
            },
            facts,
        );
    };

    const gate = defineGate({
        name: "screensaver-settings",
        find,
        bind: (handles, ledger) => {
            react = handles.react;
            dropdown = handles.dropdown;
            route = handles.route;
            settings = handles.settings;
            useObserver = handles.useObserver;
            content.start(handles.runtime);
            ledger.add("screensaver content", () => content.stop());
            // The activity contract is reported in the status, never required.
            inputActivity.start(handles.runtime);
            ledger.add("screensaver input activity", () => inputActivity.stop());
            const attached = section.attach(ledger, react, route);
            if (attached) return attached;
            ledger.subscribe(patchId, (published) => {
                const next = normalize(published);
                if (!next) {
                    lastOutcome = "state received but rejected by validation";
                    return;
                }
                rows = next;
                inputActivity.observe(published?.activity);
                content.update(published?.content);
                local.changed();
            });
            ledger.cleanup("report retry", () => {
                if (reportTimer) {
                    clearTimeout(reportTimer);
                    reportTimer = null;
                }
            });
            reportWhenReady(0);
        },
        // An open page re-renders without the rows; its wrappers pass Steam's own tree through.
        unbind: () => {
            rows = [];
            pending.clear();
            lastReport = "";
            commandError = "";
            local.changed();
        },
        status: () => ({
            claimed: section.attached(),
            section: section.status(),
            route,
            settings: !!settings,
            tracking: !!useObserver,
            inputActivity: inputActivity.status(),
            content: content.status(),
            rows: rows.length,
            lastOutcome,
            lastReport,
            commandError,
        }),
    });
    return gate;
}

registerGate("screensaver", createScreensaverSettings());
