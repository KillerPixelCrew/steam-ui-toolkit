using System.Reflection;
using System.Text.Json;

namespace SteamUiToolkit.Tests;

/// <summary>
/// The library badge surface's own contract: what the probe demands of a client before the tile
/// is claimed, what a publication puts on the wire, and what the layout report carries back.
/// </summary>
/// <remarks>
/// Every structural fact asserted here was measured against the September 2026 client beta on
/// 2026-09-11, where <c>appportrait_</c> occurs in exactly one of the 2622 loaded modules, that
/// module has exactly one <c>React.memo</c> export and exactly one function export drawing the
/// controller-support icon, and the memo's <c>type</c> is a writable and configurable own property.
/// </remarks>
public sealed class SteamLibraryBadgeTests
{
    private static readonly Func<bool> Always = () => true;

    [Fact]
    public void TheProbeNamesEveryStructuralFactTheGateResolvesOn()
    {
        string probe = ProbeOf(SteamLibraryBadgeSurface.Patch);

        Assert.Contains("ControllerSupportIcon", probe, StringComparison.Ordinal);
        Assert.Contains("appportrait_", probe, StringComparison.Ordinal);
        Assert.Contains("tileExports", probe, StringComparison.Ordinal);
        Assert.Contains("badgeExports", probe, StringComparison.Ordinal);
        Assert.Contains("claimable", probe, StringComparison.Ordinal);
        Assert.Contains("m_setDeferredSettings", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbeSelectsTheExportsByWhatTheyAreRatherThanByName()
    {
        // The live tile export is called TK and the badge Kt today. Neither name is anywhere in
        // the probe, because both are right for exactly one client build.
        string probe = ProbeOf(SteamLibraryBadgeSurface.Patch);

        Assert.DoesNotContain("exports.TK", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("exports.Kt", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryItemIcons", probe, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"tileModule":1,"tileExports":1,"badgeExports":1,"claimable":true,"settingsModule":1,"react":1}""", true)]
    // The settings store is wanted, not required: the badge draws without it.
    [InlineData("""{"tileModule":1,"tileExports":1,"badgeExports":1,"claimable":true,"settingsModule":0,"react":1}""", true)]
    // Two memos is ambiguous, which is a refusal rather than a reason to pick one.
    [InlineData("""{"tileModule":1,"tileExports":2,"badgeExports":1,"claimable":true,"settingsModule":1,"react":1}""", false)]
    [InlineData("""{"tileModule":1,"tileExports":1,"badgeExports":0,"claimable":true,"settingsModule":1,"react":1}""", false)]
    // A non-writable type could be replaced by nothing and restored to nothing.
    [InlineData("""{"tileModule":1,"tileExports":1,"badgeExports":1,"claimable":false,"settingsModule":1,"react":1}""", false)]
    [InlineData("""{"tileModule":0}""", false)]
    [InlineData("""{"error":"Steam modules unavailable"}""", false)]
    public void CompatibilityRequiresEveryFactAndAUniqueMatchForEachOne(string json, bool expected)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(expected, CompatibilityOf(SteamLibraryBadgeSurface.Patch, document.RootElement));
    }

    [Fact]
    public void AnAlreadyClaimedTileStaysCompatible()
    {
        string probe = ProbeOf(SteamLibraryBadgeSurface.Patch);

        Assert.Contains("__steamUiLibraryBadgeClaimed", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void VerificationRequiresTheClaimAndRemovalRequiresItsAbsence()
    {
        Assert.Equal("status.installed&&status.resolved&&status.claimed", FieldOf("_verifyOk"));
        Assert.Equal("!status.claimed", FieldOf("_removeOk"));
    }

    [Fact]
    public void LibrariesReachTheWireWithTheirNameConnectionAndAppIds()
    {
        SteamLibraryBadgeState state = new(
            [new SteamLibraryBadgeLibrary("Blue card", Connected: false, [70, 400])],
            InternalLabel: "Claw");

        JsonElement wire = SteamLibraryBadgeSurface.Serialize(state);
        JsonElement library = wire.GetProperty("libraries")[0];

        Assert.Equal("Blue card", library.GetProperty("name").GetString());
        Assert.False(library.GetProperty("connected").GetBoolean());
        Assert.Equal(400, library.GetProperty("appIds")[1].GetInt64());
        Assert.Equal("Claw", wire.GetProperty("internalLabel").GetString());
    }

    [Theory]
    [InlineData("""{"bigArt":true}""", true, true)]
    [InlineData("""{"bigArt":false}""", true, false)]
    [InlineData("""{"bigArt":"true"}""", false, false)]
    [InlineData("""{"bigArt":true,"extra":1}""", false, false)]
    [InlineData("""{}""", false, false)]
    public void TheLayoutReportIsExactlyOneBoolean(string json, bool valid, bool expected)
    {
        using JsonDocument payload = JsonDocument.Parse(json);

        Assert.Equal(valid, SteamLibraryBadgeSurface.TryReadHomeLayout(payload.RootElement, out bool bigArt));
        Assert.Equal(expected, bigArt);
    }

    [Fact]
    public async Task TheLayoutReportReachesTheBackendAndAMalformedOneIsRefusedByName()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new(
        [
            SteamLibraryBadgeSurface.Module(Always, () => new(null as SteamLibraryBadgeState), backend),
        ]);

        SteamUiCommandResult applied = await Dispatch(set, "homeLayout", """{"bigArt":true}""");
        SteamUiCommandResult refused = await Dispatch(set, "homeLayout", """{"bigArt":1}""");

        Assert.True(applied.Succeeded);
        Assert.Equal("The home layout payload is invalid.", refused.Error);
        Assert.Equal(["big art"], backend.Calls);
    }

    [Fact]
    public async Task ANullReadingPublishesNothingRatherThanAnEmptyLibraryList()
    {
        // An empty list is a real instruction — every installed game is internal — and not the
        // same as having nothing to say yet.
        SteamLibraryBadgeState? state = null;
        SteamUiModuleSet set = new(
        [
            SteamLibraryBadgeSurface.Module(Always, () => new(state), new RecordingBackend()),
        ]);
        SteamUiStatePublication publication = Assert.Single(set.Publications);

        Assert.Null(await publication.Read());
        state = new SteamLibraryBadgeState([]);

        Assert.Equal(0, (await publication.Read())!.Value.GetProperty("libraries").GetArrayLength());
    }

    private static string ProbeOf(ISteamUiPatch patch) =>
        (string)patch.GetType().GetField("_probeExpression", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(patch)!;

    private static string FieldOf(string name) =>
        (string)SteamLibraryBadgeSurface.Patch.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(SteamLibraryBadgeSurface.Patch)!;

    private static bool CompatibilityOf(ISteamUiPatch patch, JsonElement root) =>
        ((Func<JsonElement, bool>)patch.GetType()
            .GetField("_compatible", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(patch)!)(root);

    private static async Task<SteamUiCommandResult> Dispatch(
        SteamUiModuleSet set,
        string command,
        string payloadJson)
    {
        Assert.True(set.TryGetCommand(
            SteamLibraryBadgeSurface.PatchId, command, out SteamUiCommandDelegate? handler));
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        SteamUiBridgeRequest request = new(
            SteamUiBridgeHost.SchemaVersion,
            "request",
            SteamLibraryBadgeSurface.PatchId,
            command,
            1,
            1,
            0,
            0,
            payload.RootElement.Clone());
        return await handler!(request, CancellationToken.None);
    }

    private sealed class RecordingBackend : ISteamLibraryBadgeBackend
    {
        internal List<string> Calls { get; } = [];

        public Task<SteamUiCommandResult> HomeLayoutAsync(bool bigArt, CancellationToken cancellationToken)
        {
            Calls.Add(bigArt ? "big art" : "normal");
            return Task.FromResult(SteamUiCommandResult.Applied);
        }
    }
}
