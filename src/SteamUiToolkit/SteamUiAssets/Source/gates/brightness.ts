// Not availability-only, despite the founding comment that said Steam's own backend works on
// Windows. It does not — device-disproved 2026-08-30: SetBrightness is a native stub and
// RegisterForBrightnessChanges never fires, so the store's observable sits at its constructed 1
// and the revealed slider moves nothing. The host is the backend: the gate forwards the slider's
// writes over the bridge and feeds the store's observable from the published state, both through
// the same \\.\LCD interface the host owns.
function createBrightnessGate() {
  const patchId = "steam-ui.brightness";
  const field = "is_display_brightness_available";
  // A string key on the settings message, because the probe reads it from a separate CDP
  // evaluation where nothing from this scope is reachable. Without it this gate ran the
  // self-incompatibility teardown loop the audio namespace already paid for: the probe required
  // the flag to be hidden, a successful apply made it visible, and the patch manager tore down
  // its own work every poll — the row flickered in and out on a ~25-second cycle on the device.
  const availability = {
    marker: "__steamUiBrightnessRevealed",
    original: "__steamUiOriginalBrightnessAvailability",
  };
  const setter = {
    marker: "__steamUiOwnedSetBrightness",
    original: "__steamUiOriginalSetBrightness",
  };
  let installed = false;
  let lastError = "";
  let unsubscribe: (() => void) | null = null;
  let lastPercent: number | null = null;
  let lastRevision = -1;
  let applyingState = false;
  let requestVersion = 0;
  let pendingWrite = false;
  let confirmedState: { percent: number; revision: number } | null = null;

  const displayStore = () => {
    try {
      const req = getWebpackRuntime("brightness-store");
      return req?.("59547")?.mG?.Get?.() ?? null;
    } catch {
      return null;
    }
  };

  const settings = () => displayStore()?.m_msgSettings ?? null;

  const onState = (state) => {
    if (!installed || !state) return;
    const percent = Number(state.percent);
    const revision = Number(state.revision);
    if (
      !Number.isInteger(percent) ||
      percent < 0 ||
      percent > 100 ||
      !Number.isSafeInteger(revision) ||
      revision < 0 ||
      revision < lastRevision
    )
      return;
    lastRevision = revision;
    confirmedState = { percent, revision };
    if (pendingWrite) return;
    try {
      const observable = displayStore()?.m_flDisplayBrightness;
      applyingState = true;
      if (observable?.Set && Math.abs((observable.m_currentValue ?? -1) - percent / 100) > 0.004) {
        observable.Set(percent / 100);
      }
      lastPercent = percent;
    } catch (error) {
      lastError = "brightness state apply failed: " + String(error);
    } finally {
      applyingState = false;
    }
  };

  // The slider's writes, taken over at the one method it calls. Same replace-not-stack rule as
  // the Manager's GetState: the overlay carries the stub it replaced, so a bridge replaced in
  // place unwinds to the client's own method instead of wrapping a dead closure.
  const overrideSetter = () => {
    const display = window.SteamClient?.System?.Display;
    if (!display || typeof display.SetBrightness !== "function") {
      lastError = "SteamClient.System.Display.SetBrightness unavailable";
      return false;
    }

    const claim = claimMember(display, "SetBrightness", setter, () => (flBrightness) => {
      if (!installed || applyingState) return Promise.resolve();
      const value = Number(flBrightness);
      if (!Number.isFinite(value) || value < 0 || value > 1) return Promise.resolve();
      const percent = Math.round(value * 100);
      if (!pendingWrite && percent === lastPercent) return Promise.resolve();
      const version = ++requestVersion;
      pendingWrite = true;
      return request(patchId, "setBrightness", { percent })
        .then((readback) => {
          if (!installed || version !== requestVersion) return;
          pendingWrite = false;
          onState(readback);
          // A later external observation can already have arrived while this request completed.
          if (confirmedState) onState(confirmedState);
        })
        .catch((error) => {
          if (!installed || version !== requestVersion) return;
          pendingWrite = false;
          lastError = "brightness write failed: " + String(error);
          if (confirmedState) onState(confirmedState);
        });
    });
    if (!claim.ok) {
      lastError = claim.error;
      return false;
    }

    return true;
  };

  const restoreSetter = () => {
    const released = releaseMember(
      window.SteamClient?.System?.Display ?? null,
      "SetBrightness",
      setter,
    );
    if (!released.ok) {
      lastError = released.error ?? "brightness setter release failed";
    }
  };

  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
    const message = settings();
    if (!message || !(field in message)) {
      lastError = "display settings message unavailable";
      return { ok: false, error: lastError };
    }

    // A client already reporting brightness available needs nothing from the host, and overwriting
    // the flag would mean restoring a value that was never ours to change. Available AND MARKED
    // is different: that is this gate's own earlier reveal, surviving a bridge replaced in
    // place, and refusing it is the teardown trap. Both cases are the claim primitive's job now.
    //
    // `false` is the absent value: a client that hides the row has the flag false, so a reclaim
    // whose stored original went missing hands back a hidden row rather than `undefined`, which
    // Steam's `?? true` hook would have read as available forever.
    const claim = claimValue(message, field, availability, true, false);
    if (!claim.ok) {
      lastError = claim.error;
      return { ok: false, error: lastError };
    }

    if (!overrideSetter()) {
      // Revealing a slider whose writes go into the stub is the broken state this gate shipped
      // with; the reveal is undone rather than left half-working.
      releaseValue(message, field, availability);
      return { ok: false, error: lastError };
    }

    installed = true;
    lastPercent = null;
    lastRevision = -1;
    confirmedState = null;
    lastError = "";
    unsubscribe = subscribe(patchId, onState);
    return { ok: true, installed: true, available: message[field] === true };
  };

  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    const message = settings();
    installed = false;
    ++requestVersion;
    pendingWrite = false;
    if (unsubscribe) {
      unsubscribe();
      unsubscribe = null;
    }

    restoreSetter();
    if (!message) return { ok: true, removed: true, storeGone: true };
    const released = releaseValue(message, field, availability);
    if (!released.ok) {
      lastError = released.error ?? "brightness release failed";
      return { ok: false, error: lastError };
    }

    return { ok: true, removed: true };
  };

  const status = () => {
    const message = settings();
    return {
      ok: true,
      installed,
      available: message ? message[field] === true : false,
      setterOwned: memberClaimed(window.SteamClient?.System?.Display, "SetBrightness", setter),
      lastPercent,
      lastRevision,
      pendingWrite,
      observable: displayStore()?.m_flDisplayBrightness?.m_currentValue ?? null,
      lastError,
    };
  };

  return { install, remove, status };
}

registerGate("brightness", createBrightnessGate());
