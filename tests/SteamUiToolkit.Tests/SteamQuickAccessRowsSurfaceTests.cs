using System.Text.Json;
using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

/// <summary>The host's Quick Access rows: the one publication, the one command and the one host patch.</summary>
public sealed class SteamQuickAccessRowsSurfaceTests
{
    private static ISteamUiModule Module(RecordingBackend backend, Func<long>? revision = null)
    {
        return SteamQuickAccessRowsSurface.Module(Always,
            () => new ValueTask<SteamQuickAccessRowsState?>(null as SteamQuickAccessRowsState), backend, revision);
    }

    [Theory]
    [InlineData("{\"key\":\"\",\"value\":true}")]
    [InlineData("{\"key\":\"   \",\"value\":true}")]
    [InlineData("{\"key\":\"power.scheme\",\"value\":{}}")]
    [InlineData("{\"key\":\"power.scheme\",\"value\":null}")]
    [InlineData("{\"key\":\"power.scheme\",\"value\":true,\"extra\":1}")]
    [InlineData("{\"key\":\"power.scheme\"}")]
    [InlineData("{\"key\":7,\"value\":true}")]
    [InlineData("[\"power.scheme\",true]")]
    [InlineData("null")]
    public async Task InvalidChangesNeverReachTheBackend(string payload)
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([Module(backend)]);

        var result = await DispatchAsync(set, SteamQuickAccessRowsSurface.PatchId, "set", payload);

