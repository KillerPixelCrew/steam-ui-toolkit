using System.Text.Json;

namespace SteamUiToolkit.Tests;

/// <summary>Executes the actual injected expressions, including their embedded resolver and lifecycle wrappers.</summary>
public sealed class SteamProbeExecutionTests
{
    [Fact]
    public async Task NetworkProbeReadsHiddenOwnedAndUnclaimableGettersWithoutLoadingModules()
    {
        var gate = (SteamGatePatch)SteamNetworkSurface.Patch;
        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',()=>{
              const expression=JSON.parse(input),results=[];
              for(const mode of ['hidden','owned','native','locked']){
                const get=()=>mode==='owned'||mode==='native';get.__steamUiOwnedGetter=mode==='owned';
                const proto={};Object.defineProperty(proto,'networkManagementAvailable',{get,configurable:mode!=='locked'});
                const window={SystemNetworkStore:Object.create(proto),webpackChunksteamui:{push(){throw Error('modules loaded')}}};
                const result=JSON.parse(vm.runInNewContext(expression,{window}));
                assert.equal(result.error,undefined);results.push(result);
              }
              process.stdout.write(JSON.stringify(results));
            });
            """;
        var output = await NodeScript.RunAsync(script, gate.ProbeExpression);
        using var results = JsonDocument.Parse(output);
        Assert.True(gate.Compatible(results.RootElement[0]));
        Assert.True(gate.Compatible(results.RootElement[1]));
        Assert.False(gate.Compatible(results.RootElement[2]));
        Assert.False(gate.Compatible(results.RootElement[3]));
    }

    [Fact]
    public async Task EveryQuickAccessRowExecutesItsRealProbeAndRefusesMissingModules()
    {
        var rows = SteamSurfaceModuleTests.BuildSurfaces(new RecordingBackend())
            .SelectMany(surface => surface.Module.Patches).OfType<SteamQuickAccessRowPatch>()
            .DistinctBy(patch => patch.Id).ToArray();
        Assert.NotEmpty(rows);
        var expressions = new List<string>();
        foreach (var row in rows)
        {
            await using var transport = new FakeSteamUiTransport();
            await row.ProbeAsync(new SteamUiPatchContext(transport, TimeSpan.FromSeconds(1)), CancellationToken.None);
            expressions.Add(Assert.Single(transport.Expressions));
        }

        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',()=>{
              const runtime=()=>{throw Error('missing module invoked')};runtime.m={};
              const window={webpackChunksteamui:{push:chunk=>chunk[2](runtime)}};
              const results=JSON.parse(input).map(expression=>{
                const value=JSON.parse(vm.runInNewContext(expression,{window},{timeout:1000}));
                assert.equal(value.error,undefined);return value;
              });process.stdout.write(JSON.stringify(results));
            });
            """;
        var output = await NodeScript.RunAsync(script, expressions);
        using var results = JsonDocument.Parse(output);
        for (var index = 0; index < rows.Length; index++)
        {
            await using var transport = new FakeSteamUiTransport { EvaluationValue = results.RootElement[index].GetRawText() };
            var probe = await rows[index].ProbeAsync(new SteamUiPatchContext(transport, TimeSpan.FromSeconds(1)),
                CancellationToken.None);
            Assert.True(probe.TargetPresent);
            Assert.False(probe.Compatible, rows[index].Id);
        }
    }

    [Fact]
    public async Task EveryBuiltInGateExecutesAndChecksItsLiveStatusAndRemovalResult()
    {
        var gates = SteamSurfaceModuleTests.BuildSurfaces(new RecordingBackend())
            .SelectMany(surface => surface.Module.Patches).OfType<SteamGatePatch>()
            .DistinctBy(gate => gate.Id).ToList();
        gates.Add((SteamGatePatch)SteamPagePatch.Create("example.page", "examplePage", "v1", "Example",
            [SteamPageProbe.React, SteamPageProbe.Fields]));
        var expressions = new List<object>();
        foreach (var gate in gates)
        {
            await using var transport = new FakeSteamUiTransport();
            var context = new SteamUiPatchContext(transport, TimeSpan.FromSeconds(1));
            await gate.VerifyAsync(context, CancellationToken.None);
            var verify = Assert.Single(transport.Expressions);
            await gate.RemoveAsync(context, CancellationToken.None);
            expressions.Add(new { gate.Id, bridgeNamespace = SteamUiBridgeIdentity.Namespace,
                probe = gate.ProbeExpression, verify, remove = transport.Expressions[1] });
        }

        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',()=>{
              const results=[];
              for(const test of JSON.parse(input)){
                const runtime=()=>{throw Error('absent factories must never run')};runtime.m={};
                const window={webpackChunksteamui:{push:chunk=>chunk[2](runtime)},SteamClient:{System:{}}};
                const globals={window,SteamClient:window.SteamClient,document:{getElementById:()=>null,body:{children:[]}}};
                const probe=JSON.parse(vm.runInNewContext(test.probe,globals,{timeout:1000}));
                assert.equal(typeof probe,'object',test.Id);
                assert.ok(probe&&!Array.isArray(probe),test.Id);
                if(probe.error)assert.doesNotMatch(probe.error,/ReferenceError|SyntaxError/,test.Id);
                results.push(probe);
                const status={installed:true,resolved:true,claimed:true,namespacePresent:true,available:true,
                  setterOwned:true,subscribed:true,observing:true,nativeComponentsResolved:true,replaced:1,
                  ownedRoots:1,claimsRemaining:true};
                let removed=false,removeOk=true;
                const gate={status:()=>status,remove:()=>{removed=true;return {ok:removeOk}}};
                window[test.bridgeNamespace]={gate:()=>gate};
                assert.equal(JSON.parse(vm.runInNewContext(test.verify,globals)).ok,true,test.Id);
                status.installed=false;
                assert.notEqual(JSON.parse(vm.runInNewContext(test.verify,globals)).ok,true,test.Id);
                status.installed=true;
                assert.notEqual(JSON.parse(vm.runInNewContext(test.remove,globals)).ok,true,test.Id);
                for(const key of Object.keys(status))status[key]=key==='replaced'||key==='ownedRoots'?0:false;
                removeOk=false;
                assert.equal(JSON.parse(vm.runInNewContext(test.remove,globals)).ok,false,test.Id);
                removeOk=true;
                assert.equal(JSON.parse(vm.runInNewContext(test.remove,globals)).ok,true,test.Id);
                assert.equal(removed,true,test.Id);
                delete window[test.bridgeNamespace];
                assert.equal(JSON.parse(vm.runInNewContext(test.verify,globals)).ok,false,test.Id);
              }
              process.stdout.write(JSON.stringify(results));
            });
            """;
        var output = await NodeScript.RunAsync(script, expressions);
        using var results = JsonDocument.Parse(output);
        Assert.Equal(gates.Count, results.RootElement.GetArrayLength());
        for (var index = 0; index < gates.Count; index++)
        {
            Assert.False(gates[index].Compatible(results.RootElement[index]), gates[index].Id);
        }
    }

    [Fact]
    public async Task QuickAccessRowsRequireUniqueDependenciesEvenAfterTheirComponentsAreOwned()
    {
        var rows = SteamSurfaceModuleTests.BuildSurfaces(new RecordingBackend())
            .SelectMany(surface => surface.Module.Patches).OfType<SteamQuickAccessRowPatch>()
            .DistinctBy(patch => patch.Id).ToArray();
        Assert.NotEmpty(rows);
        var expressions = new List<string>();
        foreach (var row in rows)
        {
            await using var transport = new FakeSteamUiTransport();
            await row.ProbeAsync(new SteamUiPatchContext(transport, TimeSpan.FromSeconds(1)), CancellationToken.None);
            expressions.Add(Assert.Single(transport.Expressions));
        }

        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',()=>{
              const results=[];
              const sources=[
                'SetFPSLimitEnabled SetFPSLimit SetPerfOverlayLevel SteamClient.System.Perf',
                'is_tdp_limit_available steamos_tdp_limit_enabled tdp_limit_min tdp_limit_max',
                '#QuickAccess_Tab_Perf_TDPLimitEnabled steamos_tdp_limit showBookendLabels',
                '#PlatformPerformanceProfile_Label steamos_platform_performance_profile rgOptions',
                '#QuickAccess_Tab_Perf_Common_Settings #QuickAccess_Tab_Perf_BatteryTimeRemaining TS.ON_FRAME',
                'DialogSlider_Container DropDownField SliderField',
                'PanelSectionTitle PanelSectionRow spinner',
                'Attempting to localize token Unable to find localization token LocalizeString',
                'react.transitional.element useState cloneElement createElement',
                '#QuickAccess_Tab_Settings_Section_Controller_Title #QuickAccess_ReorderControllers_Button #QuickAccess_Tab_Perf_Title'
              ];
              for(const expression of JSON.parse(input))for(const mode of ['unique','owned','ambiguous','missing']){
                let loads=0;const runtime=()=>{loads++;throw Error('row probe loaded exports')};runtime.m={};
                sources.forEach((source,index)=>runtime.m['module'+index]=new Function('// '+source));
                if(mode==='ambiguous')runtime.m.twin=runtime.m.module5;
                if(mode==='missing')delete runtime.m.module5;
                const window={webpackChunksteamui:{push:chunk=>chunk[2](runtime)}};
                if(mode==='owned')window.SteamClient={System:{Perf:{__steamUiOwnedNamespace:true}}};
                const result=JSON.parse(vm.runInNewContext(expression,{window},{timeout:1000}));
                assert.equal(result.error,undefined);assert.equal(loads,0);
                results.push({mode,result});
              }
              process.stdout.write(JSON.stringify(results));
            });
            """;
        var output = await NodeScript.RunAsync(script, expressions);
        using var results = JsonDocument.Parse(output);
        Assert.Equal(rows.Length * 4, results.RootElement.GetArrayLength());
        for (var index = 0; index < results.RootElement.GetArrayLength(); index++)
        {
            var result = results.RootElement[index];
            await using var transport = new FakeSteamUiTransport { EvaluationValue = result.GetProperty("result").GetRawText() };
            var probe = await rows[index / 4].ProbeAsync(new SteamUiPatchContext(transport, TimeSpan.FromSeconds(1)),
                CancellationToken.None);
            Assert.Equal(result.GetProperty("mode").GetString() is "unique" or "owned", probe.Compatible);
        }
    }

    [Fact]
    public async Task EveryPageTokenProbeCountsAbsentUniqueAndAmbiguousModulesWithoutInvokingExports()
    {
        var probes = typeof(SteamPageProbe).GetProperties(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(SteamPageProbe))
            .Select(property => (SteamPageProbe)property.GetValue(null)!).ToArray();
        var patch = (SteamGatePatch)SteamPagePatch.Create("example.page", "examplePage", "v1", "Example", probes);
        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',()=>{
              const test=JSON.parse(input),results=[];
              for(const copies of [0,1,2]){
                let calls=0;
                const runtime=()=>{calls++;throw Error('count-only probe invoked a factory')};runtime.m={};
                for(const probe of test.probes){
                  const tokens=vm.runInNewContext(probe.Tokens);
                  for(let i=0;i<copies;i++)runtime.m[probe.Name+i]=new Function('// '+tokens.join(' ')+'\nthrow Error("invoked")');
                }
                const window={webpackChunksteamui:{push:chunk=>chunk[2](runtime)}};
                const result=JSON.parse(vm.runInNewContext(test.expression,{window},{timeout:1000}));
                for(const probe of test.probes)assert.equal(result[probe.Name],copies,probe.Name);
                assert.equal(calls,0);
                results.push(result);
              }
              process.stdout.write(JSON.stringify(results));
            });
            """;
        var output = await NodeScript.RunAsync(script, new { probes, expression = patch.ProbeExpression });
        using var results = JsonDocument.Parse(output);
        Assert.False(patch.Compatible(results.RootElement[0]));
        Assert.True(patch.Compatible(results.RootElement[1]));
        Assert.False(patch.Compatible(results.RootElement[2]));
    }

    [Fact]
    public async Task ComponentProbesRecognizeTheirOwnClaimsAndRejectAmbiguousExports()
    {
        var gates = new[]
        {
            (SteamGatePatch)SteamLibraryBadgeSurface.Patch,
            (SteamGatePatch)SteamNavigationPanelSurface.Patch,
            (SteamGatePatch)SteamExtensionsTabSurface.Patch,
            (SteamGatePatch)SteamHomeCarouselSurface.Patch,
            (SteamGatePatch)SteamPageSurface.Patch
        };
        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',()=>{
              const tests=JSON.parse(input),results=[];
              const source="ControllerSupportIcon appportrait_ #MainMenu_Title MainNavMenuContainer RunnningAppSeparator QuickAccessMenuBrowserView HomeTabsActive HomeActiveTab #Showcase_RecentGames Settings.Root() TopLevelTransition computedMatch";
              for(const test of tests)for(const mode of ['unclaimed','claimed','ambiguous','locked']){
                let invoked=0;
                const original=new Function('// '+source+'\nthrow Error("component invoked")');
                const wrapper=()=>{invoked++;throw Error('wrapper invoked')};
                for(const claim of ['LibraryBadge','NavigationPanel','ExtensionsTab','HomeCarousel','PageHost']){
                  wrapper['__steamUi'+claim+'Claimed']=true;
                  wrapper['__steamUi'+claim+'Original']={kind:'steam-ui-property-snapshot-v1',value:original};
                }
                const memo={$$typeof:Symbol.for('react.memo')};
                Object.defineProperty(memo,'type',{value:mode==='claimed'?wrapper:original,
                  writable:mode!=='locked',configurable:mode!=='locked'});
                const exports={memo,badge:original};if(mode==='ambiguous')exports.other={...memo,type:original};
                const runtime=id=>id==='component'?exports:{};
                runtime.m={component:new Function('// '+source),
                  react:new Function('// react.transitional.element useState cloneElement createElement'),
                  settings:new Function('// get clientSettings() m_setDeferredSettings'),
                  fields:new Function('// DialogSlider_Container DropDownField SliderField'),
                  focusable:new Function('// focusableIfEmpty onActivate "Panel"'),
                  panel:new Function('// PanelSectionTitle PanelSectionRow spinner'),
                  observer:new Function('// mobx-react-lite requires React with Hooks support')};
                if(mode==='ambiguous'&&(test.Id==='steam-ui.pages'||test.Id==='steam-ui.home-carousel'))
                  runtime.m.twin=new Function('// '+source);
                const window={webpackChunksteamui:{push:chunk=>chunk[2](runtime)},
                  collectionStore:{GetCollection(){}},appStore:{GetAppOverviewByAppID(){}}};
                const root={__reactContainer$fixture:{memoizedProps:{children:[
                  {props:{path:'/library/home',children:{type:memo}}},{props:{path:'/other'}},{props:{path:'/third'}}]},
                  child:{type:memo.type,elementType:memo}}};
                const document={getElementById:()=>root,body:{children:[root]}};
                const result=JSON.parse(vm.runInNewContext(test.expression,{window,document},{timeout:1000}));
                assert.equal(invoked,0,test.Id);
                assert.equal(result.error,undefined,test.Id);
                results.push({id:test.Id,mode,result});
              }
              process.stdout.write(JSON.stringify(results));
            });
            """;
        var output = await NodeScript.RunAsync(script, gates.Select(gate => new { gate.Id, expression = gate.ProbeExpression }));
        using var results = JsonDocument.Parse(output);
        foreach (var result in results.RootElement.EnumerateArray())
        {
            var gate = gates.Single(item => item.Id == result.GetProperty("id").GetString());
            var mode = result.GetProperty("mode").GetString();
            Assert.Equal(mode is "unclaimed" or "claimed", gate.Compatible(result.GetProperty("result")));
        }
    }
}
