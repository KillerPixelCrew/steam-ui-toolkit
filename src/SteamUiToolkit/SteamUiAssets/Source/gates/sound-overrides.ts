/**
 * Overrides selected Steam UI sounds and restores the displaced sound handlers on removal.
 * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
 */

function createSoundOverrides() {
  const patchId = "steam-ui.sound-overrides";
  const keys = { marker: "__steamUiSoundsClaimed", original: "__steamUiSoundsOriginal" };
  let manager: any = null;
  let installed = false;
  let unsubscribe: (() => void) | null = null;
  let sounds = new Map<string, string[]>();
  let generation = 0;
  let lastError = "";
  let loading = false;
  const report = (revision: number) => {
    if (!installed) return;
    void request(patchId, "status", {
      revision,
      loading,
      resources: sounds.size,
      error: lastError ? lastError.slice(0, 256) : null,
    }).catch(() => {});
  };
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
    lastError = "";
    loading = true;
    const revision = Number(state?.revision ?? 0);
    report(revision);
    if (!state?.sounds || typeof state.sounds !== "object") {
      loading = false;
      report(revision);
      return;
    }
    const entries = Object.entries(state.sounds);
    if (!entries.length) {
      loading = false;
      report(revision);
      return;
    }
    let context: AudioContext | null = null;
    const next = new Map<string, string[]>();
    // A refused entry leaves the others loading and says which one it was.
    const reject = (name: string) => {
      if (current === generation && installed) lastError = `Rejected sound: ${name}`;
    };
    try {
      context = new AudioContext();
      for (const [name, value] of entries) {
        if (!/^[a-zA-Z0-9_.-]+\.(wav|mp3|m4a|ogg)$/u.test(name) || !Array.isArray(value)) {
          reject(name);
          continue;
        }
        const valid: string[] = [];
        for (const url of value) {
          if (current !== generation || !installed) return;
          if (typeof url !== "string" || !/^data:audio\/[a-z0-9.+-]+;base64,[A-Za-z0-9+/=]+$/u.test(url)) {
            reject(name);
            continue;
          }
          try {
            const bytes = await (await fetch(url)).arrayBuffer();
            await context.decodeAudioData(bytes);
            valid.push(url);
          } catch {
            if (current === generation && installed) lastError = `Unreadable sound: ${name}`;
          }
        }
        if (valid.length) next.set(name, valid);
      }
      if (current === generation && installed) sounds = next;
    } catch (error) {
      if (current === generation && installed) lastError = String(error);
    } finally {
      if (context) await context.close().catch(() => {});
      if (current === generation && installed) {
        loading = false;
        report(revision);
      }
    }
  };
  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
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
    if (!installed) return { ok: true, absent: true };
    // Released first: a failed release keeps the gate installed with its sounds, so the next remove
    // retries it.
    const result = releaseMember(manager, "PlayAudioURLWithRepeats", keys);
    if (!result.ok) {
      lastError = result.error ?? "sound override release failed";
      return { ok: false, error: lastError };
    }
    installed = false;
    ++generation;
    loading = false;
    sounds.clear();
    unsubscribe = endSubscription(unsubscribe);
    return { ok: true, removed: true };
  };
  const status = () => ({
    ok: true,
    installed,
    claimed: memberClaimed(manager, "PlayAudioURLWithRepeats", keys),
    resources: sounds.size,
    loading,
    lastError,
  });
  return { install, remove, status };
}
registerGate("soundOverrides", createSoundOverrides());
