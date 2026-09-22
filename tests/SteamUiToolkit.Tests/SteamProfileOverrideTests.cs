using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

/// <summary>The shared Use global command a row with a game override sends.</summary>
public sealed class SteamProfileOverrideTests
{
    private sealed class Overrides : ISteamProfileOverrideBackend
    {
        internal List<string> Cleared { get; } = [];

        public Task<SteamUiCommandResult> UseGlobalAsync(string settingId, CancellationToken cancellationToken)
        {
            Cleared.Add(settingId);
            return Task.FromResult(new SteamUiCommandResult(true, null));
        }
    }

    private static SteamUiModuleSet Set(ISteamProfileOverrideBackend? overrides)
    {
        RecordingBackend backend = new();
        return new SteamUiModuleSet(
        [
            SteamVariableRefreshRow.Module(Always,
                () => new ValueTask<SteamVariableRefreshState?>(null as SteamVariableRefreshState), backend,
                overrides: overrides),
            SteamDeviceControlsRow.Module(Always,
                () => new ValueTask<SteamDeviceControlsState?>(null as SteamDeviceControlsState), backend,
                overrides: overrides)
        ]);
    }

    [Fact]
    public async Task TheSettingIdTheStateCarriedReachesTheBackend()
    {
        Overrides overrides = new();
        var set = Set(overrides);

        var vrr = await DispatchAsync(set, SteamVariableRefreshRow.PatchId, SteamProfileOverride.Command,
            """{"id":"VariableRefreshRate"}""");
        var zone = await DispatchAsync(set, SteamDeviceControlsRow.PatchId, SteamProfileOverride.Command,
            """{"id":"device:lighting.zone-color#left-ring"}""");

        Assert.True(vrr.Succeeded);
        Assert.True(zone.Succeeded);
        Assert.Equal(["VariableRefreshRate", "device:lighting.zone-color#left-ring"], overrides.Cleared);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"id":""}""")]
    [InlineData("""{"id":42}""")]
    [InlineData("""{"id":"a","extra":1}""")]
    public async Task AMalformedPayloadIsRefusedBeforeTheBackend(string payload)
    {
        Overrides overrides = new();

        var result = await DispatchAsync(Set(overrides), SteamVariableRefreshRow.PatchId,
            SteamProfileOverride.Command, payload);

        Assert.False(result.Succeeded);
        Assert.Equal("The use-global payload is invalid.", result.Error);
        Assert.Empty(overrides.Cleared);
    }

    [Fact]
    public async Task AnOverlongIdIsRefused()
    {
        Overrides overrides = new();

        var result = await DispatchAsync(Set(overrides), SteamVariableRefreshRow.PatchId,
            SteamProfileOverride.Command, $$"""{"id":"{{new string('x', SteamProfileOverride.MaxSettingIdLength + 1)}}"}""");

        Assert.False(result.Succeeded);
        Assert.Empty(overrides.Cleared);
    }

    [Fact]
    public async Task AHostWithoutProfilesRefusesWithAReason()
    {
        var result = await DispatchAsync(Set(null), SteamVariableRefreshRow.PatchId, SteamProfileOverride.Command,
            """{"id":"VariableRefreshRate"}""");

        Assert.False(result.Succeeded);
        Assert.Contains("no per-game override", result.Error, StringComparison.Ordinal);
    }
}
