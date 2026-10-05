using System.Text.Json;

namespace SteamUiToolkit.Tests;

public sealed class SteamClientScriptExecutionTests
{
    [Fact]
    public async Task AppScriptsCarryVerbatimFieldsAwaitWritesAndReportPartialFailures()
    {
        await using var transport = new FakeSteamUiTransport();
        var apps = new SteamClient(transport).Apps;
        const uint appId = 2147483655;
        const string target = "\"C:\\Owner's Games\\game.exe\"";
        const string arguments = "--profile=\"player's choice\"";
        var expressions = new Dictionary<string, string>();
        async Task Capture(string name, Func<Task> emit)
        {
            await emit();
            expressions.Add(name, transport.Expressions.Last());
        }

        await Capture("launch", () => apps.SetLaunchOptionsAsync(appId, arguments));
        await Capture("shortcut", () => apps.SetShortcutLaunchAsync(appId, target, arguments));
        await Capture("wholeShortcut", () => apps.SetShortcutLaunchAsync(appId, target, "C:\\Owner's Games", arguments));
        await Capture("remove", () => apps.RemoveShortcutAsync(appId));
        await Capture("artwork", () => apps.SetCustomArtworkAsync(appId, SteamArtworkSlot.Hero,
            new byte[] { 1, 2, 3 }, SteamArtworkFormat.Webp));
        await Capture("clearArtwork", () => apps.ClearCustomArtworkAsync(appId, SteamArtworkSlot.Hero));
        await Capture("icon", () => apps.SetShortcutIconAsync(appId, "C:\\Owner's Games\\icon.ico"));
        await Capture("clearIcon", () => apps.ClearShortcutIconAsync(appId));
        await Capture("refreshIcon", () => apps.RefreshIconAsync(appId));
        await Capture("officialIcon", () => apps.ReadOfficialIconUrlAsync(appId));
        await Capture("account", () => apps.ReadAccountIdAsync());
        await Capture("saveLogo", () => apps.SaveLogoPositionAsync(appId, new SteamLogoPosition("BottomLeft", 40, 25)));
        await Capture("readLogo", () => apps.ReadLogoPositionAsync(appId));
        await Capture("clearLogo", () => apps.ClearLogoPositionAsync(appId));
        await Capture("details", () => apps.ReadDetailsAsync(appId));
        await Capture("shortcuts", () => apps.ListShortcutsAsync());
        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',async()=>{
              try{
                const test=JSON.parse(input),calls=[],app={appid:test.appId,display_name:"Owner's game"};
                let fail='',unregistered=0,settled=false,logo=null,detailMode='ready';
                const details={strLaunchOptions:test.arguments,strShortcutExe:test.target,
                  strShortcutLaunchOptions:test.arguments,strShortcutStartDir:"C:\\Owner's Games",strInstallFolder:'D:/Installed'};
                const Apps={};
                for(const name of ['SetAppLaunchOptions','SetShortcutExe','SetShortcutStartDir','SetShortcutLaunchOptions',
                  'RemoveShortcut','ClearCustomArtworkForApp','SetCustomArtworkForApp','SetShortcutIcon','RequestIconDataForApp'])
                  Apps[name]=async(...args)=>{await Promise.resolve();calls.push([name,...args]);
                    if(name===fail)throw Error('setter failed');
                    if(name==='SetCustomArtworkForApp')assert.equal(settled,true,'set must await clear settlement');};
                Apps.RegisterForAppDetails=(id,callback)=>{
                  assert.equal(id,test.appId);if(detailMode==='throws')throw Error('details unavailable');
                  if(detailMode==='ready')queueMicrotask(()=>callback(details));return {unregister:()=>unregistered++};
                };
                const SteamClient={Apps},window={SteamClient,collectionStore:{allAppsCollection:{allApps:[app,app]}},
                  App:{m_CurrentUser:{strSteamID:'76561197960265770'}},
                  appStore:{GetAppOverviewByAppID:id=>id===test.appId?app:null,GetIconURLForApp:()=>"https://fixture/icon's.png"},
                  appDetailsStore:{SaveCustomLogoPosition:async(a,value)=>{assert.equal(a,app);logo=value},
                    GetCustomLogoPosition:a=>{assert.equal(a,app);return logo},
                    ClearCustomLogoPosition:async a=>{assert.equal(a,app);logo=null}}};
                const run=async name=>JSON.parse(await vm.runInNewContext(test.expressions[name],{
                  window,SteamClient,queueMicrotask,clearTimeout(){},
                  setTimeout:(callback,delay)=>{
                    if(delay===500){settled=true;queueMicrotask(callback)}
                    if(delay===3000&&detailMode==='timeout')queueMicrotask(callback);
                    return 1;
                  }
                }));
                assert.equal((await run('launch')).ok,true);
                assert.deepEqual(calls.splice(0),[['SetAppLaunchOptions',test.appId,test.arguments]]);
                assert.equal((await run('shortcut')).ok,true);
                assert.deepEqual(calls.splice(0),[['SetShortcutExe',test.appId,test.target],
                  ['SetShortcutLaunchOptions',test.appId,test.arguments]]);
                fail='SetShortcutStartDir';assert.equal((await run('wholeShortcut')).err,'setter failed');
                assert.deepEqual(calls.splice(0).map(call=>call[0]),['SetShortcutExe','SetShortcutStartDir']);
                fail='';assert.equal((await run('wholeShortcut')).ok,true);
                assert.deepEqual(calls.splice(0),[['SetShortcutExe',test.appId,test.target],
                  ['SetShortcutStartDir',test.appId,"C:\\Owner's Games"],['SetShortcutLaunchOptions',test.appId,test.arguments]]);
                assert.equal((await run('artwork')).ok,true);
                assert.deepEqual(calls.splice(0),[['ClearCustomArtworkForApp',test.appId,1],
                  ['SetCustomArtworkForApp',test.appId,'AQID','webp',1]]);
                for(const name of ['remove','clearArtwork','icon','clearIcon','refreshIcon'])assert.equal((await run(name)).ok,true,name);
                assert.deepEqual(calls.splice(0),[['RemoveShortcut',test.appId],['ClearCustomArtworkForApp',test.appId,1],
                  ['SetShortcutIcon',test.appId,"C:\\Owner's Games\\icon.ico"],['SetShortcutIcon',test.appId,''],
                  ['RequestIconDataForApp',test.appId]]);
                assert.equal((await run('officialIcon')).value,"https://fixture/icon's.png");
                assert.equal((await run('account')).value,'42');
                assert.equal((await run('saveLogo')).ok,true);
                assert.deepEqual(await run('readLogo'),{ok:true,anchor:'BottomLeft',width:40,height:25});
                assert.equal((await run('clearLogo')).ok,true);
                assert.deepEqual(await run('readLogo'),{ok:true,anchor:'',width:0,height:0});
                const reading=await run('details');
                assert.deepEqual(reading,{ok:true,launch:test.arguments,exe:test.target,args:test.arguments,
                  dir:"C:\\Owner's Games",install:'D:/Installed'});
                assert.deepEqual((await run('shortcuts')).shortcuts,[{id:String(test.appId),name:"Owner's game",
                  exe:test.target,dir:"C:\\Owner's Games",args:test.arguments}]);
                assert.equal(unregistered,2);
                detailMode='timeout';assert.equal((await run('details')).ok,false);assert.equal(unregistered,3);
                detailMode='throws';assert.equal((await run('shortcuts')).ok,false);
                delete window.collectionStore;assert.equal((await run('shortcuts')).ok,false);
                delete window.App;assert.equal((await run('account')).ok,false);
                delete window.appStore;assert.equal((await run('officialIcon')).ok,false);
                assert.equal((await run('saveLogo')).ok,false);
              }catch(error){console.error(error);process.exitCode=1;}
            });
            """;
        await NodeScript.RunAsync(script, new { expressions, appId, target, arguments });
    }

    [Fact]
    public async Task ShortcutCreationWritesOnlyTheOneEntryConfirmedByTheLibrary()
    {
        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',async()=>{
              try{
                const test=JSON.parse(input),results=[];
                for(const mode of ['confirmed','no-id','foreign','ambiguous','wrong-id','unloaded','unsupported']){
                  const calls=[],all=[{appid:2147483650,display_name:'Existing'}],id=2147483655;
                  const detail={strShortcutExe:test.target,strShortcutStartDir:test.directory,strShortcutLaunchOptions:test.arguments};
                  let app,unregistered=0;
                  const Apps={AddShortcut:async(...args)=>{
                    calls.push(['add',...args]);app={appid:id|0,display_name:mode==='foreign'?'Other':test.name};all.push(app);
                    if(mode==='ambiguous')all.push({appid:id+1,display_name:'Concurrent'});
                    return ['no-id','foreign'].includes(mode)?0:mode==='wrong-id'?id+1:id;
                  },RegisterForAppDetails:(value,callback)=>{
                    assert.equal(value,id);queueMicrotask(()=>callback(detail));return {unregister:()=>unregistered++};
                  }};
                  for(const [method,field] of [['SetShortcutName','display_name'],['SetShortcutExe','strShortcutExe'],
                    ['SetShortcutStartDir','strShortcutStartDir'],['SetShortcutLaunchOptions','strShortcutLaunchOptions']])
                    Apps[method]=async(value,text)=>{assert.equal(value,id);calls.push([method,text]);
                      if(field==='display_name')app[field]=text;else detail[field]=text;};
                  if(mode==='unsupported')delete Apps.AddShortcut;
                  const window={collectionStore:mode==='unloaded'?{}:{allAppsCollection:{allApps:all}},
                    appStore:{GetAppOverviewByAppID:()=>app}};
                  const result=JSON.parse(await vm.runInNewContext(test.expression,{window,SteamClient:{Apps},queueMicrotask,
                    clearTimeout(){},setTimeout:()=>1}));
                  if(['confirmed','no-id'].includes(mode)){
                    assert.equal(result.confirmed,true);assert.equal(result.value,String(id));assert.equal(result.mismatch,'');
                    assert.deepEqual(calls.slice(1),[['SetShortcutName',test.name],['SetShortcutExe',test.target],
                      ['SetShortcutStartDir',test.directory],['SetShortcutLaunchOptions',test.arguments]]);
                    assert.equal(unregistered,mode==='no-id'?2:1);
                  }else{
                    assert.equal(calls.filter(call=>call[0]!=='add').length,0,mode);
                    if(['ambiguous','wrong-id'].includes(mode))assert.equal(result.confirmed,false);
                    else assert.equal(result.ok,false,mode);
                  }
                  assert.equal(calls.filter(call=>call[0]==='add').length,['unloaded','unsupported'].includes(mode)?0:1);
                  if(calls.length)assert.deepEqual(calls[0],['add',test.name,test.target,test.directory,test.arguments]);
                  results.push({mode,result});
                }
                process.stdout.write(JSON.stringify(results));
              }catch(error){console.error(error);process.exitCode=1;}
            });
            """;
        const string name = "Owner's game";
        const string target = "\"C:\\Owner's Games\\game.exe\"";
        const string directory = "C:\\Owner's Games";
        const string arguments = "--name=\"owner's\"";
        var output = await NodeScript.RunAsync(script, new
        {
            name, target, directory, arguments,
            expression = SteamApps.AddShortcutScript(name, target, directory, arguments)
        });
        using var results = JsonDocument.Parse(output);
        foreach (var result in results.RootElement.EnumerateArray())
        {
            var outcome = SteamApps.ParseAddShortcut(FakeSteamUiTransport.Answer(result.GetProperty("result").GetRawText()));
            Assert.Equal(result.GetProperty("mode").GetString() is "confirmed" or "no-id", outcome.Confirmed);
        }
    }

    [Fact]
    public async Task CollectionSyncPreservesUserMembershipAndKeepsCreatedIdentityAfterLaterFailure()
    {
        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',async()=>{
              try{
                const test=JSON.parse(input),all=[{appid:1},{appid:2},{appid:3},{appid:-2147483648}],calls=[];
                const make=(id,apps)=>({id,allApps:apps,Save:async()=>{calls.push('save '+id)},
                  AsDragDropCollection(){return {AddApps:items=>this.allApps.push(...items),
                    RemoveApps:items=>{this.allApps=this.allApps.filter(app=>!items.includes(app))}}},
                  AsDeletableCollection:()=>({Delete:async()=>calls.push('delete '+id)})});
                const existing=make('owned',[all[0],all[2]]),sameName=make('user',[all[1]]);
                const store={allAppsCollection:{allApps:all},userCollections:[existing,sameName],
                  NewUnsavedCollection:(name,unused,apps)=>{
                    assert.equal(name,"Owner's collection");assert.equal(unused,undefined);
                    calls.push('create');return make('new',apps);
                  }};
                const window={collectionStore:store},run=async expression=>JSON.parse(await vm.runInNewContext(expression,{window}));
                assert.deepEqual(await run(test.sync),{ok:true,id:'owned',count:3});
                assert.deepEqual(existing.allApps.map(app=>app.appid),[3,2,-2147483648]);
                assert.deepEqual(sameName.allApps.map(app=>app.appid),[2]);assert.deepEqual(calls.splice(0),['save owned']);
                assert.equal((await run(test.sync)).ok,true);assert.deepEqual(calls.splice(0),[]);
                assert.deepEqual(await run(test.empty),{ok:true,id:null,count:0});
                assert.deepEqual(calls.splice(0),['save owned','delete owned']);
                assert.deepEqual(await run(test.create),{ok:true,id:'new',count:2});
                assert.deepEqual(calls.splice(0),['create','save new']);
                let saves=0;
                store.NewUnsavedCollection=(name,unused,apps)=>{
                  assert.equal(name,"Owner's collection");
                  const col=make('new',apps);col.allApps=[];col.Save=async()=>{if(++saves>1)throw Error('later save failed')};return col;
                };
                assert.deepEqual(await run(test.create),{ok:false,err:'later save failed',id:'new'});
                delete window.collectionStore;assert.equal((await run(test.create)).ok,false);
              }catch(error){console.error(error);process.exitCode=1;}
            });
            """;
        await NodeScript.RunAsync(script, new
        {
            sync = SteamCollections.SyncScript("owned", "Owner's collection", [2, 2147483648, 2, 999], [1], true),
            empty = SteamCollections.SyncScript("owned", "Owner's collection", [], [2, 3, 2147483648], true),
            create = SteamCollections.SyncScript(null, "Owner's collection", [1, 2], [], true)
        });
    }

    [Fact]
    public async Task RunningAppsObserverSeedsOnceTracksChangesAndCanBeRemovedAndInstalledAgain()
    {
        await using var transport = new FakeSteamUiTransport();
        var probe = new SteamClient(transport).RunningApps;
        var lease = await probe.SubscribeAsync();
        await probe.ObserveAsync();
        var observe = Assert.Single(transport.Expressions);
        await lease.DisposeAsync();
        var remove = transport.Expressions.Last();
        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',()=>{
              const test=JSON.parse(input);let callback,registrations=0,removals=0,mode='ok';
              const appStore={allApps:[{appid:10,display_status:4},{appid:20,display_status:1},{appid:-2147483648,display_status:4}]};
              const SteamClient={GameSessions:{RegisterForAppLifetimeNotifications:handler=>{
                registrations++;if(mode==='throws')throw Error('registration failed');
                if(mode==='no-handle')return {};callback=handler;return {unregister:()=>removals++};
              }}};
              const window={appStore},globals={window,appStore,SteamClient};
              const run=expression=>JSON.parse(vm.runInNewContext(expression,globals));
              assert.deepEqual(run(test.observe),{ok:true,ids:[10,2147483648],generation:1});
              assert.equal(run(test.observe).generation,1);assert.equal(registrations,1);
              callback({unAppID:20,bRunning:true});callback({unAppID:20,bRunning:true});
              callback({unAppID:10,bRunning:false});callback({unAppID:10,bRunning:false});
              for(const id of [0,-1,1.5,4294967296,'invalid'])callback({unAppID:id,bRunning:true});
              assert.deepEqual(run(test.observe),{ok:true,ids:[2147483648,20],generation:3});
              assert.equal(run(test.remove).ok,true);assert.equal(removals,1);assert.equal(window.__steamUiRunningApps,undefined);
              assert.equal(run(test.remove).ok,true);assert.equal(removals,1);
              mode='throws';assert.equal(run(test.observe).ok,false);assert.equal(window.__steamUiRunningApps,undefined);
              mode='no-handle';assert.equal(run(test.observe).ok,false);assert.equal(window.__steamUiRunningApps,undefined);
              mode='ok';assert.deepEqual(run(test.observe),{ok:true,ids:[10,2147483648],generation:1});
              assert.equal(registrations,4);assert.equal(run(test.remove).ok,true);assert.equal(removals,2);
            });
            """;
        await NodeScript.RunAsync(script, new { observe, remove });
    }

    [Fact]
    public async Task StartupMovieScriptsKeepTheExactChoiceIncludingPartialFailureAndANewerSteamChoice()
    {
        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',async()=>{
              try{
                const test=JSON.parse(input),calls=[];
                const state={startup_movie_id:'7',startup_movie_local_path:"/movie's.webm",startup_movie_shuffle:true};
                let fail=false;
                const window={settingsStore:{clientSettings:state,GetClientSetting:key=>[state[key],async value=>{
                  calls.push([key,value]);if(fail&&key==='startup_movie_local_path')throw Error('setter failed');state[key]=value;
                }]}};
                const run=async expression=>JSON.parse(await vm.runInNewContext(expression,{window,setTimeout}));
                assert.deepEqual(await run(test.aside),{ok:true,choice:{movieId:'7',localPath:"/movie's.webm",shuffle:true}});
                assert.deepEqual(calls.map(call=>call[0]),Object.keys(state));
                assert.deepEqual(state,{startup_movie_id:'',startup_movie_local_path:'',startup_movie_shuffle:false});
                assert.equal((await run(test.restore)).ok,true);
                assert.deepEqual(state,{startup_movie_id:'7',startup_movie_local_path:"/movie's.webm",startup_movie_shuffle:true});
                calls.length=0;state.startup_movie_id='new-choice';
                assert.deepEqual(await run(test.restore),{ok:true,choice:null});assert.equal(calls.length,0);
                state.startup_movie_id='7';fail=true;
                const partial=await run(test.aside);
                assert.equal(partial.ok,false);assert.equal(partial.err,'setter failed');
                assert.deepEqual(partial.choice,{movieId:'7',localPath:"/movie's.webm",shuffle:true});
              }catch(error){console.error(error);process.exitCode=1;}
            });
            """;
        await NodeScript.RunAsync(script, new
        {
            aside = SteamStartupMovie.SetAsideScript(),
            restore = SteamStartupMovie.RestoreScript(new SteamStartupMovieChoice("7", "/movie's.webm", true))
        });
    }

    [Fact]
    public async Task LibraryScriptsPreserveMountedRegistrationsAndReplaceOnlyWhenAsked()
    {
        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',async()=>{
              try{
                const test=JSON.parse(input),removed=[],labels=[];let added=0;
                const folders=[{nFolderIndex:1,strFolderPath:'e:/lib/',bIsMounted:false},
                  {nFolderIndex:2,strFolderPath:'E:/Lib',bIsMounted:true},
                  {nFolderIndex:3,strFolderPath:'F:/Other',bIsMounted:true}];
                const SteamClient={InstallFolder:{GetInstallFolders:async()=>folders,
                  RemoveInstallFolder:async id=>removed.push(id),AddInstallFolder:async()=>{added++;return 4},
                  SetFolderLabel:async(id,label)=>labels.push([id,label])}};
                const run=async expression=>JSON.parse(await vm.runInNewContext(expression,{SteamClient}));
                assert.equal((await run(test.adopt)).existing,true);
                assert.deepEqual(removed,[1]);assert.equal(added,0);assert.deepEqual(labels,[]);
                removed.length=0;
                assert.equal((await run(test.replace)).existing,false);
                assert.deepEqual(removed,[1,2]);assert.equal(added,1);assert.deepEqual(labels,[[4,"Card's library"]]);
                removed.length=0;const result=await run(test.remove);
                assert.equal(result.removed,2);assert.deepEqual(removed,[1,2]);
              }catch(error){console.error(error);process.exitCode=1;}
            });
            """;
        await NodeScript.RunAsync(script, new
        {
            adopt = SteamInstallFolders.BuildAddExpression("E:\\Lib", null, false),
            replace = SteamInstallFolders.BuildAddExpression("E:\\Lib", "Card's library", true),
            remove = SteamInstallFolders.BuildRemoveExpression("E:\\Lib")
        });
    }
}
