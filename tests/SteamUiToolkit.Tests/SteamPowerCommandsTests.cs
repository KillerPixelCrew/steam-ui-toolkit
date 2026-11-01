namespace SteamUiToolkit.Tests;

/// <summary>Steam's power menu, its suspend flow without the PC suspend, and the resume notification.</summary>
public sealed class SteamPowerCommandsTests
{
    [Fact]
    public async Task PowerMenuCallsSteamOnceAndRefusesMissingOrExpiredStores()
    {
        const string script = """
                              const vm=require('node:vm'),assert=require('node:assert/strict');
                              let input='';process.stdin.on('data',v=>input+=v);process.stdin.on('end',()=>{
                                const expressions=JSON.parse(input),calls=[];
                                const ui={OpenPowerMenu(...args){calls.push(args);}};
                                const context=vm.createContext({window:{SteamUIStore:ui}});
                                const run=key=>vm.runInContext(expressions[key],context);
                                assert.equal(run('valid'),true);
                                assert.deepEqual(calls,[[null]]);
                                assert.equal(run('expired'),false);
                                ui.OpenPowerMenu=undefined;assert.equal(run('valid'),false);
                                ui.OpenPowerMenu=()=>{throw Error('broken');};assert.equal(run('valid'),false);
                                context.window.SteamUIStore=undefined;assert.equal(run('valid'),false);
                                assert.equal(calls.length,1);
                              });
                              """;
        var future = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
        await NodeScript.RunAsync(script, new
        {
            valid = SteamPowerCommands.CreateOpenPowerMenuExpression(future),
            expired = SteamPowerCommands.CreateOpenPowerMenuExpression(0)
        });
    }

    [Fact]
    public async Task SuspendFlowSuppressesThePcSuspendAndReportsHowItEnded()
    {
        const string script = """
                              const vm=require('node:vm'),assert=require('node:assert/strict');
                              let input='';process.stdin.on('data',v=>input+=v);process.stdin.on('end',async()=>{
                                const expressions=JSON.parse(input),calls=[];
                                // Mirrors Steam's sleep manager: blockers or a running suspend ignore the request.
                                const store={m_bSuspending:false,m_eProgress:0,mode:'complete',blockers:0,
                                  get suspending(){return this.m_bSuspending;},
                                  GetSuspendResumeState(){return this.m_eProgress;},
                                  InitiateSleep(...args){calls.push(args);
                                    if(this.blockers>0||this.m_bSuspending)return;
                                    this.m_bSuspending=true;this.m_eProgress=0;
                                    if(this.mode==='stuck')return;
                                    setTimeout(()=>{if(this.mode==='complete')this.m_eProgress=1;this.m_bSuspending=false;},200);
                                  }};
                                const context=vm.createContext({window:{SuspendResumeStore:store},setTimeout});
                                const run=key=>vm.runInContext(expressions[key],context);
                                assert.equal(await run('flow'),'completed');
                                assert.deepEqual(calls,[[true]]);
                                assert.equal(store.suspending,false);
                                store.mode='fail';assert.equal(await run('flow'),'failed');
                                store.blockers=1;assert.equal(await run('flow'),'refused');store.blockers=0;
                                assert.equal(calls.length,3);
                                store.m_bSuspending=true;assert.equal(await run('flow'),'refused');
                                assert.equal(calls.length,3);store.m_bSuspending=false;
                                store.mode='stuck';const started=Date.now();
                                assert.equal(await run('short'),'timeout');
                                assert.ok(Date.now()-started<2000);
                                assert.equal(store.suspending,true);store.m_bSuspending=false;
                                assert.equal(await run('expired'),'unavailable');
                                assert.equal(calls.length,4);
                                store.GetSuspendResumeState=undefined;assert.equal(await run('flow'),'unavailable');
                                store.GetSuspendResumeState=()=>1;store.InitiateSleep=undefined;
                                assert.equal(await run('flow'),'unavailable');
                                context.window.SuspendResumeStore=undefined;assert.equal(await run('flow'),'unavailable');
                                context.window.SuspendResumeStore={InitiateSleep(){throw Error('broken');},GetSuspendResumeState:()=>0,suspending:false};
                                assert.equal(await run('flow'),'unavailable');
                                assert.ok(calls.every(c=>c.length===1&&c[0]===true));
                              });
                              """;
        var future = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
        await NodeScript.RunAsync(script, new
        {
            flow = SteamPowerCommands.CreateSuspendFlowExpression(future, TimeSpan.FromSeconds(5)),
            @short = SteamPowerCommands.CreateSuspendFlowExpression(future, TimeSpan.FromMilliseconds(300)),
            expired = SteamPowerCommands.CreateSuspendFlowExpression(0, TimeSpan.FromSeconds(5))
        });
    }

