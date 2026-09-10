using System.Text.Json;

namespace SteamUiToolkit.Tests;

public sealed class SteamHybridCoreTests
{
    [Fact]
    public void StateKeepsIdentitySeparateFromTheLabelTheUserReads()
    {
        var state = new SteamHybridCoreState(true,
            [new("automatic", "Automatic"), new("prefer-performance", "Prefer performance cores")],
            "prefer-performance",
            "4 performance and 4 efficiency cores.");

        JsonElement json = SteamHybridCoreRow.Serialize(state);

        Assert.Equal("prefer-performance", json.GetProperty("current").GetString());
        Assert.Equal("automatic", json.GetProperty("options")[0].GetProperty("id").GetString());
        Assert.Equal(
            "Prefer performance cores", json.GetProperty("options")[1].GetProperty("label").GetString());
    }

    [Fact]
    public void TheRowOwnsOneCommandAndItIsNotThePowerProfileRowsCommand()
    {
        SteamUiModuleSet modules = new([
            SteamHybridCoreRow.Module(() => true, () => new(null as SteamHybridCoreState), new Backend()),
            SteamPowerProfileRow.Module(
                () => true, () => new(null as SteamPowerProfileState), new ProfileBackend()),
        ]);

        Assert.Equal(SteamHybridCoreRow.Commands, modules.AllowedCommands[SteamHybridCoreRow.PatchId]);
        Assert.NotEqual(SteamHybridCoreRow.PatchId, SteamPowerProfileRow.PatchId);
        Assert.False(
            modules.TryGetCommand(SteamHybridCoreRow.PatchId, "setPowerProfile", out _));
    }

    [Theory]
    [InlineData("{\"target\":\"prefer-efficiency\"}", true)]
    [InlineData("{\"target\":123}", false)]
    [InlineData("{\"target\":\"\"}", false)]
    [InlineData("{\"target\":\"prefer-efficiency\",\"extra\":true}", false)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    public async Task DispatchValidatesPayloadAndForwardsTheCancellationToken(string json, bool valid)
    {
        Backend backend = new();
        SteamUiModuleSet modules = new([SteamHybridCoreRow.Module(
            () => true, () => new(null as SteamHybridCoreState), backend)]);
        Assert.True(modules.TryGetCommand(SteamHybridCoreRow.PatchId, "setHybridCores", out var handler));
        using JsonDocument payload = JsonDocument.Parse(json);
        using CancellationTokenSource cancellation = new();
        SteamUiBridgeRequest request = new(SteamUiBridgeHost.SchemaVersion, "request",
            SteamHybridCoreRow.PatchId, "setHybridCores", 1, 2, 3, 4, payload.RootElement.Clone());

        SteamUiCommandResult result = await handler!(request, cancellation.Token);

        Assert.Equal(valid, result.Succeeded);
        if (valid)
        {
            var call = Assert.Single(backend.Calls);
            Assert.Equal("prefer-efficiency", call.Option);
            Assert.Equal(cancellation.Token, call.Token);
        }
        else
        {
            Assert.Empty(backend.Calls);
            Assert.Equal("The processor core preference payload is invalid.", result.Error);
        }
    }

    private sealed class Backend : ISteamHybridCoreBackend
    {
        internal List<(string Option, CancellationToken Token)> Calls { get; } = [];

        public Task<SteamUiCommandResult> SetHybridCoresAsync(
            string option, CancellationToken cancellationToken)
        {
            Calls.Add((option, cancellationToken));
            return Task.FromResult(new SteamUiCommandResult(true, null));
        }
    }

    private sealed class ProfileBackend : ISteamPowerProfileBackend
    {
        public Task<SteamUiCommandResult> SetPowerProfileAsync(
            string option, CancellationToken cancellationToken)
            => Task.FromResult(new SteamUiCommandResult(true, null));
    }
}
