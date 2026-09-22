using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>What answers a row's "Use global" action.</summary>
/// <remarks>
///     A host with per-game profiles marks a row whose value the running game overrides by setting that
///     state's <c>OverrideId</c>. The row then says so and offers one action that removes the override,
///     so the value falls back to the host's global one. The toolkit only carries the id back; what an
///     override is and where it falls back to is the host's policy.
/// </remarks>
public interface ISteamProfileOverrideBackend
{
    /// <summary>Removes the running game's override for one setting.</summary>
    /// <param name="settingId">The id the row's state carried in <c>OverrideId</c>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> UseGlobalAsync(string settingId, CancellationToken cancellationToken);
}

/// <summary>The "Use global" command every row that can carry an override shares.</summary>
public static class SteamProfileOverride
{
    /// <summary>The command name the injected rows send.</summary>
    public const string Command = "useGlobal";

    /// <summary>Longest accepted setting id.</summary>
    public const int MaxSettingIdLength = 200;

    /// <summary>The handler for one row's patch.</summary>
    /// <param name="patchId">The row's patch.</param>
    /// <param name="backend">What answers it, or null when the host has no per-game profiles.</param>
    /// <returns>The handler.</returns>
    internal static SteamUiCommandHandler Handler(string patchId, ISteamProfileOverrideBackend? backend)
    {
        return SteamSurfaceModule.Command<string>(
            patchId,
            Command,
            TryRead,
            (settingId, cancellationToken) => backend is null
                ? Task.FromResult(new SteamUiCommandResult(false, "This row has no per-game override to clear."))
                : backend.UseGlobalAsync(settingId, cancellationToken),
            "The use-global payload is invalid.");
    }

    /// <summary>Reads the one-property payload <c>{ "id": "…" }</c>.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="settingId">The setting id.</param>
    /// <returns>Whether the payload has exactly that shape.</returns>
    internal static bool TryRead(JsonElement payload, out string settingId)
    {
        return SteamUiPayload.TryReadBoundedString(payload, "id", MaxSettingIdLength, out settingId)
               && SteamUiPayload.HasExactly(payload, 1);
    }
}
