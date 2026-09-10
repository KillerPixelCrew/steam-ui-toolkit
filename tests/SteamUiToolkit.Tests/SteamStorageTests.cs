using System.Reflection;
using System.Text.Json;

namespace SteamUiToolkit.Tests;

/// <summary>
/// The storage surface's contract: what the probe demands before claiming a transport that carries
/// every service call Steam makes, and what a publication puts on the wire.
/// </summary>
/// <remarks>
/// Measured against the live client on 2026-09-10. Exactly one module names
/// <c>StorageDeviceManager.IsServiceAvailable#1</c>, exactly one exports the transport provider, and
/// <c>SendMsg</c> is a writable, configurable prototype property with no own property on the
/// instance — which is what lets removal delete the claim and leave Valve's method showing through.
/// </remarks>
public sealed class SteamStorageTests
{
    private static readonly Func<bool> Always = () => true;

    [Fact]
    public void EveryActionSteamCanInvokeHasACommand()
    {
        // Read off the client's own generated message classes. A missing one is a button that does
        // nothing, which is the silent-control failure the guidance forbids.
        Assert.Equal(["adopt", "unmount", "eject", "format", "trimall"], SteamStorageSurface.Commands);
    }

    [Fact]
    public void TheProbePinsTheServiceAndTheTransportSeparately()
    {
        string probe = ProbeOf();

        Assert.Contains("StorageDeviceManager.IsServiceAvailable#1", probe, StringComparison.Ordinal);
        Assert.Contains("GetDefaultTransport", probe, StringComparison.Ordinal);
        Assert.Contains("transportResolved", probe, StringComparison.Ordinal);
        Assert.Contains("claimable", probe, StringComparison.Ordinal);
        Assert.Contains("__steamUiStorageClaimed", probe, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"service":1,"transportModule":1,"transportResolved":1,"claimable":true}""", true)]
    // No transport resolved: nothing to claim, and claiming the wrong object carries Steam's traffic.
    [InlineData("""{"service":1,"transportModule":1,"transportResolved":0,"claimable":true}""", false)]
    [InlineData("""{"service":1,"transportModule":1,"transportResolved":1,"claimable":false}""", false)]
    // Two modules naming the service is ambiguous rather than a reason to pick one.
    [InlineData("""{"service":2,"transportModule":1,"transportResolved":1,"claimable":true}""", false)]
    [InlineData("""{"service":0,"transportModule":1,"transportResolved":1,"claimable":true}""", false)]
    [InlineData("""{"error":"Steam modules unavailable"}""", false)]
    public void CompatibilityRequiresAUniqueServiceAndAClaimableTransport(string json, bool expected)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(expected, CompatibilityOf(document.RootElement));
    }

    [Fact]
    public void TheStateReachesTheWireWithSteamsOwnFieldNames()
    {
        SteamStorageState state = new(
            [new SteamStorageDrive("disk0", Formattable: true, Unformatted: false)],
            [new SteamStorageBlockDevice("vol0", "disk0", ["D:\\"], HasSteamLibrary: true)],
            AdoptSupported: true,
            UnmountSupported: true);

        JsonElement wire = SteamStorageSurface.Serialize(state);

        Assert.Equal("disk0", wire.GetProperty("drives")[0].GetProperty("id").GetString());
        Assert.True(wire.GetProperty("drives")[0].GetProperty("formattable").GetBoolean());
        Assert.Equal("vol0", wire.GetProperty("blockDevices")[0].GetProperty("id").GetString());
        Assert.Equal("D:\\", wire.GetProperty("blockDevices")[0].GetProperty("mountPaths")[0].GetString());
        Assert.True(wire.GetProperty("adoptSupported").GetBoolean());
    }

    [Fact]
    public async Task EjectAcceptsEitherAVolumeOrADriveAndRefusesNeither()
    {
        // Steam names one or the other depending on which row the user pressed, so requiring both
        // would make half its own buttons refuse.
        RecordingStorageBackend backend = new();
        SteamUiModuleSet set = new(
        [
            SteamStorageSurface.Module(Always, () => new(null as SteamStorageState), backend),
        ]);

        Assert.True((await Dispatch(set, "eject", """{"blockDeviceId":"vol0"}""")).Succeeded);
        Assert.True((await Dispatch(set, "unmount", """{"driveId":"disk0"}""")).Succeeded);
        SteamUiCommandResult refused = await Dispatch(set, "eject", """{}""");

        Assert.Equal("The storage eject payload named neither a volume nor a drive.", refused.Error);
        Assert.Equal(["eject vol0/", "eject /disk0"], backend.Calls);
    }

    [Fact]
    public async Task AdoptAndFormatRequireADriveAndTrimRequiresNothing()
    {
        RecordingStorageBackend backend = new();
        SteamUiModuleSet set = new(
        [
            SteamStorageSurface.Module(Always, () => new(null as SteamStorageState), backend),
        ]);

        Assert.True((await Dispatch(set, "adopt", """{"driveId":"disk0"}""")).Succeeded);
        Assert.True((await Dispatch(set, "trimall", """{}""")).Succeeded);
        SteamUiCommandResult refused = await Dispatch(set, "format", """{"driveId":""}""");

        Assert.Equal("The storage format payload is invalid.", refused.Error);
        Assert.Equal(["adopt disk0", "trimall"], backend.Calls);
    }

    [Fact]
    public async Task ANullReadingPublishesNothingRatherThanAnEmptyMachine()
    {
        // An empty state is a real answer — "no removable drives" — so it must not be what "we do
        // not know yet" looks like.
        RecordingStorageBackend backend = new();
        SteamStorageState? state = null;
        SteamUiModuleSet set = new(
        [
            SteamStorageSurface.Module(Always, () => new(state), backend),
        ]);
        SteamUiStatePublication publication = Assert.Single(set.Publications);

        Assert.Null(await publication.Read());
        state = new SteamStorageState([], []);

        Assert.Equal(0, (await publication.Read())!.Value.GetProperty("drives").GetArrayLength());
    }

    private static string ProbeOf() =>
        (string)SteamStorageSurface.Patch.GetType()
            .GetField("_probeExpression", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(SteamStorageSurface.Patch)!;

    private static bool CompatibilityOf(JsonElement root) =>
        ((Func<JsonElement, bool>)SteamStorageSurface.Patch.GetType()
            .GetField("_compatible", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(SteamStorageSurface.Patch)!)(root);

    private static async Task<SteamUiCommandResult> Dispatch(
        SteamUiModuleSet set, string command, string payloadJson)
    {
        Assert.True(set.TryGetCommand(
            SteamStorageSurface.PatchId, command, out SteamUiCommandDelegate? handler));
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        SteamUiBridgeRequest request = new(
            SteamUiBridgeHost.SchemaVersion, "request", SteamStorageSurface.PatchId, command,
            1, 2, 3, 4, payload.RootElement.Clone());
        return await handler!(request, CancellationToken.None);
    }

    private sealed class RecordingStorageBackend : ISteamStorageBackend
    {
        internal List<string> Calls { get; } = [];

        private Task<SteamUiCommandResult> Record(string call)
        {
            Calls.Add(call);
            return Task.FromResult(SteamUiCommandResult.Applied);
        }

        public Task<SteamUiCommandResult> AdoptAsync(string driveId, CancellationToken cancellationToken) =>
            Record($"adopt {driveId}");

        public Task<SteamUiCommandResult> EjectAsync(
            string blockDeviceId, string driveId, CancellationToken cancellationToken) =>
            Record($"eject {blockDeviceId}/{driveId}");

        public Task<SteamUiCommandResult> FormatAsync(string driveId, CancellationToken cancellationToken) =>
            Record($"format {driveId}");

        public Task<SteamUiCommandResult> TrimAllAsync(CancellationToken cancellationToken) => Record("trimall");
    }
}
