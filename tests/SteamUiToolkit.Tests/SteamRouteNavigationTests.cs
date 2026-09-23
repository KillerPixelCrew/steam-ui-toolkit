using SteamUiToolkit.Surfaces;

namespace SteamUiToolkit.Tests;

/// <summary>
///     Host-requested navigation: one bounded push on Steam's router, agreeing with the gates' own
///     <c>navigateSteamRoute</c> on what a route may be.
/// </summary>
public sealed class SteamRouteNavigationTests
{
    [Theory]
    [InlineData("/wsgm/artwork/77", true)]
    [InlineData("/library", true)]
    [InlineData("/", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("wsgm/artwork/77", false)]
    [InlineData("/wsgm/\nartwork", false)]
    public void OnlyAnAbsoluteNonRootRouteIsNavigable(string? route, bool expected)
    {
        Assert.Equal(expected, SteamRouteNavigation.IsNavigable(route));
    }

    [Fact]
    public void ARouteLongerThanTheGatesAcceptIsNotNavigable()
    {
        Assert.False(SteamRouteNavigation.IsNavigable("/" + new string('a', SteamRouteNavigation.MaximumRouteLength)));
    }

    [Fact]
    public async Task PushesExactlyTheRouteAndRefusesWithoutARouterOrWhenExpired()
    {
        const string script = """
                              const vm=require('node:vm'),assert=require('node:assert/strict');
                              let input='';process.stdin.on('data',v=>input+=v);process.stdin.on('end',()=>{
                                const expressions=JSON.parse(input),pushed=[];
                                const history={push(route){pushed.push(route);}};
                                const context=vm.createContext({window:{tempNavStore:{m_history:history}}});
                                const run=key=>vm.runInContext(expressions[key],context);
                                assert.equal(run('valid'),true);assert.deepEqual(pushed,['/wsgm/artwork/77']);
                                assert.equal(run('quoted'),true);assert.equal(pushed.at(-1),'/wsgm/a"b\\c');
                                assert.equal(run('expired'),false);
                                context.window.tempNavStore={};assert.equal(run('valid'),false);
                                context.window.tempNavStore={m_history:{push(){throw Error('no');}}};
                                assert.equal(run('valid'),false);
                                assert.equal(pushed.length,2);
                              });
                              """;
        var future = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
        await NodeScript.RunAsync(script, new
        {
            valid = SteamRouteNavigation.CreateExpression("/wsgm/artwork/77", future),
            quoted = SteamRouteNavigation.CreateExpression("/wsgm/a\"b\\c", future),
            expired = SteamRouteNavigation.CreateExpression("/wsgm/artwork/77", 0)
        });
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task CompletionRequiresTheSameReadyGeneration(bool replaced, bool unavailable, bool expected)
    {
        await using var transport = Transport();
        transport.Health = unavailable ? SteamUiTransportHealth.Unavailable : SteamUiTransportHealth.Ready;
        transport.OnEvaluate = call =>
        {
            Assert.Equal(SteamUiTargetRole.SharedJsContext, call.Role);
            var result = transport.Reply("true");
            if (replaced)
            {
                transport.Generations = transport.Generations with { Document = 2 };
            }

            return Task.FromResult(result);
        };

        Assert.Equal(expected, await SteamRouteNavigation.NavigateAsync(transport, "/wsgm/artwork/77"));
        Assert.Equal(unavailable ? 0 : 1, transport.Expressions.Count);
    }

    [Fact]
    public async Task AnUnnavigableRouteNeverReachesSteam()
    {
        await using var transport = Transport();

        Assert.False(await SteamRouteNavigation.NavigateAsync(transport, "/"));
        Assert.Empty(transport.Expressions);
    }

    [Fact]
    public async Task ACancelledRequestNeverDispatches()
    {
        await using var transport = Transport();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SteamRouteNavigation.NavigateAsync(transport, "/wsgm/artwork/77", cancellation.Token));
        Assert.Empty(transport.Expressions);
    }

    private static FakeSteamUiTransport Transport()
    {
        return new FakeSteamUiTransport
        {
            RejectSubscriptions = true,
            RejectBindings = true,
            EvaluationValue = "true"
        };
    }
}
