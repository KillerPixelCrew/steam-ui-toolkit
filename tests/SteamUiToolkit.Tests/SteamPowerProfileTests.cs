using System.Text.Json;

namespace SteamUiToolkit.Tests;

public sealed class SteamPowerProfileTests
{
    [Fact]
    public void StateKeepsIdentitySeparateFromLocalizedLabels()
    {
        var state = new SteamPowerProfileState(true,
            [new("a", "Ausbalanciert"), new("b", "Ausbalanciert")], "b", "Ready");
        JsonElement json = SteamPowerProfileRow.Serialize(state);
        Assert.Equal("b", json.GetProperty("current").GetString());
        Assert.Equal("a", json.GetProperty("options")[0].GetProperty("id").GetString());
        Assert.Equal("Ausbalanciert", json.GetProperty("options")[1].GetProperty("label").GetString());
        SteamUiModuleSet modules = new([SteamPowerProfileRow.Module(() => true, () => new(state), new Backend())]);
        Assert.Equal(SteamPowerProfileRow.Commands, modules.AllowedCommands[SteamPowerProfileRow.PatchId]);
    }

    private sealed class Backend : ISteamPowerProfileBackend
    {
        internal List<(string Option, CancellationToken Token)> Calls { get; } = [];
        public Task<SteamUiCommandResult> SetPowerProfileAsync(string option, CancellationToken cancellationToken)
        {
            Calls.Add((option, cancellationToken));
            return Task.FromResult(new SteamUiCommandResult(true, null));
        }
    }

    [Theory]
    [InlineData("{\"target\":\"scheme-id\"}", true)]
    [InlineData("{\"target\":123}", false)]
    [InlineData("{\"target\":\"\"}", false)]
    [InlineData("{\"target\":\"scheme-id\",\"extra\":true}", false)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    public async Task DispatchValidatesPayloadAndForwardsTheCancellationToken(string json, bool valid)
    {
        Backend backend = new();
        SteamUiModuleSet modules = new([SteamPowerProfileRow.Module(() => true,
            () => new(null as SteamPowerProfileState), backend)]);
        Assert.True(modules.TryGetCommand(SteamPowerProfileRow.PatchId, "setPowerProfile", out var handler));
        using JsonDocument payload = JsonDocument.Parse(json);
        using CancellationTokenSource cancellation = new();
        SteamUiBridgeRequest request = new(SteamUiBridgeHost.SchemaVersion, "request",
            SteamPowerProfileRow.PatchId, "setPowerProfile", 1, 2, 3, 4, payload.RootElement.Clone());
        SteamUiCommandResult result = await handler!(request, cancellation.Token);
        Assert.Equal(valid, result.Succeeded);
        if (valid)
        {
            var call = Assert.Single(backend.Calls);
            Assert.Equal("scheme-id", call.Option);
            Assert.Equal(cancellation.Token, call.Token);
        }
        else
        {
            Assert.Empty(backend.Calls);
            Assert.Equal("The power-profile payload is invalid.", result.Error);
        }
    }
}
