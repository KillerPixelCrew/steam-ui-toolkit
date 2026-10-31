/**
 * Overrides selected Steam UI sounds and restores the displaced sound handlers on removal.
 * @returns The defineGate gate; remove must release it before its bridge is replaced.
 */

function createSoundOverrides() {
  const patchId = "steam-ui.sound-overrides";
  const keys = { marker: "__steamUiSoundsClaimed", original: "__steamUiSoundsOriginal" };
  let manager: any = null;
  let sounds = new Map<string, string[]>();
  let generation = 0;
  // Why the last publication's sounds did not all load; the gate's own lastError is its lifecycle's.
  let decodeError = "";
  let loading = false;
  const installed = () => gate.installed();
  const report = (revision: number) => {
    if (!installed()) return;
    void request(patchId, "status", {
      revision,
      loading,
      resources: sounds.size,
      error: decodeError ? decodeError.slice(0, 256) : null,
    }).catch(() => {});
  };
  // Pure: Steam's audio store by its own tokens and shape, and the playback method the claim takes.
  // A client without them is incompatible and keeps stock audio.
  const find = (runtime) => {
    let candidate: any = null;
    try {
      const store: any = runtime.exported(
        ["m_GamepadUIAudioStore", "m_bHomeAndQuickAccessButtonsEnabled"],
        (value) => !!value?.GamepadUIAudio?.AudioPlaybackManager,
      );
      candidate = store.GamepadUIAudio.AudioPlaybackManager;
    } catch (error) {
      return gateIncompatible(`Gamepad audio manager unavailable: ${String(error)}`, { manager: false });
    }
    return typeof candidate?.PlayAudioURLWithRepeats === "function"
      ? gateReady(candidate, { manager: true })
      : gateIncompatible("Gamepad audio manager unavailable", { manager: false });
  };
  const reconcile = async (state) => {
    const current = ++generation;
    // Retract before decoding: stale or corrupt assets never displace working stock audio.
    sounds = new Map();
    decodeError = "";
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
      if (current === generation && installed()) decodeError = `Rejected sound: ${name}`;
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
          if (current !== generation || !installed()) return;
          if (typeof url !== "string" || !/^data:audio\/[a-z0-9.+-]+;base64,[A-Za-z0-9+/=]+$/u.test(url)) {
            reject(name);
            continue;
          }
          try {
            const bytes = await (await fetch(url)).arrayBuffer();
            await context.decodeAudioData(bytes);
            valid.push(url);
          } catch {
            if (current === generation && installed()) decodeError = `Unreadable sound: ${name}`;
          }
        }
        if (valid.length) next.set(name, valid);
      }
      if (current === generation && installed()) sounds = next;
    } catch (error) {
      if (current === generation && installed()) decodeError = String(error);
    } finally {
      if (context) await context.close().catch(() => {});
      if (current === generation && installed()) {
        loading = false;
        report(revision);
      }
    }
  };
  const gate = defineGate({
    name: "sound-overrides",
    find,
    bind: (handle, ledger) => {
      manager = handle;
      const result = ledger.member(
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
      if (!result.ok) return result.error;
      ledger.subscribe(patchId, (state) => {
        void reconcile(state);
      });
    },
    // Released before this runs: a failed release keeps the gate installed with its sounds, so the
    // next remove retries it.
    unbind: () => {
      ++generation;
      loading = false;
      sounds.clear();
    },
    status: () => ({
      claimed: memberClaimed(manager, "PlayAudioURLWithRepeats", keys),
      resources: sounds.size,
      loading,
      decodeError,
    }),
  });
  return gate;
}
registerGate("soundOverrides", createSoundOverrides());