    [Fact]
    public async Task ResumeCallsSteamOnceAndRefusesMissingOrExpiredStores()
    {
        const string script = """
                              const vm=require('node:vm'),assert=require('node:assert/strict');
                              let input='';process.stdin.on('data',v=>input+=v);process.stdin.on('end',()=>{
                                const expressions=JSON.parse(input),calls=[];
                                const store={InitiateResume(...args){calls.push(args);}};
                                const context=vm.createContext({window:{SuspendResumeStore:store}});
                                const run=key=>vm.runInContext(expressions[key],context);
                                assert.equal(run('valid'),true);
                                assert.deepEqual(calls,[[]]);
                                assert.equal(run('expired'),false);
                                store.InitiateResume=undefined;assert.equal(run('valid'),false);
                                context.window.SuspendResumeStore=undefined;assert.equal(run('valid'),false);
                                assert.equal(calls.length,1);
                              });
                              """;
        var future = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
        await NodeScript.RunAsync(script, new
        {
            valid = SteamPowerCommands.CreateResumeExpression(future),
            expired = SteamPowerCommands.CreateResumeExpression(0)
        });
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task CommandsRequireTheSameReadyGeneration(bool replaced, bool unavailable, bool expected)
    {
        await using var transport = PowerTransport("true", replaced, unavailable);
        Assert.Equal(expected, await SteamPowerCommands.OpenPowerMenuAsync(transport));
        Assert.Equal(expected, await SteamPowerCommands.NotifyResumedAsync(transport));
        Assert.Equal(unavailable ? 0 : 2, transport.Expressions.Count);
    }

    [Theory]
    [InlineData("completed", false, false, SteamSuspendFlowResult.Completed)]
    [InlineData("refused", false, false, SteamSuspendFlowResult.Refused)]
    [InlineData("failed", false, false, SteamSuspendFlowResult.Failed)]
    [InlineData("timeout", false, false, SteamSuspendFlowResult.TimedOut)]
    [InlineData("unavailable", false, false, SteamSuspendFlowResult.Unavailable)]
    [InlineData("other", false, false, SteamSuspendFlowResult.Unavailable)]
    [InlineData("completed", true, false, SteamSuspendFlowResult.Unavailable)]
    [InlineData("completed", false, true, SteamSuspendFlowResult.Unavailable)]
    public async Task SuspendFlowMapsOnlyTrustedAnswers(string reply, bool replaced, bool unavailable,
        SteamSuspendFlowResult expected)
    {
        await using var transport = PowerTransport(reply, replaced, unavailable);
        Assert.Equal(expected,
            await SteamPowerCommands.RunSuspendFlowAsync(transport, SteamPowerCommands.DefaultSuspendBudget));
        Assert.Equal(unavailable ? 0 : 1, transport.Expressions.Count);
    }

    [Fact]
    public async Task SuspendFlowWaitsPastItsBudgetAndRejectsInvalidBudgets()
    {
        await using var transport = PowerTransport("completed", replaced: false, unavailable: false);
        TimeSpan? timeout = null;
        transport.OnEvaluate = call =>
        {
            timeout = call.Timeout;
            return Task.FromResult(transport.Reply("completed"));
        };
        await SteamPowerCommands.RunSuspendFlowAsync(transport, TimeSpan.FromSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(6), timeout);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            SteamPowerCommands.RunSuspendFlowAsync(transport, TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            SteamPowerCommands.RunSuspendFlowAsync(transport, TimeSpan.FromSeconds(26)));
        Assert.Single(transport.Expressions);
    }

    [Fact]
    public async Task CancelledRequestsNeverDispatch()
    {
        await using var transport = PowerTransport("true", replaced: false, unavailable: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SteamPowerCommands.OpenPowerMenuAsync(transport, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SteamPowerCommands.RunSuspendFlowAsync(transport, SteamPowerCommands.DefaultSuspendBudget,
                cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SteamPowerCommands.NotifyResumedAsync(transport, cancellation.Token));
        Assert.Empty(transport.Expressions);
    }

    [Fact]
    public async Task CancellationDuringTheFlowReachesTheTransport()
    {
        await using var transport = PowerTransport("completed", replaced: false, unavailable: false);
        using var cancellation = new CancellationTokenSource();
        transport.OnEvaluate = async call =>
        {
            Assert.Equal(cancellation.Token, call.CancellationToken);
            await cancellation.CancelAsync();
            call.CancellationToken.ThrowIfCancellationRequested();
            return transport.Reply("completed");
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SteamPowerCommands.RunSuspendFlowAsync(transport, SteamPowerCommands.DefaultSuspendBudget,
                cancellation.Token));
    }

    /// <summary>A borrowed transport answering SharedJSContext, optionally replaced during the call.</summary>
    private static FakeSteamUiTransport PowerTransport(string reply, bool replaced, bool unavailable)
    {
        var transport = new FakeSteamUiTransport
        {
            RejectSubscriptions = true,
            RejectBindings = true,
            Health = unavailable ? SteamUiTransportHealth.Unavailable : SteamUiTransportHealth.Ready
        };
        transport.OnEvaluate = call =>
        {
            Assert.Equal(SteamUiTargetRole.SharedJsContext, call.Role);
            var result = transport.Reply(reply);
            if (replaced)
            {
                transport.Generations = transport.Generations with { Document = transport.Generations.Document + 1 };
            }

            return Task.FromResult(result);
        };
        return transport;
    }
}
