using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One physical drive as Steam's storage manager understands it.</summary>
/// <param name="Id">
///     The drive's identifier. A number, not a string: Steam declares it <c>uint32</c> and renders it
///     through comparisons against a selected row id, so a string never matches and the row can never
///     be selected.
/// </param>
/// <param name="Model">The drive's model, which Steam shows as the row's name.</param>
/// <param name="Vendor">The drive's vendor, shown beside the model.</param>
/// <param name="SizeBytes">
///     The drive's capacity. Omitting it is what renders the row as "NaN B of NaN B" — Steam formats
///     the number it is given without checking that it got one.
/// </param>
/// <param name="Ejectable">Whether the drive can be removed, which also picks its icon.</param>
/// <param name="Formattable">Whether Steam may offer to format it.</param>
/// <param name="Unformatted">Whether it currently carries no usable filesystem.</param>
/// <param name="MediaAvailable">Whether media is present in the reader.</param>
/// <remarks>
///     The gate publishes Steam's idle adopt stage (1, not 0 — 0 is its Invalid member) for every
///     drive. Steam renders a spinner for any other stage, and an omitted field compares unequal to
///     the idle value too, so a drive without it spins forever.
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

/// <summary>One mounted volume on a drive.</summary>
/// <param name="Id">The volume's identifier, numeric for the same reason the drive's is.</param>
/// <param name="DriveId">The <see cref="SteamStorageDrive.Id" /> this volume sits on.</param>
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
///     Every operation is the host's. The injected half owns no storage behaviour at all, which is what
///     keeps one Windows implementation behind both Steam's pages and WSGM's own surfaces instead of
///     two that can disagree.
/// </remarks>
public interface ISteamStorageBackend
{
    /// <summary>Makes a drive a Steam library, the way SteamOS's adopt does.</summary>
    /// <param name="driveId">The drive to adopt.</param>
    /// <param name="label">The name the user gave it in Steam's Format Drive modal, or empty.</param>
    /// <param name="validate">
    ///     Steam's validate flag from that modal. On this client it is preset from the media type and
    ///     the user can toggle it; the host decides what, if anything, it means for its own format.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome.</returns>
    /// <remarks>
    ///     Steam's storage page never sends <c>Format</c>: its Format Drive modal sends Adopt with a
    ///     name. Adopt of a drive that carries no filesystem therefore means erase and register; adopt
    ///     of one that already has a filesystem means register what is there. The host owns that
    ///     distinction, and the destructive half of it stays behind the host's own switch.
    /// </remarks>
    Task<SteamUiCommandResult> AdoptAsync(
        uint driveId, string label, bool validate, CancellationToken cancellationToken);

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
///     Steam's own SteamOS storage management, revived on Windows.
/// </summary>
/// <remarks>
///     Big Picture ships a complete storage UI — drives, volumes, format, adopt, eject, trim — that
///     never appears on Windows. Mapped against the live client on 2026-09-10, the whole surface hangs
///     off one question: its hooks ask <c>StorageDeviceManager.IsServiceAvailable#1</c> over the WebUI
///     service transport, and every other query is gated on that answer. The Windows client has no
///     service behind it, so the answer never comes and the pages stay inert.
///     <para>
///         The gate claims <c>SendMsg</c> on the live transport instance. The method is defined on the
///         transport prototype as writable and configurable and the instance carries no own property, so
///         the claim is an own property that removal deletes, leaving Valve's method showing through
///         untouched. Every message not addressed to <c>StorageDeviceManager.</c> is forwarded to the
///         original unexamined, which matters because that one method carries all of Steam's service
///         traffic.
///     </para>
///     <para>
///         The message vocabulary is read from the client's own generated classes rather than guessed:
///         <c>IsServiceAvailable</c>, <c>GetState</c>, <c>Adopt</c>, <c>Unmount</c>, <c>Eject</c>,
///         <c>Format</c>, <c>TrimAll</c>. <c>CStorageDeviceManagerDrive</c> carries <c>id</c>,
///         <c>model</c>, <c>vendor</c>, <c>serial</c>, <c>is_ejectable</c>, <c>size_bytes</c>,
///         <c>media_type</c>, <c>is_unformatted</c>, <c>adopt_stage</c>, <c>is_formattable</c> and
///         <c>is_media_available</c>; <c>CStorageDeviceManagerBlockDevice</c> carries <c>id</c>,
///         <c>drive_id</c>, <c>path</c>, <c>friendly_path</c>, <c>label</c>, <c>size_bytes</c>,
///         <c>mount_paths</c>, <c>has_steam_library</c> and a few flags. Identifiers are <c>uint32</c>.
///     </para>
///     <para>
///         Three things about the client that this surface exists to get right, each found by driving it:
///         Steam asks <c>IsServiceAvailable</c> and <c>GetState</c> once and caches them forever, so the
///         gate invalidates its query keys on install and on every changed publication; the library-folder
///         row finds its volume by an exact match against <c>mount_paths</c>, so the library path has to
///         be published beside the volume root; and the page never sends <c>Format</c> — its Format Drive
///         modal sends <c>Adopt</c> with a label, because on SteamOS adopting a blank drive is what erases
///         it. Requests arrive as an envelope whose <c>Body()</c> holds the message.
///     </para>
/// </remarks>
public static class SteamStorageSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.storage";

