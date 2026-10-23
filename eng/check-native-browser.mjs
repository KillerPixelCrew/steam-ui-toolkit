import assert from "node:assert/strict";
import { fragment, instantiate, loadAsset, withSource } from "./check-harness.mjs";

const history = () => {
    const listeners = new Set();
    return {
        location: { pathname: "/wsgm/browser" },
        listeners,
        listen: (callback) => {
            listeners.add(callback);
            return () => listeners.delete(callback);
        },
        push(pathname, state = {}) {
            this.location = { pathname, state };
            for (const callback of [...listeners]) callback(this.location);
        },
        replace(pathname, state) {
            this.push(pathname, state);
        },
    };
};
const calls = [];
const frame = (kind, overrides = {}) => {
    const instance = {
        History: history(),
        params: { browserInfo: { m_unPID: 41, m_unAppID: 42, m_gameID: "42" } },
        IsMainGamepadUIWindow: () => kind === "main",
        IsOverlayWindow: () => kind !== "main",
        IsGamepadUIOverlayWindow: () => kind === "gamepad",
        IsDesktopOverlayWindow: () => kind === "desktop",
        Navigator: { ExternalWeb: (url) => calls.push({ instance, url }) },
        Navigate: (path, replace, immediate, state) =>
            calls.push({ instance, path, replace, immediate, state }),
        ...overrides,
    };
    return instance;
};
const main = frame("main");
const gamepad = frame("gamepad");
const desktop = frame("desktop", {
    DesktopOverlay: {
        web_requests: [],
        AddWebPageRequest: (url, activate) => calls.push({ desktop, url, activate }),
        SetWindowVisibility: (name, value) => calls.push({ name, value }),
        BringWindowToFront: (name) => calls.push({ front: name }),
    },
});
const store = { SteamUIWindows: [main], OverlayWindows: [gamepad, desktop] };
let currentWindow = main;
const useWindow = withSource(
    () => currentWindow,
    "function(){return React.useContext(context).instance}",
);
const frameType = (props) => props;
let gameTitle = "Test.game";
let input;
let lastBarProps;
const forwardedKeys = [];
const originalKeyUp = () => {};
const originalKeyboardRef = { current: null };
const addressBar = withSource((props) => {
    lastBarProps = props;
    return {
        type: "toolbar",
        props: {
            children: {
                type: "native-input",
                ref: (value) => {
                    input = value;
                },
                props: {
                    refKeyboardHandle: originalKeyboardRef,
                    onKeyDown: (event) => {
                        forwardedKeys.push(event);
                        if (event.nativeCanceled) event.preventDefault();
                        return "native-key";
                    },
                    onKeyUp: originalKeyUp,
                    onEnterKeyPress: () => {
                        calls.push({ typed: input.value });
                        return "VKClose";
                    },
                },
            },
        },
    };
}, "function(){bCanChangeURL; DisplayURL; onEnterKeyPress}");
const jsx = {};
let chromeTransform;
let chromeReleased = false;
const browser = instantiate(
    {
        URL,
        WeakRef,
        sourceMatches: (value, tokens) =>
            typeof value === "function" && tokens.every((token) => String(value).includes(token)),
        JsxRuntimeTokens: ["jsx"],
        interceptElements: (host, _name, transform) => {
            assert.equal(host, jsx);
            chromeTransform = transform;
            return { ok: true };
        },
        releaseElements: (host) => {
            assert.equal(host, jsx);
            chromeReleased = true;
        },
        window: {
            SteamUIStore: { WindowStore: store },
            appStore: { GetAppOverviewByAppID: () => ({ display_name: gameTitle }) },
        },
        runtime: {
            exported: (markers, shape) => {
                const value = markers.includes("VirtualKeyboardManager")
                    ? useWindow
                    : markers.includes("BrowserBackstack")
                      ? frameType
                      : markers.includes("URLInput")
                        ? addressBar
                        : { GamepadUI: { ExternalWeb: () => "/externalweb" } };
                assert.ok(shape(value));
                return value;
            },
            resolve: () => jsx,
        },
    },
    fragment(loadAsset(), "overlay-browser.ts"),
    "resolveSteamOverlayBrowser(runtime)",
);

