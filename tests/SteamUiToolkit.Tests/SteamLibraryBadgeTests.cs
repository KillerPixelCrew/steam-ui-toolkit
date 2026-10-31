using System.Text.Json;
using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

/// <summary>
///     The library badge surface's own contract: what a publication puts on the wire and what the
///     layout report carries back. What each of the three gates' probes demands of a client before it
///     claims anything is the gate's own find, exercised against the emitted asset by
///     <c>eng/check-library.mjs</c>.
/// </summary>
public sealed class SteamLibraryBadgeTests
{
    [Fact]
    public void TheModuleDeclaresTheBadgeAndTheStatUnderOnePublication()
    {
        var module = SteamLibraryBadgeSurface.Module(
            Always, () => new ValueTask<SteamLibraryBadgeState?>(null as SteamLibraryBadgeState),
            new RecordingBackend());

        Assert.Equal(
            [
                SteamLibraryBadgeSurface.PatchId, SteamLibraryBadgeSurface.DetailsPatchId,
                SteamLibraryBadgeSurface.AvailabilityPatchId
            ],
            module.Patches.Select(patch => patch.Id));
        Assert.Equal(SteamLibraryBadgeSurface.PatchIds, module.Patches.Select(patch => patch.Id));
        Assert.Equal(SteamLibraryBadgeSurface.PatchId, Assert.Single(module.Publications).PatchId);
    }

    [Fact]
    public void BadgesAreShownUnlessTheHostTurnsThemOffAndShortcutsReachTheWire()
    {
        SteamLibraryBadgeState shown = new([]);
        SteamLibraryBadgeState hidden = new(
            [],
            3,
            [new SteamShortcutAvailability(4000000000, false, "Blue card", "Insert the card.")],
            ShowBadges: false);

        Assert.True(SteamLibraryBadgeSurface.Serialize(shown).GetProperty("showBadges").GetBoolean());
        var wire = SteamLibraryBadgeSurface.Serialize(hidden);
        Assert.False(wire.GetProperty("showBadges").GetBoolean());
        var shortcut = wire.GetProperty("shortcuts")[0];
        Assert.Equal(4000000000u, shortcut.GetProperty("appId").GetUInt32());
        Assert.False(shortcut.GetProperty("available").GetBoolean());
        Assert.Equal("Blue card", shortcut.GetProperty("location").GetString());
    }

    [Fact]
    public void LibrariesReachTheWireWithTheirNameConnectionAndAppIds()
    {
        SteamLibraryBadgeState state = new([new SteamLibraryBadgeLibrary("Blue card", false, [70, 400])]);

        var wire = SteamLibraryBadgeSurface.Serialize(state);
        var library = wire.GetProperty("libraries")[0];

        Assert.Equal("Blue card", library.GetProperty("name").GetString());
        Assert.False(library.GetProperty("connected").GetBoolean());
        Assert.Equal(400, library.GetProperty("appIds")[1].GetInt64());
        Assert.False(wire.TryGetProperty("internalLabel", out _), "the toolkit holds no library name of its own");
    }

    [Theory]
    [InlineData("""{"bigArt":true}""", true, true)]
    [InlineData("""{"bigArt":false}""", true, false)]
    [InlineData("""{"bigArt":"true"}""", false, false)]
    [InlineData("""{"bigArt":true,"extra":1}""", false, false)]
    [InlineData("""{}""", false, false)]
    public void TheLayoutReportIsExactlyOneBoolean(string json, bool valid, bool expected)
    {
        using var payload = JsonDocument.Parse(json);

        Assert.Equal(valid, SteamLibraryBadgeSurface.TryReadHomeLayout(payload.RootElement, out var bigArt));
        Assert.Equal(expected, bigArt);
    }

    [Fact]
    public async Task TheLayoutReportReachesTheBackendAndAMalformedOneIsRefusedByName()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new(
        [
            SteamLibraryBadgeSurface.Module(Always,
                () => new ValueTask<SteamLibraryBadgeState?>(null as SteamLibraryBadgeState), backend)
        ]);

        var applied = await DispatchAsync(
            set, SteamLibraryBadgeSurface.PatchId, "homeLayout", """{"bigArt":true}""");
        var refused = await DispatchAsync(
            set, SteamLibraryBadgeSurface.PatchId, "homeLayout", """{"bigArt":1}""");

        Assert.True(applied.Succeeded);
        Assert.Equal("The home layout payload is invalid.", refused.Error);
        Assert.Equal(["home layout big art"], backend.Calls);
    }
}
