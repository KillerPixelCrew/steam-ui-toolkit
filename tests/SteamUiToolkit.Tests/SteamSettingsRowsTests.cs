namespace SteamUiToolkit.Tests;

/// <summary>The settings rows reach the renderer under the names <c>settings.ts</c> reads.</summary>
public sealed class SteamSettingsRowsTests
{
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
}
