// Keep this fragment valid JavaScript: the same bytes are embedded for standalone C# probes
// and composed into the bridge. Features supply fingerprints, never their own registry scan.
function createSteamUiModuleResolver(scope) {
    let runtime;
    window.webpackChunksteamui?.push([
        [`steam_ui_${scope}_${Date.now()}`],
        {},
        (value) => {
            runtime = value;
        },
    ]);
    if (!runtime?.m) throw new Error("Steam modules unavailable");
    // A factory's source never changes once registered, and every fingerprint match reads all of them.
    const sources = new WeakMap();
    const sourceOf = (factory) => {
        let source = sources.get(factory);
        if (source === undefined) {
            source = Function.prototype.toString.call(factory);
            sources.set(factory, source);
        }
        return source;
    };
    // No memory of a failure. Steam's loader records a module before running its factory and never
    // re-runs one that threw, so a later call returns whatever exports that factory set, the object
    // Steam's own code now uses, and the shape tests below accept or refuse it. Remembering the
    // failure made a module that threw once during a cold start unusable for the bridge's life.
    const requirePresent = (id) => {
        if (typeof id !== "string" || typeof runtime.m[id] !== "function")
            throw new Error(`Steam module absent: ${id}`);
        try {
            return runtime(id);
        } catch (error) {
            throw new Error(`Steam module resolution failed: ${id}: ${String(error)}`);
        }
    };
    const matches = (tokens) => {
        if (
            !Array.isArray(tokens) ||
            tokens.length < 1 ||
            !tokens.every((token) => typeof token === "string" && token.length > 0)
        )
            throw new Error("Steam module fingerprint invalid");
        return Object.keys(runtime.m).filter((id) => {
            const factory = runtime.m[id];
            if (typeof factory !== "function") return false;
            const source = sourceOf(factory);
            return tokens.every((token) => source.includes(token));
        });
    };
    requirePresent.count = (tokens) => matches(tokens).length;
    requirePresent.findUnique = (tokens) => {
        const ids = matches(tokens);
        return ids.length === 1 ? [ids[0], sourceOf(runtime.m[ids[0]])] : null;
    };
    requirePresent.resolve = (tokens) => {
        const ids = matches(tokens);
        if (ids.length !== 1)
            throw new Error(`Steam module ${ids.length ? "ambiguous" : "absent"}: ${tokens.join(", ")}`);
        return requirePresent(ids[0]);
    };
    // One export of a uniquely fingerprinted module, chosen by what it is. Client builds renumber
    // modules and rename exports, so neither a module id nor an export name is an identity: the
    // September 2026 beta did both and took down every gate that had named them. Aliases of one value
    // count once; no fit or two distinct fits throws, so a moved export says so instead of guessing.
    requirePresent.exported = (tokens, predicate) => {
        if (typeof predicate !== "function") throw new Error("Steam export predicate invalid");
        const exports = requirePresent.resolve(tokens);
        const fits = new Set();
        for (const name of Object.keys(exports ?? {})) {
            try {
                const value = exports[name];
                if (predicate(value)) fits.add(value);
            } catch {
                // An export whose getter or shape test throws is not the one being looked for.
            }
        }
        if (fits.size !== 1)
            throw new Error(`Steam export ${fits.size ? "ambiguous" : "absent"}: ${tokens.join(", ")}`);
        return [...fits][0];
    };
    return requirePresent;
}

// @steam-ui-module-resolver-end
