using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One physical drive as Steam's storage manager understands it.</summary>
/// <param name="Id">
/// The drive's identifier. A number, not a string: Steam declares it <c>uint32</c> and renders it
/// through comparisons against a selected row id, so a string never matches and the row can never
/// be selected.
/// </param>
/// <param name="Model">The drive's model, which Steam shows as the row's name.</param>
/// <param name="Vendor">The drive's vendor, shown beside the model.</param>
/// <param name="SizeBytes">
/// The drive's capacity. Omitting it is what renders the row as "NaN B of NaN B" — Steam formats
/// the number it is given without checking that it got one.
/// </param>
/// <param name="Ejectable">Whether the drive can be removed, which also picks its icon.</param>
/// <param name="Formattable">Whether Steam may offer to format it.</param>
/// <param name="Unformatted">Whether it currently carries no usable filesystem.</param>
/// <param name="MediaAvailable">Whether media is present in the reader.</param>
/// <remarks>
/// <see cref="SteamStorageAdoptStage.Idle"/> is published for every drive. Steam renders a
/// spinner for any other stage, so a drive with no stage at all spins forever — which is exactly
/// what an omitted field does, since undefined compares unequal to the idle value.
/// </remarks>
public sealed record SteamStorageDrive(
    uint Id,
    string Model,
    string Vendor,
    long SizeBytes,
    bool Ejectable,
    bool Formattable,
    bool Unformatted,
    bool MediaAvailable = true);

/// <summary>How far a drive is through being adopted as a Steam library.</summary>
/// <remarks>
/// Steam's own enum. Only <see cref="Idle"/> renders the drive's icon; everything else renders a
/// spinner, which is the whole reason the value has to be published rather than left out.
/// </remarks>
public enum SteamStorageAdoptStage
{
    /// <summary>Steam's first enum member, which it treats as no valid stage at all.</summary>
    Invalid = 0,

    /// <summary>Nothing in progress. The only stage that renders a drive rather than a spinner.</summary>
    Idle = 1,
}

/// <summary>One mounted volume on a drive.</summary>
/// <param name="Id">The volume's identifier, numeric for the same reason the drive's is.</param>
/// <param name="DriveId">The <see cref="SteamStorageDrive.Id"/> this volume sits on.</param>
/// <param name="Label">The volume label, which Steam shows as the row's name.</param>
/// <param name="FriendlyPath">The path as the user would recognise it, for example <c>D:\</c>.</param>
/// <param name="SizeBytes">The volume's size.</param>
/// <param name="MountPaths">Where it is mounted.</param>
/// <param name="HasSteamLibrary">Whether a Steam library is registered on it.</param>
public sealed record SteamStorageBlockDevice(
    uint Id,
    uint DriveId,
    string Label,
    string FriendlyPath,
    long SizeBytes,
    IReadOnlyList<string> MountPaths,
    bool HasSteamLibrary);

/// <summary>The storage state Steam's own pages render.</summary>
/// <param name="Drives">The drives to show.</param>
/// <param name="BlockDevices">The volumes on them.</param>
/// <param name="AdoptSupported">Whether the host can register a drive as a Steam library.</param>
/// <param name="UnmountSupported">Whether the host can eject.</param>
/// <param name="TrimSupported">Whether the host can trim.</param>
/// <param name="TrimRunning">Whether a trim is in progress right now.</param>
/// <param name="Revision">Monotonic host observation revision.</param>
public sealed record SteamStorageState(
    IReadOnlyList<SteamStorageDrive> Drives,
    IReadOnlyList<SteamStorageBlockDevice> BlockDevices,
    bool AdoptSupported = false,
    bool UnmountSupported = false,
    bool TrimSupported = false,
    bool TrimRunning = false,
    long Revision = 0);

/// <summary>What performs the storage actions Steam's pages invoke.</summary>
/// <remarks>
/// Every operation is the host's. The injected half owns no storage behaviour at all, which is what
/// keeps one Windows implementation behind both Steam's pages and WSGM's own surfaces instead of
/// two that can disagree.
/// </remarks>
public interface ISteamStorageBackend
{
    /// <summary>Registers a drive as a Steam library.</summary>
    /// <param name="driveId">The drive to adopt.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> AdoptAsync(uint driveId, CancellationToken cancellationToken);

    /// <summary>Safely ejects a volume.</summary>
    /// <param name="blockDeviceId">The volume to eject, or zero when the drive is named instead.</param>
    /// <param name="driveId">The drive to eject, or zero when the volume is named instead.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> EjectAsync(
        uint blockDeviceId, uint driveId, CancellationToken cancellationToken);

    /// <summary>Formats a drive.</summary>
    /// <param name="driveId">The drive to format.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> FormatAsync(uint driveId, CancellationToken cancellationToken);

    /// <summary>Trims every drive that supports it.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> TrimAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Steam's own SteamOS storage management, revived on Windows.
