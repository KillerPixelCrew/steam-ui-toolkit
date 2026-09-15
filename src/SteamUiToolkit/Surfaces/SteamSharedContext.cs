using System.Linq;

namespace SteamUiToolkit.Surfaces;

/// <summary>Reads whether SharedJSContext is still the connection a request was observed on.</summary>
internal static class SteamSharedContext
{
    /// <summary>Whether SharedJSContext is ready at exactly these generations.</summary>
    /// <param name="transport">The host-owned transport.</param>
    /// <param name="generations">The generations the caller observed.</param>
    /// <returns>True only for a ready channel that has not been replaced since.</returns>
    internal static bool IsReadyAt(ISteamUiTransport transport, SteamUiGenerations generations) =>
        transport.GetSnapshots().Any(snapshot => snapshot.Role == SteamUiTargetRole.SharedJsContext
            && snapshot.Health == SteamUiTransportHealth.Ready
            && snapshot.Generations == generations);
}
