/**
 * Exposes the shared JSX transform claim to scripts outside this bundle.
 * @returns Named transform registration, removal and ownership checks; registrations belong to this bridge.
 */

function createElementsGate() {
    // The bridge's shared resolver, so no chunk is pushed on every registration and check.
    const runtime = () => getWebpackRuntime("elements").resolve([...JsxRuntimeTokens]);
    const validName = (name) =>
        typeof name === "string" && name.length > 0;

    const register = (name, transform) => {
        if (!validName(name) || typeof transform !== "function") {
            return {ok: false, error: "invalid element transform"};
        }
        try {
            return interceptElements(runtime(), name, transform);
        } catch (error) {
            return {ok: false, error: String(error)};
        }
    };

    const unregister = (name) => {
        if (!validName(name)) return {ok: false, error: "invalid element transform name"};
        try {
            return releaseElements(runtime(), name);
        } catch (error) {
            return {ok: false, error: String(error)};
        }
    };

    const registered = (name) => {
        try {
            return validName(name) && elementsIntercepted(runtime(), name);
        } catch {
            return false;
        }
    };

    return {register, unregister, registered};
}

registerGate("elements", createElementsGate());
