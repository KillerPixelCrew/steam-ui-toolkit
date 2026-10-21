# The Steam-native file and folder picker

Steam has no picker a page can open, and a Windows dialog opens behind Big Picture where no
controller reaches it. The toolkit draws one as a Steam modal (`file-picker.ts`) and answers it from
the host (`SteamFilePickerSurface`). Every page that needs a local path uses this one; a consumer
supplies the title, the mode, the extensions it accepts and what happens with the path afterwards,
and nothing else.

## The page contract

```ts
showSteamFilePicker(ui, options): Promise<string | null>
runSteamFilePicker(ui, options): Promise<SteamFilePickerResult>
```

| Option       | Meaning                                                                                                              |
| ------------ | -------------------------------------------------------------------------------------------------------------------- |
| `title`      | The modal title. Default "Choose a file" or "Choose a folder".                                                       |
| `mode`       | `file` chooses a file and only enters folders; `folder` chooses the folder on screen and lists no files; `any` both. |
| `extensions` | Extensions with their dot, such as `[".exe", ".bat"]`. Empty or absent offers every file type. Ignored in `folder`.  |
| `start`      | Where to open first. A preference: a start the host cannot list falls back to the first drive, without an error.      |
| `signal`     | An `AbortSignal`. Aborting closes the picker and settles it as cancelled; an already aborted signal opens nothing.    |

`showSteamFilePicker` resolves with the chosen path or `null`. `runSteamFilePicker` resolves with
`{outcome: "selected", path, folder}`, `{outcome: "cancelled"}` or `{outcome: "failed", reason}`,
for a page that wants to tell a cancel from a picker that could not open. Both settle exactly once.

What `null`/`cancelled` covers: Cancel, B, Steam closing the modal, the page unmounting, a CEF
reload, bridge disposal, the consumer's signal. What is never returned: a path the host has not just
confirmed. A choice is sent to the host's `resolve` command and returned only as the host answers
it, in the full ordinary form the file system knows it by. A file deleted after the listing, a card
pulled, a folder that became a file or a type the page did not ask for shows its reason on screen,
lists the folder again and leaves the picker open. A choice still being checked when the user moves
on, closes or aborts belongs to the folder left behind: the host is told to stop, and its late
answer, success or refusal, is dropped.

Controls: A opens a folder or chooses a file, X uses the current folder (`folder` and `any`), Y goes
up a level, LT/RT turn pages, B cancels. Up, Refresh, Previous page, Next page, Cancel and Use this
folder are buttons as well, so touch and mouse work without a keyboard. Refresh lists the places and
the folder again, which is how a drive inserted while Steam runs becomes reachable. A listing error
lists the places again too, so a removed drive leaves them.

Size: the picker is Steam's own dialog, opened through `showSteamModal` with `wide: true`. That
sets the dialog's `bAllowFullSize` prop, the one its author named, which lifts the 600px the
dialog otherwise keeps its body to; the dialog then sizes itself to its content within Steam's
own bound of 88% of the viewport width and 90% of its height. The picker asks for 900px and
`max-width:100%`, so a client that keeps the dialog narrow, or a viewport under about 1080px wide,
clamps it instead of clipping it: about 814px on a 980px-wide handheld layout. The places and file
panes share that width one to two, with a floor on the places, each scrolling on its own with
long names wrapped, and the pane height is `clamp(200px, 90vh - 310px, 520px)`, so the title,
path row, pager, message and Cancel/Use this folder row stay on screen at 1280x720 and 980x640.
The dialog's backdrop, focus trap, B and dismissal are untouched; only the picker's own
`steam-ui-kit-picker-*` rules change, and no rule names Steam's dialog frame. Steam draws no
title inside the dialog in Big Picture, so the picker draws the page's title itself.

Large folders: the host answers every entry, the bridge delivers the answer in 256 KiB parts, and
the page draws `SteamFilePickerPageSize` rows at a time. Every row is reachable; nothing is capped.
The file pane is keyed by folder and page, so a new listing mounts fresh and takes focus at its
first row, while a refresh of the same folder keeps the row that had it.

A consumer that needs an executable for one game keeps that game's identity in its own closure:

```ts
const chooseLaunchCommand = (appId: number) =>
    void showSteamFilePicker(ui, {
        title: "Choose a program or script",
        mode: "file",
        extensions: [".exe", ".bat", ".cmd", ".ps1"],
    }).then((path) => path && request(MyPatchId, "setCustomLaunch", {appId, path}));
```

The toolkit knows nothing about launch actions or importers; the host command behind `request` owns
what the path means and revalidates it in its own terms.

## The host contract

Register `SteamFilePickerSurface.Module(enabled)` once. It installs no patch and publishes nothing;
it answers three commands under `steam-ui.file-picker`, each on a worker so a drive that stops
answering never holds the bridge pump, and each stopping when the page cancels it:

| Command      | Payload                        | Answer                                       |
| ------------ | ------------------------------ | -------------------------------------------- |
| `listPlaces` | `{}`                           | `{places: [{path, name, kind, detail}]}`     |
| `listFolder` | `{path, extensions}`           | `{path, parent, entries: [...], error}`      |
| `resolve`    | `{path, kind, extensions}`     | `{path, folder, error}`                      |

`listPlaces` offers the drives that are ready (free space as the detail), then Desktop, Documents,
Downloads, Pictures, Music and Videos as the known folders Windows reports for the user, wherever a
redirected profile keeps them. A drive without media, or one that stops answering while it is asked,
is simply not offered. `listFolder` answers subfolders first, then the files whose extension is in
the list, each sorted by name; an empty list means folders only and `.*` means every file type.
Hidden and system entries are skipped, as Explorer skips them. An entry that is a junction or a
symbolic link carries `link: true`; it is listed as what it is, entered under the path the user
chose, and never followed below the folder on screen. `resolve` says whether the path still exists
as the kind asked for (`file`, `folder`, `any`) with a matching extension, and answers the ordinary
full path.