assert.equal(calls.length, 0, "resolving chrome creates no browser");
assert.equal(browser.useWindow, useWindow);
const effects = [];
let invalidations = 0;
const react = {
    Fragment: "fragment",
    useState: (initial) => [
        initial,
        () => {
            invalidations++;
        },
    ],
    useEffect: (effect) => effects.push(effect),
    useRef: (initial) => ({ current: initial }),
    createElement: (type, fields, ...children) => {
        const { ref, ...props } = fields ?? {};
        return { type, ref, props: { ...props, ...(children.length ? { children } : {}) } };
    },
    isValidElement: (value) => value?.props !== undefined,
    cloneElement: (element, { ref = element.ref, ...props }, ...children) => ({
        ...element,
        ref,
        props: {
            ...element.props,
            ...props,
            ...(children.length
                ? { children: children.length === 1 ? children[0] : children }
                : {}),
        },
    }),
    Children: {
        map: (children, map) => (Array.isArray(children) ? children.map(map) : map(children)),
    },
};
assert.equal(
    browser.installChrome(
        { react, smallButton: "native-button" },
        () => "https://www.google.com/search?q=",
    ),
    true,
);
const create = (type, props, key) => ({ type, props, key });
const nativeProps = { external: true, path: "/externalweb", browser: { name: "ExternalWeb" } };
const nativeFrame = chromeTransform(create, frameType, nativeProps);
assert.equal(nativeFrame.type(nativeFrame.props).props.bCanChangeURL, true);
assert.equal(chromeTransform(create, frameType, { ...nativeProps, external: false }), undefined);
assert.equal(chromeTransform(create, frameType, { ...nativeProps, path: "/steamweb" }), undefined);
const nativeBar = chromeTransform(create, addressBar, nativeProps);
const renderedInput = nativeBar.type(nativeBar.props).props.children;
const inputDocument = { activeElement: null };
const element = {
    ownerDocument: inputDocument,
    value: "",
    validity: "",
    setCustomValidity(value) {
        this.validity = value;
    },
    setAttribute() {},
    removeAttribute() {},
    reportValidity() {},
    focus() {
        this.ownerDocument.activeElement = this;
    },
    click() {},
    setSelectionRange() {},
};
inputDocument.activeElement = element;
const inputInstance = {
    element,
    get value() {
        return this.element.value;
    },
};
renderedInput.ref(inputInstance);
let keyboardHides = 0;
const keyboardHandle = {
    HideVirtualKeyboard: () => {
        keyboardHides++;
    },
};
renderedInput.props.refKeyboardHandle(keyboardHandle);
assert.equal(
    originalKeyboardRef.current,
    keyboardHandle,
    "the field's original keyboard-handle ref is forwarded",
);
assert.equal(
    renderedInput.props.onKeyUp,
    originalKeyUp,
    "unrelated native DOM handlers are preserved",
);
const keyEvent = (key, extra = {}) => ({
    key,
    target: element,
    defaultPrevented: false,
    preventDefault() {
        this.defaultPrevented = true;
    },
    stopPropagation() {
        this.propagationStopped = true;
    },
    ...extra,
});
element.value = "example.org/physical";
const physicalEnter = keyEvent("Enter");
assert.equal(renderedInput.props.onKeyDown(physicalEnter), "VKClose");
assert.equal(calls.at(-1).typed, "https://example.org/physical");
assert.equal(
    keyboardHides,
    1,
    "successful DOM Enter closes the same focused field's native keyboard",
);
assert.equal(physicalEnter.defaultPrevented, true);
assert.equal(physicalEnter.propagationStopped, true);
const afterPhysicalEnter = calls.length;
assert.equal(renderedInput.props.onKeyDown(physicalEnter), "native-key");
assert.equal(renderedInput.props.onKeyDown(keyEvent("Enter", { repeat: true })), "native-key");
assert.equal(calls.length, afterPhysicalEnter, "a consumed or repeating Enter cannot submit twice");
assert.equal(keyboardHides, 1, "consumed or repeating Enter does not close the keyboard again");
calls.pop();
for (const event of [
    keyEvent("ArrowLeft"),
    keyEvent("Enter", { isComposing: true }),
    keyEvent("Enter", { nativeEvent: { isComposing: true } }),
    keyEvent("Enter", { keyCode: 229 }),
    keyEvent("Enter", { nativeEvent: { keyCode: 229 } }),
]) {
    const count = calls.length;
    assert.equal(renderedInput.props.onKeyDown(event), "native-key");
    assert.equal(forwardedKeys.at(-1), event);
    assert.equal(
        event.defaultPrevented,
        false,
        "IME and non-Enter keys keep their native behavior",
    );
    assert.equal(calls.length, count);
    assert.equal(keyboardHides, 1, "IME and non-Enter keys leave the native keyboard alone");
}
const canceledEnter = keyEvent("Enter", { nativeCanceled: true });
assert.equal(renderedInput.props.onKeyDown(canceledEnter), "native-key");
assert.equal(calls.length, 0, "native handlers may consume Enter before the browser commit");
element.value = "javascript:alert(1)";
assert.equal(renderedInput.props.onKeyDown(keyEvent("Enter")), undefined);
assert.equal(calls.length, 0, "physical Enter uses the same URL policy as the native OSK");
assert.ok(element.validity);
assert.equal(keyboardHides, 1, "invalid addresses keep the same field's native keyboard open");
inputDocument.activeElement = {};
element.value = "example.org/not-focused";
assert.equal(renderedInput.props.onKeyDown(keyEvent("Enter")), "VKClose");
assert.equal(calls.pop().typed, "https://example.org/not-focused");
assert.equal(keyboardHides, 1, "a stale field cannot close another focused field's keyboard");
inputDocument.activeElement = element;
renderedInput.props.refKeyboardHandle(null);
assert.equal(
    originalKeyboardRef.current,
    null,
    "native keyboard-handle cleanup reaches the original ref",
);
element.value = "example.org/no-handle";
assert.equal(renderedInput.props.onKeyDown(keyEvent("Enter")), "VKClose");
calls.pop();
assert.equal(
    keyboardHides,
    1,
    "an unavailable field handle never falls back to a different keyboard owner",
);
renderedInput.props.refKeyboardHandle(keyboardHandle);
for (const [typed, expected] of [
    ["example.com/guide", "https://example.com/guide"],
    ["localhost:8080", "https://localhost:8080/"],
    ["shield & poison", "https://www.google.com/search?q=shield%20%26%20poison"],
    ["https://example.org/", "https://example.org/"],
    ["about:blank", "about:blank"],
]) {
    element.value = typed;
    assert.equal(
        renderedInput.props.onEnterKeyPress(),
        "VKClose",
        "successful entry retains native OSK close result",
    );
    assert.equal(calls.pop().typed, expected);
    assert.equal(
        keyboardHides,
        1,
        "OSK VKClose remains owned by Steam's virtual-keyboard callback path",
    );
}
for (const typed of [
    "",
    "javascript:alert(1)",
    "file:///C:/secret",
    "steam://run/220",
    "https://name:secret@example.org/",
    "x".repeat(4097),
    "é".repeat(1000),
]) {
    element.value = typed;
    const count = calls.length;
    assert.equal(
        renderedInput.props.onEnterKeyPress(),
        undefined,
        "invalid entry keeps the native OSK open",
    );
    assert.equal(calls.length, count);
    assert.ok(element.validity);
    assert.equal(element.value, typed, "rejected drafts remain editable");
}
renderedInput.props.onChange();
assert.equal(element.validity, "");
currentWindow = gamepad;
const gameBar = nativeBar.type(nativeBar.props);
const gameInput = gameBar.props.children[0];
const gameAction = gameBar.props.children[1];
gameInput.ref(inputInstance);
const beforeGameSearch = calls.length;
gameAction.props.onClick();
assert.equal(element.value, "Test.game ");
assert.equal(calls.length, beforeGameSearch, "game search only seeds a draft until Enter");
assert.equal(gameInput.props.onEnterKeyPress(), "VKClose");
assert.equal(
    calls.pop().typed,
    "https://www.google.com/search?q=Test.game",
    "a title resembling a host remains an explicit game search",
);
gameTitle = "DOOM: The Dark Ages";
const colonBar = nativeBar.type(nativeBar.props);
const colonInput = colonBar.props.children[0];
const colonAction = colonBar.props.children[1];
colonInput.ref(inputInstance);
colonAction.props.onClick();
assert.equal(
    calls.length,
    beforeGameSearch,
    "a title containing a colon is not transmitted on selection",
);
assert.equal(colonInput.props.onEnterKeyPress(), "VKClose");
assert.equal(
    calls.pop().typed,
    "https://www.google.com/search?q=DOOM%3A%20The%20Dark%20Ages",
    "explicit game searches do not mistake a title for a custom scheme",
);
currentWindow = desktop;
const desktopKeyPress = withSource(
    (event) => calls.push({ typed: event.target.value }),
    'function(event){if(event.key==="Enter")manager.Browser.LoadURL(event.target.value)}',
);
const desktopRef = () => {};
const desktopInput = chromeTransform(create, "native-input", {
    type: "input",
    ref: desktopRef,
    onKeyPress: desktopKeyPress,
});
const desktopControls = desktopInput.type(desktopInput.props);
const desktopField = desktopControls.props.children[0];
desktopField.ref({ element });
element.value = "shield & poison";
desktopField.props.onKeyPress({ key: "Enter", target: element, preventDefault() {} });
assert.equal(calls.pop().typed, "https://www.google.com/search?q=shield%20%26%20poison");
currentWindow = main;
assert.equal(browser.identify(main).kind, "main");
assert.equal(browser.identify(main).gameId, null, "Big Picture does not invent a game identity");
assert.equal(calls.length, 0, "opening trusted controls does not navigate or search");
assert.equal(browser.open(main, "https://example.org/"), true);
assert.equal(calls.at(-1).instance, main);
assert.equal(calls.at(-1).url, "https://example.org/");
assert.equal(main.History.listeners.size, 1);
main.History.push("/externalweb", { url: "https://example.org/", initialIndex: 3, finalIndex: 5 });
main.History.push("/wsgm/browser");
assert.equal(browser.open(main, undefined, "https://home.invalid/"), true);
assert.deepEqual(calls.at(-1).state, {
    url: "https://example.org/",
    initialIndex: 3,
    finalIndex: 5,
});
assert.equal(main.History.listeners.size, 1, "repeat opens share one native history subscription");
assert.equal(browser.open(main, undefined, "about:blank", true), true);
assert.equal(
    calls.at(-1).replace,
    true,
    "direct entry replaces its handoff so native Back cannot loop",
);
assert.equal(
    calls.at(-1).state.initialIndex,
    3,
    "direct entry preserves Steam's retained native session",
);
assert.equal(
    browser.identify(gamepad).kind,
    "gamepad",
    "desktop and gamepad windows for one game are distinct valid contexts",
);
assert.equal(browser.identify(desktop).kind, "desktop");
assert.equal(browser.open(gamepad, "https://example.org/guide"), true);
assert.equal(calls.at(-1).instance, gamepad);
assert.equal(browser.open(desktop, "https://example.org/desktop"), true);
assert.equal(calls.at(-1).front, "Browser");
const before = calls.length;
for (const url of [
    "file:///C:/notes.txt",
    "javascript:alert(1)",
    "data:text/html,hi",
    "https://name:secret@example.org/",
]) {
    assert.equal(browser.open(main, url), false);
}
assert.equal(calls.length, before, "refused addresses never reach native navigation");
assert.equal(browser.identify(frame("main")), null, "foreign window instances are refused");
store.SteamUIWindows.push(frame("main"));
assert.equal(
    browser.open(main, "https://example.org/"),
    false,
    "ambiguous main instances are refused",
);
store.SteamUIWindows.pop();
store.OverlayWindows.push(frame("gamepad"));
assert.equal(browser.identify(gamepad), null, "duplicate same-kind game contexts remain ambiguous");
store.OverlayWindows.pop();
const prior = main.History;
main.History = history();
assert.equal(browser.open(main, "about:blank"), true);
assert.equal(prior.listeners.size, 0, "a replaced history releases its old subscription");
assert.equal(main.History.listeners.size, 1);
for (const effect of effects) effect();
browser.dispose();
assert.equal(chromeReleased, true, "removal releases the shared trusted-chrome transform");
assert.ok(
    invalidations >= 4,
    "removal invalidates already mounted native frame and address controls",
);
const beforeStale = calls.length;
const beforeStaleKeys = forwardedKeys.length;
const beforeStaleKeyboardHides = keyboardHides;
element.value = "javascript:alert(1)";
renderedInput.props.onEnterKeyPress();
renderedInput.props.onKeyDown(keyEvent("Enter"));
renderedInput.props.onKeyDown(keyEvent("ArrowLeft"));
gameInput.props.onEnterKeyPress();
gameAction.props.onClick();
desktopField.props.onKeyPress({ key: "Enter", target: element, preventDefault() {} });
assert.equal(
    calls.length,
    beforeStale,
    "stale mounted callbacks are inert immediately after removal",
);
assert.equal(element.value, "javascript:alert(1)");
assert.equal(
    keyboardHides,
    beforeStaleKeyboardHides,
    "disposed callbacks cannot close a native keyboard",
);
assert.equal(
    forwardedKeys.length,
    beforeStaleKeys,
    "disposed DOM callbacks do not forward to native handlers",
);
assert.deepEqual(
    nativeFrame.type(nativeFrame.props).props,
    nativeProps,
    "mounted frame restores its exact native props",
);
nativeBar.type(nativeBar.props);
assert.equal(
    Object.hasOwn(lastBarProps, "bCanChangeURL"),
    false,
    "mounted header restores the original absent edit flag",
);
const restoredDesktop = desktopInput.type(desktopInput.props);
assert.equal(restoredDesktop.props.onKeyPress, desktopKeyPress);
assert.equal(restoredDesktop.ref, desktopRef, "desktop input restores its original native ref");
assert.equal(main.History.listeners.size, 0);
assert.equal(gamepad.History.listeners.size, 0);
assert.equal(browser.open(main, "https://example.org/"), false);
assert.equal(browser.identify(main), null);
console.log(
    "Native browser: lazy Big Picture/game windows, scoped navigation/resume, URL rejection, ambiguity and history cleanup passed.",
);
