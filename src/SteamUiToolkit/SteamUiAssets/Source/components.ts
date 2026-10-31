/**
 * Owns every host row in Steam's Quick Access Performance and Quick Settings tabs: the host's rows
 * drawn from SteamSettingsRow descriptors, the few bespoke controls, and Valve's own components.
 * @returns The defineGate gate; remove releases the shared tab claim and every subscription.
 */
function createNativeComponentHost() {
    // The publications this host draws from. Rows, the frame-limit row and the lighting editor are
    // drawn while their state is published; Valve's components while the performance state is, since
    // that is what they read. The layout places them, and the folds keep its sections closed or open.
    const RowsPatchId = "steam-ui.quick-access-rows";
    const PerformancePatchId = "steam-ui.performance";
    const FrameLimitPatchId = "steam-ui.frame-limit";
    const LightingZonesPatchId = "steam-ui.lighting-zones";
    const LayoutPatchId = "steam-ui.quick-access-layout";
    // This host's name on the shared useMemo claim (ownership.ts).
    const MemoName = "quickAccessTabs";

    const listeners = new Set<() => void>();
    // What bind built from the handles find resolved, or null while the gate is not installed. The
    // wrapped tab roots read it on every render, so a removed host draws Valve's tab untouched.
    let bound: any = null;
    // The Quick Settings panel Steam rendered, captured at render time. S14 puts resolution and
    // refresh rate in Quick Settings, not Performance — but the panel is a LOCAL function of the
    // tabs module, not an export, so it is only ever known once the tab array passes through the
    // patched memo. Null means it has not been seen yet, which the status reports.
    let quickSettingsRoot = null;

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

    // What a folded section says on its heading's detail line. A bespoke control with a value worth a
    // glance leaves it here as it renders, a host row group by its rows' `summary`, and the section
    // joins them. Rows stay mounted while their section is folded, so the line stays current.
    const summaries: Record<string, string> = {};
    const summarize = (kind, text) => {
        summaries[kind] = typeof text === "string" ? text : "";
    };

    // Which bespoke controls drew something on their last render. A section header exists for the
    // rows under it, so a section whose rows all returned null is only a title. Valve's rows report
    // nothing here and count as drawn while they are mounted; a host row group counts as drawn while
    // it has rows.
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
        // "no state" is what every render sees while a delivery is being rejected, so the generic
        // reason must not overwrite the precise one the subscription recorded.
        if (
            reason === "no state" &&
            renderOutcomes[kind] === "state received but rejected by validation"
        ) {
            return null;
        }

        renderOutcomes[kind] = reason;
        return null;
    };

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
    // Every command carries a fresh action generation, which request allocates, so its echo can be
    // matched to the write.
    const sendCommand = (patchId, command, payload) => request(patchId, command, payload);
    // A refused write's reason, whole, for the row's description.
    const refusalText = (reason) => normalizeText(String(reason?.message ?? reason));
    // The sections' folds, the mechanism every Quick Access tab shares (gate-helpers.ts).
    const panelFolds = createSteamFolds();
    const isFolded = (open, id) => panelFolds.isFolded(open, id);
    const setFolded = (id, folded) => panelFolds.setFolded(id, folded, notify);
    // The one function export carrying every token. Through the shared matcher, so an export Steam
    // aliases under two names counts once and a getter that throws counts as no match.
    const uniqueFunction = (exports, requiredTokens) =>
        uniqueSteamExport(
            exports,
            (value) =>
                typeof value === "function" &&
                requiredTokens.every((token) => String(value).includes(token)),
        );
    const normalizeText = (value) => (typeof value === "string" ? value : "");
    // The last state each publication delivered, normalized, keyed by the names below. A row mounted
    // again when the panel reopens starts from it, so it draws on its first render.
    const acceptedStates = new Map();
    // A row the host marks says so in its own description, in Steam's accent colour, led by the label the
    // host's layout publishes (steam-ui.quick-access-layout). The toolkit holds no word of its own for it.
    const accentDescription = (controlRuntime, accent, text) => {
        const label = accent ? normalizeText(acceptedStates.get("layout")?.accentLabel) : "";
        return steamAccentDescription(
            controlRuntime.react,
            [label, text].filter(Boolean).join(" · "),
            accent === true,
        );
    };

    // The host's rows: groups of SteamSettingsRow descriptors, each placed by the layout section that
    // names its id. Validated rather than trusted, one group and one row at a time, so a malformed
    // entry costs itself rather than the whole publication.
    const rowsOf = (rows) =>
        Object.freeze(
            Array.isArray(rows)
                ? rows.filter(
                      (row) =>
                          !!row &&
                          typeof row === "object" &&
                          typeof row.key === "string" &&
                          row.key.length > 0 &&
                          typeof row.kind === "string",
                  )
                : [],
        );
    const normalizeRowsState = (value) => {
        if (
            !value ||
            typeof value !== "object" ||
            !Array.isArray(value.sections) ||
            !Number.isSafeInteger(value.revision)
        )
            return null;
        const ids = new Set();
        const sections: Readonly<{
            id: string;
            title: string;
            glyph: string;
            rows: readonly any[];
            groups: readonly Readonly<{ id: string; title: string; rows: readonly any[] }>[];
        }>[] = [];
        for (const item of value.sections) {
            const id = normalizeText(item?.id);
            if (!id || ids.has(id)) continue;
            ids.add(id);
            const groups = Array.isArray(item.groups) ? item.groups : [];
            sections.push(
                Object.freeze({
                    id,
                    title: normalizeText(item.title),
                    glyph: normalizeText(item.glyph),
                    rows: rowsOf(item.rows),
                    groups: Object.freeze(
                        groups
                            .filter((group) => !!group && typeof group === "object")
                            .map((group, index) =>
                                Object.freeze({
                                    id: normalizeText(group.id) || String(index),
                                    title: normalizeText(group.title),
                                    rows: rowsOf(group.rows),
                                }),
                            ),
                    ),
                }),
            );
        }
        return Object.freeze({ revision: value.revision, sections: Object.freeze(sections) });
    };
    // Every row of a group, its sub-groups' included.
    const rowsOfSection = (section) => [
        ...section.rows,
        ...section.groups.flatMap((group) => group.rows),
    ];
    // What a host row group says on a folded heading: its rows' summaries, in order.
    const rowsSummary = (section) =>
        rowsOfSection(section)
            .map((row) => normalizeText(row.summary))
            .filter(Boolean)
            .join(" · ");
    const publishedSection = (id) =>
        acceptedStates.get("rows")?.sections.find((section) => section.id === id) ?? null;

    // The lighting editor's zones. A zone with a malformed id, label or colour rejects the state, as
    // a dropdown whose entries select nothing would otherwise draw.
    const normalizeLightingZonesState = (value) => {
        if (!value || typeof value !== "object" || !Array.isArray(value.zones)) return null;
        const zones: Readonly<{
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
        const colorOf = (color) => (color === null || color === undefined ? null : Number(color));
        const invalidColor = (color) =>
            color !== null && (!Number.isInteger(color) || color < 0 || color > 0xffffff);
        for (const zone of value.zones) {
            if (!zone || typeof zone !== "object") return null;
            const id = normalizeText(zone.id);
            const label = normalizeText(zone.label);
            const desiredColor = colorOf(zone.desiredColor);
            const observedColor = colorOf(zone.observedColor);
            if (
                !id.trim() ||
                !label ||
                ids.has(id) ||
                invalidColor(desiredColor) ||
                invalidColor(observedColor)
            )
                return null;
            ids.add(id);
            zones.push(
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
        return Object.freeze({ zones: Object.freeze(zones) });
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
            const state = acceptedStates.get("frameLimit") ?? null;
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
            // A refused write shows why under the slider until the next write.
            const send = (command, nextValue) => {
                setRefusal("");
                void sendCommand(FrameLimitPatchId, command, { value: nextValue }).catch((reason) =>
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
                send("setFrameLimit", nextValue);
            };
            // Takes a NOTCH INDEX, not a rate: the refresh mode is a notch slider, so what the control
            // hands back is a position in the accepted list.
            const setRefresh = (notchIndex) => {
                const hz = state.refreshRates[notchIndex];
                if (!Number.isInteger(hz)) return;
                send("setRefreshRate", hz);
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
                      onChange: (next) => send("setFrameLimit", next ? 0 : cappedValue),
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

    // The inline lighting editor: a switch that opens it, then the zone, a preview of the colour being
    // staged, and hue, saturation and value sliders. Each element lays out its own panel row.
    const createLightingZonesControl = (controlRuntime) =>
        function SteamUiLightingZones() {
            const state = acceptedStates.get("lightingZones") ?? null;
            // A refusal shows on the editor's switch until the next write.
            const [refusal, setRefusal] = controlRuntime.react.useState("");
            const queueColorCommit = useTrailingCommit(controlRuntime, 350, ({ zone, color }) => {
                setRefusal("");
                void sendCommand(LightingZonesPatchId, "setLightingColor", { zone, color }).catch(
                    (reason) => setRefusal(refusalText(reason)),
                );
            });
            const [selectedZone, setSelectedZone] = controlRuntime.react.useState("");
            const [editingColor, setEditingColor] = controlRuntime.react.useState(false);
            const zones = state?.zones?.filter((zone) => zone.available) ?? [];
            const zone =
                zones.find((candidate) => candidate.id === selectedZone) ?? zones[0] ?? null;
            const color = zone ? (zone.observedColor ?? zone.desiredColor) : null;
            const hsv = color === null ? null : rgbToHsv(color);
            // Unconditional, ahead of every early return: these are hooks.
            const hueEcho = useEchoedValue(controlRuntime, hsv?.hue ?? null);
            const saturationEcho = useEchoedValue(controlRuntime, hsv?.saturation ?? null);
            const colorBrightnessEcho = useEchoedValue(controlRuntime, hsv?.brightness ?? null);
            if (!state) return note("lightingZones", "no state");
            if (!zone || !hsv) return note("lightingZones", "no available lighting zone");
            if (!controlRuntime.toggle)
                return note("lightingZones", "Steam ToggleField was not resolved");
            drew("lightingZones");
            summarize("lightingZones", zone.label);

            const rows: unknown[] = [];
            const inRow = (key, element) =>
                rows.push(controlRuntime.react.createElement(controlRuntime.row, { key }, element));
            const slider = (key, properties) =>
                inRow(key, controlRuntime.react.createElement(controlRuntime.slider, properties));
            inRow(
                "steam-ui-lighting-edit",
                controlRuntime.react.createElement(controlRuntime.toggle, {
                    label: "Edit color",
                    icon: controlRuntime.icon("pencil"),
                    // Which zones the host marks, after its accent label, so it shows without opening the
                    // editor, or why the last colour was refused. Plain text, not accented.
                    description:
                        refusal ||
                        (zones.some((candidate) => candidate.accent)
                            ? [
                                  normalizeText(acceptedStates.get("layout")?.accentLabel),
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
            );
            if (editingColor) {
                const options = zones.map((candidate) => ({
                    data: candidate.id,
                    label: candidate.label,
                }));
                inRow(
                    "steam-ui-lighting-zone",
                    controlRuntime.react.createElement(controlRuntime.dropdown, {
                        label: "Lighting zone",
                        icon: controlRuntime.icon("zones"),
                        rgOptions: options,
                        selectedOption: zone.id,
                        onChange: (option) => {
                            if (option && zones.some((candidate) => candidate.id === option.data)) {
                                setSelectedZone(option.data);
                            }
                        },
                        disabled: options.length < 2,
                        description: accentDescription(controlRuntime, zone.accent, zone.statusText),
                        layout: "below",
                    }),
                );
                const stagedColor = hsvToRgb(
                    hueEcho.value ?? hsv.hue,
                    saturationEcho.value ?? hsv.saturation,
                    colorBrightnessEcho.value ?? hsv.brightness,
                );
                inRow(
                    "steam-ui-lighting-preview",
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
                );
                const commitColor = (hue, saturation, brightness) =>
                    queueColorCommit({ zone: zone.id, color: hsvToRgb(hue, saturation, brightness) });
                slider("steam-ui-lighting-hue", {
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
                slider("steam-ui-lighting-saturation", {
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
                slider("steam-ui-lighting-color-brightness", {
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
            return controlRuntime.react.createElement(controlRuntime.react.Fragment, null, ...rows);
        };

    // The Steam field each row kind draws with, beyond React. A row whose field this client did not
    // resolve is left out and named in the render outcomes, never drawn as an unresolved component.
    const RowFields = Object.freeze({
        boolean: ["toggleField"],
        choice: ["dropdown"],
        range: ["sliderField"],
        text: ["textField"],
        secret: ["textField"],
        note: ["valueField"],
        action: ["valueField", "dialogButton"],
        order: ["valueField", "smallButton"],
        color: ["valueField", "smallButton", "textField"],
        curve: ["valueField", "sliderField"],
        "resolution-list": ["valueField", "dialogButton"],
    });
    const missingRowField = (ui, kind) =>
        (Object.hasOwn(RowFields, kind) ? RowFields[kind] : ["valueField"]).find(
            (name) => !ui[name],
        ) ?? null;

    // One host row group: its rows through the settings renderer, in Steam's panel rows, then its
    // sub-groups. The settings pages' own draft keeping: a refused write shows why on its row, and a
    // publication that changes another row keeps what is being edited here.
    const createRowSectionControl = (controlRuntime, ui) =>
        function SteamUiQuickAccessRows({ id }) {
            const react = controlRuntime.react;
            const state = acceptedStates.get("rows") ?? null;
            const drafts = useSteamSettingDrafts(react, state?.revision);
            const section = publishedSection(id);
            const kind = "rows:" + id;
            if (!section) return note(kind, "no state");
            const change = drafts.change((row, value) =>
                sendCommand(RowsPatchId, "set", { key: row.key, value }),
            );
            const action = (row) => change(row, true);
            let drawn = 0;
            const draw = (row) => {
                const missing = missingRowField(ui, row.kind);
                if (missing) {
                    renderOutcomes["row " + row.key] = `Steam ${missing} was not resolved`;
                    return null;
                }
                drawn += 1;
                return react.createElement(
                    controlRuntime.row,
                    { key: row.key },
                    renderSteamSettingRow(
                        ui,
                        drafts.row({ ...row, layout: "below" }),
                        drafts.draft(row),
                        change,
                        action,
                    ),
                );
            };
            const rows = section.rows.map(draw);
            const groups = section.groups.map((group) =>
                renderSteamUiGroup(
                    controlRuntime,
                    { key: id + "." + group.id, title: group.title || undefined },
                    ...group.rows.map(draw),
                ),
            );
            renderOutcomes[kind] = `rendered ${drawn} row(s)`;
            return react.createElement(react.Fragment, null, ...rows, ...groups);
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

    // What a folded section's heading reports: the summary each of its kinds leaves, in the order the
    // section names them: a bespoke control's own, and a host row group's rows' summaries.
    const sectionSummary = (section) =>
        [...new Set<string>(section.kinds)]
            .flatMap((kind) => {
                const group = publishedSection(kind);
                return [summaries[kind], group ? rowsSummary(group) : ""];
            })
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

    // Every layout kind any of the layout's sections names, on either tab.
    const namedKinds = (layout) =>
        new Set<string>(
            layout
                ? [
                      ...layout.performance,
                      ...layout.performanceEnd,
                      ...layout.quickSettings,
                      ...layout.quickSettingsEnd,
                  ].flatMap((section) => section.kinds)
                : [],
        );

    // A host row group no layout section names but that carries its own title: drawn as a section of
    // its own on the Performance tab, under its own glyph, folding under its own id.
    const titledSection = (controls, section, folds) => {
        const { controlRuntime } = controls;
        const summary = rowsSummary(section);
        return renderSteamUiGroup(
            controlRuntime,
            {
                key: section.id,
                title: section.title,
                icon: section.glyph ? renderSteamGlyph(controlRuntime.react, section.glyph) : undefined,
                detail: summary || undefined,
                collapsed: isFolded(folds, section.id),
                onToggle: () => setFolded(section.id, !isFolded(folds, section.id)),
            },
            controlRuntime.react.createElement(controls.rowSection, {
                key: "steam-ui-rows-" + section.id,
                id: section.id,
            }),
        );
    };

    // Draws the host's rows into one tab's tree. Rows come from the bespoke table and the host's row
    // groups, each under the layout kind it is placed by; the layout's sections draw them in the order
    // they name their kinds.
    const appendControls = (controls, tree, placement = "perf") => {
        const { controlRuntime, bespoke, rowSection } = controls;
        const h = controlRuntime.react.createElement;
        const layout = acceptedStates.get("layout") ?? null;
        const folds = acceptedStates.get("folds") ?? null;
        const rowsState = acceptedStates.get("rows") ?? null;
        const named = namedKinds(layout);
        // Rendered React elements from Steam's own untyped runtime, with the kind each draws.
        const rows: [string, unknown][] = [];
        // Kinds with at least one row that drew.
        const drawn = new Set<string>();
        for (const [kind, key, tab, component, publication, ownRows] of bespoke) {
            if (tab !== placement || !component || !acceptedStates.get(publication)) continue;
            rows.push([
                kind,
                ownRows
                    ? h(component, { key })
                    : h(controlRuntime.row, { key }, h(component)),
            ]);
            // Valve's components report nothing and count as drawn while they are mounted.
            if (kind.startsWith("valve") || drawnKinds.has(kind)) drawn.add(kind);
        }
        // A group the layout names is placed wherever it is named; without a layout the untitled ones
        // join the Performance tab's group, and the titled ones head sections of their own.
        for (const section of rowsState?.sections ?? []) {
            if (!rowsOfSection(section).length) continue;
            if (layout ? !named.has(section.id) : placement !== "perf" || section.title) continue;
            rows.push([section.id, h(rowSection, { key: "steam-ui-rows-" + section.id, id: section.id })]);
            drawn.add(section.id);
        }
        const titled =
            placement === "perf"
                ? (rowsState?.sections ?? []).filter(
                      (section) =>
                          section.title && !named.has(section.id) && rowsOfSection(section).length,
                  )
                : [];
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
        // A section draws its rows in the order it names their kinds; rows sharing a kind keep the row
        // table's order. One with none of them is left out, and a kind no section names is not drawn
        // while a layout is published.
        const sections = (list) =>
            list.flatMap((section) => {
                const own = [...new Set<string>(section.kinds)].flatMap((kind) =>
                    rows.filter(([rowKind]) => rowKind === kind).map(([, element]) => element),
                );
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
        const pluginRows = pluginFrontendElements(placement, controlRuntime.react);
        if (!rows.length && !titled.length && !pluginRows.length) {
            appendDiagnostics[placement] = { controls: 0, inserted: false, ownSection: false };
            return tree;
        }
        const count = rows.length + titled.length;

        // Quick Settings keeps Valve's common controls intact. The native-row filtering
        // below is about Steam's FPS counter rows on the PERFORMANCE panel; running it against a
        // different tab's tree would be hiding rows this code has never even looked at.
        if (placement === "quickSettings") {
            appendDiagnostics[placement] = { controls: count, inserted: true, ownSection: true };
            // The host's sections lead the tab rather than trailing it: brightness and the shortcut
            // toggles read below them naturally, and a dropdown at the bottom of a scrolling tab is
            // the control a user finds last. Valve's own sections between are drawn as kit blocks too,
            // so the tab reads as one column of groups. The closing sections follow Valve's.
            return h(
                controlRuntime.react.Fragment,
                null,
                steamUiKitStyle(controlRuntime.react),
                ...sections(leading),
                h("div", { key: "steam-ui-valve-sections", className: "steam-ui-kit-valve" }, tree),
                ...sections(trailing),
                ...pluginRows,
            );
        }

        // The host's rows go into titled kit sections appended after whatever the native performance
        // panel rendered. `tree` is the ELEMENT the performance root returned, and an element's
        // children hold only what was passed in, never what its component produces when React renders
        // it, so nothing is inserted into Steam's own section: appending depends on nothing about
        // Steam's internal tree shape, so it cannot be broken by a Steam UI change or by the fields
        // Windows hides.
        const own = h(controlRuntime.react.Fragment, null, ...sections(leading));

        // Steam's FPS rows are hidden only when the host's layout asks, for a host whose own overlay
        // replaces them; otherwise Valve's tree is untouched. What remains of it is the battery line,
        // which the kit draws small under its class.
        const native = h(
            "div",
            { key: "steam-ui-native-performance", className: "steam-ui-kit-battery" },
            layout?.hideValveFpsRows === true ? withNativeRowsHidden(controlRuntime, tree) : tree,
        );
        // Described when status asks rather than on every render of the panel.
        let description: string | undefined;
        appendDiagnostics.perf = {
            controls: count,
            inserted: true,
            ownSection: true,
            get tree() {
                return (description ??= JSON.stringify(describe(controlRuntime, tree, 0)));
            },
            nativeFiltered: native.props.children !== tree,
        };
        return h(
            controlRuntime.react.Fragment,
            null,
            steamUiKitStyle(controlRuntime.react),
            native,
            own,
            ...titled.map((section) => titledSection(controls, section, folds)),
            ...pluginRows,
            // The closing sections follow every other one, including the host's own titled groups and
            // the plugins' rows, so Valve's reset stays the last control.
            ...sections(trailing),
        );
    };

    // The performance panel's factory, by tokens only it carries.
    const PerformanceRootTokens = [
        "#QuickAccess_Tab_Perf_Common_Settings",
        "#QuickAccess_Tab_Perf_BatteryTimeRemaining",
        "TS.ON_FRAME",
    ];
    // Valve's own components, from the perf-components module, selected by the localization token each
    // draws, never by a minified export name. Live-probed 2026-08-30 that each token matches exactly
    // one export of that module, and 2026-09-02 that the per-game toggle is an export of its own.
    const PerfComponentTokens = ["#QuickAccess_Tab_Perf_EnableVRR", "#QuickAccess_Tab_Perf_LimitFrameRate"];

    // Pure: resolves every module the host draws with and writes nothing. A module that is missing or
    // matches twice is a client this host does not support; Valve's components and the settings
    // fields are wanted, not required, so a client without one keeps every row that does not need it.
    const find = (runtime) => {
        const performanceFactory = runtime.findUnique(PerformanceRootTokens);
        if (!performanceFactory)
            return gateIncompatible("performance panel factory was not a unique match");
        const fields = resolveSteamFieldComponents(runtime);
        const panel = resolveSteamPanelComponents(runtime);
        const localize = resolveSteamLocalizer(runtime);
        if (!fields || !panel || !localize || !fields.sliderField || !fields.dropdown)
            return gateIncompatible(
                "React, fields, layout or localization runtime was not a unique match",
            );
        const performanceRoot = uniqueFunction(runtime(performanceFactory[0]), [
            "TS.ON_FRAME",
            "return",
        ]);
        if (!performanceRoot) return gateIncompatible("performance panel root was not a unique match");
        // Steam's Focusable, for the kit's folding section headings. Not required: without it a
        // heading is a plain div and the sections simply do not fold.
        let focusable = null;
        try {
            focusable = resolveNativeFocusable(runtime);
        } catch {
            focusable = null;
        }
        // The settings renderer's fields: a client without them keeps the rows that need only the
        // toggle, dropdown and slider.
        let settingsUi: ReturnType<typeof resolveSteamSettingsComponents> = null;
        try {
            settingsUi = resolveSteamSettingsComponents(runtime);
        } catch {
            settingsUi = null;
        }
        const perfComponents = runtime.findUnique(PerfComponentTokens);
        const perfExports = perfComponents ? runtime(perfComponents[0]) : null;
        const valve = (token) => (perfExports ? uniqueFunction(perfExports, [token]) : null);
        const handles = {
            fields,
            panel,
            localize,
            performanceRoot,
            focusable,
            settingsUi,
            valve: {
                profileHeader: valve("#QuickAccess_Tab_Perf_GameSpecificSettings"),
                // The toggle reads current_game_id for availability, current==active for its checked
                // state, and writes through SetGameSpecificProfileEnabled — all state the host already
                // backs. Without this row nothing in the tab can enable a per-game profile.
                profileToggle: valve("#QuickAccess_Tab_Perf_ToggleGameSettings"),
                reset: valve("#QuickAccess_Tab_Perf_ResetToDefault"),
                refreshRate: valve("#QuickAccess_Tab_Perf_RefreshRate"),
                overlayLevel: valve("#QuickAccess_Tab_Perf_Overlay_Level"),
            },
        };
        return gateReady(handles, {
            settingsFields: !!settingsUi,
            valveComponents: Object.values(handles.valve).filter(Boolean).length,
        });
    };

    // Builds every control from what find resolved. Nothing here touches Steam.
    const buildControls = (handles) => {
        const { fields, panel, localize, focusable, settingsUi, valve } = handles;
        const react = fields.react;
        // The icon renderer is built once per control runtime and closes over Steam's React, so a row
        // asks for a glyph by name and never touches element construction itself.
        const icon = createIconRenderer(react);
        const controlRuntime = {
            react,
            slider: fields.sliderField,
            dropdown: fields.dropdown,
            // Steam's own ToggleField. Not required: a client where it cannot be found still gets
            // every control that does not need one, rather than losing the whole native surface.
            toggle: fields.toggleField,
            section: panel.section,
            row: panel.row,
            localize,
            icon,
            focusable,
        };
        // What the settings renderer draws a host row with: the settings fields where they resolved,
        // the Quick Access fields beside them, and the glyphs and localizer a row's icon and label
        // token are drawn through.
        const ui = {
            ...(settingsUi ?? {}),
            react,
            focusable: settingsUi?.focusable ?? focusable,
            toggleField: settingsUi?.toggleField ?? fields.toggleField,
            dropdown: settingsUi?.dropdown ?? fields.dropdown,
            sliderField: settingsUi?.sliderField ?? fields.sliderField,
            textField: settingsUi?.textField ?? fields.textField,
            dialogButton: settingsUi?.dialogButton ?? fields.dialogButton,
            icon,
            localize,
        };
        const frameLimit = createFrameLimitControl(controlRuntime);
        const lightingZones = createLightingZonesControl(controlRuntime);
        const overlayLevel = valve.overlayLevel
            ? withIcon(controlRuntime, valve.overlayLevel, controlRuntime.icon("layers"))
            : null;
        // What stays bespoke, in one table: [kind, key, tab, component, publication, ownRows]. A row is
        // mounted while its publication has a state; the host's layout decides the sections and the
        // order of their kinds, this table orders rows that share a kind and places a tab without a
        // layout. Valve's profile header and per-game toggle are two exports drawn as one kind, and the
        // lighting editor lays out its own panel rows.
        const bespoke = [
            ["valveProfileHeader", "steam-ui-valve-profile-header", "perf", valve.profileHeader, "performance", false],
            ["valveProfileHeader", "steam-ui-valve-profile-toggle", "perf", valve.profileToggle, "performance", false],
            ["valveOverlayLevel", "steam-ui-valve-overlay-level", "perf", overlayLevel, "performance", false],
            ["frameLimit", "steam-ui-frame-limit", "perf", frameLimit, "frameLimit", false],
            ["lightingZones", "steam-ui-lighting-zones", "quickSettings", lightingZones, "lightingZones", true],
            ["valveRefreshRate", "steam-ui-valve-refresh-rate", "quickSettings", valve.refreshRate, "performance", false],
            ["valveReset", "steam-ui-valve-reset", "perf", valve.reset, "performance", false],
        ] as const;
        return {
            controlRuntime,
            ui,
            bespoke,
            rowSection: createRowSectionControl(controlRuntime, ui),
            performanceRoot: handles.performanceRoot,
        };
    };

    // The tab roots, wrapped. Each re-renders when the host hears anything, and draws Valve's tab
    // untouched once the host is removed.
    const useHostRevision = (react) => {
        const [, setRevision] = react.useState(0);
        react.useEffect(() => subscribeHost(() => setRevision((value) => value + 1)), []);
    };
    const createTabTransform = (controls) => {
        const { controlRuntime, performanceRoot } = controls;
        const react = controlRuntime.react;
        const quickSettingsWrapCache = new Map();
        function SteamUiPerformanceRoot(props) {
            useHostRevision(react);
            const tree = performanceRoot(props);
            return bound ? appendControls(bound, tree, "perf") : tree;
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
                            useHostRevision(react);
                            quickSettingsRoot = original;
                            const tree = original(props);
                            return bound ? appendControls(bound, tree, "quickSettings") : tree;
                        };
                        quickSettingsWrapCache.set(original, wrapped);
                    }
                    return wrapped;
                },
                fallbackKey: "steam-ui-quick-settings-root",
            },
        ];
        // What each memoized array was answered with. useMemo hands back the same array until its
        // dependencies change, so a cached array is scanned once and the tab list keeps one identity,
        // which spares the wrapped panels a render on every render of the tab host. The wrappers above
        // are fixed for this closure, so an answer never goes stale.
        const answered = new WeakMap<any[], any[]>();
        const wrapTabs = (value) => {
            // Many memoized arrays hold objects too; only one with a tab's panel element is worth copying.
            let tabs = false;
            for (let index = 0; index < value.length && !tabs; index++) {
                const item = value[index];
                tabs = !!item && typeof item === "object" && react.isValidElement(item.panel);
            }
            if (!tabs) return value;
            let result = value;
            for (const wrapper of wrappers) {
                const matches = result.filter(
                    (item) =>
                        item &&
                        typeof item === "object" &&
                        react.isValidElement(item.panel) &&
                        wrapper.match(item.panel.type),
                );
                if (matches.length !== 1) continue;
                result = result.map((item) => {
                    if (item !== matches[0]) return item;
                    const panel = react.createElement(wrapper.component(item.panel.type), {
                        ...item.panel.props,
                        key: item.panel.key ?? wrapper.fallbackKey,
                    });
                    return { ...item, panel };
                });
            }
            return result;
        };
        // The tab array passes through the one useMemo claim every surface shares (ownership.ts).
        return (value) => {
            // Every useMemo result in the client passes through here. A tab list holds tab objects, so an
            // empty array, or one that starts with a string or number, is answered before any filtering.
            if (!Array.isArray(value) || !value.length) return value;
            if (typeof value[0] === "string" || typeof value[0] === "number") return value;
            const known = answered.get(value);
            if (known) return known;
            const result = wrapTabs(value);
            answered.set(value, result);
            return result;
        };
    };

    // The publications the host draws from, under the names the controls read them by.
    const Publications = [
        ["rows", RowsPatchId, normalizeRowsState],
        ["layout", LayoutPatchId, normalizeQuickAccessLayout],
        ["folds", SteamFoldsPatchId, (value) => panelFolds.normalize(value)],
        // Valve's components read the performance store; only whether a state arrived matters here.
        ["performance", PerformancePatchId, (value) => (value && typeof value === "object" ? true : null)],
        ["frameLimit", FrameLimitPatchId, normalizeFrameLimitState],
        ["lightingZones", LightingZonesPatchId, normalizeLightingZonesState],
    ] as const;

    const gate = defineGate({
        name: "native-components",
        find,
        bind: (handles: any, ledger) => {
            bound = buildControls(handles);
            for (const [name, patchId, normalize] of Publications) {
                // A publication the host does not offer is not allowlisted, and simply never draws.
                try {
                    ledger.subscribe(patchId, (value) => {
                        const normalized = normalize(value);
                        // A state that arrives and fails validation is not the same as one that never
                        // arrived: the render outcomes say which.
                        if (normalized === null && value) {
                            renderOutcomes[name] = "state received but rejected by validation";
                        }
                        acceptedStates.set(name, normalized);
                        notify();
                    });
                } catch {
                    // Not offered by this host.
                }
            }
            ledger.cleanup("plugin frontends", subscribePluginFrontends(() => notify()));
            const intercepted = ledger.memo(bound.controlRuntime.react, MemoName, createTabTransform(bound));
            if (!intercepted.ok)
                return intercepted.error || "React useMemo wrapper could not be installed";
            notify();
        },
        // Mounted wrappers draw Valve's tab untouched from their next render on.
        unbind: () => {
            bound = null;
            acceptedStates.clear();
            drawnKinds.clear();
            notify();
        },
        status: (handles: any) => ({
            hostVersion: 2,
            performanceRootWrapped:
                !!handles && memoIntercepted(handles.fields.react, MemoName),
            // Everything above can be true while the panel still shows nothing, because insertion
            // depends on the shape of the tree Steam renders. This is the part that says so.
            lastAppend: appendDiagnostics.perf,
            lastAppendQuickSettings: appendDiagnostics.quickSettings,
            quickSettingsRootResolved: !!quickSettingsRoot,
            // And this says which rows drew, and why the others did not.
            renderOutcomes,
            toggleResolved: !!handles?.fields?.toggleField,
            publications: [...acceptedStates.keys()].filter((name) => acceptedStates.get(name) !== null),
        }),
    });
    return gate;
}

registerGate("nativeComponents", createNativeComponentHost());
