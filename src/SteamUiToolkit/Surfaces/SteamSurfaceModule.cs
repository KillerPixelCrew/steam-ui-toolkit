using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>The three ways a value a surface asked for can be stored, in the wire's vocabulary.</summary>
public enum SteamSettingPersistence
{
    /// <summary>Let the backend decide the profile from what is running.</summary>
    Automatic,

    /// <summary>Write the global profile.</summary>
    Global,

    /// <summary>Write the running application's own profile.</summary>
    Application
}

/// <summary>Reads one exact payload shape.</summary>
/// <typeparam name="T">The value the shape carries.</typeparam>
/// <param name="payload">The request payload.</param>
/// <param name="value">The value, when this returns true.</param>
/// <returns>Whether the payload had that shape.</returns>
internal delegate bool SteamPayloadReader<T>(JsonElement payload, out T value);

/// <summary>Building blocks the surface module factories share.</summary>
internal static class SteamSurfaceModule
{
    /// <summary>Declares a surface that publishes one typed state.</summary>
    /// <param name="id">The module id.</param>
    /// <param name="patchId">The patch id the state is published under.</param>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">The current state, or null to publish nothing this round.</param>
    /// <param name="typeInfo">The state's generated serializer metadata.</param>
    /// <param name="patches">The patches that install the surface.</param>
    /// <param name="commands">The commands the surface answers.</param>
    /// <returns>The module to register.</returns>
    internal static ISteamUiModule Declare<T>(
        string id,
        string patchId,
        Func<bool> enabled,
        Func<ValueTask<T?>> read,
        JsonTypeInfo<T> typeInfo,
        IReadOnlyList<ISteamUiPatch> patches,
        IReadOnlyList<SteamUiCommandHandler> commands)
        where T : class
    {
        return new SteamUiModule(
            id,
            patches,
            [Publication(patchId, enabled, read, typeInfo)],
            commands);
    }

    /// <summary>One typed state publication.</summary>
    /// <remarks>
    ///     A null reading publishes nothing that round, which keeps "momentarily unavailable" distinct
    ///     from a zero — the same rule <see cref="SteamUiStatePublication" /> documents.
    /// </remarks>
    internal static SteamUiStatePublication Publication<T>(
        string patchId,
        Func<bool> enabled,
        Func<ValueTask<T?>> read,
        JsonTypeInfo<T> typeInfo)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(read);
        return new SteamUiStatePublication(patchId, enabled, async () =>
        {
            var state = await read().ConfigureAwait(false);
            return state is null ? null : JsonSerializer.SerializeToElement(state, typeInfo);
        });
    }

    /// <summary>A command whose payload must have one exact shape before the backend is reached.</summary>
    /// <param name="patchId">The addressed patch.</param>
    /// <param name="command">The command name.</param>
    /// <param name="reader">Reads the payload's exact shape.</param>
    /// <param name="apply">The backend operation for a valid payload.</param>
    /// <param name="invalid">The fixed refusal for any other payload.</param>
    /// <returns>The command handler.</returns>
    internal static SteamUiCommandHandler Command<T>(
        string patchId,
        string command,
        SteamPayloadReader<T> reader,
        Func<T, CancellationToken, Task<SteamUiCommandResult>> apply,
        string invalid)
    {
        return new SteamUiCommandHandler(patchId, command, (request, cancellationToken) =>
            reader(request.Payload, out var value)
                ? apply(value, cancellationToken)
                : Invalid(invalid));
    }

    /// <summary>A command that carries no payload the backend reads.</summary>
    /// <param name="patchId">The addressed patch.</param>
    /// <param name="command">The command name.</param>
    /// <param name="apply">The backend operation.</param>
    /// <returns>The command handler.</returns>
    internal static SteamUiCommandHandler Command(
        string patchId,
        string command,
        Func<CancellationToken, Task<SteamUiCommandResult>> apply)
    {
        return new SteamUiCommandHandler(patchId, command, (_, cancellationToken) => apply(cancellationToken));
    }

    /// <summary>Reads the wire shape every row-authored value write uses: a number and where to keep it.</summary>
    internal static bool TryReadValueWrite(
        JsonElement payload,
        out int value,
        out SteamSettingPersistence persistence)
    {
        persistence = default;
        if (!SteamUiPayload.TryReadInt(payload, "value", int.MinValue, int.MaxValue, out value)
            || !payload.TryGetProperty("persistence", out var persistenceProperty)
            || persistenceProperty.ValueKind != JsonValueKind.String
            || !SteamUiPayload.HasExactly(payload, 2))
        {
            return false;
        }

        switch (persistenceProperty.GetString())
        {
            case "automatic":
                persistence = SteamSettingPersistence.Automatic;
                return true;
            case "global":
                persistence = SteamSettingPersistence.Global;
                return true;
            case "application":
                persistence = SteamSettingPersistence.Application;
                return true;
            default:
                return false;
        }
    }

    /// <summary>A handler that refuses with one fixed reason before the backend is reached.</summary>
    internal static Task<SteamUiCommandResult> Invalid(string reason)
    {
        return Task.FromResult(new SteamUiCommandResult(false, reason));
    }
}
