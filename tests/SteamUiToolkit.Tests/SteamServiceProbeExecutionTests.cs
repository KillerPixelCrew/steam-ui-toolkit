using System.Text.Json;

namespace SteamUiToolkit.Tests;

public sealed class SteamServiceProbeExecutionTests
{
    [Theory]
    [InlineData("audio")]
    [InlineData("performance")]
    [InlineData("brightness")]
    [InlineData("bluetooth")]
    [InlineData("screensaver")]
    [InlineData("sound")]
    public async Task ServiceProbesAcceptUniqueAndOwnedServicesAndRefuseAmbiguousOrIncompleteOnes(string service)
    {
        var gate = (SteamGatePatch)(service switch
        {
            "audio" => SteamAudioSurface.Patch,
            "performance" => SteamPerformanceSurface.Patch,
            "brightness" => SteamBrightnessSurface.Patch,
            "bluetooth" => SteamBluetoothSurface.Patch,
            "screensaver" => SteamScreensaverSurface.Patch,
            "sound" => SteamSoundOverrideSurface.Patch,
            _ => throw new ArgumentOutOfRangeException(nameof(service))
        });
        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',()=>{
              const test=JSON.parse(input),results=[];
              const modes=['unique','owned','absent','ambiguous','ambiguous-export','incomplete'];
              if(['audio','performance','brightness'].includes(test.service))modes.push('native');
              if(test.service==='bluetooth')modes.push('locked','missing-cache','ambiguous-cache');
              if(test.service==='screensaver')modes.push('missing-settings','ambiguous-settings');
              for(const mode of modes){
                const loaded=[],exports={},runtime=id=>{loaded.push(id);return exports[id]};runtime.m={};
                const add=(id,source,value)=>{
                  runtime.m[id]=new Function('// '+source+'\nthrow Error("factory executed")');exports[id]=value;
                };
                add('unrelated','unrelated module',{danger:()=>{throw Error('unrelated export invoked')}});
                const forbidden=()=>{throw Error('service operation invoked')};
                const window={SteamClient:{System:{}},webpackChunksteamui:{push:chunk=>chunk[2](runtime)}};
                let source,make,getCalls=0,routeCalls=0;
                switch(test.service){
                  case 'audio':
                    source='SteamClient.System.Audio RegisterForDeviceAdded m_bAvailable';
                    make=()=>mode==='incomplete'?{}:{m_bAvailable:false,RegisterOrUpdateDevice:forbidden};
                    if(mode==='owned')window.SteamClient.System.Audio={__steamUiOwnedNamespace:true};
                    if(mode==='native')window.SteamClient.System.Audio={};
                    break;
                  case 'performance':
                    source='SteamClient.System.Perf RegisterForStateChanges m_msgState';
                    make=()=>{
                      function Store(){throw Error('m_msgState export constructed')}
                      Store.Get=()=>{getCalls++;return mode==='incomplete'?{}:{m_msgState:{}}};return Store;
                    };
                    if(mode==='owned')window.SteamClient.System.Perf={__steamUiOwnedNamespace:true};
                    if(mode==='native')window.SteamClient.System.Perf={};
                    break;
                  case 'brightness':
                    source='m_flDisplayBrightness is_display_brightness_available';
                    make=()=>{
                      function Store(){throw Error('m_flDisplayBrightness export constructed')}
                      Store.Get=()=>{getCalls++;return {m_msgSettings:{
                        is_display_brightness_available:mode==='owned'||mode==='native',
                        __steamUiBrightnessRevealed:mode==='owned'}}};return Store;
                    };
                    window.SteamClient.System.Display={SetBrightness:forbidden};
                    if(mode!=='incomplete')window.SteamClient.System.Display.RegisterForBrightnessChanges=forbidden;
                    break;
                  case 'bluetooth':
                    source='BluetoothManager.GetState#1';
                    make=()=>{
                      const service={};
                      for(const name of ['GetState','SetDiscovering','Pair','CancelPair','Connect','Disconnect',
                        'Forget','SetTrusted','SetWakeAllowed','GetDeviceDetails'])service[name]=forbidden;
                      if(mode==='owned'){
                        service.GetState=()=>{throw Error('owned GetState invoked')};
                        service.GetState.__steamUiOwnedBluetoothService=true;
                        service.GetState.__steamUiOriginalBluetoothServiceMethod={kind:'steam-ui-property-snapshot-v1',value:forbidden};
                      }
                      if(mode==='incomplete')delete service.Forget;
                      if(mode==='locked')Object.defineProperty(service,'GetState',{writable:false,configurable:false});
                      return service;
                    };
                    add('cache','ReactQueryDevtools offlineFirst',{cache:{invalidateQueries:forbidden,getQueryState:forbidden}});
                    if(mode==='missing-cache')delete runtime.m.cache;
                    if(mode==='ambiguous-cache')add('cacheTwin','ReactQueryDevtools offlineFirst',{});
                    break;
                  case 'screensaver':
                    source='GameAPIOSK: /gameapiosk';
                    make=()=>({Settings:{Customization:()=>{routeCalls++;return mode==='incomplete'?'invalid':'/settings/customization'}}});
                    add('react','react.transitional.element useState cloneElement createElement',{});
                    add('fields','DialogSlider_Container DropDownField SliderField',{});
                    add('section','"#Settings_Customization_Screensaver" ForceScreensaver',{});
                    add('settings','get clientSettings() m_setDeferredSettings',{settings:{clientSettings:{}}});
                    if(mode==='owned')exports.react={react:{useMemo:Object.assign(forbidden,{__steamUiOwnedUseMemo:true})}};
                    if(mode==='missing-settings')delete runtime.m.settings;
                    if(mode==='ambiguous-settings')add('settingsTwin','get clientSettings() m_setDeferredSettings',{});
                    break;
                  case 'sound':
                    source='m_GamepadUIAudioStore m_bHomeAndQuickAccessButtonsEnabled';
                    make=()=>{
                      const play=()=>{throw Error('audio playback invoked')};
                      if(mode==='owned'){
                        play.__steamUiSoundsClaimed=true;
                        play.__steamUiSoundsOriginal={kind:'steam-ui-property-snapshot-v1',value:forbidden};
                      }
                      return {GamepadUIAudio:{AudioPlaybackManager:mode==='incomplete'?{}:{PlayAudioURLWithRepeats:play}}};
                    };
                    break;
                }
                if(mode!=='absent'){
                  const value=make(),values={first:value,alias:value};
                  if(mode==='ambiguous-export')values.second=make();
                  add('service',source,values);
                  if(mode==='ambiguous')add('twin',source,{first:make()});
                }
                const result=JSON.parse(vm.runInNewContext(test.expression,
                  {window,SteamClient:window.SteamClient},{timeout:1000}));
                assert.equal(loaded.includes('unrelated'),false,test.service+' '+mode);
                if(['absent','ambiguous'].includes(mode))assert.equal(loaded.includes('service'),false);
                if(['performance','brightness'].includes(test.service))
                  assert.equal(getCalls,['absent','ambiguous','ambiguous-export'].includes(mode)?0:1);
                if(test.service==='screensaver')
                  assert.equal(routeCalls,['absent','ambiguous','ambiguous-export'].includes(mode)?0:1);
                assert.doesNotMatch(result.error||'',/ReferenceError|SyntaxError/);
                results.push({mode,result});
              }
              process.stdout.write(JSON.stringify(results));
            });
            """;
        var output = await NodeScript.RunAsync(script, new { service, expression = gate.ProbeExpression });
        using var results = JsonDocument.Parse(output);
        foreach (var result in results.RootElement.EnumerateArray())
        {
            var mode = result.GetProperty("mode").GetString();
            Assert.Equal(mode is "unique" or "owned", gate.Compatible(result.GetProperty("result")));
        }
    }

    [Fact]
    public async Task CountOnlyMenuAndDetailsProbesRequireUniqueModulesAndNeverLoadExports()
    {
        var gates = new[]
        {
            (SteamGatePatch)SteamPowerMenuSurface.Patch,
            (SteamGatePatch)SteamGameContextMenuSurface.Patch,
            (SteamGatePatch)SteamLibraryBadgeSurface.DetailsPatch
        };
        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',()=>{
              const results=[];
              for(const test of JSON.parse(input))for(const mode of ['unique','absent','ambiguous']){
                let loads=0;const runtime=()=>{loads++;throw Error('count-only probe loaded exports')};runtime.m={};
                const add=(id,source)=>runtime.m[id]=new Function('// '+source);
                add('react','react.transitional.element useState cloneElement createElement');
                add('jsx','react.transitional.element .jsx .jsxs');
                const source=test.Id==='steam-ui.power-menu'?'#Quit_Shutdown #SwitchToDesktop':
                  test.Id==='steam-ui.game-context-menu'?'GetTargetApps BuildManageSubmenu GetPrimaryActionMenuItem':
                  'GameStatsSection:" PlayBarDetailLabel:" LastPlayedInfo:"';
                if(mode!=='absent')add('primary',source);
                if(mode==='ambiguous')add('twin',source);
                const window={webpackChunksteamui:{push:chunk=>chunk[2](runtime)}};
                const result=JSON.parse(vm.runInNewContext(test.expression,{window},{timeout:1000}));
                assert.equal(result.error,undefined,test.Id);assert.equal(loads,0,test.Id);
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
            Assert.Equal(result.GetProperty("mode").GetString() == "unique", gate.Compatible(result.GetProperty("result")));
        }
    }

    [Fact]
    public async Task ThemeProbeFindsPopupAndPortalDocumentsWithoutLoadingModules()
    {
        var gate = (SteamGatePatch)SteamThemeStyleSurface.Patch;
        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',()=>{
              const expression=JSON.parse(input),results=[];
              for(const mode of ['popups','portal','owned','throws','absent']){
                const window={webpackChunksteamui:{push(){throw Error('modules loaded')}}};
                if(['popups','owned','throws'].includes(mode))window.g_PopupManager={GetPopups:()=>{
                  if(mode==='throws')throw Error('popup unavailable');return [{document:{}},{document:{}}];
                }};
                const root=['portal','owned'].includes(mode)?{__reactContainer$fixture:{}}:{};
                const result=JSON.parse(vm.runInNewContext(expression,{window,document:{getElementById:()=>root}}));
                assert.equal(result.error,undefined);
                assert.equal(result.popups,['popups','owned'].includes(mode)?2:0);
                assert.equal(result.reactRoot,['portal','owned'].includes(mode)?1:0);
                results.push({mode,result});
              }
              process.stdout.write(JSON.stringify(results));
            });
            """;
        var output = await NodeScript.RunAsync(script, gate.ProbeExpression);
        using var results = JsonDocument.Parse(output);
        foreach (var result in results.RootElement.EnumerateArray())
        {
            Assert.Equal(result.GetProperty("mode").GetString() != "absent", gate.Compatible(result.GetProperty("result")));
        }
    }
}
