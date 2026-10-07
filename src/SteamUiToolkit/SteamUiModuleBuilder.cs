using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Reads and validates one plugin-owned command payload.</summary>
/// <typeparam name="T">The validated value passed to the command implementation.</typeparam>
/// <param name="payload">The bridge payload.</param>
/// <param name="value">The parsed value when validation succeeds.</param>
/// <returns>Whether the payload had the exact accepted shape.</returns>
public delegate bool SteamUiPayloadReader<T>(JsonElement payload, out T value);

/// <summary>Public building blocks for consumer- and plugin-owned typed Steam UI modules.</summary>
/// <remarks>
///     A null typed reading withholds that publication; it does not clear the document's last state.
///     To retract content while its patch remains installed, publish the surface's explicit empty
///     state. Patch enablement and removal are separate host decisions. A publication revision must
///     change whenever its serialized state changes, including a change to empty state.
/// </remarks>
public static class SteamUiModuleBuilder
{
    /// <summary>Declares a surface that publishes one typed state.</summary>
    /// <typeparam name="T">The published reference-type state.</typeparam>
    /// <param name="id">The module id.</param>
    /// <param name="patchId">The patch the state is published under.</param>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">Reads the current state, or null to publish nothing this round.</param>
    /// <param name="typeInfo">Source-generated serializer metadata for the state.</param>
    /// <param name="patches">The patches that install the surface.</param>
    /// <param name="commands">The commands the surface answers.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module<T>(
        string id,
        string patchId,
        Func<bool> enabled,
        Func<ValueTask<T?>> read,
        JsonTypeInfo<T> typeInfo,
        IReadOnlyList<ISteamUiPatch> patches,
        IReadOnlyList<SteamUiCommandHandler> commands)
        where T : class
    {
        return new SteamUiModule(id, patches, [Publication(patchId, enabled, read, typeInfo)], commands);
    }

    /// <summary>Creates a typed state publication with source-generated JSON metadata.</summary>
    /// <typeparam name="T">The published reference-type state.</typeparam>
    /// <param name="patchId">The patch receiving the state.</param>
    /// <param name="enabled">Whether this publication is currently active.</param>
    /// <param name="read">Reads the current state, or null to publish nothing.</param>
    /// <param name="typeInfo">Source-generated serializer metadata for the state.</param>
    /// <returns>The module publication.</returns>
    public static SteamUiStatePublication Publication<T>(
        string patchId,
        Func<bool> enabled,
        Func<ValueTask<T?>> read,
        JsonTypeInfo<T> typeInfo)
        where T : class
    {
        return Create(patchId, enabled, read, typeInfo, null);
    }

    /// <summary>Creates a typed state publication that the host stamps with a revision.</summary>
    /// <typeparam name="T">The published reference-type state.</typeparam>
    /// <param name="patchId">The patch receiving the state.</param>
    /// <param name="enabled">Whether this publication is currently active.</param>
    /// <param name="read">Reads the current state, or null to publish nothing.</param>
    /// <param name="typeInfo">Source-generated serializer metadata for the state.</param>
    /// <param name="revision">
    ///     The state's current revision; see <see cref="SteamUiStatePublication.Revision" />.
    /// </param>
    /// <returns>The module publication.</returns>
    public static SteamUiStatePublication Publication<T>(
        string patchId,
        Func<bool> enabled,
        Func<ValueTask<T?>> read,
        JsonTypeInfo<T> typeInfo,
        Func<long> revision)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(revision);
        return Create(patchId, enabled, read, typeInfo, revision);
    }

    private static SteamUiStatePublication Create<T>(
        string patchId,
        Func<bool> enabled,
        Func<ValueTask<T?>> read,
        JsonTypeInfo<T> typeInfo,
        Func<long>? revision)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patchId);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return new SteamUiStatePublication(patchId, enabled, async () =>
        {
            var state = await read().ConfigureAwait(false);
            return state is null ? null : JsonSerializer.SerializeToElement(state, typeInfo);
        }, revision);
    }

    /// <summary>Creates a command that validates one exact payload before invoking its backend.</summary>
    /// <typeparam name="T">The parsed payload value.</typeparam>
    /// <param name="patchId">The addressed patch.</param>
    /// <param name="command">The command name.</param>
    /// <param name="reader">The exact payload reader.</param>
    /// <param name="apply">The backend operation.</param>
    /// <param name="invalid">The refusal shown when payload validation fails.</param>
    /// <returns>The command handler.</returns>
    /// <remarks>
    ///     <paramref name="apply" /> runs on the bridge's request pump in arrival order and must return
    ///     its task promptly; see <see cref="SteamUiCommandDelegate" />.
    /// </remarks>
    public static SteamUiCommandHandler Command<T>(
        string patchId,
        string command,
        SteamUiPayloadReader<T> reader,
        Func<T, CancellationToken, Task<SteamUiCommandResult>> apply,
        string invalid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(apply);
        return new SteamUiCommandHandler(patchId, command, (request, cancellationToken) =>
            reader(request.Payload, out var value)
                ? apply(value, cancellationToken)
                : Task.FromResult(SteamUiCommandResult.Invalid(invalid)));
    }

    /// <summary>Creates a command whose backend does not consume a payload.</summary>
    /// <param name="patchId">The addressed patch.</param>
    /// <param name="command">The command name.</param>
    /// <param name="apply">The backend operation.</param>
    /// <returns>The command handler.</returns>
    /// <remarks>
    ///     <paramref name="apply" /> runs on the bridge's request pump in arrival order and must return
    ///     its task promptly; see <see cref="SteamUiCommandDelegate" />.
    /// </remarks>
    public static SteamUiCommandHandler Command(
        string patchId,
        string command,
        Func<CancellationToken, Task<SteamUiCommandResult>> apply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(apply);
        return new SteamUiCommandHandler(patchId, command, (_, cancellationToken) => apply(cancellationToken));
    }
}
