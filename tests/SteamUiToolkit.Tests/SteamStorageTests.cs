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
    public async Task TheActualStorageProbeCountsSingletonAccessorsWithoutCallingThem()
    {
        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',()=>{
              const expression=JSON.parse(input),results=[];
              for(const copies of [0,1,2]){
                let calls=0,loads=0;
                class UnknownProvider{constructor(){calls++;throw Error('constructed')}GetDefaultTransport(){calls++;throw Error('invoked')}}
                function unknown(){calls++;throw Error('invoked')}
                function singletonAccessor(){return unresolvedSingleton;}
                function anotherAccessor(){return anotherUnresolvedSingleton;}
                function parameterized(value){return unresolvedSingleton;}
                async function asyncAccessor(){return unresolvedSingleton;}
                function* generatorAccessor(){return unresolvedSingleton;}
                Object.defineProperty(unknown,'toString',{value:()=>singletonAccessor.toString()});
                const exports={UnknownProvider,unknown,parameterized,asyncAccessor,generatorAccessor,
                  arrow:()=>unresolvedSingleton};
                Object.defineProperty(exports,'unsafeBinding',{enumerable:true,get(){calls++;throw Error('getter invoked')}});
                if(copies>0){
                  Object.defineProperty(exports,'binding',{enumerable:true,get:()=>singletonAccessor});
                  exports.alias=singletonAccessor;
                }
                if(copies>1)exports.second=anotherAccessor;
                const runtime=id=>{assert.equal(id,'transport');loads++;return exports;};
                runtime.m={service:new Function('// StorageDeviceManager.IsServiceAvailable#1'),
                  transport:new Function('// GetDefaultTransport m_transport')};
                const window={webpackChunksteamui:{push:chunk=>chunk[2](runtime)}};
                const result=JSON.parse(vm.runInNewContext(expression,{window},{timeout:1000}));
                assert.equal(result.service,1);assert.equal(result.transportModule,1);
                assert.equal(result.provider,copies);assert.equal(calls,0);assert.equal(loads,1);
                results.push(result);
              }
              process.stdout.write(JSON.stringify(results));
            });
            """;
        var output = await NodeScript.RunAsync(script, Gate.ProbeExpression);
        using var results = JsonDocument.Parse(output);
        Assert.False(Gate.Compatible(results.RootElement[0]));
        Assert.True(Gate.Compatible(results.RootElement[1]));
        Assert.False(Gate.Compatible(results.RootElement[2]));
    }

    [Fact]
    public async Task TheActualStorageProbeNeverLoadsMissingOrAmbiguousModules()
    {
        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',()=>{
              const expression=JSON.parse(input);
              for(const serviceCopies of [0,1,2])for(const transportCopies of [0,1,2]){
                let loads=0;
                const runtime=()=>{loads++;throw Error('must not load')};runtime.m={};
                for(let i=0;i<serviceCopies;i++)runtime.m['service'+i]=new Function('// StorageDeviceManager.IsServiceAvailable#1');
                for(let i=0;i<transportCopies;i++)runtime.m['transport'+i]=new Function('// GetDefaultTransport m_transport');
                if(serviceCopies===1&&transportCopies===1)continue;
                const window={webpackChunksteamui:{push:chunk=>chunk[2](runtime)}};
                const result=JSON.parse(vm.runInNewContext(expression,{window},{timeout:1000}));
                assert.equal(result.service,serviceCopies);assert.equal(result.transportModule,transportCopies);
                assert.equal(result.provider,0);assert.equal(loads,0);
              }
              process.stdout.write('done');
            });
            """;
        Assert.Equal("done", await NodeScript.RunAsync(script, Gate.ProbeExpression));
    }

    [Fact]
    public async Task TheActualStorageProbeStaysCompatibleAfterSendMsgIsOwnedWithoutReadingTheTransport()
    {
        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',()=>{
              const expression=JSON.parse(input),results=[];let transportReads=0;
              class Transport{SendMsg(){throw Error('transport invoked')}}
              const transport=new Transport(),provider={GetDefaultTransport:()=>{transportReads++;return transport}};
              function singletonAccessor(){return provider;}
              const runtime=id=>{assert.equal(id,'transport');return {singletonAccessor}};
              runtime.m={service:new Function('// StorageDeviceManager.IsServiceAvailable#1'),
                transport:new Function('// GetDefaultTransport m_transport')};
              const window={webpackChunksteamui:{push:chunk=>chunk[2](runtime)}};
              const probe=()=>JSON.parse(vm.runInNewContext(expression,{window},{timeout:1000}));
              results.push(probe());
              const owned=()=>{throw Error('owned wrapper invoked')};
              owned.__steamUiStorageClaimed=true;
              owned.__steamUiStorageOriginal={kind:'steam-ui-property-snapshot-v1',hadOwn:false,value:Transport.prototype.SendMsg};
              Object.defineProperty(transport,'SendMsg',{value:owned,writable:true,configurable:true});
              results.push(probe());
              assert.deepEqual(results[1],results[0]);
              assert.equal(transport.SendMsg,owned,'the probe must leave the claim intact');
              assert.equal(transportReads,0,'compatibility must not call GetDefaultTransport');
              process.stdout.write(JSON.stringify(results));
            });
            """;
        var output = await NodeScript.RunAsync(script, Gate.ProbeExpression);
        using var results = JsonDocument.Parse(output);
        Assert.True(Gate.Compatible(results.RootElement[0]));
        Assert.True(Gate.Compatible(results.RootElement[1]));
    }

    [Fact]
    public void TheProbeUsesTheSharedSelectorWithoutCallingTheAccessorOrTransport()
    {
        var probe = Gate.ProbeExpression;

        Assert.Contains("StorageDeviceManager.IsServiceAvailable#1", probe, StringComparison.Ordinal);
        Assert.Contains("GetDefaultTransport", probe, StringComparison.Ordinal);
        Assert.Contains("req.storageProvider()", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("selection.accessor()", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("provider.GetDefaultTransport()", probe, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"service":1,"transportModule":1,"provider":1}""", true)]
    [InlineData("""{"service":1,"transportModule":1}""", false)]
    [InlineData("""{"service":1,"transportModule":1,"provider":0}""", false)]
    [InlineData("""{"service":1,"transportModule":1,"provider":2}""", false)]
    // Two modules naming the service is ambiguous rather than a reason to pick one.
    [InlineData("""{"service":2,"transportModule":1,"provider":1}""", false)]
    [InlineData("""{"service":0,"transportModule":1,"provider":1}""", false)]
    [InlineData("""{"service":1,"transportModule":0,"provider":1}""", false)]
    [InlineData("""{"service":1,"transportModule":2,"provider":1}""", false)]
    [InlineData("""{"error":"Steam modules unavailable"}""", false)]
    [InlineData("""[1]""", false)]
    public void CompatibilityRequiresAUniqueServiceModuleTransportModuleAndProvider(string json, bool expected)
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
        Assert.True((await DispatchAsync(set, patchId, "format", Storage(3000000000, 0))).Succeeded);
        Assert.True((await DispatchAsync(set, patchId, "trimall", """{}""")).Succeeded);
        var refused = await DispatchAsync(set, patchId, "format", Storage(0, 0));
        var extra = await DispatchAsync(
            set, patchId, "adopt", """{"driveId":1,"blockDeviceId":0,"label":"","validate":false,"x":1}""");

        Assert.Equal("The storage format payload is invalid.", refused.Error);
        Assert.Equal("The storage adopt payload is invalid.", extra.Error);
        Assert.Equal(
            ["adopt 1 'Games' validate=True", "adopt 1 '' validate=False", "format 3000000000", "trimall"],
            backend.Calls);
    }

    // The one shape every storage action sends.
    private static string Storage(uint driveId, uint blockDeviceId, string label = "", bool validate = false)
    {
        return $$"""{"driveId":{{driveId}},"blockDeviceId":{{blockDeviceId}},"label":"{{label}}","validate":{{(validate ? "true" : "false")}}}""";
    }
}