        Assert.False(result.Succeeded);
        Assert.Equal("The Quick Access row payload is invalid.", result.Error);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task OversizedKeysTextsAndCurvesAreRefusedAtTheirBounds()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([Module(backend)]);
        var longest = new string('k', SteamQuickAccessRowsSurface.MaximumKeyLength);
        string Payload(string key, string value)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object> { ["key"] = key, ["value"] = value });
        }

        var key = await DispatchAsync(set, SteamQuickAccessRowsSurface.PatchId, "set",
            Payload(longest + "k", "x"));
        var text = await DispatchAsync(set, SteamQuickAccessRowsSurface.PatchId, "set",
            Payload("name", new string('t', SteamQuickAccessRowsSurface.MaximumTextLength + 1)));
        var points = string.Join(",", Enumerable.Range(0, SteamQuickAccessRowsSurface.MaximumArrayLength + 1)
            .Select(index => $"{{\"input\":{index},\"output\":1}}"));
        var curve = await DispatchAsync(set, SteamQuickAccessRowsSurface.PatchId, "set",
            $"{{\"key\":\"fan\",\"value\":[{points}]}}");
        Assert.False(key.Succeeded);
        Assert.False(text.Succeeded);
        Assert.False(curve.Succeeded);
        Assert.Empty(backend.Calls);

        var atBounds = await DispatchAsync(set, SteamQuickAccessRowsSurface.PatchId, "set",
            Payload(longest, new string('t', SteamQuickAccessRowsSurface.MaximumTextLength)));
        Assert.True(atBounds.Succeeded);
        Assert.Single(backend.Calls);
    }

    [Theory]
    [InlineData("{\"key\":\"power.vrr\",\"value\":true}", "setting power.vrr true")]
    [InlineData("{\"key\":\"power.sustained\",\"value\":15}", "setting power.sustained 15")]
    [InlineData("{\"key\":\"power.scheme\",\"value\":\"balanced\"}", "setting power.scheme \"balanced\"")]
    [InlineData("{\"value\":[{\"input\":40,\"output\":35}],\"key\":\"fan\"}",
        "setting fan [{\"input\":40,\"output\":35}]")]
    public async Task EveryValueKindReachesTheBackendWithItsKeyUnchanged(string payload, string call)
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([Module(backend)]);

        var result = await DispatchAsync(set, SteamQuickAccessRowsSurface.PatchId, "set", payload);

        Assert.True(result.Succeeded);
        Assert.Equal(call, Assert.Single(backend.Calls));
    }

    [Fact]
    public void TheModuleRegistersTheOneHostPatchThePublicationAndTheSetCommand()
    {
        var module = Module(new RecordingBackend());

        Assert.Same(SteamQuickAccessRowsSurface.HostPatch, Assert.Single(module.Patches));
        Assert.Equal(SteamQuickAccessRowsSurface.HostPatchId, SteamQuickAccessRowsSurface.HostPatch.Id);
        Assert.Equal("nativeComponents", SteamQuickAccessRowsSurface.HostPatch.GateName);
        Assert.Equal(SteamUiTargetRole.SharedJsContext, SteamQuickAccessRowsSurface.HostPatch.TargetRole);
        Assert.Equal(SteamQuickAccessRowsSurface.PatchId, Assert.Single(module.Publications).PatchId);
        var command = Assert.Single(module.Commands);
        Assert.Equal(SteamQuickAccessRowsSurface.PatchId, command.PatchId);
        Assert.Equal(["set"], SteamQuickAccessRowsSurface.Commands);
        Assert.Equal(command.Command, Assert.Single(SteamQuickAccessRowsSurface.Commands));
        Assert.Equal("quick-access-rows", module.Id);
    }

    [Fact]
    public async Task TheHostPatchAsksTheGateItselfInEveryPhase()
    {
        await using var transport = new FakeSteamUiTransport();
        var context = new SteamUiPatchContext(transport, TimeSpan.FromSeconds(1));
        var patch = SteamQuickAccessRowsSurface.HostPatch;

        await patch.ProbeAsync(context, CancellationToken.None);
        await patch.ApplyAsync(context, CancellationToken.None);
        await patch.VerifyAsync(context, CancellationToken.None);
        await patch.RemoveAsync(context, CancellationToken.None);

        Assert.Equal(4, transport.Expressions.Count);
        Assert.All(transport.Expressions, expression =>
            Assert.Contains("b.gate(\"nativeComponents\")", expression, StringComparison.Ordinal));
        Assert.Contains("bridge.probe()", transport.Expressions[0], StringComparison.Ordinal);
        Assert.Contains("bridge.install()", transport.Expressions[1], StringComparison.Ordinal);
        Assert.Contains("status.installed===true&&status.held===true", transport.Expressions[2],
            StringComparison.Ordinal);
        Assert.Contains("bridge.remove()", transport.Expressions[3], StringComparison.Ordinal);
    }

    [Fact]
    public void TheHostStatusReportNamesEachTabsAppendTheRowOutcomesAndTheToggle()
    {
        using var status = JsonDocument.Parse(
            """{"ok":true,"status":{"lastAppend":{"controls":3},"lastAppendQuickSettings":null,"renderOutcomes":{"frameLimit":"rendered"},"toggleResolved":true}}""");
        using var unreachable = JsonDocument.Parse("""{"ok":false,"error":"bridge unavailable"}""");
        using var other = JsonDocument.Parse("""{"ok":false}""");

        Assert.Equal(
            """{"append":{"controls":3},"appendQuickSettings":{"never":true},"rows":{"frameLimit":"rendered"},"toggle":true}""",
            SteamQuickAccessRowsSurface.HostStatusReport(status.RootElement, null));
        Assert.Equal("""{"error":"bridge unavailable"}""",
            SteamQuickAccessRowsSurface.HostStatusReport(unreachable.RootElement, "bridge unavailable"));
        Assert.Null(SteamQuickAccessRowsSurface.HostStatusReport(other.RootElement, "something else"));
    }

    [Fact]
    public void EveryFieldTheHostRowsReadIsOnTheWireInCamelCase()
    {
        SteamQuickAccessRowsState state = new(
        [
            new SteamQuickAccessRowSection("powerProfile",
            [
                new SteamSettingsRow("power.scheme", SteamSettingsRowKind.Choice, "Windows power profile",
                    Text: "custom",
                    Choices:
                    [
                        new SteamSettingsChoice("balanced", "Balanced"),
                        new SteamSettingsChoice("custom", "Custom", false)
                    ],
                    Icon: "power", LabelToken: "#QuickAccess_Tab_Perf_PowerProfile", Summary: "Custom")
            ]),
            new SteamQuickAccessRowSection("settings.gpu", [], "Intel graphics", "M0 0h24v24H0Z",
                [new SteamSettingsSection("Adapter", [], "adapter")])
        ], 9);

        var wire = SteamQuickAccessRowsSurface.Serialize(state);

        Assert.Equal(9, wire.GetProperty("revision").GetInt64());
        var first = wire.GetProperty("sections")[0];
        Assert.Equal("powerProfile", first.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("title").ValueKind);
        var row = first.GetProperty("rows")[0];
        Assert.Equal("power", row.GetProperty("icon").GetString());
        Assert.Equal("#QuickAccess_Tab_Perf_PowerProfile", row.GetProperty("labelToken").GetString());
        Assert.Equal("Custom", row.GetProperty("summary").GetString());
        Assert.True(row.GetProperty("choices")[0].GetProperty("selectable").GetBoolean());
        Assert.False(row.GetProperty("choices")[1].GetProperty("selectable").GetBoolean());
        var titled = wire.GetProperty("sections")[1];
        Assert.Equal("Intel graphics", titled.GetProperty("title").GetString());
        Assert.Equal("M0 0h24v24H0Z", titled.GetProperty("glyph").GetString());
        Assert.Equal("adapter", titled.GetProperty("groups")[0].GetProperty("id").GetString());
        Assert.Equal("Adapter", titled.GetProperty("groups")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task PublicationRevisionComesFromTheOwnerWithoutReadingItsState()
    {
        RecordingBackend backend = new();
        var revision = 3L;
        var reads = 0;
        var module = SteamQuickAccessRowsSurface.Module(Always, () =>
        {
            reads++;
            return new ValueTask<SteamQuickAccessRowsState?>(new SteamQuickAccessRowsState([], revision));
        }, backend, () => revision);
        var publication = Assert.Single(module.Publications);

        Assert.NotNull(publication.Revision);
        Assert.Equal(3L, publication.Revision!());
        revision++;
        Assert.Equal(4L, publication.Revision!());
        Assert.Equal(0, reads);
        var wire = await publication.Read();
        Assert.Equal(4L, wire!.Value.GetProperty("revision").GetInt64());
        Assert.Empty(wire.Value.GetProperty("sections").EnumerateArray());
        Assert.Equal(1, reads);
        Assert.Null(Assert.Single(Module(backend).Publications).Revision);
    }
}
