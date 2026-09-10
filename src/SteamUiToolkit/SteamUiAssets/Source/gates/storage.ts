// Steam's own storage device manager, revived on Windows.
//
// Big Picture ships a complete SteamOS storage UI — drives, block devices, format, adopt, eject,
// trim — and on Windows it never appears. Mapped against the live client on 2026-09-10: the whole
// surface hangs off one question. Its hooks call
//
//   StorageDeviceManager.IsServiceAvailable#1
//
// through the WebUI service transport, and every other query is `enabled:` on that answer. The
// Windows client has no service behind it, so the answer never arrives and the UI stays inert.
//
// The transport is where this is claimable. Each generated client resolves
// `GetDefaultTransport().SendMsg(name, request, responseType, options)`, and `SendMsg` lives on the
// transport prototype as a writable, configurable property. Claiming it on the *instance* scopes
// the change to the one live transport and lets removal delete the own property so the prototype
// method shows through again, untouched.
//
// Everything not addressed to StorageDeviceManager is forwarded to the original synchronously and
// unexamined. This carries all of Steam's service traffic, so the filter is a name prefix checked
// first and nothing else happens on that path.
//
// The service vocabulary, read from the client's own message classes:
//
//   IsServiceAvailable, GetState, StateChanged, Eject, Adopt, Format, Unmount, TrimAll
//   CStorageDeviceManagerDrive        id, is_formattable, is_unformatted
//   CStorageDeviceManagerBlockDevice  block_device_id, drive_id, mount_paths, has_steam_library
//   CStorageDeviceManagerState        drives, block_devices, is_adopt_supported,
//                                     is_unmount_supported, is_trim_supported, is_trim_running
function createStorageService() {
  const patchId = "steam-ui.storage";
  const claimKeys = {
    marker: "__steamUiStorageClaimed",
    original: "__steamUiStorageOriginal",
  } as const;

  const ServicePrefix = "StorageDeviceManager.";
  const TransportToken = "GetDefaultTransport";
  const ServiceToken = "StorageDeviceManager.IsServiceAvailable#1";

  // A machine with more drives than this is not a handheld, and the state is rendered as rows.
  const MaximumDrives = 32;

  let runtime;
  let transport = null;
  let installed = false;
  let lastError = "";
  let unsubscribe: (() => void) | null = null;

  // What the host says the machine's storage looks like. Empty until it publishes, and an empty
  // state is still answered: "no removable drives" is a truthful answer and the page renders it,
  // where refusing to answer leaves Steam's spinner up forever.
  let state = {
    drives: [] as unknown[],
    block_devices: [] as unknown[],
    is_adopt_supported: false,
    is_unmount_supported: false,
    is_trim_supported: false,
    is_trim_running: false,
  };
  let answered = 0;
  let forwarded = 0;
  let lastMethod = "";

  // Steam's callers only ever ask a response two things, so the response is duck-typed rather than
  // built as a protobuf. Constructing a real Message would mean owning the wire format, which is
  // the client's business and not something this should mirror.
  const ok = (body) => Promise.resolve({ BSuccess: () => true, Body: () => body });

  const failed = (reason) =>
    Promise.resolve({ BSuccess: () => false, Body: () => ({}), GetErrorMessage: () => reason });

  // The request arrives already encoded. Steam's encoder yields a Message, which answers toObject(),
  // so the fields are readable without decoding bytes; anything that does not is treated as empty
  // rather than guessed at.
  const readRequest = (request) => {
    try {
      const fields = typeof request?.toObject === "function" ? request.toObject() : request;
      return fields && typeof fields === "object" ? fields : {};
    } catch {
      return {};
    }
  };

  const handle = (name, request) => {
    lastMethod = name;
    answered++;
    const method = name.slice(ServicePrefix.length).split("#")[0];
    const fields = readRequest(request);
    switch (method) {
      case "IsServiceAvailable":
        return ok({ is_available: () => true });
      case "GetState":
        return ok({ toObject: () => ({ state }) });
      // Every action is the host's to perform: this half owns no storage operation, which is what
      // keeps Windows formatting and ejecting in one place rather than two.
      case "Adopt":
      case "Unmount":
      case "Eject":
      case "Format":
      case "TrimAll": {
        const command = method.toLowerCase();
        request0(command, {
          driveId: typeof fields.drive_id === "string" ? fields.drive_id : "",
          blockDeviceId: typeof fields.block_device_id === "string" ? fields.block_device_id : "",
        });
        return ok({ toObject: () => ({}) });
      }
      default:
        return failed(`unhandled storage method ${method}`);
    }
  };

  // Fire-and-forget: Steam's UI does not wait on the action's own response, it waits for the state
  // to change. Reporting the outcome is the host's job through the next publication.
  const request0 = (command, payload) => {
    try {
      request(patchId, command, payload).catch(() => {});
    } catch {
      // An unallowlisted command must not take the transport down with it.
    }
  };

  const resolve = () => {
    runtime = getWebpackRuntime("storage");
    // The service module names the method this whole surface is gated on.
    if (!runtime.findUnique([ServiceToken])) {
      lastError = "storage service module was not a unique match";
      return false;
    }

    // The transport provider: exactly one module exports a function returning an object with
    // GetDefaultTransport.
    const ids = runtime.findUnique([TransportToken, "m_transport"]);
    if (!ids) {
      lastError = "transport provider was not a unique match";
      return false;
    }

    const exports = runtime(ids[0]);
    const keys = Object.keys(exports).filter((name) => typeof exports[name] === "function");
    for (const key of keys) {
      try {
        const provider = exports[key]();
        const candidate = provider?.GetDefaultTransport?.();
        if (candidate && typeof candidate.SendMsg === "function") {
          transport = candidate;
          return true;
        }
      } catch {
        // Not the provider; keep looking.
      }
    }

    lastError = "no export yielded a transport";
    return false;
  };

  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
    try {
      if (!resolve()) return { ok: false, error: lastError };
    } catch (error) {
      lastError = "storage transport resolution failed: " + String(error);
      return { ok: false, error: lastError };
    }

    const claim = claimMember(transport, "SendMsg", claimKeys, (original: any) => {
      if (typeof original !== "function") return original;
      return function SteamUiStorageSendMsg(this: unknown, name, request, response, options) {
        // Prefix first and nothing else on the pass-through path: this method carries every
        // service call Steam makes.
        if (typeof name === "string" && name.startsWith(ServicePrefix)) {
          return handle(name, request);
        }
        forwarded++;
        return original.call(this, name, request, response, options);
      };
    });
    if (!claim.ok) {
      lastError = claim.error;
      return { ok: false, error: lastError };
    }

    installed = true;
    lastError = "";
    unsubscribe = subscribe(patchId, (published) => {
      if (!published || typeof published !== "object") return;
      const drives = Array.isArray(published.drives) ? published.drives : [];
      const devices = Array.isArray(published.blockDevices) ? published.blockDevices : [];
      state = {
        drives: drives.slice(0, MaximumDrives).map((drive) => ({
          id: String(drive?.id ?? ""),
          is_formattable: drive?.formattable === true,
          is_unformatted: drive?.unformatted === true,
        })),
        block_devices: devices.slice(0, MaximumDrives).map((device) => ({
          block_device_id: String(device?.id ?? ""),
          drive_id: String(device?.driveId ?? ""),
          mount_paths: Array.isArray(device?.mountPaths) ? device.mountPaths.map(String) : [],
          has_steam_library: device?.hasSteamLibrary === true,
        })),
        is_adopt_supported: published.adoptSupported === true,
        is_unmount_supported: published.unmountSupported === true,
        is_trim_supported: published.trimSupported === true,
        is_trim_running: published.trimRunning === true,
      };
    });
    return { ok: true, installed: true, reclaimed: claim.reclaimed };
  };

  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    installed = false;
    if (unsubscribe) {
      unsubscribe();
      unsubscribe = null;
    }

    const released = releaseMember(transport, "SendMsg", claimKeys);
    if (!released.ok) {
      lastError = released.error ?? "storage transport release failed";
      return { ok: false, error: lastError };
    }

    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    resolved: !!transport,
    claimed: memberClaimed(transport, "SendMsg", claimKeys),
    drives: state.drives.length,
    blockDevices: state.block_devices.length,
    // Everything above can be true while the page shows nothing, because Steam only asks once its
    // own route is open. These say whether it ever asked.
    answered,
    forwarded,
    lastMethod,
    lastError,
  });

  return { install, remove, status };
}

registerGate("storage", createStorageService());
