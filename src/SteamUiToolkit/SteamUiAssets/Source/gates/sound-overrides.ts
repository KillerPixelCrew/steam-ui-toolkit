// Exact resource-name overrides on the Gamepad UI manager only. Pack format and discovery belong
// to the host. No Steam file changes and no interception of voice/chat audio managers.
function createSoundOverrides() {
  const patchId = "steam-ui.sound-overrides";
  const keys = { marker: "__steamUiSoundsClaimed", original: "__steamUiSoundsOriginal" };
  let manager: any = null;
  let installed = false;
  let unsubscribe: (() => void) | null = null;
  let sounds = new Map<string, string[]>();
  let generation = 0;
  let lastError = "";
  const resolve = () => {
    try {
      const runtime = getWebpackRuntime("sound-overrides");
      const store: any = runtime.exported(
        ["m_GamepadUIAudioStore", "m_bHomeAndQuickAccessButtonsEnabled"],
        (value) => !!value?.GamepadUIAudio?.AudioPlaybackManager,
      );
      const candidate = store.GamepadUIAudio.AudioPlaybackManager;
      return typeof candidate.PlayAudioURLWithRepeats === "function" ? candidate : null;
    } catch {
      return null;
    }
  };
  const reconcile = async (state) => {
    const current = ++generation;
    // Retract before decoding: stale or corrupt assets never displace working stock audio.
    sounds = new Map();
    if (!state?.sounds || typeof state.sounds !== "object") return;
    const entries = Object.entries(state.sounds);
    if (entries.length > 128) {
      lastError = "Too many sound resources";
      return;
    }
    const context = new AudioContext();
    const next = new Map<string, string[]>();
    let total = 0;
    try {
      for (const [name, value] of entries) {
        if (
          !/^[a-zA-Z0-9_.-]+\.(wav|mp3|m4a|ogg)$/u.test(name) ||
          !Array.isArray(value) ||
          value.length > 16
        )
          continue;
        const valid: string[] = [];
        for (const url of value) {
          if (current !== generation || !installed) return;
          if (
            typeof url !== "string" ||
            url.length > 1400000 ||
            !/^data:audio\/[a-z0-9.+-]+;base64,[A-Za-z0-9+/=]+$/u.test(url)
          )
            continue;
          total += url.length;
          if (total > 24000000) throw new Error("Sound assets exceed the publication budget");
          try {
            const bytes = await (await fetch(url)).arrayBuffer();
            await context.decodeAudioData(bytes);
            valid.push(url);
          } catch {
            lastError = `Unreadable sound: ${name}`;
          }
        }
        if (valid.length) next.set(name, valid);
      }
      if (current === generation && installed) sounds = next;
    } catch (error) {
      lastError = String(error);
    } finally {
      await context.close().catch(() => {});
    }
  };
  const install = () => {
    if (installed) return { ok: true, installed: true };
    manager = resolve();
    if (!manager) return { ok: false, error: "Gamepad audio manager unavailable" };
    const result = claimMember(
      manager,
      "PlayAudioURLWithRepeats",
      keys,
      (original: any) =>
        function (this: any, url, ...args) {
          // Stock URLs can be relative or absolute. Unknown directories are never remapped.
          let options: string[] | undefined;
          if (typeof url === "string") {
            try {
              const path = url.startsWith("/sounds/")
                ? url.split(/[?#]/u, 1)[0]
                : new URL(url).pathname;
              if (path.startsWith("/sounds/") && !path.slice(8).includes("/"))
                options = sounds.get(path.slice(8));
            } catch {}
          }
          const chosen = options?.length
            ? options[Math.floor(Math.random() * options.length)]
            : url;
          return original.call(this, chosen, ...args);
        },
    );
    if (!result.ok) return result;
    installed = true;
    unsubscribe = subscribe(patchId, (state) => {
      void reconcile(state);
    });
    return { ok: true, installed: true };
  };
  const remove = () => {
    ++generation;
    sounds.clear();
    unsubscribe = endSubscription(unsubscribe);
    const result = releaseMember(manager, "PlayAudioURLWithRepeats", keys);
    if (result.ok) installed = false;
    return result;
  };
  const status = () => ({
    ok: true,
    installed,
    claimed: memberClaimed(manager, "PlayAudioURLWithRepeats", keys),
    resources: sounds.size,
    lastError,
  });
  return { install, remove, status };
}
registerGate("soundOverrides", createSoundOverrides());
