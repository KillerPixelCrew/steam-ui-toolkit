namespace SteamUiToolkit.Tests;

public sealed class SteamNativeSurfaceReplayTests
{
    [Theory]
    [InlineData(SteamUiTransportHealth.Ready, false, 1)]
    [InlineData(SteamUiTransportHealth.Unavailable, false, 0)]
    [InlineData(SteamUiTransportHealth.Ready, true, 0)]
    public async Task AdmissionRequiresTheObservedReadyGeneration(SteamUiTransportHealth health, bool stale, int calls)
    {
        await using var transport = new FakeSteamUiTransport { Health = health, EvaluationValue = "true" };
        var observed = transport.Generations;
        if (stale)
        {
            transport.AdvanceGenerationWithoutEvent();
        }

        Assert.Equal(calls == 1, await SteamNativeSurfaceCommands.ReplayAsync(transport,
            SteamNativeSurfaceAction.QuickAccess, 42, 123, observed));
        Assert.Equal(calls, transport.Expressions.Count);
        Assert.Equal(0, transport.ReleasedSubscriptions);
    }

    [Theory]
    [InlineData("true", false, false, true)]
    [InlineData("false", false, false, false)]
    [InlineData("{\"ok\":true}", false, false, false)]
    [InlineData("true", true, false, false)]
    [InlineData("true", false, true, false)]
    public async Task CompletionRequiresAPositiveReplyAndTheSameReadyGeneration(
        string value, bool replaced, bool unavailable, bool expected)
    {
        await using var transport = new FakeSteamUiTransport();
        var observed = transport.Generations;
        transport.OnEvaluate = call =>
        {
            Assert.Equal(SteamUiTargetRole.SharedJsContext, call.Role);
            Assert.Equal(TimeSpan.FromSeconds(2), call.Timeout);
            var reply = transport.Reply(value);
            if (replaced)
            {
                transport.AdvanceGenerationWithoutEvent();
            }

            if (unavailable)
            {
                transport.Health = SteamUiTransportHealth.Unavailable;
            }

            return Task.FromResult(reply);
        };

        Assert.Equal(expected, await SteamNativeSurfaceCommands.ReplayAsync(transport,
            SteamNativeSurfaceAction.Home, 42, 123, observed));
        Assert.Single(transport.Expressions);
    }

    [Fact]
    public async Task CancellationNeverDispatches()
    {
        await using var transport = new FakeSteamUiTransport();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SteamNativeSurfaceCommands.ReplayAsync(
            transport, SteamNativeSurfaceAction.Keyboard, 0, 0, transport.Generations, cancellation.Token));
        Assert.Empty(transport.Expressions);
    }
}
