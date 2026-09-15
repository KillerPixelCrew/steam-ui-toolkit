using System.Text.Json;

namespace SteamUiToolkit.Tests;

/// <summary>The choice rows: Windows power profiles, power presets and hybrid core preference.</summary>
public sealed class SteamChoiceRowTests
{
    [Fact]
    public void PowerProfileStateKeepsIdentitySeparateFromLocalizedLabels()
    {
        var state = new SteamPowerProfileState(true,
            [new("a", "Ausbalanciert"), new("b", "Ausbalanciert")], "b", "Ready");
        JsonElement json = SteamPowerProfileRow.Serialize(state);
        Assert.Equal("b", json.GetProperty("current").GetString());
        Assert.Equal("a", json.GetProperty("options")[0].GetProperty("id").GetString());
        Assert.Equal("Ausbalanciert", json.GetProperty("options")[1].GetProperty("label").GetString());
    }

    [Fact]
    public void HybridCoreStateKeepsIdentitySeparateFromTheLabelTheUserReads()
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

    [Theory]
    [InlineData("setAcPowerPreset", true)]
    [InlineData("setBatteryPowerPreset", false)]
    public async Task ClearingAnAssignmentForwardsNullToTheCorrectSource(string command, bool ac)
    {
        RecordingBackend backend = new();
        SteamUiModuleSet modules = new([SteamPowerPresetRow.Module(SurfaceDispatch.Always,
            () => new(null as SteamPowerPresetState), backend)]);

        Assert.True((await SurfaceDispatch.DispatchAsync(
            modules, SteamPowerPresetRow.PatchId, command, "{\"target\":null}")).Succeeded);
        Assert.Equal($"preset {(ac ? "ac" : "battery")} null", Assert.Single(backend.Calls));
        Assert.Equal(default, Assert.Single(backend.Tokens));
    }

    [Theory]
    [InlineData("{\"target\":\"battery\"}", true)]
    [InlineData("{\"target\":\"custom\"}", false)]
    [InlineData("{\"target\":123}", false)]
    [InlineData("{\"target\":\"battery\",\"extra\":true}", false)]
    public async Task PresetCommandsStaySeparateFromWindowsProfiles(string json, bool valid)
    {
        RecordingBackend backend = new();
        SteamUiModuleSet modules = new([
            SteamPowerProfileRow.Module(SurfaceDispatch.Always, () => new(null as SteamPowerProfileState), new RecordingBackend()),
            SteamPowerPresetRow.Module(SurfaceDispatch.Always, () => new(null as SteamPowerPresetState), backend),
        ]);
        using CancellationTokenSource cancellation = new();

        SteamUiCommandResult result = await SurfaceDispatch.DispatchAsync(
            modules, SteamPowerPresetRow.PatchId, "setAcPowerPreset", json, cancellation.Token);

        Assert.Equal(valid, result.Succeeded);
        if (valid)
        {
            Assert.Equal("preset ac battery", Assert.Single(backend.Calls));
            Assert.Equal(cancellation.Token, Assert.Single(backend.Tokens));
        }
        else
        {
            Assert.Empty(backend.Calls);
        }
    }

    [Theory]
    [InlineData("power-profile", "{\"target\":\"scheme-id\"}", "scheme-id")]
    [InlineData("power-profile", "{\"target\":123}", null)]
    [InlineData("power-profile", "{\"target\":\"\"}", null)]
    [InlineData("power-profile", "{\"target\":\"scheme-id\",\"extra\":true}", null)]
    [InlineData("power-profile", "{}", null)]
    [InlineData("power-profile", "[]", null)]
    [InlineData("hybrid-core", "{\"target\":\"prefer-efficiency\"}", "prefer-efficiency")]
    [InlineData("hybrid-core", "{\"target\":123}", null)]
    [InlineData("hybrid-core", "{\"target\":\"\"}", null)]
    [InlineData("hybrid-core", "{\"target\":\"prefer-efficiency\",\"extra\":true}", null)]
    [InlineData("hybrid-core", "{}", null)]
    [InlineData("hybrid-core", "[]", null)]
    public async Task DispatchValidatesPayloadAndForwardsTheCancellationToken(
        string row, string json, string? option)
    {
        RecordingBackend backend = new();
        (ISteamUiModule module, string patchId, string command, string call, string error) = row switch
        {
            "power-profile" => (
                SteamPowerProfileRow.Module(
                    SurfaceDispatch.Always, () => new(null as SteamPowerProfileState), backend),
                SteamPowerProfileRow.PatchId,
                "setPowerProfile",
                "profile",
                "The power-profile payload is invalid."),
            _ => (
                SteamHybridCoreRow.Module(
                    SurfaceDispatch.Always, () => new(null as SteamHybridCoreState), backend),
                SteamHybridCoreRow.PatchId,
                "setHybridCores",
                "hybrid",
                "The processor core preference payload is invalid."),
        };
        using CancellationTokenSource cancellation = new();

        SteamUiCommandResult result = await SurfaceDispatch.DispatchAsync(
            new SteamUiModuleSet([module]), patchId, command, json, cancellation.Token);

        Assert.Equal(option is not null, result.Succeeded);
        if (option is not null)
        {
            Assert.Equal($"{call} {option}", Assert.Single(backend.Calls));
            Assert.Equal(cancellation.Token, Assert.Single(backend.Tokens));
        }
        else
        {
            Assert.Empty(backend.Calls);
            Assert.Equal(error, result.Error);
        }
    }
}
