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

  // Claiming the transport is not enough, and this is the part that was wrong: Steam asks each of
  // these questions exactly once. Both queries are registered with `staleTime: 1/0`, and the state
  // query is `enabled:` on the availability answer, so the client's whole storage UI hangs off one
  // cached boolean.
  //
  // That answer is already cached by the time this gate can install. The availability query runs
  // when the first component using it mounts, which happens while the library is building itself --
  // before Steam's UI exists as a patch target at all. It goes to the real transport, the Windows
  // client has no service behind it, and the rejection is cached forever. From then on nothing asks
  // again: the drive menu's Eject and Format entries are gated on `is_unmount_supported` and
  // `is_adopt_supported` from a state query that is disabled, so they are simply absent, and no
  // StorageDeviceManager call is ever made for this gate to answer. Observed exactly that way on
  // the September 2026 beta: gate installed, resolved and claimed, 13 unrelated calls forwarded,
  // zero answered.
  //
  // Steam's own store solves this the same way when the service state changes -- it invalidates
  // both keys through the shared query client -- so this does what the client does, with the key
  // names read off the client's own module.
  const QueryClientTokens = ["ReactQueryDevtools", "offlineFirst"];
  const StorageQueryScope = "SystemStorageService";
  const AvailabilityQueryKey = [StorageQueryScope, "IsServiceAvailable"];
  const StateQueryKey = [StorageQueryScope, "State"];

  // A machine with more drives than this is not a handheld, and the state is rendered as rows.
  const MaximumDrives = 32;

  let runtime;
  let transport = null;
  let queryClient: { invalidateQueries: (options: { queryKey: unknown[] }) => void } | null = null;
  let installed = false;
  let lastError = "";
  let unsubscribe: (() => void) | null = null;
  let invalidated = 0;

  // What was last published, as the shape Steam would read. A publication that says the same thing
  // must not invalidate: the host republishes on its own poll, and invalidating each time would put
  // a GetState and a re-render on every tick for storage that has not changed.
  let stateSignature = "";

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
  let lastPayload = "";
  let lastRequest = "";

  /// One of Steam's uint32 identifiers, whatever JavaScript type it arrived as. Zero for anything
  /// that is not a usable identifier, which is the wire's own "not named".
  const asId = (value) => {
    const parsed = typeof value === "string" ? Number(value) : value;
    return typeof parsed === "number" && Number.isFinite(parsed) && parsed > 0 ? parsed : 0;
  };

  // Every route a generated message exposes a field through, tried in order. Steam's messages
  // define prototype accessors named after the declared field (that is what the `prototype.x ||
  // Sg(M())` guard at construction installs), `toObject()` may spell the key either way, and a
  // plain object hands the field back untouched. Reading only toObject() under the declared
  // spelling found nothing, so every action forwarded zero for both identifiers and the host
  // refused it as naming neither a volume nor a drive — the exact line the log printed for
  // Steam's eject. The identifier being asked for is the one this gate published, echoed back;
  // losing it here is losing our own number.
  const readId = (request, fields, declared, accessor) => {
    // The generated prototype accessor is a function named after the declared field (that is
    // what `Sg` installs: `proto[field] = () => getField(this, n)`), so on the body it has to be
    // called, not read -- reading it hands back the function, which is not an identifier.
    const body = typeof request?.Body === "function" ? request.Body() : request;
    const candidates = [
      fields?.[declared],
      fields?.[accessor],
      typeof body?.[declared] === "function" ? body[declared]() : body?.[declared],
    ];
    for (const value of candidates) {
      const id = asId(value);
      if (id > 0) return id;
    }
    return 0;
  };

  // The shape of the last action request, for the status probe: whether it was a message or a
  // plain object, what toObject() yielded, and which own and prototype names it carried. This is
  // what turns "forwarded zero" from a guess about the encoder into a fact about it.
  const describeRequest = (request, fields) => {
    try {
      const proto = request ? Object.getPrototypeOf(request) : null;
      return JSON.stringify({
        type: request?.constructor?.name ?? typeof request,
        hasToObject: typeof request?.toObject === "function",
        fieldKeys: fields && typeof fields === "object" ? Object.keys(fields).slice(0, 12) : [],
        ownKeys: request && typeof request === "object" ? Object.keys(request).slice(0, 12) : [],
        protoKeys: proto ? Object.getOwnPropertyNames(proto).slice(0, 24) : [],
      });
    } catch (error) {
      return "describe failed: " + String(error);
    }
  };

  // Steam's callers only ever ask a response two things, so the response is duck-typed rather than
  // built as a protobuf. Constructing a real Message would mean owning the wire format, which is
  // the client's business and not something this should mirror.
  // k_EResultOK. The action methods do not read the body at all: they return
  // `(await ...).GetEResult()` and the caller compares it against OK, so a response without that
  // method throws "GetEResult is not a function" inside an async click handler with nothing
  // attached to it. The button then does precisely nothing, with no error anywhere — which is what
  // eject did until this was found, while the read paths worked because the hooks use BSuccess and
  // Body instead.
  const ResultOk = 1;

  const ok = (body) =>
    Promise.resolve({ BSuccess: () => true, Body: () => body, GetEResult: () => ResultOk });

  const failed = (reason) =>
    Promise.resolve({
      BSuccess: () => false,
      Body: () => ({}),
      // 2 is k_EResultFail: a refusal has to be a result the caller can compare, not an absent
      // method that throws where the comparison would have been.
      GetEResult: () => 2,
      GetErrorMessage: () => reason,
    });

  // The request arrives already encoded. Steam's encoder yields a Message, which answers toObject(),
  // so the fields are readable without decoding bytes; anything that does not is treated as empty
  // rather than guessed at.
  // The request SendMsg receives is not the message. Steam's encoder (`I8`) wraps the generated
  // message in an envelope -- `P.InitFromObject(T, fields)` -- that carries a header beside it, and
  // the fields live on `Body()`. Reading toObject() off the envelope produced no fields at all,
  // so every action forwarded zero for both identifiers and was refused as naming neither a
  // volume nor a drive: the number being lost was the one this gate had published and Steam was
  // handing straight back. The body's toObject() (Steam's own `BT`) keys by the declared field
  // name, so block_device_id comes back spelled exactly as the message declares it.
  const readRequest = (request) => {
    try {
      const body = typeof request?.Body === "function" ? request.Body() : request;
      const fields = typeof body?.toObject === "function" ? body.toObject() : body;
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
        // Steam's identifiers are uint32 on the wire, and Unmount names the volume while the
        // drive-level actions name the drive. Zero means "not named": the client numbers these
        // from one, so it is unambiguous and the host refuses rather than guessing.
        //
        // Numbers are coerced rather than type-tested. The encoder hands these back through
        // toObject(), which is not obliged to return the same JavaScript type it was given, and a
        // strict typeof test turned an identifier that arrived as a string into zero — which the
        // host then refuses as "named neither a volume nor a drive", silently, because a refusal
        // that never reaches a backend logs nothing.
        // Adopt is Steam's SteamOS "make this drive a library": its Format Drive modal sends
        // Adopt, not Format, with the name the user typed and a validate flag. Both travel, so the
        // host can tell a register-only adopt from an erase-and-register one and label the result.
        const body = typeof request?.Body === "function" ? request.Body() : request;
        const readText = (name) => {
          const value = fields?.[name] ?? (typeof body?.[name] === "function" ? body[name]() : undefined);
          return typeof value === "string" ? value.slice(0, 64) : "";
        };
        const readFlag = (name) => {
          const value = fields?.[name] ?? (typeof body?.[name] === "function" ? body[name]() : undefined);
          return value === true;
        };
        const payload = {
          driveId: readId(request, fields, "drive_id", "driveId"),
          blockDeviceId: readId(request, fields, "block_device_id", "blockDeviceId"),
          label: readText("label"),
          validate: readFlag("validate"),
        };
        lastPayload = JSON.stringify(payload);
        lastRequest = describeRequest(request, fields);
        request0(command, payload);
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

  // The one shared query client, found by the module that builds it rather than by a name: it is
  // constructed once beside the provider and the devtools element, and exported as a plain object.
  // Duck-typed on invalidateQueries for the same reason the transport is duck-typed on SendMsg --
  // the export names are minified and change between builds, the shape does not.
  const resolveQueryClient = () => {
    const ids = runtime.findUnique(QueryClientTokens);
    if (!ids) return null;

    const exports = runtime(ids[0]);
    for (const key of Object.keys(exports)) {
      const candidate = exports[key];
      if (candidate && typeof candidate.invalidateQueries === "function") {
        return candidate;
      }
    }

    return null;
  };

  // Never fatal. A gate that answers Steam's questions is still strictly better than one that does
  // not, and the alternative to a missed invalidation is refusing to install at all.
  const invalidate = (queryKey: unknown[]) => {
    if (!queryClient) return;
    try {
      queryClient.invalidateQueries({ queryKey });
      invalidated++;
    } catch (error) {
      lastError = "invalidate failed: " + String(error);
    }
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
    queryClient = resolveQueryClient();
    unsubscribe = subscribe(patchId, (published) => {
      if (!published || typeof published !== "object") return;
      const drives = Array.isArray(published.drives) ? published.drives : [];
      const devices = Array.isArray(published.blockDevices) ? published.blockDevices : [];
      // Every field Steam declares, not only the ones an action needs. The client formats what it
      // is given without checking it got anything: a drive with no size_bytes renders "NaN B of
      // NaN B", and one with no adopt_stage renders a spinner forever, because undefined compares
      // unequal to the idle stage. Both were observed on the live page before this.
      state = {
        drives: drives.slice(0, MaximumDrives).map((drive) => ({
          id: Number(drive?.id ?? 0),
          model: String(drive?.model ?? ""),
          vendor: String(drive?.vendor ?? ""),
          serial: "",
          is_ejectable: drive?.ejectable === true,
          size_bytes: String(drive?.sizeBytes ?? 0),
          media_type: 0,
          is_unformatted: drive?.unformatted === true,
          // 1, not 0. Steam's adopt stage is a seven-value enum whose first member is Invalid and
          // whose second is the idle one the drive icon is gated on: `adopt_stage != 1` renders a
          // spinner. Publishing 0 spun the row forever, which looked exactly like omitting the
          // field and was diagnosed twice as that before the enum was read off the client.
          adopt_stage: 1,
          is_formattable: drive?.formattable === true,
          is_media_available: drive?.mediaAvailable !== false,
        })),
        block_devices: devices.slice(0, MaximumDrives).map((device) => ({
          id: Number(device?.id ?? 0),
          drive_id: Number(device?.driveId ?? 0),
          path: String(device?.friendlyPath ?? ""),
          friendly_path: String(device?.friendlyPath ?? ""),
          label: String(device?.label ?? ""),
          size_bytes: String(device?.sizeBytes ?? 0),
          is_formattable: false,
          is_read_only: false,
          is_root_device: false,
          content_type: 0,
          filesystem_type: 0,
          mount_paths: Array.isArray(device?.mountPaths) ? device.mountPaths.map(String) : [],
          is_unmounting: false,
          has_steam_library: device?.hasSteamLibrary === true,
        })),
        is_adopt_supported: published.adoptSupported === true,
        is_unmount_supported: published.unmountSupported === true,
        is_trim_supported: published.trimSupported === true,
        is_trim_running: published.trimRunning === true,
      };

      const signature = JSON.stringify(state);
      if (signature === stateSignature) return;
      stateSignature = signature;
      invalidate(StateQueryKey);
    });

    // Availability first: the state query stays disabled until that answer changes, so invalidating
    // the state key alone would drop the refetch on the floor.
    invalidate(AvailabilityQueryKey);
    invalidate(StateQueryKey);
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

    // Leaving the answers cached would leave Steam's pages offering Eject and Format against a
    // service that is no longer claimed, and the first press would reach a transport with nothing
    // behind it. Asking again puts the client back on its own answer, which is "unavailable".
    invalidate(AvailabilityQueryKey);
    invalidate(StateQueryKey);
    stateSignature = "";
    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    resolved: !!transport,
    claimed: memberClaimed(transport, "SendMsg", claimKeys),
    // Whether the client's cached answers were dropped. Zero here with answered also zero is the
    // signature of the failure this exists for: claimed, but Steam never asks.
    queryClient: !!queryClient,
    invalidated,
    drives: state.drives.length,
    blockDevices: state.block_devices.length,
    // Everything above can be true while the page shows nothing, because Steam only asks once its
    // own route is open. These say whether it ever asked.
    answered,
    forwarded,
    lastMethod,
    // What the last action actually forwarded. An identifier that arrived in an unexpected shape
    // is the difference between a press the host refused and a press it never saw.
    lastPayload,
    lastRequest,
    lastError,
  });

  return { install, remove, status };
}

registerGate("storage", createStorageService());
