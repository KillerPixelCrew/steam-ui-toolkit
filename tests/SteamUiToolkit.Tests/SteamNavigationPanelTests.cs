using System.Reflection;
using System.Text.Json;

namespace SteamUiToolkit.Tests;

/// <summary>
/// The navigation panel surface's own contract: what the probe demands of a client before the
/// panel is claimed, and what a publication puts on the wire.
/// </summary>
/// <remarks>
/// Every structural fact asserted here was measured against the live client on 2026-09-10, where
/// <c>#MainMenu_Title</c> and <c>MainNavMenuContainer</c> each occur in exactly one of the 2581
/// loaded modules, that module has exactly one export whose memo renders the container, and that
/// export's <c>type</c> is a writable and configurable own property.
/// </remarks>
public sealed class SteamNavigationPanelTests
{
    private static readonly Func<bool> Always = () => true;

    [Fact]
    public void TheProbeNamesEveryStructuralFactTheGateResolvesOn()
    {
        // Each fact is separate so an incompatible client says which one moved. A probe that
        // reported one boolean would only ever say "something changed".
        string probe = ProbeOf(SteamNavigationPanelSurface.Patch);

        Assert.Contains("#MainMenu_Title", probe, StringComparison.Ordinal);
        Assert.Contains("MainNavMenuContainer", probe, StringComparison.Ordinal);
        Assert.Contains("RunnningAppSeparator", probe, StringComparison.Ordinal);
        Assert.Contains("memoExports", probe, StringComparison.Ordinal);
        Assert.Contains("claimable", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbeSelectsTheExportByWhatItDrawsRatherThanByName()
    {
        // Minified export names are right for exactly one client build. The live panel's export is
        // called v_ today and that name is deliberately nowhere in this file or the probe.
        string probe = ProbeOf(SteamNavigationPanelSurface.Patch);

        Assert.DoesNotContain("v_", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("exports.Ie", probe, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"menuModule":1,"panelRoot":1,"memoExports":1,"react":1,"claimable":true}""", true)]
    // Two candidate exports is ambiguous, which is a refusal rather than a reason to pick one.
    [InlineData("""{"menuModule":1,"panelRoot":1,"memoExports":2,"react":1,"claimable":true}""", false)]
    // A non-writable type could be replaced by nothing and restored to nothing.
    [InlineData("""{"menuModule":1,"panelRoot":1,"memoExports":1,"react":1,"claimable":false}""", false)]
    [InlineData("""{"menuModule":0,"panelRoot":1,"memoExports":1,"react":1,"claimable":true}""", false)]
    [InlineData("""{"menuModule":1,"panelRoot":0,"memoExports":1,"react":1,"claimable":true}""", false)]
    [InlineData("""{"error":"Steam modules unavailable"}""", false)]
    public void CompatibilityRequiresEveryFactAndAUniqueMatchForEachOne(string json, bool expected)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(expected, CompatibilityOf(SteamNavigationPanelSurface.Patch, document.RootElement));
    }

    [Fact]
    public void AnAlreadyClaimedPanelStaysCompatible()
    {
        // Requiring the pre-patch shape alone would make a successful apply fail its own next probe,
        // and the manager would tear down the claim it had just verified on every poll.
        string probe = ProbeOf(SteamNavigationPanelSurface.Patch);

        Assert.Contains("__steamUiNavigationPanelClaimed", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void VerificationRequiresTheClaimAndRemovalRequiresItsAbsence()
    {
        Assert.Equal("status.installed&&status.resolved&&status.claimed", VerifyOf());
        Assert.Equal("!status.claimed", RemoveOf());
    }

    [Fact]
    public void EntriesAndHiddenNamesReachTheWireAsPublished()
    {
        SteamNavigationPanelState state = new(
            [new SteamNavigationItem("wsgm-overlay", "WSGM", Icon: "cores", After: "/library")],
            ["power"]);

        JsonElement wire = SteamNavigationPanelSurface.Serialize(state);
        JsonElement item = wire.GetProperty("items")[0];

        Assert.Equal("wsgm-overlay", item.GetProperty("id").GetString());
        Assert.Equal("WSGM", item.GetProperty("label").GetString());
        Assert.Equal("cores", item.GetProperty("icon").GetString());
        Assert.Equal("/library", item.GetProperty("after").GetString());
        Assert.Equal("power", wire.GetProperty("hidden")[0].GetString());
    }

    [Fact]
    public async Task ActivationReachesTheBackendAndAMalformedIdIsRefusedByName()
    {
        RecordingNavigationBackend backend = new();
        SteamUiModuleSet set = new(
        [
            SteamNavigationPanelSurface.Module(
                Always, () => new(null as SteamNavigationPanelState), backend),
        ]);

        SteamUiCommandResult applied = await Dispatch(set, "activate", """{"id":"wsgm-overlay"}""");
        SteamUiCommandResult refused = await Dispatch(set, "activate", """{"id":""}""");

        Assert.True(applied.Succeeded);
        Assert.Equal("The navigation activation payload is invalid.", refused.Error);
        Assert.Equal(["activate wsgm-overlay"], backend.Calls);
    }

    [Fact]
    public async Task ANullReadingPublishesNothingRatherThanAnEmptyPanel()
    {
        // An empty publication would hide nothing and add nothing, which reads the same as "no
        // opinion" but is a real instruction. Not publishing is how the surface says nothing.
        RecordingNavigationBackend backend = new();
        SteamNavigationPanelState? state = null;
        SteamUiModuleSet set = new(
        [
            SteamNavigationPanelSurface.Module(Always, () => new(state), backend),
        ]);
        SteamUiStatePublication publication = Assert.Single(set.Publications);

        Assert.Null(await publication.Read());
        state = new SteamNavigationPanelState([], ["power"]);

        Assert.Equal("power", (await publication.Read())!.Value.GetProperty("hidden")[0].GetString());
    }

    private static string ProbeOf(ISteamUiPatch patch) =>
        (string)patch.GetType().GetField("_probeExpression", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(patch)!;

    private static string VerifyOf() =>
        (string)SteamNavigationPanelSurface.Patch.GetType()
            .GetField("_verifyOk", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(SteamNavigationPanelSurface.Patch)!;

    private static string RemoveOf() =>
        (string)SteamNavigationPanelSurface.Patch.GetType()
            .GetField("_removeOk", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(SteamNavigationPanelSurface.Patch)!;

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
            SteamNavigationPanelSurface.PatchId, command, out SteamUiCommandDelegate? handler));
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        SteamUiBridgeRequest request = new(
            SteamUiBridgeHost.SchemaVersion,
            "request",
            SteamNavigationPanelSurface.PatchId,
            command,
            1,
            1,
            0,
            0,
            payload.RootElement.Clone());
        return await handler!(request, CancellationToken.None);
    }

    private sealed class RecordingNavigationBackend : ISteamNavigationPanelBackend
    {
        internal List<string> Calls { get; } = [];

        public Task<SteamUiCommandResult> ActivateAsync(string id, CancellationToken cancellationToken)
        {
            Calls.Add($"activate {id}");
            return Task.FromResult(SteamUiCommandResult.Applied);
        }
    }
}
