namespace SteamUiToolkit.Tests.Fakes;

/// <summary>Sends one bridge request straight to a module set's command handler.</summary>
internal static class SurfaceDispatch
{
    internal static readonly Func<bool> Always = () => true;

    /// <summary>A request authorized under sequence 1, action 2, context 3 and document 4.</summary>
    internal static SteamUiBridgeRequest Request(string patchId, string command, string payloadJson) =>
        new(
            SteamUiBridgeHost.SchemaVersion,
            "request",
            patchId,
            command,
            1,
            2,
            3,
            4,
            TestJson.Parse(payloadJson));

    internal static async Task<SteamUiCommandResult> DispatchAsync(
        SteamUiModuleSet set,
        string patchId,
        string command,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        Assert.True(set.TryGetCommand(patchId, command, out SteamUiCommandDelegate? handler));
        return await handler!(Request(patchId, command, payloadJson), cancellationToken);
    }
}