    /// <summary>The exact command vocabulary the injected gate sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["adopt", "unmount", "eject", "format", "trimall"];

    /// <summary>The gate that answers Steam's storage service and forwards its actions.</summary>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "storage",
        "steam-ui-storage-v2:unique-service+unique-transport-module",
        // Source text only. The probe runs on every synchronization and never calls an export of
        // the transport module: invoking unidentified exports is what has restarted a machine and
        // signed Steam out. Whether the transport can be claimed is the gate's install to answer,
        // and it reports failure through its install result.
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_storage_probe_")}}
            return JSON.stringify({
              service:count(['StorageDeviceManager.IsServiceAvailable#1']),
              transportModule:count(['GetDefaultTransport','m_transport'])
            });
          {{SteamUiProbeJs.Close}}
          """,
        root =>
            SteamUiPatchEvaluation.IsOne(root, "service")
            && SteamUiPatchEvaluation.IsOne(root, "transportModule"),
        "status.installed&&status.resolved&&status.claimed",
        "!status.claimed",
        "Storage service gate");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamStorageState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamStorageState);
    }

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
        // Unmount and eject are the same operation under two of Steam's names.
        SteamUiCommandDelegate eject = (request, cancellationToken) =>
            Eject(backend, request.Payload, cancellationToken);
        return SteamUiModuleBuilder.Module(
            id,
            PatchId,
            enabled,
            read,
            SteamSurfaceJsonContext.Default.SteamStorageState,
            [Patch],
            [
                SteamUiModuleBuilder.Command<StorageRequest>(
                    PatchId,
                    "adopt",
                    TryReadDriveRequest,
                    (request, cancellationToken) => backend.AdoptAsync(
                        request.DriveId, request.Label, request.Validate, cancellationToken),
                    "The storage adopt payload is invalid."),
                new SteamUiCommandHandler(PatchId, "unmount", eject),
                new SteamUiCommandHandler(PatchId, "eject", eject),
                SteamUiModuleBuilder.Command<StorageRequest>(
                    PatchId,
                    "format",
                    TryReadDriveRequest,
                    (request, cancellationToken) => backend.FormatAsync(request.DriveId, cancellationToken),
                    "The storage format payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "trimall", backend.TrimAllAsync)
            ]);
    }

    private static Task<SteamUiCommandResult> Eject(
        ISteamStorageBackend backend, JsonElement payload, CancellationToken cancellationToken)
    {
        // Steam names one or the other depending on which row the user pressed, so neither alone is
        // required and both being unnamed is the refusal.
        return TryReadRequest(payload, out var request) && (request.BlockDeviceId != 0 || request.DriveId != 0)
            ? backend.EjectAsync(request.BlockDeviceId, request.DriveId, cancellationToken)
            : Task.FromResult(SteamUiCommandResult.Invalid(
                "The storage eject payload named neither a volume nor a drive."));
    }

    private static bool TryReadDriveRequest(JsonElement payload, out StorageRequest request)
    {
        return TryReadRequest(payload, out request) && request.DriveId != 0;
    }

    /// <summary>
    ///     Reads the one shape every storage action sends: <c>driveId</c>, <c>blockDeviceId</c>,
    ///     <c>label</c> and <c>validate</c>, all present and nothing else.
    /// </summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="request">The request, when this returns true.</param>
    /// <returns>Whether the payload has exactly that shape.</returns>
    /// <remarks>
    ///     The two identifiers are Steam's unsigned 32-bit ids, where zero is the wire's own "not
    ///     named": Steam numbers them from one. The label is what the user typed into Steam's Format
    ///     Drive modal, blank for none.
    /// </remarks>
    private static bool TryReadRequest(JsonElement payload, out StorageRequest request)
    {
        request = default;
        if (!SteamUiPayload.HasExactly(payload, 4)
            || !TryReadId(payload, "driveId", out var drive)
            || !TryReadId(payload, "blockDeviceId", out var device)
            || !SteamUiPayload.TryReadString(payload, "label", out var label)
            || !SteamUiPayload.TryReadBoolean(payload, "validate", out var validate))
        {
            return false;
        }

        request = new StorageRequest(drive, device, string.IsNullOrWhiteSpace(label) ? "" : label, validate);
        return true;
    }

    private static bool TryReadId(JsonElement payload, string propertyName, out uint id)
    {
        id = 0;
        return payload.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetUInt32(out id);
    }

    private readonly record struct StorageRequest(uint DriveId, uint BlockDeviceId, string Label, bool Validate);
}
