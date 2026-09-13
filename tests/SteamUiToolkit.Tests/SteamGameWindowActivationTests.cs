using System.Diagnostics;
using System.Text.Json;
using SteamUiToolkit.Surfaces;

namespace SteamUiToolkit.Tests;

public sealed class SteamGameWindowActivationTests
{
    [Fact]
    public async Task ResolvesOnlyExactOverlayProcessAndRefusesMissingAmbiguousOrExpiredRequests()
    {
        const string script = """
            const vm=require('node:vm'),assert=require('node:assert/strict');
            let input='';process.stdin.on('data',v=>input+=v);process.stdin.on('end',async()=>{
              const expressions=JSON.parse(input),calls=[];
              const overlay={params:{browserInfo:{m_unPID:42,m_unAppID:123,m_gameID:'123'}},IsGamepadUIOverlayWindow:()=>true};
              const store={OverlayWindows:[overlay]};
              const apps={async RaiseWindowForGame(id){calls.push(id);}};
              const context=vm.createContext({window:{SteamUIStore:{WindowStore:store},SteamClient:{Apps:apps}}});
              const run=key=>vm.runInContext(expressions[key],context);
              assert.equal(await run('valid'),true);assert.deepEqual(calls,['123']);
              overlay.params.browserInfo.m_gameID='18446744069448138752';
              assert.equal(await run('valid'),true);assert.equal(calls.at(-1),'18446744069448138752');
              for(const invalid of [undefined,123,'0','-1','1e3','18446744073709551616']){
                overlay.params.browserInfo.m_gameID=invalid;assert.equal(await run('valid'),false);
              }
              overlay.params.browserInfo.m_gameID='123';
              assert.equal(await run('other'),false);
              assert.equal(await run('main'),false);
              assert.equal(await run('expired'),false);
              store.OverlayWindows=[overlay,overlay];assert.equal(await run('valid'),false);
              store.OverlayWindows=[];assert.equal(await run('valid'),false);
              store.OverlayWindows=[overlay];overlay.params.browserInfo.m_unAppID=0;
              assert.equal(await run('valid'),false);
              overlay.params.browserInfo.m_unAppID=123;overlay.IsGamepadUIOverlayWindow=()=>false;
              assert.equal(await run('valid'),false);overlay.IsGamepadUIOverlayWindow=()=>true;
              apps.RaiseWindowForGame=undefined;assert.equal(await run('valid'),false);
              assert.deepEqual(calls,['123','18446744069448138752']);
              apps.RaiseWindowForGame=()=>Promise.reject(Error('rejected'));
              assert.equal(await run('valid'),false);
              let complete;apps.RaiseWindowForGame=()=>new Promise(resolve=>complete=resolve);
              let finished=false;const pending=run('valid').then(value=>{finished=true;return value;});
              await Promise.resolve();assert.equal(finished,false);
              complete();assert.equal(await pending,true);
            });
            """;
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        long future = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
        {
            valid = SteamGameWindowActivation.CreateExpression(42, future),
            other = SteamGameWindowActivation.CreateExpression(99, future),
            main = SteamGameWindowActivation.CreateExpression(0, future),
            expired = SteamGameWindowActivation.CreateExpression(42, 0),
        }));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await stderr + await stdout);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task CompletionRequiresCurrentReadyGeneration(bool replaced, bool unavailable, bool expected)
    {
        await using var transport = new FakeTransport { ReplaceDuringCall = replaced, Unavailable = unavailable };
        Assert.Equal(expected, await SteamGameWindowActivation.RaiseAsync(transport, 42));
        Assert.Equal(unavailable ? 0 : 1, transport.Calls);
    }

    [Fact]
    public async Task CancelledRequestNeverDispatches()
    {
        await using var transport = new FakeTransport();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SteamGameWindowActivation.RaiseAsync(transport, 42, cancellation.Token));
        Assert.Equal(0, transport.Calls);
    }

    private sealed class FakeTransport : ISteamUiTransport
    {
        public bool ReplaceDuringCall { get; init; }
        public bool Unavailable { get; init; }
        public int Calls { get; private set; }
        private SteamUiGenerations _generations = new(1, 1, 1, 1, 1, 1);
        public event EventHandler<SteamUiNotification>? NotificationReceived { add { } remove { } }
        public event EventHandler<SteamUiTransportSnapshot>? GenerationChanged { add { } remove { } }
        public ValueTask<IAsyncDisposable> SubscribeAsync(SteamUiTargetRole role, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Must borrow the existing subscription");
        public Task SetRuntimeBindingAsync(SteamUiTargetRole role, string bindingName,
            bool enabled, TimeSpan timeout, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Must not install a binding");
        public Task<SteamUiEvaluationResult> EvaluateAsync(SteamUiTargetRole role, string expression,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.Equal(SteamUiTargetRole.SharedJsContext, role);
            Assert.Equal(TimeSpan.FromSeconds(1), timeout);
            var result = new SteamUiEvaluationResult(true, "true", null, _generations);
            if (ReplaceDuringCall) { _generations = _generations with { Document = 2 }; }
            return Task.FromResult(result);
        }
        public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots() =>
            [new(SteamUiTargetRole.SharedJsContext, Unavailable ? SteamUiTransportHealth.Unavailable : SteamUiTransportHealth.Ready,
                _generations, "shared", null, 0, 1)];
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
