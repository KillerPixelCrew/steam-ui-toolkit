namespace SteamUiToolkit.Tests;

/// <summary>The settings rows reach the renderer under the names <c>settings.ts</c> reads.</summary>
public sealed class SteamSettingsRowsTests
{
    [Fact]
    public void CurvesPublishFixedInputsAndSampledPresets()
    {
        SteamSettingsRow curve = new("fan", SteamSettingsRowKind.Curve, "Fan curve", Minimum: 0, Maximum: 100,
            Curve: [new SteamSettingsCurvePoint(40, 20), new SteamSettingsCurvePoint(80, 90)],
            CurvePresets: [new SteamSettingsCurvePreset("Quiet", [new SteamSettingsCurvePoint(40, 20), new SteamSettingsCurvePoint(80, 70)])]);
        var wire = SteamQuickAccessRowsSurface.Serialize(new SteamQuickAccessRowsState(
            [new SteamQuickAccessRowSection("deviceDescriptors", [curve])], 7));
        Assert.Equal(7, wire.GetProperty("revision").GetInt64());
        var row = wire.GetProperty("sections")[0].GetProperty("rows")[0];
        Assert.Equal(40, row.GetProperty("curve")[0].GetProperty("input").GetInt32());
        Assert.Equal(70, row.GetProperty("curvePresets")[0].GetProperty("points")[1].GetProperty("output").GetInt32());
    }

    [Fact]
    public void EveryFieldTheRendererReadsIsOnTheWireInCamelCase()
    {
        SteamSettingsPage page = new("steam", "Steam",
        [
            new SteamSettingsSection("Integration",
            [
                new SteamSettingsRow("cef", SteamSettingsRowKind.Boolean, "CEF", "Steam's debug port.", Checked: true,
                    Confirm: new SteamSettingsConfirmation(false, "Turn off?", "It goes away.", "Turn off")),
                new SteamSettingsRow("mode", SteamSettingsRowKind.Choice, "Mode", Text: "a",
                    Choices: [new SteamSettingsChoice("a", "A")]),
                new SteamSettingsRow("level", SteamSettingsRowKind.Range, "Level", Number: 3, Minimum: 0, Maximum: 10,
                    Step: 1, Suffix: " W"),
                new SteamSettingsRow("order", SteamSettingsRowKind.Order, "Order", Order: ["a", "b"], Disabled: true),
                new SteamSettingsRow("key", SteamSettingsRowKind.Secret, "Key", Text: "Set", MaximumLength: 64),
                new SteamSettingsRow("run", SteamSettingsRowKind.Action, "Run", ButtonLabel: "Go")
            ])
        ], "M1 1h2v2H1Z");

        var wire = SteamSettingsRows.Serialize([page])[0];
        var rows = wire.GetProperty("sections")[0].GetProperty("rows");

        Assert.Equal("steam", wire.GetProperty("id").GetString());
        Assert.Equal("M1 1h2v2H1Z", wire.GetProperty("glyph").GetString());
        Assert.Equal("Integration", wire.GetProperty("sections")[0].GetProperty("title").GetString());
        Assert.Equal("boolean", rows[0].GetProperty("kind").GetString());
        Assert.True(rows[0].GetProperty("checked").GetBoolean());
        Assert.False(rows[0].GetProperty("confirm").GetProperty("when").GetBoolean());
        Assert.Equal("Turn off", rows[0].GetProperty("confirm").GetProperty("confirmLabel").GetString());
        Assert.True(rows[0].GetProperty("confirm").GetProperty("destructive").GetBoolean());
        Assert.Equal("a", rows[1].GetProperty("choices")[0].GetProperty("value").GetString());
        Assert.Equal(10, rows[2].GetProperty("maximum").GetDouble());
        Assert.Equal(" W", rows[2].GetProperty("suffix").GetString());
        Assert.Equal("b", rows[3].GetProperty("order")[1].GetString());
        Assert.True(rows[3].GetProperty("disabled").GetBoolean());
        Assert.Equal(64, rows[4].GetProperty("maximumLength").GetInt32());
        Assert.Equal("Go", rows[5].GetProperty("buttonLabel").GetString());
    }

    [Fact]
    public void ScopeColumnsAndThePageLeadReachTheGateUnderTheNamesItReads()
    {
        SteamSettingsRow columns = new("timeouts", SteamSettingsRowKind.Columns, "Turn screen off after",
            Columns:
            [
                new SteamSettingsRow("battery", SteamSettingsRowKind.Choice, "On battery", Text: "300",
                    Choices: [new SteamSettingsChoice("300", "5 min")])
            ]);
        SteamNativeSettingsState state = new(
        [
            new SteamNativeSettingsPage(SteamNativeSettingsPageId.Display,
            [
                new SteamSettingsSection(null,
                    [new SteamSettingsRow("scope", SteamSettingsRowKind.Scope, "Editing", Text: "Global defaults")]),
                new SteamSettingsSection("Screen timeout", [columns])
            ], Lead: 2)
        ], 3);

        var page = SteamNativeSettingsSurface.Serialize(state).GetProperty("pages")[0];

        Assert.Equal(2, page.GetProperty("lead").GetInt32());
        Assert.Equal("scope", page.GetProperty("sections")[0].GetProperty("rows")[0].GetProperty("kind").GetString());
        var entry = page.GetProperty("sections")[1].GetProperty("rows")[0].GetProperty("columns")[0];
        Assert.Equal("battery", entry.GetProperty("key").GetString());
        Assert.Equal("5 min", entry.GetProperty("choices")[0].GetProperty("label").GetString());
    }

    [Fact]
    public void ASectionNamesItsSubpageUnderTheNamesTheRendererReads()
    {
        SteamSettingsPage page = new("display", "Display",
        [
            new SteamSettingsSection("Refresh", [new SteamSettingsRow("vrr", SteamSettingsRowKind.Boolean, "VRR")],
                "refresh", new SteamSettingsLevel("gpu", "Graphics", "Refresh · Picture", "layers",
                    "2 set for this game", "More display settings"))
        ]);

        var level = SteamSettingsRows.Serialize([page])[0].GetProperty("sections")[0].GetProperty("level");

        Assert.Equal("gpu", level.GetProperty("id").GetString());
        Assert.Equal("Graphics", level.GetProperty("title").GetString());
        Assert.Equal("Refresh · Picture", level.GetProperty("summary").GetString());
        Assert.Equal("layers", level.GetProperty("icon").GetString());
        Assert.Equal("2 set for this game", level.GetProperty("badge").GetString());
        Assert.Equal("More display settings", level.GetProperty("group").GetString());
    }
}
