import assert from "node:assert/strict";
import { fragment, instantiate, loadAsset } from "./check-harness.mjs";

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
const useWindow = () => main;
const browser = instantiate(
    {
        URL,
        WeakRef,
        window: {
            SteamUIStore: { WindowStore: store },
            appStore: { GetAppOverviewByAppID: () => ({ display_name: "Test game" }) },
        },
        runtime: {
            exported: (markers) =>
                markers.includes("VirtualKeyboardManager")
                    ? useWindow
                    : { GamepadUI: { ExternalWeb: () => "/externalweb" } },
        },
    },
    fragment(loadAsset(), "overlay-browser.ts"),
    "resolveSteamOverlayBrowser(runtime)",
);

assert.equal(calls.length, 0, "resolving chrome creates no browser");
assert.equal(browser.useWindow, useWindow);
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
browser.dispose();
assert.equal(main.History.listeners.size, 0);
assert.equal(gamepad.History.listeners.size, 0);
assert.equal(browser.open(main, "https://example.org/"), false);
assert.equal(browser.identify(main), null);
console.log(
    "Native browser: lazy Big Picture/game windows, scoped navigation/resume, URL rejection, ambiguity and history cleanup passed.",
);
