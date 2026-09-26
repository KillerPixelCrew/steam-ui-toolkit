using System.Text.Json;

namespace SteamUiToolkit.Tests;

public sealed class PersistentSteamUiTransportTests
{
    [Theory]
    [InlineData(SteamUiTargetRole.SharedJsContext)]
    [InlineData(SteamUiTargetRole.MainWindow)]
    public async Task ConnectingEnablesEveryGenerationNotificationDomain(SteamUiTargetRole role)
    {
        var factory = new ResponsiveWireFactory();
        await using var transport = new PersistentSteamUiTransport(
            new FixtureDiscovery(), factory);

        var result = await transport.EvaluateAsync(
            role,
            "'ready'",
            TimeSpan.FromSeconds(2));

        Assert.True(result.Reachable);
        Assert.Equal(
            ["Runtime.enable", "Page.enable", "DOM.enable", "Runtime.evaluate"],
            factory.Wires.Single().Methods);
    }

    [Fact]
    public async Task ConnectionIsNotPublishedUntilEveryGenerationDomainIsEnabled()
    {
        var factory = new ResponsiveWireFactory { BlockPageEnable = true };
        await using var transport = new PersistentSteamUiTransport(
            new FixtureDiscovery(), factory);
        var generationRaised = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.GenerationChanged += (_, _) => generationRaised.TrySetResult();

        var evaluation = transport.EvaluateAsync(
            SteamUiTargetRole.MainWindow,
            "'ready'",
            TimeSpan.FromSeconds(2));
        await factory.PageEnableStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(generationRaised.Task.IsCompleted);
        Assert.DoesNotContain(
            transport.GetSnapshots(),
            snapshot => snapshot.Role == SteamUiTargetRole.MainWindow
                        && snapshot.Health == SteamUiTransportHealth.Ready);

        factory.ReleasePageEnable.TrySetResult();
        Assert.True((await evaluation).Reachable);
        await generationRaised.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DocumentNotificationAdvancesGenerationAfterDomainsAreEnabled()
    {
        var factory = new ResponsiveWireFactory();
        await using var transport = new PersistentSteamUiTransport(
            new FixtureDiscovery(), factory);
        await using var subscription = await transport.SubscribeAsync(
            SteamUiTargetRole.MainWindow);
        _ = await transport.EvaluateAsync(
            SteamUiTargetRole.MainWindow,
            "'ready'",
            TimeSpan.FromSeconds(2));
        var changed = new TaskCompletionSource<SteamUiTransportSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.GenerationChanged += (_, snapshot) =>
        {
            if (snapshot.Role == SteamUiTargetRole.MainWindow
                && snapshot.Generations.Document > 1)
            {
                changed.TrySetResult(snapshot);
            }
        };

        factory.Wires.Single().Notify("DOM.documentUpdated", "{}");
        var snapshot = await changed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(snapshot.Generations.Document > 1);
    }

    [Fact]
    public async Task OneShotEvaluationReconnectsAfterItsPreviousLeaseWasReleased()
    {
        var factory = new ResponsiveWireFactory();
        await using var transport = new PersistentSteamUiTransport(
            new FixtureDiscovery(), factory);

        var first = await transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            "'first'",
            TimeSpan.FromSeconds(2));
        var second = await transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            "'second'",
            TimeSpan.FromSeconds(2));

