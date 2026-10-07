/**
 * Owns host Quick Access controls and their shared Steam render claims.
 * @returns Per-kind install/remove/status operations and a dispose operation that releases subscriptions and claims.
 */
function createNativeComponentHost() {
    let unsubscribePlugins: any = null;
    const registrations = new Map();
    const listeners = new Set<() => void>();
    let runtime;
    let controlRuntime;
    let autoTdpControl;
    let frameLimitControl;
    let controllerControl;
    let powerProfileControl;
    let hybridCoreControl;
    let cpuBoostControl;
    let powerPresetControl;
    let resolutionControl;
    let audioFormatControl;
    let settingsSectionsControl;
    let vrrControl;
    let deviceControlsControl;

    // Valve's profile header and its per-game profile toggle. On the current client they are TWO
    // exports of the perf-components module — re-probed 2026-09-02 after the header rendered with
    // no way to enable a profile: the toggle's token resolves uniquely on its own, so each mounts
    // as its own row under the one valveProfileHeader kind. And Valve's reset button. All are
    // additive: the host built none of them.
    let valveProfileHeaderControl;
    let valveProfileToggleControl;
    let valveResetControl;
    let valveRefreshRateControl;
    let valveOverlayLevelControl;

    let powerLimitControl;
    let performanceRoot;

    // The Quick Settings panel Steam rendered, captured at match time. S14 puts resolution and
    // refresh rate in Quick Settings, not Performance — but the panel is a LOCAL function of the
    // tabs module, not an export, so it is only ever known once the tab array passes through the
    // patched memo. Null means it has not been seen yet, which the status reports.
    let quickSettingsRoot = null;
    const quickSettingsWrapCache = new Map();
    // This host's name on the shared useMemo claim (ownership.ts).
    const MemoName = "quickAccessTabs";
    let disposedHost = false;
    let lastPatchError = "";

    // What the last append attempt actually did, surfaced through status(). Without it a panel that
    // inserted nothing was indistinguishable from a bridge that never ran.
    type AppendDiagnostics = {
        controls: number;
        inserted: boolean;
        ownSection: boolean;
        tree?: string;
        nativeFiltered?: boolean;
        nativeRowsHidden?: number;
    } | null;
    // One entry per wrapped tab, because "the perf panel appended fine" and "Quick Settings never
    // rendered" are different facts that a single field could only report as one.
    const appendDiagnostics: { perf: AppendDiagnostics; quickSettings: AppendDiagnostics } = {
        perf: null,
        quickSettings: null,
    };

    // Why each control did or did not draw. A control that renders null leaves no trace anywhere:
    // the row is built and appended, the panel simply has one fewer child, and every other signal
    // still reports success. This is the difference between "the host did not add it" and "the host added
    // it and the device had nothing to show".
    const renderOutcomes: Record<string, string> = {};

    // What a folded section says on its heading's detail line. A row with a value worth a glance
    // leaves it here as it renders, and the section joins its rows'. Rows stay mounted while their
    // section is folded, so the line stays current.
    const summaries: Record<string, string> = {};
    const summarize = (kind, text) => {
        summaries[kind] = typeof text === "string" ? text : "";
    };

    // Which of the host's own rows drew something on their last render. A section header exists for
    // the rows under it, so a section whose rows all returned null is only a title: with no device
    // coordinator, Power limits and Controller were exactly that. Valve's rows report nothing here
    // and count as drawn.
    const drawnKinds = new Set<string>();
    let layoutQueued = false;
    const setDrawn = (kind, drawn) => {
        if (drawnKinds.has(kind) === drawn) return;
        if (drawn) drawnKinds.add(kind);
        else drawnKinds.delete(kind);
        // Recorded while a row renders, so the panel roots hear about it afterwards instead of being
        // updated from inside another component's render.
        if (layoutQueued) return;
        layoutQueued = true;
        queueMicrotask(() => {
            layoutQueued = false;
            notify();
        });
    };
    const drew = (kind, outcome = "rendered") => {
        setDrawn(kind, true);
        renderOutcomes[kind] = outcome;
    };
    const note = (kind, reason) => {
        setDrawn(kind, false);
        delete summaries[kind];
        // "no state" is what every render sees while a delivery is being rejected, and the wrapper
        // re-renders on each host notification, so the generic reason must not overwrite the precise
        // one the subscription recorded.
        if (
            reason === "no state" &&
            renderOutcomes[kind] === "state received but rejected by validation"
        ) {
            return null;
        }

        renderOutcomes[kind] = reason;
        return null;
    };

    const definitions = Object.freeze({
        autoTdp: Object.freeze({
            patchId: "steam-ui.auto-tdp",
            command: "setAutoTdp",
        }),
        // Two commands, because this is SteamOS's unified row: one slider that is the frame cap while
        // a cap is set and the refresh rate once it is switched off.
        frameLimit: Object.freeze({
            patchId: "steam-ui.frame-limit",
            command: "setFrameLimit",
            refreshCommand: "setRefreshRate",
        }),
        controllerTarget: Object.freeze({
            patchId: "steam-ui.controller-target",
            command: "setControllerTarget",
        }),
        powerProfile: Object.freeze({
            patchId: "steam-ui.power-profile",
            command: "setPowerProfile",
        }),
        hybridCores: Object.freeze({
            patchId: "steam-ui.hybrid-cores",
            command: "setHybridCores",
        }),
        cpuBoost: Object.freeze({
            patchId: "steam-ui.cpu-boost",
            command: "setCpuBoost",
        }),
        powerPreset: Object.freeze({
            patchId: "steam-ui.power-preset",
            acCommand: "setAcPowerPreset",
            batteryCommand: "setBatteryPowerPreset",
        }),
        // Hand-built for the same reason resolution is: Valve ships a component, and its gate is a
        // namespace this client does not have. See createVrrControl.
        vrr: Object.freeze({
            patchId: "steam-ui.variable-refresh",
            command: "setVariableRefreshRate",
        }),
        // Hand-built, unlike the frame limit and VRR rows. SteamOS drives resolution through
        // gamescope and this client ships no component for it, so there is nothing to mount.
        resolution: Object.freeze({
            patchId: "steam-ui.resolution",
            command: "setResolution",
        }),
        settingsSections: Object.freeze({ patchId: "steam-ui.settings-sections", command: "set" }),
        audioFormat: Object.freeze({
            patchId: "steam-ui.audio-format",
            formatCommand: "setFormat",
            spatialCommand: "setSpatial",
        }),
        deviceControls: Object.freeze({
            patchId: "steam-ui.device-controls",
            chargeCommand: "setChargeLimit",
            brightnessCommand: "setLightingBrightness",
            colorCommand: "setLightingColor",
        }),
        // Which of the panel's own sections are folded, kept by the host so Steam rebuilding a tab
        // does not open them again. Not a row: the state is read by the panel roots. A host without
        // the module still gets folding sections; they last the session.
        panelFolds: Object.freeze({
            patchId: SteamFoldsPatchId,
            command: "setFolded",
        }),
        // The host's Quick Access layout: sections, headings, glyphs, folds and the accent label. Not a
        // row either, and it takes no command. Without it each tab draws its rows in one untitled group.
        quickAccessLayout: Object.freeze({
            patchId: "steam-ui.quick-access-layout",
            command: "",
        }),

        // Valve's own components. They carry no command because they never call the host directly: they
        // read SystemPerfStore and write through SteamClient.System.Perf.UpdateSettings, which is the
        // perf patch's vocabulary, not theirs. They still need an entry here — install() refuses any
        // kind that is not a declared definition.
        valveProfileHeader: Object.freeze({
            patchId: "steam-ui.valve-profile-header",
            command: "",
        }),
        valveReset: Object.freeze({
            patchId: "steam-ui.valve-reset",
            command: "",
        }),
        // Valve's own refresh-rate row, mounted into Quick Settings per S14. It reads
        // limits.display_refresh_manual_hz_* from SystemPerfStore, which the projection supplies only
        // under FrameLimitOnly — the strategy gate is the state, not a check here.
        valveRefreshRate: Object.freeze({
            patchId: "steam-ui.valve-refresh-rate",
            command: "",
        }),
        // Valve's performance-overlay selector replaces the retired hand-rolled imitation.
        valveOverlayLevel: Object.freeze({
            patchId: "steam-ui.valve-overlay-level",
            command: "",
        }),
        powerLimit: Object.freeze({
            patchId: "steam-ui.power-limit",
            primaryCommand: "setPrimaryLimit",
            boostCommand: "setBoostLimit",
            modeCommand: "setUnifiedMode",
        }),
    });

    const notify = () => {
        for (const listener of [...listeners]) {
            try {
                listener();
            } catch {}
        }
    };
    const subscribeHost = (listener) => {
        listeners.add(listener);
        return () => listeners.delete(listener);
    };
    // Every row command carries a fresh action generation, so its echo can be matched to the write.
    const sendCommand = (definition, command, payload) =>
        request(definition.patchId, command, payload, nextActionGeneration(definition.patchId));
    // A controlled switch's change: a boolean that differs from what the device reports is sent, and
    // its refusal goes to `refuse` for the row's description; the next change clears it ("").
    const toggleCommand = (definition, state, refuse: (text: string) => void) => (enabled) => {
        if (typeof enabled !== "boolean" || enabled === state.enabled) return;
        refuse("");
        void sendCommand(definition, definition.command, { enabled }).catch((reason) =>
            refuse(refusalText(reason)),
        );
    };
    // The one function export carrying every token. Through the shared matcher, so an export Steam
    // aliases under two names counts once and a getter that throws counts as no match.
    // The sections' folds, the mechanism every Quick Access tab shares (gate-helpers.ts).
    const panelFolds = createSteamFolds();
    const normalizePanelFoldsState = (value) => panelFolds.normalize(value);
    const isFolded = (open, id) => panelFolds.isFolded(open, id);
    const setFolded = (id, folded) => panelFolds.setFolded(id, folded, notify);
    const uniqueFunction = (exports, requiredTokens) =>
        uniqueSteamExport(
            exports,
            (value) =>
                typeof value === "function" &&
                requiredTokens.every((token) => String(value).includes(token)),
        );
    const createControlRuntime = () => {
        const controls = resolveSteamFieldComponents(runtime);
        const panel = resolveSteamPanelComponents(runtime);
        if (!controls || !panel) return null;

        const react = controls.react;
        const slider = controls.sliderField;
        const dropdown = controls.dropdown;
        // Steam's own ToggleField, from the same module as the slider and dropdown above. Selected by
        // the two markers of its class body rather than by its export name, which is minified and
        // changes with every client build. Live-verified 2026-08-29: exactly one export matches, and
        // the provider that names the module's fields lists that same class as ToggleField.
        const toggle = controls.toggleField;
        // Valve's read-only label/value row, from the Field module rather than the fields module: it
        // is what a figure the panel only reports — the profile actually in effect — is supposed to
        // look like. Without it that line was a bare div with none of Steam's type, spacing or
        // separator, which is exactly how it read. `#Field_MoreInfo_Action` occurs once in the whole
        // client bundle, so the module is unambiguous, and only this export draws LabelFieldValue.
        const labelFieldFactory = runtime.findUnique([
            "#Field_MoreInfo_Action",
            "spacingBetweenLabelAndChild",
        ]);
        const labelField = labelFieldFactory
            ? uniqueFunction(runtime(labelFieldFactory[0]), [
                  "LabelFieldValue",
                  "spacingBetweenLabelAndChild",
              ])
            : null;
        const { section, row } = panel;
        // Valve's localize-with-fallback; every row's label needs it.
        const localize = resolveSteamLocalizer(runtime);
        if (!slider || !dropdown || !localize) return null;
        // The toggle and the label field are deliberately not in that guard. They arrived after the
        // other four, so a client where either cannot be found still gets every control that does not
        // need one, rather than losing the whole native surface.
        // The icon renderer is built once per control runtime and closes over Steam's React, so a row
        // asks for a glyph by name and never touches element construction itself.
        const icon = createIconRenderer(react);
        // Steam's Focusable, for the kit's folding section headings. Not in the guard either: without
        // it a heading is a plain div and the sections simply do not fold, so a client where it is
        // not a unique match keeps every row.
        let focusable = null;
        try {
            focusable = resolveNativeFocusable(runtime);
        } catch {
            focusable = null;
        }
        return {
            react,
            slider,
            dropdown,
            toggle,
            labelField,
            section,
            row,
            localize,
            icon,
            focusable,
        };
    };
    const normalizeText = (value) => (typeof value === "string" ? value : "");
    // A row the host marks says so in its own description, in Steam's accent colour, led by the label the
    // host's layout publishes (steam-ui.quick-access-layout). The toolkit holds no word of its own for it.
    const accentDescription = (controlRuntime, accent, text) => {
        const label = accent
            ? normalizeText(acceptedStates.get("quickAccessLayout")?.accentLabel)
            : "";
        return steamAccentDescription(
            controlRuntime.react,
            [label, text].filter(Boolean).join(" · "),
            accent === true,
        );
    };
    // Deliberately small. Everything the row needs is a switch position and a reason, because the
    // device capability behind it answers in exactly those terms.
    const normalizeVrrState = (value) => {
        if (!value || typeof value !== "object" || typeof value.available !== "boolean")
            return null;
        if (typeof value.enabled !== "boolean") return null;
        return Object.freeze({
            available: value.available,
            enabled: value.enabled,
            progress: normalizeText(value.progress),
            statusText: normalizeText(value.statusText),
            accent: value.accent === true,
        });
    };
    const normalizeAutoTdpState = (value) => {
        if (!value || typeof value !== "object" || typeof value.available !== "boolean")
            return null;
        if (typeof value.enabled !== "boolean" || typeof value.controlling !== "boolean")
            return null;
        // The watts figure is only ever a display detail beside the switch, so a value that is not a
        // positive whole number is dropped rather than rejecting the whole state and taking the switch
        // away with it.
        const watts =
            typeof value.watts === "number" && Number.isInteger(value.watts) && value.watts >= 1
                ? value.watts
                : null;
        return Object.freeze({
            available: value.available,
            enabled: value.enabled,
            controlling: value.controlling,
            watts,
            progress: normalizeText(value.progress),
            statusText: normalizeText(value.statusText),
        });
    };
    const normalizeControllerState = (value) => {
        if (!value || typeof value !== "object" || typeof value.available !== "boolean")
            return null;
        if (!Array.isArray(value.targets)) return null;
        const targets: Readonly<{ id: string; label: string; available: boolean }>[] = [];
        const ids = new Set();
        for (const item of value.targets) {
            if (!item || typeof item !== "object") return null;
            const id = normalizeText(item.id);
            const label = normalizeText(item.label);
            // Uppercase is allowed because the ids the host actually sends are PascalCase —
            // SteamDeckComposite, Xbox360, DualShock4. A lowercase-only pattern rejected every one of
            // them, so the whole state normalised to null and the controller row never drew, with
            // nothing anywhere saying a state had been received and thrown away.
            if (!/^[A-Za-z0-9._-]+$/.test(id) || !label || ids.has(id)) return null;
            ids.add(id);
            targets.push(Object.freeze({ id, label, available: item.available !== false }));
        }
        const selectedTarget = normalizeText(value.selectedTarget);
        const observedTarget = normalizeText(value.observedTarget);
        if (
            (selectedTarget && !ids.has(selectedTarget)) ||
            (observedTarget && !ids.has(observedTarget))
        )
            return null;
        return Object.freeze({
            available: value.available,
            targets: Object.freeze(targets),
            selectedTarget,
            observedTarget,
            progress: normalizeText(value.progress),
            statusText: normalizeText(value.statusText),
            accent: value.accent === true,
        });
    };
    const validEnum = (value, allowed) =>
        typeof value === "string" && allowed.includes(value) ? value : null;
    const normalizePerformanceCommon = (value) => {
        if (!value || typeof value !== "object" || typeof value.available !== "boolean")
            return null;
        // Only what a row actually reads. This validator once also demanded readbackQuality,
        // policyLayer and adapterAvailability — enums no component consumed and, after the review
        // simplification deleted their only publisher, no state carried: every frame-limit
        // delivery was rejected and the row silently vanished from the QAM (device-observed
        // 2026-09-02, the first dogfooding find).
        const progress = validEnum(value.progress, [
            "idle",
            "queued",
            "applying",
            // A write the host accepted and stored but has not made yet, because what it applies to
            // is not addressable right now — a host reports it when Steam has named a running game
            // whose executable Windows has not exposed, so the cap is saved against the game rather
            // than sprayed onto the global profile. It was missing from this list, and a settled
            // outcome the host can legitimately report was therefore rejected as malformed: adjusting
            // the frame-limit slider while a game was starting deleted the row the user had just
            // touched (2026-09-04). Not busy — the value is stored, and the row stays live.
            "deferred",
            "applied",
            "rejected",
            "failed",
            "external-change",
        ]);
        if (!progress) return null;
        return Object.freeze({
            available: value.available,
            progress,
            fault: normalizeText(value.fault),
            statusText: normalizeText(value.statusText),
            accent: value.accent === true,
        });
    };
    // Validated rather than trusted, like every other semantic state: this arrives over the bridge
    // and a malformed option list would render a dropdown whose entries select nothing.
    const normalizeResolutionState = (value) => {
        if (!value || typeof value !== "object") return null;
        const options = Array.isArray(value.options)
            ? value.options.filter(
                  (option) =>
                      typeof option === "string" && /^[1-9][0-9]*x[1-9][0-9]*$/.test(option),
              )
            : [];
        return {
            available: value.available === true,
            options,
            current: typeof value.current === "string" ? value.current : "",
            statusText: typeof value.statusText === "string" ? value.statusText : "",
        };
    };

    const normalizeAudioFormatState = (value) => {
        if (!value || typeof value !== "object") return null;
        const options = (items) => {
            const values: Readonly<{ id: string; label: string }>[] = [];
            if (!Array.isArray(items)) return values;
            for (const item of items) {
                if (!item || typeof item !== "object") continue;
                const id = normalizeText(item.id);
                const label = normalizeText(item.label);
                if (id && label) values.push(Object.freeze({ id, label }));
            }

            return values;
        };
        const channelOptions = options(value.channelOptions);
        const currentChannels = normalizeText(value.currentChannels);
        const formatOptions = options(value.formatOptions);
        const spatialOptions = options(value.spatialOptions);
        const distinct = (items) => new Set(items.map((item) => item.id)).size === items.length;
        if (!distinct(channelOptions) || !distinct(formatOptions) || !distinct(spatialOptions))
            return null;
        const currentFormat = normalizeText(value.currentFormat);
        const currentSpatial = normalizeText(value.currentSpatial);
        if (
            (currentChannels && !channelOptions.some((item) => item.id === currentChannels)) ||
            (currentFormat && !formatOptions.some((item) => item.id === currentFormat)) ||
            (currentSpatial && !spatialOptions.some((item) => item.id === currentSpatial))
        )
            return null;
        return Object.freeze({
            available: value.available === true,
            channelOptions: Object.freeze(channelOptions),
            currentChannels,
            formatOptions: Object.freeze(formatOptions),
            currentFormat,
            spatialOptions: Object.freeze(spatialOptions),
            currentSpatial,
            statusText: normalizeText(value.statusText),
        });
    };

    // A host's reading for a range row, clamped into the range, or null when it is not a number.
    const clampReading = (value, minimum, maximum) =>
        typeof value === "number" && Number.isFinite(value)
            ? Math.min(maximum, Math.max(minimum, value))
            : null;
    const normalizeDeviceRange = (value) => {
        if (value === null || value === undefined) return null;
        if (!value || typeof value !== "object" || typeof value.available !== "boolean")
            return null;
        const minimum = Number(value.minimum);
        const maximum = Number(value.maximum);
        const step = Number(value.step);
        // The descriptor decides whether the slider draws; the readings only where it sits, clamped
        // into the range, with an off-step one shown as is until the user moves it.
        if (
            !Number.isInteger(minimum) ||
            !Number.isInteger(maximum) ||
            !Number.isInteger(step) ||
            minimum < 0 ||
            maximum > 100 ||
            minimum >= maximum ||
            step < 1 ||
            step > maximum - minimum
        )
            return null;
        return Object.freeze({
            available: value.available,
            minimum,
            maximum,
            step,
            desired: clampReading(value.desired, minimum, maximum),
            observed: clampReading(value.observed, minimum, maximum),
            progress: normalizeText(value.progress),
            statusText: normalizeText(value.statusText),
            accent: value.accent === true,
        });
    };
    const normalizeDeviceControlsState = (value) => {
        if (!value || typeof value !== "object" || !Array.isArray(value.lightingZones)) return null;
        const chargeLimit = normalizeDeviceRange(value.chargeLimit);
        const lightingBrightness = normalizeDeviceRange(value.lightingBrightness);
        const lightingZones: Readonly<{
            id: string;
            label: string;
            available: boolean;
            desiredColor: number | null;
            observedColor: number | null;
            progress: string;
            statusText: string;
            accent: boolean;
        }>[] = [];
        const ids = new Set();
        for (const zone of value.lightingZones) {
            if (!zone || typeof zone !== "object") return null;
            const id = normalizeText(zone.id);
            const label = normalizeText(zone.label);
            const desiredColor = zone.desiredColor === null ? null : Number(zone.desiredColor);
            const observedColor = zone.observedColor === null ? null : Number(zone.observedColor);
            if (
                !id.trim() ||
                !label ||
                ids.has(id) ||
                (desiredColor !== null &&
                    (!Number.isInteger(desiredColor) ||
                        desiredColor < 0 ||
                        desiredColor > 0xffffff)) ||
                (observedColor !== null &&
                    (!Number.isInteger(observedColor) ||
                        observedColor < 0 ||
                        observedColor > 0xffffff))
            )
                return null;
            ids.add(id);
            lightingZones.push(
                Object.freeze({
                    id,
                    label,
                    available: zone.available === true,
                    desiredColor,
                    observedColor,
                    progress: normalizeText(zone.progress),
                    statusText: normalizeText(zone.statusText),
                    accent: zone.accent === true,
                }),
            );
        }
        return Object.freeze({
            chargeLimit,
            lightingBrightness,
            lightingZones: Object.freeze(lightingZones),
        });
    };

    const normalizeFrameLimitState = (value) => {
        const common = normalizePerformanceCommon(value);
        if (!common) return null;
        const minimumFps = value.minimumFps === null ? null : Number(value.minimumFps);
        const maximumFps = value.maximumFps === null ? null : Number(value.maximumFps);
        const desiredFps = value.desiredFps === null ? null : Number(value.desiredFps);
        const observedFps = value.observedFps === null ? null : Number(value.observedFps);
        // The bounds are a pair: either both are present or neither is. Rejecting a
        // half-populated range here rather than inside the big test below is also what
        // lets the rest of it treat maximumFps as a number.
        if ((minimumFps === null) !== (maximumFps === null)) return null;
        // A cap only has to be something the limiter could hold. It is deliberately NOT required to
        // sit between the bookends: a host that raised its floor, or a limiter written behind the
        // host's back, would otherwise publish a state that deleted the whole row — and this row is
        // the only place the user could have corrected the value. Observed on a handheld (2026-09-03),
        // where a 12 FPS cap under a floor of 30 took the Quick Access slider away entirely and left
        // no way to put it back. The bookends stretch to reach the value instead.
        const capUnusable = (fps) => fps !== null && (!Number.isInteger(fps) || fps < 0);
        if (
            (minimumFps !== null &&
                maximumFps !== null &&
                (!Number.isInteger(minimumFps) ||
                    !Number.isInteger(maximumFps) ||
                    minimumFps < 0 ||
                    maximumFps < minimumFps)) ||
            capUnusable(desiredFps) ||
            capUnusable(observedFps) ||
            (common.available && minimumFps === null)
        )
            return null;

        // Zero is OFF and is deliberately outside the slider's range, which starts at a cap worth
        // playing at, so it is the one value that never stretches a bookend.
        let lowestFps = minimumFps;
        let highestFps = maximumFps;
        if (lowestFps !== null && highestFps !== null) {
            for (const fps of [desiredFps, observedFps]) {
                if (fps === null || fps <= 0) continue;
                if (fps < lowestFps) lowestFps = fps;
                if (fps > highestFps) highestFps = fps;
            }
        }

        // Cap to refresh rate, for the "(60 Hz)" half of the label. Absent under the uncoupled
        // strategy, where a cap moves no display mode and there is nothing to name.
        const refreshForCap = new Map<number, number>();
        if (value.refreshForCap && typeof value.refreshForCap === "object") {
            for (const [cap, hz] of Object.entries(value.refreshForCap)) {
                const capValue = Number(cap);
                const hzValue = Number(hz);
                if (Number.isInteger(capValue) && Number.isInteger(hzValue) && hzValue > 0) {
                    refreshForCap.set(capValue, hzValue);
                }
            }
        }
        const refreshMinHz = value.refreshMinHz === null ? null : Number(value.refreshMinHz);
        const refreshMaxHz = value.refreshMaxHz === null ? null : Number(value.refreshMaxHz);
        const currentRefreshHz =
            value.currentRefreshHz === null ? null : Number(value.currentRefreshHz);
        // The refresh half is a pair like the cap half, and it is OPTIONAL: a display that offers no
        // rates leaves the row with only its frame-limit mode rather than rejecting the state.
        // The stops the refresh mode slides between. Windows takes a MODE or refuses: a panel that
        // has 60 and 75 does not have 72, so this mode is notched to exactly what the display
        // accepted, unlike the frame cap, where the limiter really does hold any integer.
        const refreshRates: number[] = [];
        if (Array.isArray(value.refreshRates)) {
            for (const item of value.refreshRates) {
                const hz = Number(item);
                if (Number.isInteger(hz) && hz > 0 && !refreshRates.includes(hz))
                    refreshRates.push(hz);
            }
            refreshRates.sort((left, right) => left - right);
        }
        const refreshUsable =
            refreshRates.length > 0 &&
            refreshMinHz !== null &&
            refreshMaxHz !== null &&
            currentRefreshHz !== null &&
            Number.isInteger(refreshMinHz) &&
            Number.isInteger(refreshMaxHz) &&
            Number.isInteger(currentRefreshHz) &&
            refreshMinHz > 0 &&
            refreshMaxHz >= refreshMinHz;
        return Object.freeze({
            ...common,
            minimumFps: lowestFps,
            maximumFps: highestFps,
            desiredFps,
            observedFps,
            limitEnabled: value.limitEnabled === true,
            refreshForCap,
            refreshMinHz: refreshUsable ? refreshMinHz : null,
            refreshMaxHz: refreshUsable ? refreshMaxHz : null,
            currentRefreshHz: refreshUsable ? currentRefreshHz : null,
            refreshRates: refreshUsable ? Object.freeze(refreshRates) : Object.freeze([]),
        });
    };
    // The last state each kind accepted. A row mounted again when the panel reopens starts from it,
    // so it draws on its first render rather than reporting nothing until the replay arrives, which
    // would flash its section out of layout and back.
    const acceptedStates = new Map();
    const useSemanticState = (controlRuntime, kind, normalize) => {
        const definition = definitions[kind];
        const [state, setState] = controlRuntime.react.useState(
            () => acceptedStates.get(kind) ?? null,
        );
        controlRuntime.react.useEffect(
            () =>
                subscribe(definition.patchId, (value) => {
                    const normalized = normalize(value);

                    // A state that arrives and fails validation is not the same as one that never
                    // arrived, and both used to end as a null the control returned on. The controller row
                    // was invisible for exactly this reason: the host sends PascalCase target ids and the
                    // validator only accepted lowercase, so every delivery was discarded in silence.
                    if (normalized === null && value) {
                        renderOutcomes[kind] = "state received but rejected by validation";
                    }

                    acceptedStates.set(kind, normalized);
                    setState(normalized);
                }),
            [],
        );
        return state;
    };
    const isBusy = (progress) =>
        progress === "queued" || progress === "applying" || progress === "replacing";

    /// Lets a controlled slider follow the user's input before the hardware confirms it.
    ///
    /// These sliders are controlled by the observed hardware value, so with a no-op onChange the
    /// handle snapped back to that value on every render: dragging did nothing at all, and a single
    /// press moved exactly one step because only onChangeComplete ever committed. The echo holds
    /// what the user is pointing at until the release, then clears so the observed value governs
    /// again — including when the device refuses the write and the handle must spring back to what
    /// the hardware really is.
    const useEchoedValue = (controlRuntime, observed) => {
        const [echo, setEcho] = controlRuntime.react.useState(null);
        const [echoOf, setEchoOf] = controlRuntime.react.useState(observed);

        // A new observation supersedes an echo taken against the previous one; without this the
        // handle would keep showing a value the hardware had already moved away from.
        if (echoOf !== observed) {
            setEchoOf(observed);
            if (echo !== null) setEcho(null);
        }

        return {
            value: echo ?? observed,
            onChange: (next) => setEcho(typeof next === "number" ? next : null),
            onChangeComplete: (next, commit) => {
                setEcho(null);
                if (typeof next === "number" && Number.isFinite(next) && next !== observed)
                    commit(next);
            },
        };
    };

    /// Coalesces expensive device-persistent writes while preserving the last value.
    /// A colour is edited through three sliders; committing each component separately can queue
    /// stale intermediate colours behind a firmware write-rate limit. The last edit replaces the
    /// pending one, and unmount flushes it so closing QAM cannot lose the user's final colour.
    const useTrailingCommit = (controlRuntime, delayMilliseconds, commit) => {
        const pending = controlRuntime.react.useRef(null);
        const timer = controlRuntime.react.useRef(null);
        const commitRef = controlRuntime.react.useRef(commit);
        commitRef.current = commit;

        const flush = () => {
            if (timer.current !== null) {
                globalThis.clearTimeout(timer.current);
                timer.current = null;
            }
            const value = pending.current;
            pending.current = null;
            if (value !== null) commitRef.current(value);
        };
        controlRuntime.react.useEffect(
            () => () => {
                flush();
            },
            [],
        );
        return (value) => {
            pending.current = value;
            if (timer.current !== null) globalThis.clearTimeout(timer.current);
            timer.current = globalThis.setTimeout(flush, delayMilliseconds);
        };
    };
    // Steam's localizer returns the token itself when it has no string for it, which is truthy and
    // would render "#QuickAccess_..." as a label. Live-verified 2026-08-29: a known token localizes,
    // an unknown one comes straight back.
    //
    // EVERY label goes through this, not only the host-invented ones. With the rows finally
    // rendering on the reference device, "#QuickAccess_Tab_Perf_FramerateLimit" and
    // "#QuickAccess_Tab_Perf_PerfOverlayLevel" both came back raw and were shown to the user as
    // their token text. A bare localize() call here is a bug waiting for the next missing string.
    //
    // Live-probed 2026-08-30, which found the reason: neither token exists anywhere in the bundle.
    // They were never SteamOS strings absent from the Windows set — they were wrong names. The
    // client carries "#QuickAccess_Tab_Perf_LimitFrameRate" and "#QuickAccess_Tab_Perf_Overlay_Level",
    // and those localize. Both call sites now use the real names, so those two rows are translated
    // rather than permanently English.
    //
    // The fallback still earns its place, for the labels the host invents and Valve has no string for
    // (AutoTDP, the display-resolution row). Those pass no token at all rather than a plausible
    // one: a token that does not exist makes Steam log an unresolved string on every render and
    // still shows the English text.
    // Steam's localizer does not return a string. It returns a React element wrapping one, so
    // `typeof text === "string"` was false for every token and every the host label fell back to its
    // English default while Steam's own rows beside them were in the user's language. The element
    // is what should be handed to the field; only the "#" test needs the text inside it (textOf).
    const localizeOr = (controlRuntime, token, fallback) => {
        const localized = controlRuntime.localize(token);
        const text = textOf(localized);
        return text && text.length > 0 && text[0] !== "#" ? localized : fallback;
    };
    // The host's own variable-refresh switch. Valve ships one, and it cannot be used: its component is
    // gated on a react-query over SteamClient.System.DisplayManager, whose GetState this client
    // does not define — the query never succeeds and the component returns null before it reads a
    // single field the host publishes (live-probed 2026-08-30). The device capability behind this row
    // is the one already verified on the reference unit through IGCL Arc Sync.
    const createVrrControl = (controlRuntime) =>
        function SteamUiVrrControl() {
            const state = useSemanticState(controlRuntime, "vrr", normalizeVrrState);
            const [refusal, setRefusal] = controlRuntime.react.useState("");
            if (!state) return note("vrr", "no state");
            if (!state.available)
                return note("vrr", "unavailable: " + (state.statusText || "no reason"));
            if (!controlRuntime.toggle) return note("vrr", "Steam ToggleField was not resolved");
            drew("vrr");
            summarize("vrr", state.enabled ? "VRR on" : "VRR off");
            const definition = definitions.vrr;
            const toggle = controlRuntime.react.createElement(controlRuntime.toggle, {
                // Valve's own token for the row, so the label matches the client's language even though
                // the component behind it is the host's.
                label: localizeOr(
                    controlRuntime,
                    "#QuickAccess_Tab_Perf_EnableVRR",
                    "Variable refresh rate",
                ),
                icon: controlRuntime.icon("pulse"),
                description:
                    refusal || accentDescription(controlRuntime, state.accent, state.statusText),
                checked: state.enabled,
                // Controlled: the switch shows what the device reports, so a write the panel refuses
                // leaves it where the hardware actually is rather than where it was clicked.
                controlled: true,
                disabled: isBusy(state.progress),
                onChange: toggleCommand(definition, state, setRefusal),
            });
            return toggle;
        };
    const createAutoTdpControl = (controlRuntime) =>
        function SteamUiAutoTdpControl() {
            const state = useSemanticState(controlRuntime, "autoTdp", normalizeAutoTdpState);
            const [refusal, setRefusal] = controlRuntime.react.useState("");
            if (!state) return note("autoTdp", "no state");
            if (!state.available)
                return note("autoTdp", "unavailable: " + (state.statusText || "no reason"));
            // Deliberately outside createControlRuntime's guard, so a client whose ToggleField cannot
            // be located loses only this row. That silence is exactly what needed a name.
            if (!controlRuntime.toggle)
                return note("autoTdp", "Steam ToggleField was not resolved");
            drew("autoTdp");
            summarize(
                "autoTdp",
                !state.enabled
                    ? ""
                    : state.controlling && state.watts !== null
                      ? `Auto TDP holding ${state.watts} W`
                      : "Auto TDP on",
            );
            const definition = definitions.autoTdp;
            // While controlling, the watts AutoTDP settled on go in the description: a user watching the
            // slider move needs to see that something is driving it, and what it decided.
            const description =
                state.controlling && state.watts !== null
                    ? state.watts + " W · " + state.statusText
                    : state.statusText;
            return controlRuntime.react.createElement(controlRuntime.toggle, {
                // The host's own control; Valve has no string for it, so no token is passed.
                label: "Automatic TDP",
                icon: controlRuntime.icon("auto"),
                description: refusal || description || undefined,
                checked: state.enabled,
                // Controlled, so the switch shows the stored setting rather than its own click. A command
                // that does not land leaves the switch where the setting actually is instead of showing a
                // change that did not happen.
                controlled: true,
                disabled: isBusy(state.progress),
                onChange: toggleCommand(definition, state, setRefusal),
            });
        };
    const normalizePowerProfileState = (value) => {
        if (!value || typeof value.available !== "boolean" || !Array.isArray(value.options))
            return null;
        const ids = new Set();
        const options: { id: string; label: string; selectable: boolean }[] = [];
        for (const item of value.options) {
            const label = normalizeText(item?.label);
            if (
                !item ||
                typeof item.id !== "string" ||
                !/^[A-Za-z0-9._-]+$/.test(item.id) ||
                !label.trim() ||
                ids.has(item.id)
            )
                return null;
            ids.add(item.id);
            // An option the host marks unselectable names a state the user cannot pick, such as values
            // that match none of the presets: it is listed only where it is the current value, and never
            // sent.
            options.push({ id: item.id, label, selectable: item.selectable !== false });
        }
        return {
            available: value.available,
            options,
            current: normalizeText(value.current),
            statusText: normalizeText(value.statusText),
        };
    };
    // A refused write's reason, whole, for the row's description.
    const refusalText = (reason) => normalizeText(String(reason?.message ?? reason));
    // One dropdown write: pending while it is in flight, and its refusal handed to `refuse` for the
    // row's description rather than swallowed. The next write clears it ("") as it starts.
    const sendPending = (setPending, refuse: (text: string) => void, sent: Promise<unknown>) => {
        setPending(true);
        refuse("");
        void sent.catch((reason) => refuse(refusalText(reason))).finally(() => setPending(false));
    };
    // The Windows power profile, processor core and CPU boost rows: one dropdown over the same state
    // shape, differing in kind, label, glyph and how a state describes itself (the boost row carries
    // the per-game marker). No options is nothing to choose, so the reason goes to renderOutcomes
    // rather than onto an empty, disabled dropdown. The component keeps the name it is created under,
    // and the glyph is built by the caller so each row's `icon("…")` stays a literal the glyph
    // ownership check can read.
    const createChoiceControl = (
        controlRuntime,
        kind,
        label,
        name,
        icon,
        normalize = normalizePowerProfileState,
        describe = (state) => state.statusText || undefined,
    ) =>
        ({
            [name]: function () {
                const state = useSemanticState(controlRuntime, kind, normalize);
                const [pending, setPending] = controlRuntime.react.useState(false);
                const [refusal, setRefusal] = controlRuntime.react.useState("");
                if (!state) return note(kind, "no state");
                if (!state.options.length)
                    return note(kind, "no options: " + (state.statusText || "no reason"));
                const options = state.options
                    .filter((option) => option.selectable || option.id === state.current)
                    .map((option) => ({ data: option.id, label: option.label }));
                const selectable = (id) =>
                    state.options.some((option) => option.id === id && option.selectable);
                const definition = definitions[kind];
                drew(kind);
                summarize(
                    kind,
                    options.find((option) => option.data === state.current)?.label ?? "",
                );
                return controlRuntime.react.createElement(controlRuntime.dropdown, {
                    label,
                    icon: icon(),
                    rgOptions: options,
                    selectedOption: options.some((option) => option.data === state.current)
                        ? state.current
                        : undefined,
                    disabled: pending || !state.available || options.length < 2,
                    description: refusal || describe(state),
                    layout: "below",
                    onChange: (option) => {
                        if (
                            pending ||
                            !state.available ||
                            !option ||
                            option.data === state.current ||
                            !selectable(option.data)
                        )
                            return;
                        sendPending(
                            setPending,
                            setRefusal,
                            sendCommand(definition, definition.command, { target: option.data }),
                        );
                    },
                });
            },
        })[name];
    const createPowerProfileControl = (controlRuntime) =>
        createChoiceControl(
            controlRuntime,
            "powerProfile",
            "Windows power profile",
            "SteamUiPowerProfileControl",
            () => controlRuntime.icon("power"),
        );
    // A processor with one kind of core publishes no options, and has nothing to show here.
    const createHybridCoreControl = (controlRuntime) =>
        createChoiceControl(
            controlRuntime,
            "hybridCores",
            "Processor cores",
            "SteamUiHybridCoreControl",
            () => controlRuntime.icon("cores"),
        );
    // The power-profile shape plus the per-game marker, which the boost row's description carries.
    const normalizeCpuBoostState = (value) => {
        const state = normalizePowerProfileState(value);
        return state ? { ...state, accent: value.accent === true } : null;
    };
    const createCpuBoostControl = (controlRuntime) =>
        createChoiceControl(
            controlRuntime,
            "cpuBoost",
            "CPU boost mode",
            "SteamUiCpuBoostControl",
            () => controlRuntime.icon("turbo"),
            normalizeCpuBoostState,
            (state) => accentDescription(controlRuntime, state.accent, state.statusText),
        );
    const normalizePowerPresetState = (value) => {
        const state = normalizePowerProfileState(value);
        if (!state || typeof value.ac !== "string" || typeof value.battery !== "string")
            return null;
        const valid = (id) => id === "" || state.options.some((option) => option.id === id);
        if (!valid(value.ac) || !valid(value.battery)) return null;
        return {
            ...state,
            ac: value.ac,
            battery: value.battery,
            scope: normalizeText(value.scope),
            unsetLabel: normalizeText(value.unsetLabel),
            acAccent: value.acAccent === true,
            batteryAccent: value.batteryAccent === true,
        };
    };
    const createPowerPresetControl = (controlRuntime) =>
        function SteamUiPowerAssignments() {
            const state = useSemanticState(
                controlRuntime,
                "powerPreset",
                normalizePowerPresetState,
            );
            const [pending, setPending] = controlRuntime.react.useState(false);
            // A refusal belongs to the assignment whose write it answered.
            const [refusal, setRefusal] = controlRuntime.react.useState(null);
            if (!state || !state.options.length) return note("powerPreset", "no state");
            const options = [
                { data: "", label: state.unsetLabel || "Manual selection", selectable: true },
                ...state.options.map((option) => ({
                    data: option.id,
                    label: option.label,
                    selectable: option.selectable,
                })),
            ];
            const definition = definitions.powerPreset;
            // The unset entry is the way back to Global for a game's own assignment, so the override needs
            // only its marker here, not a second control.
            const assignment = (
                label,
                iconName,
                selected,
                command,
                accent: boolean,
                description?: string,
            ) =>
                controlRuntime.react.createElement(controlRuntime.dropdown, {
                    label,
                    icon: controlRuntime.icon(iconName),
                    layout: "below",
                    description:
                        refusal && refusal.command === command
                            ? refusal.text
                            : accentDescription(controlRuntime, accent, description),
                    rgOptions: options
                        .filter((option) => option.selectable || option.data === selected)
                        .map((option) => ({ data: option.data, label: option.label })),
                    selectedOption: selected,
                    disabled: pending || !state.available,
                    onChange: (option) => {
                        if (
                            pending ||
                            !state.available ||
                            !option ||
                            !options.some((item) => item.data === option.data && item.selectable)
                        )
                            return;
                        sendPending(
                            setPending,
                            (text) => setRefusal(text ? { command, text } : null),
                            sendCommand(definition, command, { target: option.data || null }),
                        );
                    },
                });
            drew("powerPreset");
            // The two assignments, named; an unset one says nothing.
            const assigned = (label, id) =>
                id ? `${label} ${options.find((option) => option.data === id)?.label ?? id}` : "";
            summarize(
                "powerPreset",
                [assigned("Plugged in", state.ac), assigned("Battery", state.battery)]
                    .filter(Boolean)
                    .join(" · ") || state.current,
            );
            // What is in effect, and why. The scope and the status belong to that one fact, so they are
            // its description rather than two more unlabelled lines: every other row in this host puts
            // its status there, and three stacked bare divs were the one place the panel stopped
            // looking like Steam.
            const detail = [state.scope, state.statusText].filter(Boolean).join(" · ");
            const active = !state.current
                ? null
                : controlRuntime.labelField
                  ? controlRuntime.react.createElement(
                        controlRuntime.labelField,
                        {
                            label: "Active profile",
                            icon: controlRuntime.icon("check"),
                            description: detail || undefined,
                        },
                        state.current,
                    )
                  : note("powerPresetActive", "Steam LabelField was not resolved");
            // A refusal has to stay visible when there is no active profile to hang it on. Readback can
            // fail with `available: false`, a reason, and no current profile at all, and on a client
            // where LabelField could not be resolved there is no row here either; both left two
            // disabled dropdowns and no explanation for why.
            const orphaned = active || !detail ? undefined : detail;
            return controlRuntime.react.createElement(
                controlRuntime.react.Fragment,
                null,
                active,
                assignment(
                    "When plugged in",
                    "plug",
                    state.ac,
                    definition.acCommand,
                    state.acAccent,
                    orphaned,
                ),
                assignment(
                    "On battery",
                    "battery",
                    state.battery,
                    definition.batteryCommand,
                    state.batteryAccent,
                ),
            );
        };
    const createControllerControl = (controlRuntime) =>
        function SteamUiControllerTargetControl() {
            const state = useSemanticState(
                controlRuntime,
                "controllerTarget",
                normalizeControllerState,
            );
            const [refusal, setRefusal] = controlRuntime.react.useState("");
            if (!state) return note("controllerTarget", "no state");
            if (!state.available)
                return note(
                    "controllerTarget",
                    "unavailable: " + (state.statusText || "no reason"),
                );
            const options = state.targets
                .filter((target) => target.available)
                .map((target) => ({ data: target.id, label: target.label }));
            // What the host reports, or nothing when neither the observed nor the selected target is one
            // of the available ones: the dropdown still draws, with no selection, so the user can pick.
            const reported = state.observedTarget || state.selectedTarget;
            const selected = options.some((option) => option.data === reported)
                ? reported
                : undefined;
            if (!options.length) return note("controllerTarget", "no available targets");
            drew("controllerTarget");
            summarize(
                "controllerTarget",
                options.find((option) => option.data === selected)?.label ?? "",
            );
            const definition = definitions.controllerTarget;
            const setTarget = (option) => {
                if (!option || !options.some((candidate) => candidate.data === option.data)) return;
                setRefusal("");
                void sendCommand(definition, definition.command, { target: option.data }).catch(
                    (reason) => setRefusal(refusalText(reason)),
                );
            };
            const dropdown = controlRuntime.react.createElement(controlRuntime.dropdown, {
                label: localizeOr(
                    controlRuntime,
                    "#QuickAccess_Tab_Settings_Section_Controller_Title",
                    "Controller",
                ),
                icon: controlRuntime.icon("swap"),
                rgOptions: options,
                selectedOption: selected,
                onChange: setTarget,
                // One target is still a choice while none is shown as selected.
                disabled: isBusy(state.progress) || (options.length < 2 && selected !== undefined),
                description:
                    refusal || accentDescription(controlRuntime, state.accent, state.statusText),
                layout: "below",
            });
            return dropdown;
        };
    const createResolutionControl = (controlRuntime) =>
        function SteamUiResolutionControl() {
            const state = useSemanticState(controlRuntime, "resolution", normalizeResolutionState);
            const [refusal, setRefusal] = controlRuntime.react.useState("");
            if (!state) return note("resolution", "no state");
            if (!state.available)
                return note("resolution", "unavailable: " + (state.statusText || "no reason"));
            if (state.options.length < 2)
                return note("resolution", `only ${state.options.length} option(s)`);
            drew("resolution");
            summarize("resolution", state.options.includes(state.current) ? state.current : "");
            const definition = definitions.resolution;
            const options = state.options.map((option) => ({ data: option, label: option }));
            const setResolution = (option) => {
                // Checked against the offered list before sending. The row cannot be the only thing
                // standing between a stray value and a mode change, but it should not be the source of
                // one either.
                if (!option || !state.options.includes(option.data)) return;
                // "target" rather than "value": that is the payload shape every dropdown here uses, and
                // the host's reader rejects an object carrying anything else.
                setRefusal("");
                void sendCommand(definition, definition.command, { target: option.data }).catch(
                    (reason) => setRefusal(refusalText(reason)),
                );
            };
            return controlRuntime.react.createElement(controlRuntime.dropdown, {
                // Not localized, deliberately. The client has no token meaning "display resolution":
                // #Settings_Display_GameResolution is a per-game override and would read wrongly in every
                // language but English. Passing a token that does not exist is worse than passing none —
                // it makes Steam log an unresolved token on every render and still shows this string.
                label: "Display resolution",
                icon: controlRuntime.icon("aspect"),
                rgOptions: options,
                // A current mode outside the offered list selects nothing rather than the first entry,
                // which would silently misreport what the display is doing.
                selectedOption: state.options.includes(state.current) ? state.current : undefined,
                onChange: setResolution,
                description: refusal || state.statusText || undefined,
                layout: "below",
            });
        };
    // Typed host settings reuse the same native fields as a routed settings page.
    const createSettingsSectionsControl = (controlRuntime, ui) =>
        function SteamUiSettingsSectionsControl() {
            const state = useSemanticState(controlRuntime, "settingsSections", (value) =>
                value && Array.isArray(value.pages) && Number.isSafeInteger(value.revision)
                    ? value
                    : null,
            );
            const folds = useSemanticState(controlRuntime, "panelFolds", normalizePanelFoldsState);
            const react = controlRuntime.react;
            // The settings page's own draft keeping: a refused write shows why on its row, and a
            // publication that changes another row keeps what is being typed here.
            const drafts = useSteamSettingDrafts(react, state?.revision);
            if (!state) return note("settingsSections", "no state");
            if (!ui?.valueField || !ui?.smallButton)
                return note(
                    "settingsSections",
                    "Steam's settings value field or small button was not resolved",
                );
            const change = drafts.change((row, value) =>
                sendCommand(definitions.settingsSections, "set", { key: row.key, value }),
            );
            const action = (row) => change(row, true);
            return react.createElement(
                react.Fragment,
                null,
                ...state.pages.map((page) => {
                    const key = "settings." + page.id;
                    return renderSteamUiGroup(
                        controlRuntime,
                        {
                            key,
                            title: page.title,
                            collapsed: isFolded(folds, key),
                            onToggle: () => setFolded(key, !isFolded(folds, key)),
                        },
                        ...(page.sections ?? []).map((section, index) =>
                            renderSteamUiGroup(
                                controlRuntime,
                                {
                                    key: key + "." + (section.id ?? index),
                                    title: section.title || undefined,
                                },
                                ...(section.rows ?? []).map((row) =>
                                    react.createElement(
                                        controlRuntime.row,
                                        { key: row.key },
                                        renderSteamSettingRow(
                                            ui,
                                            drafts.row({ ...row, layout: "below" }),
                                            drafts.draft(row),
                                            change,
                                            action,
                                        ),
                                    ),
                                ),
                            ),
                        ),
                    );
                }),
            );
        };

    const createAudioFormatControl = (controlRuntime) =>
        function SteamUiAudioFormatControl() {
            const state = useSemanticState(
                controlRuntime,
                "audioFormat",
                normalizeAudioFormatState,
            );
            const [pending, setPending] = controlRuntime.react.useState(false);
            // A refusal belongs to the dropdown whose write it answered; two share a command.
            const [refusal, setRefusal] = controlRuntime.react.useState(null);
            if (!state) return note("audioFormat", "no state");
            if (!state.available)
                return note("audioFormat", "unavailable: " + (state.statusText || "no reason"));
            const definition = definitions.audioFormat;
            const dropdown = (label, choices, current, command, icon) => {
                if (choices.length === 0) return null;
                const options = choices.map((choice) => ({ data: choice.id, label: choice.label }));
                return controlRuntime.react.createElement(controlRuntime.dropdown, {
                    label,
                    icon: controlRuntime.icon(icon),
                    rgOptions: options,
                    selectedOption: current || undefined,
                    disabled: pending || choices.length < 2,
                    description:
                        (refusal?.label === label ? refusal.text : "") ||
                        state.statusText ||
                        undefined,
                    layout: "below",
                    onChange: (option) => {
                        if (
                            pending ||
                            !option ||
                            option.data === current ||
                            !options.some((choice) => choice.data === option.data)
                        )
                            return;
                        sendPending(
                            setPending,
                            (text) => setRefusal(text ? { label, text } : null),
                            sendCommand(definition, command, { target: option.data }),
                        );
                    },
                });
            };
            const channels = dropdown(
                "Channels",
                state.channelOptions,
                state.currentChannels,
                definition.formatCommand,
                "audioChannels",
            );
            const format = dropdown(
                "Format",
                state.formatOptions,
                state.currentFormat,
                definition.formatCommand,
                "audioEncoding",
            );
            const spatial = dropdown(
                "Spatial sound",
                state.spatialOptions,
                state.currentSpatial,
                definition.spatialCommand,
                "audioSpatial",
            );
            if (!channels && !format && !spatial)
                return note("audioFormat", "fewer than two choices");
            drew("audioFormat");
            summarize(
                "audioFormat",
                [
                    state.channelOptions.find((choice) => choice.id === state.currentChannels)
                        ?.label,
                    state.formatOptions.find((choice) => choice.id === state.currentFormat)?.label,
                    state.spatialOptions.find((choice) => choice.id === state.currentSpatial)
                        ?.label,
                ]
                    .filter(Boolean)
                    .join(" · "),
            );
            return controlRuntime.react.createElement(
                controlRuntime.react.Fragment,
                null,
                channels,
                format,
                spatial,
            );
        };
    // Which notch the display is currently sitting on. A rate that is not one of the listed modes —
    // something else can leave the panel on one — takes the nearest notch at or below it rather
    // than snapping the handle to the start and reporting a rate the display is not at.
    const currentRefreshNotch = (state) => {
        if (!state || !state.refreshRates || state.refreshRates.length === 0) return null;
        const current = state.currentRefreshHz;
        if (!Number.isInteger(current)) return null;
        let notch = 0;
        for (let index = 0; index < state.refreshRates.length; index += 1) {
            if (state.refreshRates[index] <= current) notch = index;
        }
        return notch;
    };
    const createFrameLimitControl = (controlRuntime) =>
        function SteamUiFrameLimitControl() {
            const state = useSemanticState(controlRuntime, "frameLimit", normalizeFrameLimitState);
            const value = state ? (state.observedFps ?? state.desiredFps) : null;
            const echoed = useEchoedValue(controlRuntime, value);
            // Its own echo, because the two modes are two different numbers on one slider: reusing one
            // would make the handle jump to a frame cap the moment the rate mode took over. It echoes
            // the notch INDEX, which is what a notch slider reports while it is being dragged.
            // Unconditional, ahead of every early return — these are hooks.
            const refreshEchoed = useEchoedValue(controlRuntime, currentRefreshNotch(state));
            const [refusal, setRefusal] = controlRuntime.react.useState("");
            if (!state) return note("frameLimit", "no state");
            if (!state.available)
                return note("frameLimit", "unavailable: " + (state.statusText || "no reason"));
            // No observed or desired cap still draws the row: the slider sits where an unset cap does and
            // hides its number until the user moves it.
            drew("frameLimit");
            const definition = definitions.frameLimit;
            // A refused write shows why under the slider until the next write.
            const send = (command, nextValue) => {
                setRefusal("");
                void sendCommand(definition, command, { value: nextValue }).catch((reason) =>
                    setRefusal(refusalText(reason)),
                );
            };
            const setCap = (nextValue) => {
                if (
                    !Number.isInteger(nextValue) ||
                    nextValue < state.minimumFps ||
                    nextValue > state.maximumFps
                )
                    return;
                send(definition.command, nextValue);
            };
            // Takes a NOTCH INDEX, not a rate: the refresh mode is a notch slider, so what the control
            // hands back is a position in the accepted list.
            const setRefresh = (notchIndex) => {
                const hz = state.refreshRates[notchIndex];
                if (!Number.isInteger(hz)) return;
                send(definition.refreshCommand, hz);
            };

            // Off is zero, and the slider never shows it: the cap the user chose has to survive being
            // switched off and back on, so the switch below writes zero and the slider keeps sitting
            // where it was. That is how SteamOS's own "Disable Frame Limit" behaves next to its Frame
            // Limit slider. With no cap chosen yet it sits at the highest one, because no limit means
            // the most the display can run, so switching the limit on costs nothing until it is moved.
            const capped = state.limitEnabled && echoed.value > 0;
            const cappedValue = echoed.value > 0 ? echoed.value : (state.maximumFps ?? 0);
            // Recomputed every render, which is what makes it track a value still being dragged.
            const pairedHz = state.refreshForCap.get(cappedValue);

            // The row's second mode. With the cap off the slider IS the refresh rate — the whole reason
            // SteamOS merged the two rows is that they are one decision: the frame cap and the rate it
            // is presented at are the same frametime question, and vsync is what makes the pacing hold.
            // Switching the cap off does not leave a dead control behind, it hands the same slider over
            // to the rate.
            const refreshMode = !capped && state.refreshRates.length > 0;
            const sliderValue = refreshMode ? (refreshEchoed.value ?? 0) : cappedValue;
            summarize(
                "frameLimit",
                capped
                    ? `${cappedValue} fps cap`
                    : refreshMode
                      ? `${state.refreshRates[refreshEchoed.value ?? 0] ?? "?"} Hz`
                      : "No frame limit",
            );
            // Guarded like the AutoTDP row: a client whose ToggleField cannot be located loses the
            // switch and keeps the slider, rather than losing the whole row silently.
            const disableSwitch = controlRuntime.toggle
                ? controlRuntime.react.createElement(controlRuntime.toggle, {
                      // Not "#QuickAccess_Tab_Perf_LimitFrameRate_Off": that token is the notch slider's
                      // first STOP and localizes to bare "Off" ("AUS"), which reads as a row with no
                      // subject once it is a switch of its own. SteamOS names this switch outright.
                      label: "Disable frame limit",
                      icon: controlRuntime.icon("infinity"),
                      description: refreshMode
                          ? "The slider sets the refresh rate while the limit is off."
                          : undefined,
                      checked: !capped,
                      controlled: true,
                      disabled: isBusy(state.progress),
                      // Turning it back on restores the cap the slider is already sitting on, so the
                      // number the user was looking at is the one that takes effect.
                      onChange: (next) => send(definition.command, next ? 0 : cappedValue),
                  })
                : note("frameLimitSwitch", "Steam ToggleField was not resolved");
            const slider = controlRuntime.react.createElement(controlRuntime.slider, {
                // Live-verified 2026-08-30: these are tokens the client actually carries.
                // "#QuickAccess_Tab_Perf_FramerateLimit" appears nowhere in the bundle, so the row it was
                // written against fell back to English on every localized client.
                label: refreshMode
                    ? localizeOr(
                          controlRuntime,
                          "#QuickAccess_Tab_Perf_RefreshRate",
                          "Refresh rate",
                      )
                    : localizeOr(
                          controlRuntime,
                          "#QuickAccess_Tab_Perf_LimitFrameRate",
                          "Frame rate limit",
                      ),
                icon: controlRuntime.icon("frameRate"),
                // SliderField would otherwise put the glyph beside the track, which is where Valve keeps
                // the Quick Settings brightness icon on a slider that has no label at all. These sliders
                // are labelled, and the icon belongs with the label so every row in a section lines its
                // glyph up in one column.
                iconLocation: "front",
                // The two modes are two different sliders sharing one row. The frame cap is NOTCHLESS
                // under every strategy — the limiter holds any integer and the pairing is what snaps —
                // while the refresh rate is notched to exactly the modes the display accepted, because
                // Windows takes a mode or refuses and there is no continuum between 60 and 75.
                min: 0,
                max: refreshMode ? state.refreshRates.length - 1 : state.maximumFps,
                ...(refreshMode
                    ? {
                          notchCount: state.refreshRates.length,
                          notchLabels: state.refreshRates.map((hz, notchIndex) => ({
                              notchIndex,
                              label: `${hz}`,
                              value: hz,
                          })),
                          notchTicksVisible: true,
                      }
                    : { min: state.minimumFps }),
                step: 1,
                value: sliderValue,
                // "60 FPS (60 Hz)" is how SteamOS's unified row names a cap and the rate it will be
                // presented at. In refresh mode the notch label already carries the number.
                valueSuffix: refreshMode ? " Hz" : pairedHz ? ` FPS (${pairedHz} Hz)` : " FPS",
                showValue: !refreshMode && echoed.value !== null,
                showBookendLabels: !refreshMode,
                disabled: isBusy(state.progress),
                description:
                    refusal ||
                    accentDescription(
                        controlRuntime,
                        state.accent,
                        state.fault || state.statusText,
                    ),
                onChange: refreshMode ? refreshEchoed.onChange : echoed.onChange,
                onChangeComplete: (next) =>
                    refreshMode
                        ? refreshEchoed.onChangeComplete(next, setRefresh)
                        : echoed.onChangeComplete(next, setCap),
            });
            return controlRuntime.react.createElement(
                controlRuntime.react.Fragment,
                null,
                slider,
                disableSwitch,
            );
        };
    const rgbToHsv = (color) => {
        const red = ((color >> 16) & 0xff) / 255;
        const green = ((color >> 8) & 0xff) / 255;
        const blue = (color & 0xff) / 255;
        const maximum = Math.max(red, green, blue);
        const minimum = Math.min(red, green, blue);
        const delta = maximum - minimum;
        let hue = 0;
        if (delta > 0) {
            if (maximum === red) hue = 60 * (((green - blue) / delta) % 6);
            else if (maximum === green) hue = 60 * ((blue - red) / delta + 2);
            else hue = 60 * ((red - green) / delta + 4);
        }
        if (hue < 0) hue += 360;
        return {
            hue: Math.round(hue),
            saturation: maximum === 0 ? 0 : Math.round((delta / maximum) * 100),
            brightness: Math.round(maximum * 100),
        };
    };
    const hsvToRgb = (hue, saturation, brightness) => {
        const h = ((Number(hue) % 360) + 360) % 360;
        const s = Math.min(100, Math.max(0, Number(saturation))) / 100;
        const v = Math.min(100, Math.max(0, Number(brightness))) / 100;
        const chroma = v * s;
        const x = chroma * (1 - Math.abs(((h / 60) % 2) - 1));
        const m = v - chroma;
        let red = 0;
        let green = 0;
        let blue = 0;
        if (h < 60) [red, green] = [chroma, x];
        else if (h < 120) [red, green] = [x, chroma];
        else if (h < 180) [green, blue] = [chroma, x];
        else if (h < 240) [green, blue] = [x, chroma];
        else if (h < 300) [red, blue] = [x, chroma];
        else [red, blue] = [chroma, x];
        return (
            (Math.round((red + m) * 255) << 16) |
            (Math.round((green + m) * 255) << 8) |
            Math.round((blue + m) * 255)
        );
    };
    const rgbCss = (color) => `#${Number(color).toString(16).padStart(6, "0")}`;

    const normalizePowerLimitRange = (value) => {
        if (!value || typeof value !== "object") return null;
        const {
            minimumWatts: min,
            maximumWatts: max,
            stepWatts: step,
            observedWatts: observed,
        } = value;
        // A valid descriptor draws the slider. The reading is a display detail: none leaves the slider
        // at its minimum with no number, one outside the range is shown at the nearer end, and one off
        // the step is shown as is until the user moves it.
        if (
            ![min, max, step].every(Number.isInteger) ||
            min < 1 ||
            min >= max ||
            step < 1 ||
            step > max - min
        )
            return null;
        return {
            available: value.available === true,
            min,
            max,
            step,
            observed: clampReading(observed, min, max),
            progress: normalizeText(value.progress),
            statusText: normalizeText(value.statusText),
            accent: value.accent === true,
        };
    };
    const normalizePowerLimitState = (value) =>
        value && typeof value === "object"
            ? {
                  sustained: normalizePowerLimitRange(value.sustained),
                  boost: normalizePowerLimitRange(value.boost),
                  unified: value.unified === true,
                  canSelectMode: value.canSelectMode === true,
                  modeAccent: value.modeAccent === true,
              }
            : null;
    const createPowerLimitControl = (controlRuntime) =>
        function SteamUiPowerLimits() {
            const state = useSemanticState(controlRuntime, "powerLimit", normalizePowerLimitState);
            const definition = definitions.powerLimit;
            const sustainedEcho = useEchoedValue(
                controlRuntime,
                state?.sustained?.observed ?? null,
            );
            const boostEcho = useEchoedValue(controlRuntime, state?.boost?.observed ?? null);
            const pending = controlRuntime.react.useRef(false);
            const [sending, setSending] = controlRuntime.react.useState(false);
            const [error, setError] = controlRuntime.react.useState("");
            if (!state) return note("powerLimit", "no state");
            const busy =
                sending || isBusy(state.sustained?.progress) || isBusy(state.boost?.progress);
            const rows: unknown[] = [];
            if (state.canSelectMode && controlRuntime.toggle) {
                rows.push(
                    controlRuntime.react.createElement(controlRuntime.toggle, {
                        key: "mode",
                        label: "Unified TDP",
                        checked: state.unified,
                        controlled: true,
                        disabled: busy,
                        description: accentDescription(
                            controlRuntime,
                            state.modeAccent,
                            error || "Coordinate sustained and boost limits with one target.",
                        ),
                        onChange: (unified) => {
                            if (
                                pending.current ||
                                busy ||
                                typeof unified !== "boolean" ||
                                unified === state.unified
                            )
                                return;
                            pending.current = true;
                            setSending(true);
                            setError("");
                            void sendCommand(definition, definition.modeCommand, { unified })
                                .catch((reason) =>
                                    setError(normalizeText(reason?.message ?? String(reason))),
                                )
                                .finally(() => {
                                    pending.current = false;
                                    setSending(false);
                                });
                        },
                    }),
                );
            }
            for (const [key, label, iconName, range, echo, command] of [
                [
                    "pl1",
                    state.unified ? "TDP" : "Sustained power (PL1)",
                    "bolt",
                    state.sustained,
                    sustainedEcho,
                    definition.primaryCommand,
                ],
                [
                    "pl2",
                    "Boost power (PL2)",
                    "boost",
                    state.boost,
                    boostEcho,
                    definition.boostCommand,
                ],
            ] as const) {
                if (!range || (state.unified && key === "pl2")) continue;
                const commit = (watts) => {
                    if (
                        pending.current ||
                        busy ||
                        !range.available ||
                        !Number.isInteger(watts) ||
                        watts < range.min ||
                        watts > range.max ||
                        (watts - range.min) % range.step !== 0 ||
                        watts === range.observed
                    )
                        return;
                    pending.current = true;
                    setSending(true);
                    setError("");
                    void sendCommand(definition, command, { watts })
                        .catch((reason) =>
                            setError(normalizeText(reason?.message ?? String(reason))),
                        )
                        .finally(() => {
                            pending.current = false;
                            setSending(false);
                        });
                };
                rows.push(
                    controlRuntime.react.createElement(
                        controlRuntime.row,
                        { key },
                        controlRuntime.react.createElement(controlRuntime.slider, {
                            label,
                            icon: controlRuntime.icon(iconName),
                            iconLocation: "front",
                            min: range.min,
                            max: range.max,
                            step: range.step,
                            // No reading sits at the minimum with no number, and sends nothing until moved.
                            value: echo.value ?? range.min,
                            valueSuffix: " W",
                            showValue: echo.value !== null,
                            showBookendLabels: true,
                            disabled: busy || !range.available,
                            description: accentDescription(
                                controlRuntime,
                                range.accent,
                                error ||
                                    (state.unified
                                        ? `Sustained ${state.sustained?.observed ?? "?"} W · Boost ${state.boost?.observed ?? "?"} W`
                                        : range.statusText),
                            ),
                            onChange: echo.onChange,
                            onChangeComplete: (next) => echo.onChangeComplete(next, commit),
                        }),
                    ),
                );
            }
            if (!rows.length) return note("powerLimit", "no usable power limit");
            drew("powerLimit", `rendered ${rows.length} row(s)`);
            summarize(
                "powerLimit",
                state.unified
                    ? `${state.sustained?.observed ?? "?"} W`
                    : `${state.sustained?.observed ?? "?"} W sustained · ${state.boost?.observed ?? "?"} W boost`,
            );
            return controlRuntime.react.createElement(controlRuntime.react.Fragment, null, ...rows);
        };

    const createDeviceControlsControl = (controlRuntime) =>
        // `sections` is the Quick Settings sections of the host's layout, or null without one.
        function SteamUiDeviceControls({ sections }) {
            const state = useSemanticState(
                controlRuntime,
                "deviceControls",
                normalizeDeviceControlsState,
            );
            const definition = definitions.deviceControls;
            // A refusal belongs to the row whose write it answered, and shows there until the next write.
            const [refusal, setRefusal] = controlRuntime.react.useState(null);
            const refusalFor = (command) =>
                refusal && refusal.command === command ? refusal.text : "";
            const send = (command, payload) => {
                setRefusal(null);
                void sendCommand(definition, command, payload).catch((reason) =>
                    setRefusal({ command, text: refusalText(reason) }),
                );
            };
            const queueColorCommit = useTrailingCommit(controlRuntime, 350, ({ zone, color }) =>
                send(definition.colorCommand, { zone, color }),
            );
            const [selectedZone, setSelectedZone] = controlRuntime.react.useState("");
            const [editingColor, setEditingColor] = controlRuntime.react.useState(false);
            const folds = useSemanticState(controlRuntime, "panelFolds", normalizePanelFoldsState);
            const chargeValue = state?.chargeLimit
                ? (state.chargeLimit.observed ?? state.chargeLimit.desired)
                : null;
            const brightnessValue = state?.lightingBrightness
                ? (state.lightingBrightness.observed ?? state.lightingBrightness.desired)
                : null;
            const zones = state?.lightingZones?.filter((zone) => zone.available) ?? [];
            const zone =
                zones.find((candidate) => candidate.id === selectedZone) ?? zones[0] ?? null;
            const color = zone ? (zone.observedColor ?? zone.desiredColor) : null;
            const hsv = color === null ? null : rgbToHsv(color);
            const chargeEcho = useEchoedValue(controlRuntime, chargeValue);
            const brightnessEcho = useEchoedValue(controlRuntime, brightnessValue);
            const hueEcho = useEchoedValue(controlRuntime, hsv?.hue ?? null);
            const saturationEcho = useEchoedValue(controlRuntime, hsv?.saturation ?? null);
            const colorBrightnessEcho = useEchoedValue(controlRuntime, hsv?.brightness ?? null);
            if (!state) return note("deviceControls", "no state");

            const rows: unknown[] = [];
            const appendSlider = (key, properties) => {
                rows.push(
                    controlRuntime.react.createElement(
                        controlRuntime.row,
                        { key },
                        controlRuntime.react.createElement(controlRuntime.slider, properties),
                    ),
                );
            };
            // A range with no reading still draws, at its minimum with no number, and sends nothing until
            // the user moves it.
            if (state.chargeLimit?.available) {
                const range = state.chargeLimit;
                appendSlider("steam-ui-charge-limit", {
                    label: "Battery charge limit",
                    icon: controlRuntime.icon("percent"),
                    iconLocation: "front",
                    min: range.minimum,
                    max: range.maximum,
                    step: range.step,
                    value: chargeEcho.value ?? range.minimum,
                    valueSuffix: "%",
                    showValue: chargeEcho.value !== null,
                    showBookendLabels: true,
                    disabled: isBusy(range.progress),
                    description:
                        refusalFor(definition.chargeCommand) ||
                        accentDescription(controlRuntime, range.accent, range.statusText),
                    onChange: chargeEcho.onChange,
                    onChangeComplete: (next) =>
                        chargeEcho.onChangeComplete(next, (percent) =>
                            send(definition.chargeCommand, { percent }),
                        ),
                });
            }

            const chargingRows = rows.splice(0);
            if (state.lightingBrightness?.available) {
                const range = state.lightingBrightness;
                appendSlider("steam-ui-lighting-brightness", {
                    label: "Lighting brightness",
                    icon: controlRuntime.icon("bulb"),
                    iconLocation: "front",
                    min: range.minimum,
                    max: range.maximum,
                    step: range.step,
                    value: brightnessEcho.value ?? range.minimum,
                    valueSuffix: "%",
                    showValue: brightnessEcho.value !== null,
                    showBookendLabels: true,
                    disabled: isBusy(range.progress),
                    description:
                        refusalFor(definition.brightnessCommand) ||
                        accentDescription(controlRuntime, range.accent, range.statusText),
                    onChange: brightnessEcho.onChange,
                    onChangeComplete: (next) =>
                        brightnessEcho.onChangeComplete(next, (percent) =>
                            send(definition.brightnessCommand, { percent }),
                        ),
                });
            }

            if (zone && hsv && controlRuntime.toggle) {
                rows.push(
                    controlRuntime.react.createElement(
                        controlRuntime.row,
                        { key: "steam-ui-lighting-edit" },
                        controlRuntime.react.createElement(controlRuntime.toggle, {
                            label: "Edit color",
                            icon: controlRuntime.icon("pencil"),
                            // Which zones the host marks, after its accent label, so it shows without opening the
                            // editor, or why the last colour was refused. Plain text, not accented.
                            description:
                                refusalFor(definition.colorCommand) ||
                                (zones.some((candidate) => candidate.accent)
                                    ? [
                                          normalizeText(
                                              acceptedStates.get("quickAccessLayout")?.accentLabel,
                                          ),
                                          zones
                                              .filter((candidate) => candidate.accent)
                                              .map((candidate) => candidate.label)
                                              .join(", "),
                                      ]
                                          .filter(Boolean)
                                          .join(" · ")
                                    : undefined),
                            checked: editingColor,
                            controlled: true,
                            onChange: setEditingColor,
                        }),
                    ),
                );
            }

            if (zone && hsv && controlRuntime.toggle && editingColor) {
                const options = zones.map((candidate) => ({
                    data: candidate.id,
                    label: candidate.label,
                }));
                rows.push(
                    controlRuntime.react.createElement(
                        controlRuntime.row,
                        { key: "steam-ui-lighting-zone" },
                        controlRuntime.react.createElement(controlRuntime.dropdown, {
                            label: "Lighting zone",
                            icon: controlRuntime.icon("zones"),
                            rgOptions: options,
                            selectedOption: zone.id,
                            onChange: (option) => {
                                if (
                                    option &&
                                    zones.some((candidate) => candidate.id === option.data)
                                ) {
                                    setSelectedZone(option.data);
                                }
                            },
                            disabled: options.length < 2,
                            description: accentDescription(
                                controlRuntime,
                                zone.accent,
                                zone.statusText,
                            ),
                            layout: "below",
                        }),
                    ),
                );

                const stagedColor = hsvToRgb(
                    hueEcho.value ?? hsv.hue,
                    saturationEcho.value ?? hsv.saturation,
                    colorBrightnessEcho.value ?? hsv.brightness,
                );
                rows.push(
                    controlRuntime.react.createElement(
                        controlRuntime.row,
                        { key: "steam-ui-lighting-preview" },
                        controlRuntime.react.createElement("div", {
                            title: rgbCss(stagedColor),
                            style: {
                                background: rgbCss(stagedColor),
                                border: "1px solid rgba(255,255,255,.7)",
                                borderRadius: "4px",
                                height: "32px",
                                width: "100%",
                            },
                        }),
                    ),
                );
                const commitColor = (hue, saturation, brightness) =>
                    queueColorCommit({
                        zone: zone.id,
                        color: hsvToRgb(hue, saturation, brightness),
                    });
                appendSlider("steam-ui-lighting-hue", {
                    label: localizeOr(controlRuntime, "#ColorPicker_Hue", "Hue"),
                    icon: controlRuntime.icon("rainbow"),
                    iconLocation: "front",
                    min: 0,
                    max: 360,
                    step: 1,
                    value: hueEcho.value,
                    valueSuffix: "°",
                    showValue: true,
                    disabled: isBusy(zone.progress),
                    trackStyleOverride: {
                        background: "linear-gradient(to right,#f00,#ff0,#0f0,#0ff,#00f,#f0f,#f00)",
                        "--left-track-color": "transparent",
                    },
                    onChange: hueEcho.onChange,
                    onChangeComplete: (next) =>
                        hueEcho.onChangeComplete(next, (hue) =>
                            commitColor(
                                hue,
                                saturationEcho.value ?? hsv.saturation,
                                colorBrightnessEcho.value ?? hsv.brightness,
                            ),
                        ),
                });
                appendSlider("steam-ui-lighting-saturation", {
                    label: localizeOr(controlRuntime, "#ColorPicker_Saturation", "Saturation"),
                    icon: controlRuntime.icon("droplet"),
                    iconLocation: "front",
                    min: 0,
                    max: 100,
                    step: 1,
                    value: saturationEcho.value,
                    valueSuffix: "%",
                    showValue: true,
                    disabled: isBusy(zone.progress),
                    onChange: saturationEcho.onChange,
                    onChangeComplete: (next) =>
                        saturationEcho.onChangeComplete(next, (saturation) =>
                            commitColor(
                                hueEcho.value ?? hsv.hue,
                                saturation,
                                colorBrightnessEcho.value ?? hsv.brightness,
                            ),
                        ),
                });
                appendSlider("steam-ui-lighting-color-brightness", {
                    label: localizeOr(controlRuntime, "#ColorPicker_Brightness", "Brightness"),
                    icon: controlRuntime.icon("contrast"),
                    iconLocation: "front",
                    min: 0,
                    max: 100,
                    step: 1,
                    value: colorBrightnessEcho.value,
                    valueSuffix: "%",
                    showValue: true,
                    disabled: isBusy(zone.progress),
                    onChange: colorBrightnessEcho.onChange,
                    onChangeComplete: (next) =>
                        colorBrightnessEcho.onChangeComplete(next, (brightness) =>
                            commitColor(
                                hueEcho.value ?? hsv.hue,
                                saturationEcho.value ?? hsv.saturation,
                                brightness,
                            ),
                        ),
                });
            }

            if (!rows.length && !chargingRows.length)
                return note("deviceControls", "no compatible charge or lighting rows");
            drew("deviceControls", `rendered ${rows.length + chargingRows.length} row(s)`);
            // Two groups, two detail lines, each under the kind its section names: the charge limit, and
            // the lighting's brightness and zone.
            summarize("charging", chargeValue === null ? "" : `Limit ${chargeValue}%`);
            summarize(
                "lighting",
                [brightnessValue === null ? "" : `${brightnessValue}%`, zone ? zone.label : ""]
                    .filter(Boolean)
                    .join(" · "),
            );
            // Each group draws in the layout's section that names its kind, or untitled without a layout.
            // A layout that names neither leaves the group out.
            const group = (kind, groupRows) => {
                const section = Array.isArray(sections)
                    ? sections.find((candidate) => candidate.kinds.includes(kind))
                    : untitledSection(kind, [kind]);
                return groupRows.length && section
                    ? hostSection(controlRuntime, section, true, groupRows, folds)
                    : null;
            };
            return controlRuntime.react.createElement(
                controlRuntime.react.Fragment,
                null,
                group("charging", chargingRows),
                group("lighting", rows),
            );
        };

    // Steam's own FPS counter rows, which a host whose limiter overlay replaces them hides. Identified by
    // localising the same tokens Steam did rather than by CSS class or visible text: the classes
    // are hashed per client build and the text changes with the user's language, while the token is
    // the one thing that is neither.
    const NativeFpsTokens = [
        "#QuickAccess_Tab_Perf_FPS_Corner",
        "#QuickAccess_Tab_Perf_FPS_Contrast",
    ];
    let filteredNative: { inner: unknown; component: unknown } | null = null;
    // Localized once the runtime answers for at least one token. An empty answer is asked again on
    // the next render, because the localization table can arrive after the panel first draws.
    let nativeFpsLabels: { runtime: unknown; labels: string[] } | null = null;
    // Rows hidden by each render that filters: the root and every component wrapper on the way down
    // count their own, because a wrapper's output exists only once React renders it, after the root
    // has returned. The diagnostic is their sum, written after each of those renders.
    const hiddenByComponent = new Map<unknown, number>();
    const publishHidden = (component, hidden: number) => {
        hiddenByComponent.set(component, hidden);
        if (!appendDiagnostics.perf) return;
        let total = 0;
        for (const count of hiddenByComponent.values()) total += count;
        appendDiagnostics.perf.nativeRowsHidden = total;
    };

    // Wrappers that carry the filter into a component's own render output, cached against the
    // component so React keeps seeing one stable type per original and never remounts the subtree.
    const descendCache = new WeakMap();

    /// Removes the native rows whose label matches one of the tokens above.
    ///
    /// Descends through RENDERED output, not just props.children. The rows sit about ten levels
    /// inside Steam's panel behind component elements, and a component's children do not exist
    /// until React renders it — so a walk over props.children alone reaches nothing, which is why
    /// the filter previously ran and hid zero rows. Each function component met on the way down is
    /// replaced by a wrapper that renders the original and filters what it returns, which is the
    /// same mechanism Decky's createReactTreePatcher uses to reach into this panel.
    const hideNativeRows = (
        controlRuntime,
        element,
        labels,
        depth,
        counted: { hidden: number },
    ) => {
        if (depth > 12 || !controlRuntime.react.isValidElement(element)) return element;

        // Compared as text on both sides: a label is sometimes a localiser element and sometimes a
        // plain string, and matching the raw prop found nothing at all.
        const label = textOf(element.props && element.props.label);
        if (label !== null && labels.includes(label)) {
            counted.hidden++;
            return null;
        }

        // A plain function component renders through a wrapper so its output is filtered too; any
        // other element is filtered through its children, dropping the rows that matched.
        return (
            descendInto(
                controlRuntime.react,
                element,
                descendCache,
                (type) =>
                    function SteamUiDescend(props) {
                        const own = { hidden: 0 };
                        const output = hideNativeRows(controlRuntime, type(props), labels, 0, own);
                        publishHidden(type, own.hidden);
                        return output;
                    },
            ) ??
            mapChildren(controlRuntime.react, element, (kid) =>
                hideNativeRows(controlRuntime, kid, labels, depth + 1, counted),
            )
        );
    };

    /// Wraps Steam's performance root so its OUTPUT can be filtered.
    ///
    /// The root returns a single component element with no static children, so its rows exist only
    /// once React renders it. Calling it from inside a component of our own is what puts its output
    /// in reach; the wrapper is cached against the inner component so React sees a stable type and
    /// does not remount the panel on every render.
    const withNativeRowsHidden = (controlRuntime, tree) => {
        const inner: any = tree && tree.type;
        if (typeof inner !== "function") return tree;
        if (nativeFpsLabels?.runtime !== controlRuntime) {
            const localized = NativeFpsTokens.map((token) =>
                textOf(controlRuntime.localize(token)),
            ).filter((text) => typeof text === "string" && text.length > 0 && text[0] !== "#");
            if (!localized.length) return tree;
            nativeFpsLabels = { runtime: controlRuntime, labels: localized };
        }
        const labels = nativeFpsLabels!.labels;
        if (!filteredNative || filteredNative.inner !== inner) {
            hiddenByComponent.clear();
            filteredNative = {
                inner,
                component: function SteamUiFilteredPerformance(props) {
                    const own = { hidden: 0 };
                    const filtered = hideNativeRows(controlRuntime, inner(props), labels, 0, own);
                    publishHidden(inner, own.hidden);
                    return filtered;
                },
            };
        }

        return controlRuntime.react.createElement(filteredNative.component, tree.props);
    };

    // Valve's own rows take no props, so the only way to put a glyph on one is to render it here and
    // clone what it returned. Calling the component as a plain function makes its hooks this
    // wrapper's hooks, which is safe because the call is unconditional, and is what the native-row
    // filter above already does.
    //
    // Only the overlay-level row is wrapped. It returns Valve's own slider wrapper, which spreads
    // every prop it does not recognize into SliderField and on into Field, so `icon` arrives where
    // a row's icon belongs. The other Valve rows cannot take one this way: the per-game toggle
    // returns a Fragment, which drops any prop but `key`; the reset row is a button rather than a
    // field; and the profile header already draws the game's own capsule art as its icon.
    // The icon is built by the caller rather than named here, so the glyph gate sees this placement
    // as the same `icon("name")` shape as every other one instead of needing a rule of its own.
    const withIcon = (controlRuntime, component, icon) =>
        function SteamUiValveRowWithIcon() {
            const rendered = component({});
            // Only a component element can carry the prop onward. Valve's row is a function that spreads
            // what it does not recognize into SliderField, and that is the whole reason this works; a
            // future build returning a Fragment or a host element would take the icon nowhere and warn
            // on every render instead, so those are handed back untouched.
            return icon &&
                controlRuntime.react.isValidElement(rendered) &&
                typeof rendered.type === "function"
                ? controlRuntime.react.cloneElement(rendered, { icon, iconLocation: "front" })
                : rendered;
        };

    // The host's Quick Access layout (steam-ui.quick-access-layout): each tab's sections, their
    // headings, glyphs and folds, and the row kinds drawn under each. The toolkit holds none of its
    // own. A section without an id or a kind list is skipped rather than costing the whole layout.
    const normalizeQuickAccessSections = (value) => {
        const sections: Readonly<{
            id: string;
            title: string;
            icon: string;
            folds: boolean;
            kinds: readonly string[];
        }>[] = [];
        if (!Array.isArray(value)) return Object.freeze(sections);
        const ids = new Set();
        for (const item of value) {
            const id = normalizeText(item?.id);
            if (!id || ids.has(id) || !Array.isArray(item.kinds)) continue;
            ids.add(id);
            sections.push(
                Object.freeze({
                    id,
                    title: normalizeText(item.title),
                    icon: normalizeText(item.icon),
                    folds: item.folds === true,
                    kinds: Object.freeze(
                        item.kinds.filter((kind) => typeof kind === "string" && kind),
                    ),
                }),
            );
        }
        return Object.freeze(sections);
    };
    const normalizeQuickAccessLayout = (value) =>
        value && typeof value === "object"
            ? Object.freeze({
                  performance: normalizeQuickAccessSections(value.performance),
                  performanceEnd: normalizeQuickAccessSections(value.performanceEnd),
                  quickSettings: normalizeQuickAccessSections(value.quickSettings),
                  quickSettingsEnd: normalizeQuickAccessSections(value.quickSettingsEnd),
                  hideValveFpsRows: value.hideValveFpsRows === true,
                  accentLabel: normalizeText(value.accentLabel),
              })
            : null;
    // Without a layout a tab's rows draw in one untitled group that does not fold.
    const untitledSection = (id, kinds) =>
        Object.freeze({ id, title: "", icon: "", folds: false, kinds: Object.freeze(kinds) });

    // 18px is the size Valve's own header rule gives a section icon, against a 16px header. The glyph
    // is named by the host; a name the kit does not draw leaves the heading without one.
    const sectionIcon = (controlRuntime, name) => controlRuntime.icon(name, 18);

    // What a folded section's heading reports: the summaries of its rows, in the row table's order,
    // then those a row leaves under a kind of the section's own, which is how the device rows report
    // their charging and lighting sections.
    const sectionSummary = (section) =>
        [
            ...new Set([
                ...controlRows.map((row) => row[0]).filter((kind) => section.kinds.includes(kind)),
                ...section.kinds,
            ]),
        ]
            .map((kind) => summaries[kind])
            .filter(Boolean)
            .join(" · ");

    // A section is a kit group: a heading with the section's glyph, its title and, folded, what its
    // rows report, over the rows. A section with no title has no heading, and one the host does not
    // fold stays open. A section whose rows all draw nothing stays mounted, so those rows keep their
    // subscriptions and can bring it back when state arrives; it is only taken out of layout. The
    // fold is kept under the section's id. `folds` is the host's published open list, or null.
    const hostSection = (controlRuntime, section, shown, rows, folds) =>
        !section.title
            ? renderSteamUiGroup(controlRuntime, { key: section.id, hidden: !shown }, ...rows)
            : renderSteamUiGroup(
                  controlRuntime,
                  {
                      key: section.id,
                      title: section.title,
                      icon: sectionIcon(controlRuntime, section.icon),
                      detail: sectionSummary(section) || undefined,
                      hidden: !shown,
                      ...(section.folds
                          ? {
                                collapsed: isFolded(folds, section.id),
                                onToggle: () => setFolded(section.id, !isFolded(folds, section.id)),
                            }
                          : {}),
                  },
                  ...rows,
              );

    // Built once the controls resolve, rather than on every render of the panel.
    let controlRows: any[][] = [];

    // Shape of what Steam's performance root returned, so the rows it renders can be identified
    // without guessing. Needed to suppress Steam's own FPS counter rows in favour of the host's own
    // overlay: their DOM classes are hashed per client build and unusable as selectors.
    const describe = (controlRuntime, element, depth) => {
        if (!controlRuntime.react.isValidElement(element)) return typeof element;
        const t: any = element.type;
        const name = typeof t === "string" ? t : t?.displayName || t?.name || "anonymous";
        const kids = controlRuntime.react.Children.toArray(element.props?.children);
        return depth >= 2 || !kids.length
            ? name
            : { [name]: kids.map((k) => describe(controlRuntime, k, depth + 1)) };
    };

    // `layout` is the host's published layout, or null.
    const appendControls = (
        controlRuntime,
        tree,
        placement = "perf",
        folds = null,
        layout: any = null,
    ) => {
        // Rendered React elements from Steam's own untyped runtime, with the kind each draws.
        const rows: [string, unknown][] = [];
        // Kinds with at least one row that drew. Valve's components report nothing, so theirs count.
        const drawn = new Set<string>();
        for (const [kind, key, component, rowPlacement] of controlRows) {
            if (rowPlacement !== placement || !registrations.has(kind) || !component) continue;
            rows.push([
                kind,
                controlRuntime.react.createElement(
                    controlRuntime.row,
                    { key },
                    controlRuntime.react.createElement(component),
                ),
            ]);
            if (kind.startsWith("valve") || drawnKinds.has(kind)) drawn.add(kind);
        }
        const leading = layout
            ? placement === "perf"
                ? layout.performance
                : layout.quickSettings
            : [
                  untitledSection(
                      "steam-ui-" + placement,
                      rows.map(([kind]) => kind),
                  ),
              ];
        const trailing = layout
            ? placement === "perf"
                ? layout.performanceEnd
                : layout.quickSettingsEnd
            : [];
        // A section draws the rows of its kinds in the row table's order. One with none of them is left
        // out, and a kind no section names is not drawn while a layout is published.
        const sections = (list) =>
            list.flatMap((section) => {
                const own = rows
                    .filter(([kind]) => section.kinds.includes(kind))
                    .map(([, element]) => element);
                return own.length
                    ? [
                          hostSection(
                              controlRuntime,
                              section,
                              section.kinds.some((kind) => drawn.has(kind)),
                              own,
                              folds,
                          ),
                      ]
                    : [];
            });
        const deviceControls =
            placement === "quickSettings" &&
            registrations.has("deviceControls") &&
            deviceControlsControl
                ? controlRuntime.react.createElement(deviceControlsControl, {
                      key: "steam-ui-device-controls",
                      sections: layout
                          ? [...layout.quickSettings, ...layout.quickSettingsEnd]
                          : null,
                  })
                : null;
        const settingsSections =
            placement === "perf" && registrations.has("settingsSections") && settingsSectionsControl
                ? controlRuntime.react.createElement(settingsSectionsControl, {
                      key: "steam-ui-settings-sections",
                  })
                : null;
        const pluginRows = pluginFrontendElements(placement, controlRuntime.react);
        if (!rows.length && !deviceControls && !settingsSections && !pluginRows.length) {
            appendDiagnostics[placement] = { controls: 0, inserted: false, ownSection: false };
            return tree;
        }
        const controls = rows.length + (deviceControls ? 1 : 0) + (settingsSections ? 1 : 0);

        // Quick Settings keeps Valve's common controls intact. The native-row filtering
        // below is about Steam's FPS counter rows on the PERFORMANCE panel; running it against a
        // different tab's tree would be hiding rows this code has never even looked at.
        if (placement === "quickSettings") {
            appendDiagnostics[placement] = {
                controls,
                inserted: true,
                ownSection: true,
            };
            // The host's sections lead the tab rather than trailing it: brightness and the shortcut
            // toggles read below them naturally, and a dropdown at the bottom of a scrolling tab is
            // the control a user finds last. Valve's own sections between are drawn as kit blocks too,
            // so the tab reads as one column of groups. The closing sections and the device controls'
            // own follow Valve's.
            return controlRuntime.react.createElement(
                controlRuntime.react.Fragment,
                null,
                steamUiKitStyle(controlRuntime.react),
                ...sections(leading),
                controlRuntime.react.createElement(
                    "div",
                    { key: "steam-ui-valve-sections", className: "steam-ui-kit-valve" },
                    tree,
                ),
                ...sections(trailing),
                deviceControls,
                ...pluginRows,
            );
        }

        // The host's rows go into titled PanelSections, appended after whatever the native
        // performance panel rendered.
        //
        // The previous implementation searched the tree for a component identical to
        // controlRuntime.section and inserted into it. That could never work, on any OS: `tree` is
        // the ELEMENT returned by performanceRoot(props), and an element's props.children holds only
        // what was passed IN, never what its component produces when React renders it. Steam's
        // section exists only after that rendering, so the walk terminated on a root with no
        // children — measured on the reference device as depthReached 0, sectionSeen false, with the
        // section component itself resolved and all five rows built. It failed silently, which is
        // why an empty Quick Access panel survived so long: every other signal said success.
        //
        // Appending a section instead depends on nothing about Steam's internal tree shape, so it
        // cannot be broken by a Steam UI change or by the fields Windows hides.
        const own = controlRuntime.react.createElement(
            controlRuntime.react.Fragment,
            null,
            ...sections(leading),
        );

        // Steam's FPS rows are hidden only when the host's layout asks, for a host whose own overlay
        // replaces them; otherwise Valve's tree is untouched. What remains of it is the battery line,
        // which the kit draws small under its class.
        const native = controlRuntime.react.createElement(
            "div",
            { key: "steam-ui-native-performance", className: "steam-ui-kit-battery" },
            layout?.hideValveFpsRows === true ? withNativeRowsHidden(controlRuntime, tree) : tree,
        );
        // Described when status asks rather than on every render of the panel.
        let description: string | undefined;
        appendDiagnostics.perf = {
            controls,
            inserted: true,
            ownSection: true,
            get tree() {
                return (description ??= JSON.stringify(describe(controlRuntime, tree, 0)));
            },
            nativeFiltered: native.props.children !== tree,
        };
        return controlRuntime.react.createElement(
            controlRuntime.react.Fragment,
            null,
            steamUiKitStyle(controlRuntime.react),
            native,
            own,
            settingsSections,
            ...pluginRows,
            // The closing sections follow every other one, including dynamically published host
            // settings sections.
            ...sections(trailing),
        );
    };
    // Resolve every dependency before changing React or registering a component.
    const resolveControls = () => {
        runtime = getWebpackRuntime("native-components");
        const performanceFactory = runtime.findUnique([
            "#QuickAccess_Tab_Perf_Common_Settings",
            "#QuickAccess_Tab_Perf_BatteryTimeRemaining",
            "TS.ON_FRAME",
        ]);
        controlRuntime = createControlRuntime();
        if (!performanceFactory) {
            lastPatchError = "performance panel factory was not a unique match";
            return false;
        }
        if (!controlRuntime) {
            lastPatchError = "React, fields, layout or localization runtime was not a unique match";
            return false;
        }
        performanceRoot = uniqueFunction(runtime(performanceFactory[0]), ["TS.ON_FRAME", "return"]);
        if (!performanceRoot) {
            lastPatchError = "performance panel root was not a unique match";
            return false;
        }
        autoTdpControl = createAutoTdpControl(controlRuntime);
        frameLimitControl = createFrameLimitControl(controlRuntime);
        controllerControl = createControllerControl(controlRuntime);
        powerProfileControl = createPowerProfileControl(controlRuntime);
        hybridCoreControl = createHybridCoreControl(controlRuntime);
        cpuBoostControl = createCpuBoostControl(controlRuntime);
        powerPresetControl = createPowerPresetControl(controlRuntime);
        resolutionControl = createResolutionControl(controlRuntime);
        audioFormatControl = createAudioFormatControl(controlRuntime);
        let settingsRuntime: ReturnType<typeof resolveSteamSettingsComponents> = null;
        try {
            settingsRuntime = resolveSteamSettingsComponents(runtime);
        } catch {
            // Host settings need more native components than the independent QAM controls do.
        }
        settingsSectionsControl = createSettingsSectionsControl(controlRuntime, settingsRuntime);
        vrrControl = createVrrControl(controlRuntime);
        deviceControlsControl = createDeviceControlsControl(controlRuntime);
        powerLimitControl = createPowerLimitControl(controlRuntime);

        // Selected by the localization token it draws, never by a minified export name: the names are
        // right for today's build and are not guaranteed for the next. Live-probed 2026-08-30 that
        // this token matches exactly one export of the components module.
        const perfComponents = runtime.findUnique([
            "#QuickAccess_Tab_Perf_EnableVRR",
            "#QuickAccess_Tab_Perf_LimitFrameRate",
        ]);
        const perfExports = perfComponents ? runtime(perfComponents[0]) : null;
        valveProfileHeaderControl = perfExports
            ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_GameSpecificSettings"])
            : null;
        // The toggle reads current_game_id for availability, current==active for its checked state,
        // and writes through SetGameSpecificProfileEnabled — all state the host already backs. Without
        // this row nothing in the tab can enable a per-game profile.
        valveProfileToggleControl = perfExports
            ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_ToggleGameSettings"])
            : null;
        valveResetControl = perfExports
            ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_ResetToDefault"])
            : null;
        valveRefreshRateControl = perfExports
            ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_RefreshRate"])
            : null;
        const valveOverlayLevel = perfExports
            ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_Overlay_Level"])
            : null;
        valveOverlayLevelControl = valveOverlayLevel
            ? withIcon(controlRuntime, valveOverlayLevel, controlRuntime.icon("layers"))
            : null;

        // Registration, component and placement share one table. The host's layout decides the
        // sections; this table determines the order of controls within each section.
        controlRows = [
            [
                "valveProfileHeader",
                "steam-ui-valve-profile-header",
                valveProfileHeaderControl,
                "perf",
            ],
            [
                "valveProfileHeader",
                "steam-ui-valve-profile-toggle",
                valveProfileToggleControl,
                "perf",
            ],
            ["valveOverlayLevel", "steam-ui-valve-overlay-level", valveOverlayLevelControl, "perf"],
            ["frameLimit", "steam-ui-frame-limit", frameLimitControl, "perf"],
            ["powerProfile", "steam-ui-power-profile", powerProfileControl, "perf"],
            ["hybridCores", "steam-ui-hybrid-cores", hybridCoreControl, "perf"],
            ["cpuBoost", "steam-ui-cpu-boost", cpuBoostControl, "perf"],
            ["powerPreset", "steam-ui-power-preset", powerPresetControl, "perf"],
            ["vrr", "steam-ui-vrr", vrrControl, "perf"],
            ["powerLimit", "steam-ui-power-limits", powerLimitControl, "perf"],
            ["autoTdp", "steam-ui-auto-tdp", autoTdpControl, "perf"],
            ["resolution", "steam-ui-resolution", resolutionControl, "quickSettings"],
            ["audioFormat", "steam-ui-audio-format", audioFormatControl, "quickSettings"],
            [
                "valveRefreshRate",
                "steam-ui-valve-refresh-rate",
                valveRefreshRateControl,
                "quickSettings",
            ],
            ["controllerTarget", "steam-ui-controller-target", controllerControl, "perf"],
            ["valveReset", "steam-ui-valve-reset", valveResetControl, "perf"],
        ];

        return true;
    };

    const ensurePatched = () => {
        if (controlRuntime && performanceRoot && memoIntercepted(controlRuntime.react, MemoName))
            return true;
        try {
            if (!resolveControls()) return false;
        } catch (error) {
            lastPatchError = "native component runtime resolution failed: " + String(error);
            return false;
        }

        function SteamUiPerformanceRoot(props) {
            const [, setRevision] = controlRuntime.react.useState(0);
            controlRuntime.react.useEffect(
                () => subscribeHost(() => setRevision((value) => value + 1)),
                [],
            );
            const folds = useSemanticState(controlRuntime, "panelFolds", normalizePanelFoldsState);
            const layout = useSemanticState(
                controlRuntime,
                "quickAccessLayout",
                normalizeQuickAccessLayout,
            );
            return appendControls(controlRuntime, performanceRoot(props), "perf", folds, layout);
        }

        // One wrapper per wrapped tab, matched by root identity in the same memoized tab array.
        // Each root must match exactly once or it is left alone — the discipline that kept the
        // performance wrap honest, applied per root rather than to the array as a whole.
        // The performance panel is matched by export identity; the Quick Settings panel CANNOT be —
        // a tap on the tab array (2026-08-30) showed its type is a local function no module exports.
        // It is matched by its own source instead, on two Valve strings the host's gates never touch: the
        // Other-section title and the reorder-controllers button. Deliberately NOT the brightness
        // title, because that is the surface the host's own gate reveals, and a selector must not be
        // entangled with a thing this code changes.
        const wrappers = [
            {
                match: (type) => type === performanceRoot,
                component: () => SteamUiPerformanceRoot,
                fallbackKey: "steam-ui-performance-root",
            },
            {
                match: (type) =>
                    type !== performanceRoot &&
                    sourceMatches(type, [
                        "#QuickAccess_Tab_Settings_Section_Other_Title",
                        "#QuickAccess_ReorderControllers_Button",
                    ]),
                // The original is only known at match time, so the wrapper is built then — and cached by
                // original, because a fresh component identity on every memo pass would remount the whole
                // tab on each render.
                component: (original) => {
                    let wrapped = quickSettingsWrapCache.get(original);
                    if (!wrapped) {
                        wrapped = function SteamUiQuickSettingsRoot(props) {
                            const [, setRevision] = controlRuntime.react.useState(0);
                            controlRuntime.react.useEffect(
                                () => subscribeHost(() => setRevision((value) => value + 1)),
                                [],
                            );
                            quickSettingsRoot = original;
                            const folds = useSemanticState(
                                controlRuntime,
                                "panelFolds",
                                normalizePanelFoldsState,
                            );
                            const layout = useSemanticState(
                                controlRuntime,
                                "quickAccessLayout",
                                normalizeQuickAccessLayout,
                            );
                            return appendControls(
                                controlRuntime,
                                original(props),
                                "quickSettings",
                                folds,
                                layout,
                            );
                        };
                        quickSettingsWrapCache.set(original, wrapped);
                    }
                    return wrapped;
                },
                fallbackKey: "steam-ui-quick-settings-root",
            },
        ];
        // The tab array passes through the one useMemo claim every surface shares (ownership.ts).
        const transformTabs = (value) => {
            // Every useMemo result in the client passes through here. A tab list holds tab objects, so an
            // empty array, or one that starts with a string or number, is answered before any filtering.
            if (!Array.isArray(value) || !value.length) return value;
            if (typeof value[0] === "string" || typeof value[0] === "number") return value;
            // Many memoized arrays hold objects too; only one with a tab's panel element is worth copying.
            let tabs = false;
            for (let index = 0; index < value.length && !tabs; index++) {
                const item = value[index];
                tabs =
                    !!item &&
                    typeof item === "object" &&
                    controlRuntime.react.isValidElement(item.panel);
            }
            if (!tabs) return value;
            let result = value;
            for (const wrapper of wrappers) {
                const matches = result.filter(
                    (item) =>
                        item &&
                        typeof item === "object" &&
                        controlRuntime.react.isValidElement(item.panel) &&
                        wrapper.match(item.panel.type),
                );
                if (matches.length !== 1) continue;
                result = result.map((item) => {
                    if (item !== matches[0]) return item;
                    const panel = controlRuntime.react.createElement(
                        wrapper.component(item.panel.type),
                        {
                            ...item.panel.props,
                            key: item.panel.key ?? wrapper.fallbackKey,
                        },
                    );
                    return { ...item, panel };
                });
            }
            return result;
        };
        const intercepted = interceptMemo(controlRuntime.react, MemoName, transformTabs);
        if (!intercepted.ok) {
            lastPatchError = intercepted.error || "React useMemo wrapper could not be installed";
            return false;
        }
        lastPatchError = "";
        return true;
    };
    const install = (kind) => {
        unsubscribePlugins ??= subscribePluginFrontends(() => notify());
        if (disposedHost) return { ok: false, error: "component host disposed" };
        if (!Object.hasOwn(definitions, kind))
            return { ok: false, error: "component is not allowlisted" };
        if (!ensurePatched())
            return {
                ok: false,
                error: lastPatchError || "native performance root is incompatible",
            };
        registrations.set(kind, definitions[kind].patchId);
        notify();
        return { ok: true, kind, registered: true, hostVersion: 1 };
    };
    const remove = (kind) => {
        if (!Object.hasOwn(definitions, kind)) return { ok: true, absent: true };
        registrations.delete(kind);
        notify();
        if (!registrations.size && controlRuntime) {
            releaseMemo(controlRuntime.react, MemoName);
        }
        return { ok: true, kind, registered: false };
    };
    const status = (kind) => ({
        ok: Object.hasOwn(definitions, kind),
        kind,
        registered: registrations.has(kind),
        hostVersion: 1,
        performanceRootWrapped: !!controlRuntime && memoIntercepted(controlRuntime.react, MemoName),
        // Everything above can be true while the panel still shows nothing, because insertion
        // depends on the shape of the tree Steam renders. This is the part that says so.
        lastAppend: appendDiagnostics.perf,
        lastAppendQuickSettings: appendDiagnostics.quickSettings,
        quickSettingsRootResolved: !!quickSettingsRoot,
        // And this says which rows drew, and why the others did not.
        renderOutcomes,
        toggleResolved: !!(controlRuntime && controlRuntime.toggle),
        lastError: lastPatchError,
    });
    const disposeHostResources = () => {
        unsubscribePlugins?.();
        disposedHost = true;
        registrations.clear();
        notify();
        listeners.clear();
        if (controlRuntime) releaseMemo(controlRuntime.react, MemoName);
    };
    return { install, remove, status, dispose: disposeHostResources };
}

registerGate("nativeComponents", createNativeComponentHost());
