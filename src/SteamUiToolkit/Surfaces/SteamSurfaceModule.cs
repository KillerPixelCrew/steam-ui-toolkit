using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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
    Application,
}

/// <summary>Building blocks the surface module factories share.</summary>
internal static class SteamSurfaceModule
{
    /// <summary>One typed state publication.</summary>
    /// <remarks>
    /// A null reading publishes nothing that round, which keeps "momentarily unavailable" distinct
    /// from a zero — the same rule <see cref="SteamUiStatePublication"/> documents.
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
            T? state = await read().ConfigureAwait(false);
            return state is null ? null : JsonSerializer.SerializeToElement(state, typeInfo);
        });
    }

    /// <summary>Reads the wire shape every row-authored value write uses: a number and where to keep it.</summary>
    internal static bool TryReadValueWrite(
        JsonElement payload,
        out int value,
        out SteamSettingPersistence persistence)
    {
        value = default;
        persistence = default;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("value", out JsonElement valueProperty)
            || valueProperty.ValueKind != JsonValueKind.Number
            || !valueProperty.TryGetInt32(out value)
            || !payload.TryGetProperty("persistence", out JsonElement persistenceProperty)
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
    internal static Task<SteamUiCommandResult> Invalid(string reason) =>
        Task.FromResult(new SteamUiCommandResult(false, reason));
}