        Assert.True(first.Reachable);
        Assert.True(second.Reachable);
        Assert.Equal(2, factory.Wires.Count);
    }

    [Fact]
    public async Task ReleaseThenResubscribeRejectsLateConnectionFromPreviousOwner()
    {
        var factory = new ResponsiveWireFactory { BlockFirstConnection = true };
        await using var transport = new PersistentSteamUiTransport(
            new FixtureDiscovery(), factory);
        var first = await transport.SubscribeAsync(
            SteamUiTargetRole.SharedJsContext);
        await factory.FirstConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await first.DisposeAsync();
        await using var replacement = await transport.SubscribeAsync(
            SteamUiTargetRole.SharedJsContext);
        factory.ReleaseFirstConnect.TrySetResult();

        var result = await transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            "'replacement'",
            TimeSpan.FromSeconds(2));

        Assert.True(result.Reachable);
        Assert.Equal(2, factory.Wires.Count);
        await factory.Wires[0].Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DisabledTransportRetainsIntentWithoutSendingCdpTraffic()
    {
        var factory = new ResponsiveWireFactory();
        await using var transport = new PersistentSteamUiTransport(
            new FixtureDiscovery(), factory);
        transport.SetEnabled(false);

        var disabled = await transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            "'disabled'",
            TimeSpan.FromSeconds(2));
        Assert.Empty(factory.Wires);

        transport.SetEnabled(true);
        var enabled = await transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            "'enabled'",
            TimeSpan.FromSeconds(2));

        Assert.False(disabled.Reachable);
        Assert.True(enabled.Reachable);
        Assert.Single(factory.Wires);
    }

    [Fact]
    public async Task DisablingClosesAnExistingChannelAndReenableReconnectsRetainedSubscriber()
    {
        var factory = new ResponsiveWireFactory();
        await using var transport = new PersistentSteamUiTransport(
            new FixtureDiscovery(), factory);
        await using var subscription = await transport.SubscribeAsync(
            SteamUiTargetRole.SharedJsContext);
        var first = await transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            "'first'",
            TimeSpan.FromSeconds(2));

        transport.SetEnabled(false);
        await factory.Wires[0].Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        transport.SetEnabled(true);
        var second = await transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            "'second'",
            TimeSpan.FromSeconds(2));

        Assert.True(first.Reachable);
        Assert.True(second.Reachable);
        Assert.Equal(2, factory.Wires.Count);
    }

    [Fact]
    public async Task SuccessfulEvaluationRestoresHealthAfterTransientJavascriptFailure()
    {
        var factory = new ResponsiveWireFactory { FailFirstEvaluation = true };
        await using var transport = new PersistentSteamUiTransport(
            new FixtureDiscovery(), factory);
        await using var subscription = await transport.SubscribeAsync(
            SteamUiTargetRole.SharedJsContext);

        var failed = await transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            "'first'",
            TimeSpan.FromSeconds(2));
        Assert.Equal(
            SteamUiTransportHealth.Incompatible,
            transport.GetSnapshots().Single(snapshot => snapshot.Role == SteamUiTargetRole.SharedJsContext).Health);

        var recovered = await transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            "'second'",
            TimeSpan.FromSeconds(2));

        Assert.True(failed.Reachable);
        Assert.NotNull(failed.Error);
        Assert.True(recovered.Reachable);
        Assert.Equal(
            SteamUiTransportHealth.Ready,
            transport.GetSnapshots().Single(snapshot => snapshot.Role == SteamUiTargetRole.SharedJsContext).Health);
    }

    [Fact]
    public async Task UnansweredEvaluationsRetireTheConnectionSoTheChannelReconnects()
    {
        // A renderer that stops servicing CDP keeps its websocket open, so nothing in the close
        // path ever fires. Before this, a timed-out evaluation left the channel Ready with the
        // same dead socket and every later request timed out too — for three minutes on
        // 2026-09-26, until Steam restarted its own helper.
        var factory = new ResponsiveWireFactory { SilenceEvaluations = true };
        await using var transport = new PersistentSteamUiTransport(
            new FixtureDiscovery(), factory);
        await using var subscription = await transport.SubscribeAsync(
            SteamUiTargetRole.SharedJsContext);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await transport.EvaluateAsync(
                SteamUiTargetRole.SharedJsContext,
                "'wedged'",
                TimeSpan.FromMilliseconds(200));
            Assert.False(result.Reachable);
        }

        var connected = await WaitUntilAsync(() =>
        {
            lock (factory.Wires)
            {
                return factory.Wires.Count > 1;
            }
        });

        Assert.True(connected, "the channel never rebuilt its connection after the unanswered run");
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    [Fact]
    public async Task ThrowingGenerationSubscriberDoesNotBlockOtherSubscribersOrChannel()
    {
        var factory = new ResponsiveWireFactory();
        await using var transport = new PersistentSteamUiTransport(
            new FixtureDiscovery(), factory);
        await using var subscription = await transport.SubscribeAsync(
            SteamUiTargetRole.MainWindow);
        Assert.True((await transport.EvaluateAsync(
            SteamUiTargetRole.MainWindow,
            "'ready'",
            TimeSpan.FromSeconds(2))).Reachable);
        var observed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.GenerationChanged += (_, _) => throw new InvalidOperationException("fixture");
        transport.GenerationChanged += (_, snapshot) =>
        {
            if (snapshot.Role == SteamUiTargetRole.MainWindow
                && snapshot.Generations.Document > 1)
            {
                observed.TrySetResult();
            }
        };

        factory.Wires.Single().Notify("DOM.documentUpdated", "{}");
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True((await transport.EvaluateAsync(
            SteamUiTargetRole.MainWindow,
            "'still-ready'",
            TimeSpan.FromSeconds(2))).Reachable);
    }

    [Fact]
    public async Task SlowGenerationSubscriberDoesNotBlockConnectionSetup()
    {
        var factory = new ResponsiveWireFactory();
        await using var transport = new PersistentSteamUiTransport(
            new FixtureDiscovery(), factory);
        var handlerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.GenerationChanged += (_, _) =>
        {
            handlerStarted.TrySetResult();
            releaseHandler.Task.GetAwaiter().GetResult();
        };

        var evaluation = transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            "'ready'",
            TimeSpan.FromSeconds(2));
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            Assert.True((await evaluation.WaitAsync(TimeSpan.FromSeconds(1))).Reachable);
        }
        finally
        {
            releaseHandler.TrySetResult();
        }
    }

    [Fact]
    public async Task ChannelStaysConnectedUntilItsLastSubscriberLeaves()
    {
        var factory = new ResponsiveWireFactory();
        await using var transport = new PersistentSteamUiTransport(
            new FixtureDiscovery(), factory);
        var first = await transport.SubscribeAsync(SteamUiTargetRole.MainWindow);
        var second = await transport.SubscribeAsync(SteamUiTargetRole.MainWindow);
        Assert.True((await transport.EvaluateAsync(
            SteamUiTargetRole.MainWindow,
            "'ready'",
            TimeSpan.FromSeconds(2))).Reachable);

        await first.DisposeAsync();
        Assert.True((await transport.EvaluateAsync(
            SteamUiTargetRole.MainWindow,
            "'still-ready'",
            TimeSpan.FromSeconds(2))).Reachable);
        Assert.Single(factory.Wires);

        await second.DisposeAsync();
        await factory.Wires[0].Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 4)]
    [InlineData(2, 16)]
    [InlineData(3, 30)]
    [InlineData(99, 30)]
    public void RetryBackoffProgressesAndCaps(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
            PersistentSteamUiTransport.RetryDelay(attempt));
    }

    [Fact]
    public async Task FailedAttachDoesNotPublishTheCandidateIntoTheSession()
    {
        var factory = new ResponsiveWireFactory();
        await using var active = new PersistentSteamUiTransport(
            new FixtureDiscovery(), factory);
        var disposed = new PersistentSteamUiTransport(
            new FixtureDiscovery(), new ResponsiveWireFactory());
        await disposed.DisposeAsync();
        SteamUiTransportSession.SetEnabled(true);
        try
        {
            Assert.Throws<ObjectDisposedException>(() => SteamUiTransportSession.Attach(disposed));
            SteamUiTransportSession.Attach(active);

            var result = await SteamUiTransportSession.EvaluateAsync(
                "'active'",
                TimeSpan.FromSeconds(2));

            Assert.True(result.Reachable);
        }
        finally
        {
            SteamUiTransportSession.Detach(active);
        }
    }

    [Fact]
    public async Task PublicTransportOperationsRejectInvalidDeadlinesBeforeConnecting()
    {
        var factory = new ResponsiveWireFactory();
        await using var transport = new PersistentSteamUiTransport(
            new FixtureDiscovery(), factory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            "'invalid'",
            TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => transport.SetRuntimeBindingAsync(
            SteamUiTargetRole.SharedJsContext,
            "fixture",
            true,
            TimeSpan.FromSeconds(31)));

        Assert.Empty(factory.Wires);
    }

    private sealed class FixtureDiscovery : ISteamUiEndpointDiscovery
    {
        public Task<SteamUiEndpoint?> DiscoverAsync(
            SteamUiTargetRole role,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<SteamUiEndpoint?>(new SteamUiEndpoint(
                "browser-1",
                "target-" + role,
                role,
                new Uri($"ws://127.0.0.1:8080/devtools/page/{role}"),
                "page",
                role.ToString(),
                "https://steamloopback.host/index.html"));
        }
    }

    private sealed class ResponsiveWireFactory : ISteamUiCdpWireFactory
    {
        private int _connectCount;

        internal List<ResponsiveWire> Wires { get; } = [];

        internal bool BlockFirstConnection { get; init; }

        internal bool BlockPageEnable { get; init; }

        internal bool FailFirstEvaluation { get; init; }

        internal bool SilenceEvaluations { get; init; }

        internal TaskCompletionSource FirstConnectStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseFirstConnect { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource PageEnableStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleasePageEnable { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ISteamUiCdpWire> ConnectAsync(
            SteamUiEndpoint endpoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connect = Interlocked.Increment(ref _connectCount);
            if (connect == 1 && BlockFirstConnection)
            {
                FirstConnectStarted.TrySetResult();
                // A socket connect is allowed to finish just after cancellation. The transport,
                // not a cooperative test double, must reject that previous ownership generation.
                await ReleaseFirstConnect.Task.ConfigureAwait(false);
            }

            var wire = new ResponsiveWire(
                BlockPageEnable,
                FailFirstEvaluation,
                SilenceEvaluations,
                PageEnableStarted,
                ReleasePageEnable);
            lock (Wires)
            {
                Wires.Add(wire);
            }

            return wire;
        }
    }

    /// <summary>Answers every CDP call the transport makes, optionally holding or failing one.</summary>
    private sealed class ResponsiveWire(
        bool blockPageEnable,
        bool failFirstEvaluation,
        bool silenceEvaluations,
        TaskCompletionSource pageEnableStarted,
        TaskCompletionSource releasePageEnable) : QueueWire
    {
        private int _evaluations;

        internal List<string> Methods { get; } = [];

        public override async Task SendAsync(
            ReadOnlyMemory<byte> message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = JsonDocument.Parse(message);
            var id = request.RootElement.GetProperty("id").GetInt32();
            var method = request.RootElement.GetProperty("method").GetString()!;
            Methods.Add(method);
            if (blockPageEnable && method == "Page.enable")
            {
                pageEnableStarted.TrySetResult();
                await releasePageEnable.Task.WaitAsync(cancellationToken);
            }

            // The wedged renderer: the socket stays open and the request is simply never answered.
            if (silenceEvaluations && method == "Runtime.evaluate")
            {
                return;
            }

            string result;
            if (method == "Runtime.evaluate"
                && failFirstEvaluation
                && Interlocked.Increment(ref _evaluations) == 1)
            {
                result = "{\"exceptionDetails\":{\"text\":\"fixture failure\"},"
                         + "\"result\":{\"type\":\"undefined\"}}";
            }
            else
            {
                result = method == "Runtime.evaluate"
                    ? "{\"result\":{\"type\":\"string\",\"value\":\"ok\"}}"
                    : "{}";
            }

            Enqueue($"{{\"id\":{id},\"result\":{result}}}");
        }
    }
}
