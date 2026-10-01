# Steam sound overrides

`SteamSoundOverrideSurface.Module` publishes `SteamSoundOverrideState` through the shared runtime.
The host supplies exact filenames from its current Steam resource discovery and audio data URLs,
up to 128 resources and 16 variants per resource. Each encoded asset is limited to 1,400,000
characters and the entire asset set to 24,000,000 characters. Only WAV, MP3, M4A and Ogg resource
names are admitted. Pack installation, licensing, selection and persistent state belong to the
host.

The `soundOverrides` gate resolves one Gamepad UI store by the conjunction
`m_GamepadUIAudioStore` and `m_bHomeAndQuickAccessButtonsEnabled`, then validates the exported
Gamepad audio manager. It claims that instance's `PlayAudioURLWithRepeats` using durable ownership
metadata. It does not patch a shared prototype, chat manager or Steam file. Only exact names
directly under `/sounds/` are overridden; unknown paths and invalid assets use the original URL.
Extra playback arguments and the original return value are preserved. Root-relative sound URLs
work even when the shared document has an opaque URL such as `about:blank`.

A publication retracts the preceding map before asynchronously checking decoding. A stale decode
cannot publish after a newer selection or removal. Defaults are an empty map. Removal unsubscribes,
clears assets and restores the exact original property, including inherited membership. Status
reports installation, ownership, admitted resource count and the last decoding failure. The
emitted-asset check `eng/check-sound-overrides.mjs` covers fallback, defaults, inherited restoration
and reclaiming an orphaned injection.