Paths: `X:\folder`, `\\server\share\folder`, the same behind `\\?\` or `\\?\UNC\`, forward slashes
and `.`/`..` segments are accepted and answered as one ordinary form without a trailing separator.
Refused before anything is opened, with `error` set: a relative, drive-relative (`C:Games`) or
rooted-only (`\Games`) path, `\\server` without a share, any other `\\?\` or `\\.\` path
(`GLOBALROOT`, a volume GUID, a pipe, a physical drive) and a legacy device name (`CON`, `NUL`,
`COM1`) in any segment.

Errors are sentences the user can act on: "This drive is no longer available." when the drive is
gone, "This folder no longer exists." or "This file no longer exists." when only the item is,
"This folder cannot be opened." when access is denied, "Choose a folder, not a file." and its
mirror for a kind mismatch, "That file type cannot be chosen here." for an extension the page did
not offer. A denied folder is refused, never shown as empty.

The same `ListPlaces`, `ListFolder` and `Resolve` methods serve a host's own overlay picker, so both
surfaces browse through one backend.

## Audit of the picker as found, 2026-10-21

Traced end to end before the change: `SteamFilePickerSurface.cs`, `file-picker.ts`, the bridge's
request and cancel path, the host runtime's in-flight table and the part transport. Of the issue's
checklist, these were already right and were kept: enumeration off the bridge pump on a worker with
the request token linked to the page's cancel; a ticket per listing so a late answer is dropped; the
modal's unmount settling the promise once; hidden and system entries skipped; an all-files wildcard
distinct from folders only; Downloads from `SHGetKnownFolderPath`; the whole folder answered and the
transport splitting it. The two-pane layout was sized to the modal, but the modal itself was
Steam's ordinary 600px body (its `DialogContent_InnerWidth` cap, read from the installed client's
stylesheet), so the panes were folded narrow; see Size above for the dialog's full-size prop that
lifts it, added the same day.

Gaps found in the source and fixed here:

| Gap                                                                                                                   | Fix                                                                                            |
| --------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------- |
| A relative path was passed to `GetFullPath` first, so `Games` listed a folder under the host's working directory.     | Full-path check before normalization; `C:Games`, `\Games`, `..\x` and `\\server` are refused.  |
| `\\.\C:\`, `\\?\GLOBALROOT\...`, a volume GUID, a pipe and `C:\CON` were opened as the file system saw fit.           | Device prefixes and legacy device names in any segment refused with their own error.           |
| A chosen entry was returned straight from the listing, so a file deleted or a card pulled came back as a success.     | The `resolve` command; the page answers only what the host just confirmed.                      |
| `IgnoreInaccessible` turned a folder the user may not open into an empty folder.                                       | Not set; the open failure is reported as "This folder cannot be opened."                        |
| A folder on a removed drive reported "no longer exists", the same as a deleted folder; raw `IOException` text leaked.  | Drive and folder told apart by the root's presence and the Win32 code; fixed sentences.          |
| Junctions and symbolic links were indistinguishable from folders.                                                       | `link` on the entry, shown as "Link"; nothing followed below the folder on screen.              |
| A listing left behind by navigation ran to completion on its worker; only the page's timeout ever sent a cancel.       | `request` takes an `AbortSignal`; the page aborts the superseded listing, the host stops it.    |
| A refused or failing start path left an empty error state with nothing opened.                                          | The start is a preference; it falls back to the first place.                                   |
| A listing error left a pulled drive in the places until the picker was reopened; nothing refreshed the drive list.    | Places re-listed after a listing error and by the Refresh button.                              |
| Thousands of rows were rendered as one list.                                                                            | Pages of `SteamFilePickerPageSize` rows, buttons and LT/RT, every row reachable.               |
| Buttons were disabled while loading, which drops controller focus; the file pane had no focus rule on a new listing.   | Nothing disabled; the pane is keyed by folder and page and takes focus when it mounts.          |
| A consumer could not cancel the picker or tell a cancel from a picker that could not open.                              | `signal` and `runSteamFilePicker`.                                                              |
| Pictures, Music and Videos were not offered though artwork and movie pickers exist.                                     | Added as known folders.                                                                        |
| A refusal message was wiped by the re-listing that followed it.                                                         | The message rides through the listing.                                                         |

Established offline, by `SteamFilePickerSurfaceTests` and `eng/check-file-picker.mjs`: file and
folder selection, the extension filter, the redirected known folder, a 3,050-entry folder answered
whole past the part size, a denied folder, a gone folder and a gone drive, a stale listing after
navigating elsewhere with the superseded request cancelled, cancellation by B, Cancel, Steam's modal,
unmount and `AbortSignal`, path normalization, device-namespace refusal, a file deleted before
confirmation, a choice left pending by navigation, close or abort, links, and paging.

Not established offline, and needing a Big Picture pass in the current client with a controller:

- a WSGM page opening the picker and the modal taking focus;
- the dialog drawn at its full size: the two panes side by side at 1280x720 and on a handheld
  layout around 980x640, nothing clipped sideways, the title, path, pager and action rows on
  screen, each pane scrolling on its own;
- D-pad and stick through places, rows and the action rows, A, B, X, Y, LT/RT;
- focus landing on the first row after entering a folder, and staying put on a refresh;
- the importer choosing a folder and a ROM, and the custom launch action choosing an executable;
- an SD card pulled while its folder is open, then Refresh showing it again after reinsertion;
- a CEF reload with the picker open leaving no stale answer behind;
- touch and mouse on the rows and buttons.
