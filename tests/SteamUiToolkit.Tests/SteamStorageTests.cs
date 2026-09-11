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
            [new SteamStorageDrive(
                Id: 1,
                Model: "Realtek PCIE CardReader",
                Vendor: "",
                SizeBytes: 256_003_538_944,
                Ejectable: true,
                Formattable: true,
                Unformatted: false)],
            [new SteamStorageBlockDevice(
                Id: 2,
                DriveId: 1,
                Label: "SDCard1",
                FriendlyPath: "D:\\",
                SizeBytes: 256_002_359_296,
                MountPaths: ["D:\\", "D:\\SteamLibrary"],
                HasSteamLibrary: true)],
            AdoptSupported: true,
            UnmountSupported: true);

        JsonElement wire = SteamStorageSurface.Serialize(state);

        Assert.Equal(1u, wire.GetProperty("drives")[0].GetProperty("id").GetUInt32());
        Assert.True(wire.GetProperty("drives")[0].GetProperty("formattable").GetBoolean());
        Assert.Equal(
            "Realtek PCIE CardReader", wire.GetProperty("drives")[0].GetProperty("model").GetString());
        Assert.Equal(2u, wire.GetProperty("blockDevices")[0].GetProperty("id").GetUInt32());
        Assert.Equal(1u, wire.GetProperty("blockDevices")[0].GetProperty("driveId").GetUInt32());
        Assert.Equal("D:\\", wire.GetProperty("blockDevices")[0].GetProperty("mountPaths")[0].GetString());

        // The library path travels with the root because Steam finds the volume behind a folder by
        // looking for a block device whose mount paths contain that folder.
        Assert.Equal(
            "D:\\SteamLibrary",
            wire.GetProperty("blockDevices")[0].GetProperty("mountPaths")[1].GetString());

        // Both support flags, because Steam gates Format on one and Eject on the other.
        Assert.True(wire.GetProperty("adoptSupported").GetBoolean());
        Assert.True(wire.GetProperty("unmountSupported").GetBoolean());
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

        Assert.True((await Dispatch(set, "eject", """{"blockDeviceId":2}""")).Succeeded);
        Assert.True((await Dispatch(set, "unmount", """{"driveId":1}""")).Succeeded);
        SteamUiCommandResult refused = await Dispatch(set, "eject", """{}""");

        Assert.Equal("The storage eject payload named neither a volume nor a drive.", refused.Error);

        // Zero is Steam's "not named" rather than a drive, so it refuses like an absent property.
        SteamUiCommandResult zero = await Dispatch(
            set, "eject", """{"blockDeviceId":0,"driveId":0}""");
        Assert.Equal("The storage eject payload named neither a volume nor a drive.", zero.Error);
        Assert.Equal(["eject 2/0", "eject 0/1"], backend.Calls);
    }

    [Fact]
    public async Task AdoptAndFormatRequireADriveAndTrimRequiresNothing()
    {
        RecordingStorageBackend backend = new();
        SteamUiModuleSet set = new(
        [
            SteamStorageSurface.Module(Always, () => new(null as SteamStorageState), backend),
        ]);

        // Steam's Format Drive modal sends Adopt with the typed name and its validate flag, so
        // both have to survive the trip; a bare adopt still works with neither.
        Assert.True((await Dispatch(
            set, "adopt", """{"driveId":1,"label":"Games","validate":true}""")).Succeeded);
        Assert.True((await Dispatch(set, "adopt", """{"driveId":1}""")).Succeeded);
        Assert.True((await Dispatch(set, "trimall", """{}""")).Succeeded);
        SteamUiCommandResult refused = await Dispatch(set, "format", """{"driveId":0}""");

        Assert.Equal("The storage format payload is invalid.", refused.Error);
        Assert.Equal(
            ["adopt 1 'Games' validate=True", "adopt 1 '' validate=False", "trimall"],
            backend.Calls);
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

        public Task<SteamUiCommandResult> AdoptAsync(
            uint driveId, string label, bool validate, CancellationToken cancellationToken) =>
            Record($"adopt {driveId} '{label}' validate={validate}");

        public Task<SteamUiCommandResult> EjectAsync(
            uint blockDeviceId, uint driveId, CancellationToken cancellationToken) =>
            Record($"eject {blockDeviceId}/{driveId}");

        public Task<SteamUiCommandResult> FormatAsync(uint driveId, CancellationToken cancellationToken) =>
            Record($"format {driveId}");

        public Task<SteamUiCommandResult> TrimAllAsync(CancellationToken cancellationToken) => Record("trimall");
    }
}
