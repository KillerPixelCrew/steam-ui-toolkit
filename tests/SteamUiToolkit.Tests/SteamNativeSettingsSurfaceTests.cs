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
        Backend backend = new();

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
        Backend backend = new();

        var result = await DispatchAsync(Modules(backend), SteamNativeSettingsSurface.PatchId, "set",
            "{\"key\":\"device/lighting\",\"value\":" + value + "}");

        Assert.True(result.Succeeded);
        Assert.Equal(("device/lighting", value), Assert.Single(backend.Calls));
    }

    [Fact]
    public async Task BoundariesRefuseOversizedKeysAndStrings()
    {
        Backend backend = new();
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

    [Theory]
    [InlineData("descriptors")]
    [InlineData("root")]
    [InlineData("react")]
    [InlineData("jsx")]
    [InlineData("fields")]
    [InlineData("focusable")]
    public void EveryNativeContractMustBeUnique(string missing)
    {
        var gate = (SteamGatePatch)SteamNativeSettingsSurface.Patch;
        var facts = new Dictionary<string, int>
        {
            ["descriptors"] = 1,
            ["root"] = 1,
            ["react"] = 1,
            ["jsx"] = 1,
            ["fields"] = 1,
            ["focusable"] = 1
        };
        Assert.True(gate.Compatible(JsonSerializer.SerializeToElement(facts)));
        facts[missing] = 2;
        Assert.False(gate.Compatible(JsonSerializer.SerializeToElement(facts)));
    }

    private static SteamUiModuleSet Modules(Backend backend)
    {
        return new SteamUiModuleSet([
            SteamNativeSettingsSurface.Module(Always,
                () => new ValueTask<SteamNativeSettingsState?>(null as SteamNativeSettingsState), backend)
        ]);
    }

    private sealed class Backend : ISteamNativeSettingsBackend
    {
        internal List<(string Key, string Value)> Calls { get; } = [];

        public Task<SteamUiCommandResult> SetAsync(string key, JsonElement value, CancellationToken cancellationToken)
        {
            Calls.Add((key, value.GetRawText()));
            return Task.FromResult(SteamUiCommandResult.Applied);
        }
    }
}
