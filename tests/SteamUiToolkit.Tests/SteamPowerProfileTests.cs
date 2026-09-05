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
        public Task<SteamUiCommandResult> SetPowerProfileAsync(string option, CancellationToken cancellationToken)
            => Task.FromResult(new SteamUiCommandResult(true, null));
    }
}
