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
  const failed = new Set();
  const requirePresent = (id) => {
    if (typeof id !== "string" || typeof runtime.m[id] !== "function")
      throw new Error(`Steam module absent: ${id}`);
    if (failed.has(id)) throw new Error(`Steam module resolution previously failed: ${id}`);
    try {
      return runtime(id);
    } catch (error) {
      failed.add(id);
      throw new Error(`Steam module resolution failed: ${id}: ${String(error)}`);
    }
  };
  const matches = (tokens) => {
    if (
      !Array.isArray(tokens) ||
      tokens.length < 1 ||
      tokens.length > 16 ||
      !tokens.every((token) => typeof token === "string" && token.length > 0 && token.length <= 512)
    )
      throw new Error("Steam module fingerprint invalid");
    const ids = Object.keys(runtime.m);
    if (ids.length > 32768) throw new Error("Steam module registry exceeds the discovery bound");
    return ids.filter((id) => {
      const factory = runtime.m[id];
      if (typeof factory !== "function") return false;
      const source = Function.prototype.toString.call(factory);
      return tokens.every((token) => source.includes(token));
    });
  };
  requirePresent.count = (tokens) => matches(tokens).length;
  requirePresent.findUnique = (tokens) => {
    const ids = matches(tokens);
    return ids.length === 1 ? [ids[0], Function.prototype.toString.call(runtime.m[ids[0]])] : null;
  };
  requirePresent.resolve = (tokens) => {
    const ids = matches(tokens);
    if (ids.length !== 1)
      throw new Error(`Steam module ${ids.length ? "ambiguous" : "absent"}: ${tokens.join(", ")}`);
    return requirePresent(ids[0]);
  };
  return requirePresent;
}
// @steam-ui-module-resolver-end
