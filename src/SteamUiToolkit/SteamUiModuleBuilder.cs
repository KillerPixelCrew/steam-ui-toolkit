using System;
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
public static class SteamUiModuleBuilder
{
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
        ArgumentException.ThrowIfNullOrWhiteSpace(patchId);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return new SteamUiStatePublication(patchId, enabled, async () =>
        {
            var state = await read().ConfigureAwait(false);
            return state is null ? null : JsonSerializer.SerializeToElement(state, typeInfo);
        });
    }

    /// <summary>Creates a command that validates one exact payload before invoking its backend.</summary>
    /// <typeparam name="T">The parsed payload value.</typeparam>
    /// <param name="patchId">The addressed patch.</param>
    /// <param name="command">The command name.</param>
    /// <param name="reader">The exact payload reader.</param>
    /// <param name="apply">The backend operation.</param>
    /// <param name="invalid">The refusal shown when payload validation fails.</param>
    /// <returns>The command handler.</returns>
    public static SteamUiCommandHandler Command<T>(
        string patchId,
        string command,
        SteamUiPayloadReader<T> reader,
        Func<T, CancellationToken, Task<SteamUiCommandResult>> apply,
        string invalid)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(apply);
        return new SteamUiCommandHandler(patchId, command, (request, cancellationToken) =>
            reader(request.Payload, out var value)
                ? apply(value, cancellationToken)
                : Task.FromResult(new SteamUiCommandResult(false, invalid)));
    }

    /// <summary>Creates a command whose backend does not consume a payload.</summary>
    /// <param name="patchId">The addressed patch.</param>
    /// <param name="command">The command name.</param>
    /// <param name="apply">The backend operation.</param>
    /// <returns>The command handler.</returns>
    public static SteamUiCommandHandler Command(
        string patchId,
        string command,
        Func<CancellationToken, Task<SteamUiCommandResult>> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        return new SteamUiCommandHandler(patchId, command, (_, cancellationToken) => apply(cancellationToken));
    }
}
