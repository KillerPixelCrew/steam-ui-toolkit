using System.Text.Json;
using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

public sealed class SteamNativeSettingsSurfaceTests
{
    [Theory]
    [InlineData("{\"key\":\"\",\"value\":true}")]
    [InlineData("{\"key\":\"device/lighting\",\"value\":{}}")]
    [InlineData("{\"key\":\"device/lighting\",\"value\":[]}")]
    [InlineData("{\"key\":\"device/lighting\",\"value\":null}")]
    [InlineData("{\"key\":\"device/lighting\",\"value\":true,\"extra\":1}")]
    public async Task InvalidChangesNeverReachTheOwner(string payload)
    {
        RecordingBackend backend = new();

        var result = await DispatchAsync(Modules(backend), SteamNativeSettingsSurface.PatchId, "set", payload);

        Assert.False(result.Succeeded);
        Assert.Empty(backend.Calls);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("14")]
    [InlineData("\"hsla(180, 50%, 50%, 1)\"")]
    public async Task PrimitiveValuesReachTheCurrentOwnerOnce(string value)
    {
        RecordingBackend backend = new();

        var result = await DispatchAsync(Modules(backend), SteamNativeSettingsSurface.PatchId, "set",
            "{\"key\":\"device/lighting\",\"value\":" + value + "}");

        Assert.True(result.Succeeded);
        Assert.Equal($"setting device/lighting {value}", Assert.Single(backend.Calls));
    }

    [Theory]
    [InlineData("{\"operation\":\"add\",\"width\":1760,\"height\":1100}", true)]
    [InlineData("{\"operation\":\"remove\",\"width\":1760,\"height\":1100}", true)]
    [InlineData("{\"operation\":\"steps\",\"width\":2560,\"height\":1600}", true)]
    [InlineData("{\"operation\":\"refresh\"}", true)]
    [InlineData("{\"operation\":\"restore\"}", true)]
    [InlineData("{\"operation\":\"restart\"}", false)]
    [InlineData("{\"operation\":\"add\",\"width\":\"1760\",\"height\":1100}", false)]
    [InlineData("{\"operation\":\"add\",\"width\":99,\"height\":1100}", false)]
    [InlineData("{\"operation\":\"refresh\",\"extra\":1}", false)]
    public async Task ResolutionEditsKeepABoundedClosedVocabulary(string value, bool accepted)
    {
        RecordingBackend backend = new();
        var result = await DispatchAsync(Modules(backend), SteamNativeSettingsSurface.PatchId, "set",
            "{\"key\":\"resolutions\",\"value\":" + value + "}");
        Assert.Equal(accepted, result.Succeeded);
        Assert.Equal(accepted ? 1 : 0, backend.Calls.Count);
    }

    [Fact]
    public async Task BoundariesRefuseOversizedKeysAndStrings()
    {
        RecordingBackend backend = new();
        var modules = Modules(backend);
        var longKey = JsonSerializer.Serialize(new string('k', 1025));
        var longValue = JsonSerializer.Serialize(new string('v', 4097));

        Assert.False((await DispatchAsync(modules, SteamNativeSettingsSurface.PatchId, "set",
            "{\"key\":" + longKey + ",\"value\":true}")).Succeeded);
        Assert.False((await DispatchAsync(modules, SteamNativeSettingsSurface.PatchId, "set",
            "{\"key\":\"device/lighting\",\"value\":" + longValue + "}")).Succeeded);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public void NativePageAndOpaqueColorMetadataSurvivePublication()
    {
        var state = new SteamNativeSettingsState([
            new SteamNativeSettingsPage(SteamNativeSettingsPageId.Controller, [
                new SteamSettingsSection("Lighting", [
                    new SteamSettingsRow("device/lighting", SteamSettingsRowKind.Color, "Color",
                        Text: "#123456", ColorAlpha: false)
                ], "lighting")
            ])
        ], 42);

        var wire = SteamNativeSettingsSurface.Serialize(state);
        var page = wire.GetProperty("pages")[0];

        Assert.Equal("controller", page.GetProperty("id").GetString());
        Assert.Equal("lighting", page.GetProperty("sections")[0].GetProperty("id").GetString());
        Assert.False(page.GetProperty("sections")[0].GetProperty("rows")[0].GetProperty("colorAlpha").GetBoolean());
        Assert.Equal(42, wire.GetProperty("revision").GetInt64());
    }

    private static SteamUiModuleSet Modules(RecordingBackend backend)
    {
        return new SteamUiModuleSet([
            SteamNativeSettingsSurface.Module(Always,
                () => new ValueTask<SteamNativeSettingsState?>(null as SteamNativeSettingsState), backend)
        ]);
    }
}
