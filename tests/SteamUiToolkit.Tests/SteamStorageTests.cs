using System.Text.Json;
using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

/// <summary>
///     The storage surface's contract: what the probe demands before claiming a transport that carries
///     every service call Steam makes, and what a publication puts on the wire.
/// </summary>
/// <remarks>
///     Measured against the live client on 2026-09-10. Exactly one module names
///     <c>StorageDeviceManager.IsServiceAvailable#1</c>, exactly one exports the transport provider, and
///     <c>SendMsg</c> is a writable, configurable prototype property with no own property on the
///     instance — which is what lets removal delete the claim and leave Valve's method showing through.
/// </remarks>
public sealed class SteamStorageTests
{
    private static SteamGatePatch Gate => (SteamGatePatch)SteamStorageSurface.Patch;

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
        var probe = Gate.ProbeExpression;

        Assert.Contains("StorageDeviceManager.IsServiceAvailable#1", probe, StringComparison.Ordinal);
        Assert.Contains("GetDefaultTransport", probe, StringComparison.Ordinal);
        // Source text only: the probe never calls an export of the transport module to reach it.
        Assert.DoesNotContain("GetDefaultTransport?.(", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("SendMsg", probe, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"service":1,"transportModule":1}""", true)]
    // Two modules naming the service is ambiguous rather than a reason to pick one.
    [InlineData("""{"service":2,"transportModule":1}""", false)]
    [InlineData("""{"service":0,"transportModule":1}""", false)]
    [InlineData("""{"service":1,"transportModule":0}""", false)]
    [InlineData("""{"service":1,"transportModule":2}""", false)]
    [InlineData("""{"error":"Steam modules unavailable"}""", false)]
    [InlineData("""[1]""", false)]
    public void CompatibilityRequiresAUniqueServiceAndTransportModule(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, Gate.Compatible(document.RootElement));
    }

    [Fact]
    public void TheStateReachesTheWireWithSteamsOwnFieldNames()
    {
        SteamStorageState state = new(
            [
                new SteamStorageDrive(
                    1,
                    "Realtek PCIE CardReader",
                    "",
                    256_003_538_944,
                    true,
                    true,
                    false)
            ],
            [
                new SteamStorageBlockDevice(
                    2,
                    1,
                    "SDCard1",
                    "D:\\",
                    256_002_359_296,
                    ["D:\\", "D:\\SteamLibrary"],
                    true)
            ],
            true,
            true);

        var wire = SteamStorageSurface.Serialize(state);

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
        RecordingBackend backend = new();
        SteamUiModuleSet set = new(
        [
            SteamStorageSurface.Module(Always, () => new ValueTask<SteamStorageState?>(null as SteamStorageState),
                backend)
        ]);
        var patchId = SteamStorageSurface.PatchId;

        Assert.True((await DispatchAsync(set, patchId, "eject", Storage(0, 2))).Succeeded);
        Assert.True((await DispatchAsync(set, patchId, "unmount", Storage(1, 0))).Succeeded);
        var refused = await DispatchAsync(set, patchId, "eject", """{}""");

        Assert.Equal("The storage eject payload named neither a volume nor a drive.", refused.Error);

        // Zero is Steam's "not named" rather than a drive.
        var zero = await DispatchAsync(set, patchId, "eject", Storage(0, 0));
        Assert.Equal("The storage eject payload named neither a volume nor a drive.", zero.Error);

        // Ids are unsigned 32-bit, so one above int.MaxValue is a drive like any other.
        Assert.True((await DispatchAsync(set, patchId, "eject", Storage(3000000000, 0))).Succeeded);
        Assert.Equal(["eject 2/0", "eject 0/1", "eject 0/3000000000"], backend.Calls);
    }

    [Fact]
    public async Task AdoptAndFormatRequireADriveAndTrimRequiresNothing()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new(
        [
            SteamStorageSurface.Module(Always, () => new ValueTask<SteamStorageState?>(null as SteamStorageState),
                backend)
        ]);
        var patchId = SteamStorageSurface.PatchId;

        // Steam's Format Drive modal sends Adopt with the typed name and its validate flag, so
        // both have to survive the trip; a bare adopt still works with neither.
        Assert.True((await DispatchAsync(set, patchId, "adopt", Storage(1, 0, "Games", true))).Succeeded);
        Assert.True((await DispatchAsync(set, patchId, "adopt", Storage(1, 0))).Succeeded);
        Assert.True((await DispatchAsync(set, patchId, "trimall", """{}""")).Succeeded);
        var refused = await DispatchAsync(set, patchId, "format", Storage(0, 0));
        var extra = await DispatchAsync(
            set, patchId, "adopt", """{"driveId":1,"blockDeviceId":0,"label":"","validate":false,"x":1}""");

        Assert.Equal("The storage format payload is invalid.", refused.Error);
        Assert.Equal("The storage adopt payload is invalid.", extra.Error);
        Assert.Equal(
            ["adopt 1 'Games' validate=True", "adopt 1 '' validate=False", "trimall"],
            backend.Calls);
    }

    // The one shape every storage action sends.
    private static string Storage(uint driveId, uint blockDeviceId, string label = "", bool validate = false)
    {
        return $$"""{"driveId":{{driveId}},"blockDeviceId":{{blockDeviceId}},"label":"{{label}}","validate":{{(validate ? "true" : "false")}}}""";
    }
}
