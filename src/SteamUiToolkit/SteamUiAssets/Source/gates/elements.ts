// The JSX-runtime claim (interceptElements in ownership.ts), for scripts outside this bundle.
//
// A consumer's own resident script runs in a separate evaluation and cannot reach the claim's
// functions, so it registers its transform here, through the bridge's gate registry, instead of
// wrapping the runtime itself: two wrappers on `jsx` would each hand back the other on removal, and a
// wrapper under a claim is invisible to the claim's own verification. This gate installs nothing of
// its own; it is the claim's front door, and a registration lives exactly as long as this bridge.
function createElementsGate() {
  const RuntimeTokens = ["react.transitional.element", ".jsx", ".jsxs"] as const;
  const MaximumNameLength = 64;

  const runtime = () => getWebpackRuntime("elements").resolve([...RuntimeTokens]);
  const validName = (name) =>
    typeof name === "string" && name.length > 0 && name.length <= MaximumNameLength;

  const register = (name, transform) => {
    if (!validName(name) || typeof transform !== "function") {
      return { ok: false, error: "invalid element transform" };
    }
    try {
      return interceptElements(runtime(), name, transform);
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };

  const unregister = (name) => {
    if (!validName(name)) return { ok: false, error: "invalid element transform name" };
    try {
      return releaseElements(runtime(), name);
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };

  const registered = (name) => {
    try {
      return validName(name) && elementsIntercepted(runtime(), name);
    } catch {
      return false;
    }
  };

  return { register, unregister, registered };
}

registerGate("elements", createElementsGate());