/// </summary>
/// <remarks>
/// Big Picture ships a complete storage UI — drives, volumes, format, adopt, eject, trim — that
/// never appears on Windows. Mapped against the live client on 2026-09-10, the whole surface hangs
/// off one question: its hooks ask <c>StorageDeviceManager.IsServiceAvailable#1</c> over the WebUI
/// service transport, and every other query is gated on that answer. The Windows client has no
/// service behind it, so the answer never comes and the pages stay inert.
/// <para>
/// The gate claims <c>SendMsg</c> on the live transport instance. The method is defined on the
/// transport prototype as writable and configurable and the instance carries no own property, so
/// the claim is an own property that removal deletes, leaving Valve's method showing through
/// untouched. Every message not addressed to <c>StorageDeviceManager.</c> is forwarded to the
/// original unexamined, which matters because that one method carries all of Steam's service
/// traffic.
/// </para>
/// <para>
/// The message vocabulary is read from the client's own generated classes rather than guessed:
/// <c>IsServiceAvailable</c>, <c>GetState</c>, <c>Eject</c>, <c>Adopt</c>, <c>Format</c>,
/// <c>Unmount</c>, <c>TrimAll</c>, over <c>CStorageDeviceManagerDrive</c>
/// (<c>id</c>, <c>is_formattable</c>, <c>is_unformatted</c>) and
/// <c>CStorageDeviceManagerBlockDevice</c> (<c>block_device_id</c>, <c>drive_id</c>,
/// <c>mount_paths</c>, <c>has_steam_library</c>).
/// </para>
/// </remarks>
public static class SteamStorageSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.storage";

    /// <summary>The exact command vocabulary the injected gate sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["adopt", "unmount", "eject", "format", "trimall"];

    /// <summary>The gate that answers Steam's storage service and forwards its actions.</summary>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        id: PatchId,
        resourceKey: "steam-ui.service-transport",
        gateName: "storage",
        fingerprint: "steam-storage-v1:unique-service+claimable-transport",
        probeExpression: $$"""
            {{SteamUiProbeJs.CountingPreamble("steam_ui_storage_probe_")}}
              const service=count(['StorageDeviceManager.IsServiceAvailable#1']);
              const transportModule=req.findUnique(['GetDefaultTransport','m_transport']);
              if(!transportModule)return JSON.stringify({service,transportModule:0});
              const exports=req(transportModule[0]);
              let transport=null;
              for(const key of Object.keys(exports)){
                if(typeof exports[key]!=='function')continue;
                try{
                  const candidate=exports[key]()?.GetDefaultTransport?.();
                  if(candidate&&typeof candidate.SendMsg==='function'){transport=candidate;break;}
                }catch{}
              }
              const proto=transport?Object.getOwnPropertyDescriptor(
                Object.getPrototypeOf(transport),'SendMsg'):null;
              return JSON.stringify({
                service,
                transportModule:1,
                transportResolved:transport?1:0,
                // Writable and configurable, or the claim could neither replace nor restore it.
                claimable:!!proto&&proto.writable===true&&proto.configurable===true,
                // Already ours is compatible; a successful apply must not fail its own next probe.
                claimed:!!transport&&transport.SendMsg.__steamUiStorageClaimed===true
              });
            }catch(error){return JSON.stringify({error:String(error)}); } })()
            """,
        compatible: root =>
            SteamUiPatchEvaluation.IsOne(root, "service")
            && SteamUiPatchEvaluation.IsOne(root, "transportModule")
            && SteamUiPatchEvaluation.IsOne(root, "transportResolved")
            && SteamGatePatch.Flag(root, "claimable"),
        verifyOk: "status.installed&&status.resolved&&status.claimed",
        removeOk: "!status.claimed",
        subject: "Storage service gate");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamStorageState state) =>
        JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamStorageState);

    /// <summary>Declares the surface as one module: the gate, the state, and the actions.</summary>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">The storage state, or null when there is nothing to say.</param>
    /// <param name="backend">What performs the actions.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamStorageState?>> read,
        ISteamStorageBackend backend,
        string id = "storage")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            patches: [Patch],
            publications:
            [
                SteamSurfaceModule.Publication(
                    PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamStorageState),
            ],
            commands:
            [
                new(PatchId, "adopt", (request, cancellationToken) =>
                    TryReadId(request, "driveId", out uint drive)
                        ? backend.AdoptAsync(drive, cancellationToken)
                        : SteamSurfaceModule.Invalid("The storage adopt payload is invalid.")),
                // Unmount and eject are the same operation under two of Steam's names.
                new(PatchId, "unmount", (request, cancellationToken) => Eject(backend, request, cancellationToken)),
                new(PatchId, "eject", (request, cancellationToken) => Eject(backend, request, cancellationToken)),
                new(PatchId, "format", (request, cancellationToken) =>
                    TryReadId(request, "driveId", out uint drive)
                        ? backend.FormatAsync(drive, cancellationToken)
                        : SteamSurfaceModule.Invalid("The storage format payload is invalid.")),
                new(PatchId, "trimall", (_, cancellationToken) => backend.TrimAllAsync(cancellationToken)),
            ]);
    }

    private static Task<SteamUiCommandResult> Eject(
        ISteamStorageBackend backend, SteamUiBridgeRequest request, CancellationToken cancellationToken)
    {
        // Steam names one or the other depending on which row the user pressed, so neither alone is
        // required and both being absent is the only refusal.
        _ = TryReadId(request, "blockDeviceId", out uint device);
        _ = TryReadId(request, "driveId", out uint drive);
        return device == 0 && drive == 0
            ? SteamSurfaceModule.Invalid("The storage eject payload named neither a volume nor a drive.")
            : backend.EjectAsync(device, drive, cancellationToken);
    }

    /// <summary>Reads one of Steam's storage identifiers, which are unsigned and never zero.</summary>
    /// <param name="request">The request carrying the payload.</param>
    /// <param name="propertyName">Which identifier to read.</param>
    /// <param name="id">The identifier, or zero when absent.</param>
    /// <returns>Whether a usable identifier was present.</returns>
    /// <remarks>
    /// Zero is treated as absent rather than as a drive. Steam numbers these from one, so nothing
    /// answers to zero, and reading a missing property as a valid id would send the host looking
    /// for a drive that was never named.
    /// </remarks>
    private static bool TryReadId(SteamUiBridgeRequest request, string propertyName, out uint id)
    {
        id = 0;
        if (!SteamUiPayload.TryReadInt(request.Payload, propertyName, 1, int.MaxValue, out int value))
        {
            return false;
        }

        id = (uint)value;
        return true;
    }
}
