using System.Text;
using System.Text.Json;

namespace SteamUiToolkit.Tests;

public sealed class SteamUiCdpConnectionTests
{
    private static readonly SteamUiEndpoint Endpoint = new(
        "browser-1",
        "target-1",
        SteamUiTargetRole.SharedJsContext,
        new Uri("ws://127.0.0.1:8080/devtools/page/target-1"),
        "page",
        "SharedJSContext",
        "https://steamloopback.host/index.html");

    [Fact]
    public async Task EvaluationIgnoresOrphanAndCompletesMatchingRequest()
    {
        var wire = new FakeWire();
        wire.Sent = request =>
        {
            using var document = JsonDocument.Parse(request);
            var id = document.RootElement.GetProperty("id").GetInt32();
            wire.Enqueue("{\"id\":999,\"result\":{}}"u8.ToArray());
            wire.Enqueue(Encoding.UTF8.GetBytes(
                $"{{\"id\":{id},\"result\":{{\"result\":{{\"type\":\"string\",\"value\":\"ok\"}}}}}}"));
        };
        await using var connection = new SteamUiCdpConnection(
            Endpoint, wire, (_, _) => { }, (_, _) => { });
        connection.Start();

        var value = await connection.EvaluateAsync(
            "JSON.stringify({ok:true})", TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("ok", value);
    }

    [Fact]
    public async Task MalformedFrameFaultsPendingRequestAndChannel()
    {
        var wire = new FakeWire();
        wire.Sent = _ => wire.Enqueue("[]"u8.ToArray());
        Exception? closed = null;
        await using var connection = new SteamUiCdpConnection(
            Endpoint, wire, (_, _) => { }, (_, error) => closed = error);
        connection.Start();

        await Assert.ThrowsAnyAsync<Exception>(() => connection.EvaluateAsync(
            "'x'", TimeSpan.FromSeconds(1), CancellationToken.None));
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsType<InvalidDataException>(closed);
    }

    [Fact]
    public async Task CallerCancellationDoesNotPoisonPersistentChannel()
    {
        var wire = new FakeWire();
        var sends = 0;
        wire.Sent = request =>
        {
            sends++;
            if (sends == 1)
            {
                return;
            }
            using var document = JsonDocument.Parse(request);
            var id = document.RootElement.GetProperty("id").GetInt32();
            wire.Enqueue(Encoding.UTF8.GetBytes(
                $"{{\"id\":{id},\"result\":{{\"result\":{{\"type\":\"string\",\"value\":\"second\"}}}}}}"));
        };
        await using var connection = new SteamUiCdpConnection(
            Endpoint, wire, (_, _) => { }, (_, _) => { });
        connection.Start();
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.EvaluateAsync(
            "'first'", TimeSpan.FromSeconds(1), cancellation.Token));
        var second = await connection.EvaluateAsync(
            "'second'", TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("second", second);
    }

    [Fact]
    public async Task SlowNotificationHandlerDoesNotBlockResponseReader()
    {
        var wire = new FakeWire();
        var handlerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        wire.Sent = request =>
        {
            using var document = JsonDocument.Parse(request);
            int id = document.RootElement.GetProperty("id").GetInt32();
            wire.Enqueue("{\"method\":\"Runtime.consoleAPICalled\",\"params\":{}}"u8.ToArray());
            wire.Enqueue(Encoding.UTF8.GetBytes(
                $"{{\"id\":{id},\"result\":{{\"result\":{{\"type\":\"string\",\"value\":\"ok\"}}}}}}"));
        };
        await using var connection = new SteamUiCdpConnection(
            Endpoint,
            wire,
            (_, _) =>
            {
                handlerStarted.TrySetResult();
                releaseHandler.Task.GetAwaiter().GetResult();
            },
            (_, _) => { });
        connection.Start();

        Task<string?> evaluation = connection.EvaluateAsync(
            "'ok'", TimeSpan.FromSeconds(1), CancellationToken.None);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            Assert.Equal("ok", await evaluation.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            releaseHandler.TrySetResult();
        }
    }

    [Fact]
    public async Task NotificationHandlerFailureDoesNotPoisonChannel()
    {
        var wire = new FakeWire();
        wire.Sent = request =>
        {
            using var document = JsonDocument.Parse(request);
            int id = document.RootElement.GetProperty("id").GetInt32();
            wire.Enqueue("{\"method\":\"Runtime.consoleAPICalled\",\"params\":{}}"u8.ToArray());
            wire.Enqueue(Encoding.UTF8.GetBytes(
                $"{{\"id\":{id},\"result\":{{\"result\":{{\"type\":\"string\",\"value\":\"ok\"}}}}}}"));
        };
        await using var connection = new SteamUiCdpConnection(
            Endpoint,
            wire,
            (_, _) => throw new InvalidOperationException("fixture failure"),
            (_, _) => { });
        connection.Start();

        string? value = await connection.EvaluateAsync(
            "'ok'", TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("ok", value);
    }

    private sealed class FakeWire : ISteamUiCdpWire
    {
        private readonly Queue<byte[]> _messages = new();
        private readonly SemaphoreSlim _available = new(0);

        internal Action<byte[]>? Sent { get; set; }

        internal void Enqueue(byte[] message)
        {
            lock (_messages)
            {
                _messages.Enqueue(message);
            }
            _available.Release();
        }

        public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent?.Invoke(message.ToArray());
            return Task.CompletedTask;
        }

        public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
        {
            await _available.WaitAsync(cancellationToken);
            lock (_messages)
            {
                return _messages.Dequeue();
            }
        }

        public ValueTask DisposeAsync()
        {
            _available.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

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

        SteamUiEvaluationResult result = await transport.EvaluateAsync(
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

        Task<SteamUiEvaluationResult> evaluation = transport.EvaluateAsync(
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
        await using IAsyncDisposable subscription = await transport.SubscribeAsync(
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
        SteamUiTransportSnapshot snapshot = await changed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(snapshot.Generations.Document > 1);
    }

    [Fact]
    public async Task OneShotEvaluationReconnectsAfterItsPreviousLeaseWasReleased()
    {
        var factory = new ResponsiveWireFactory();
        await using var transport = new PersistentSteamUiTransport(
            new FixtureDiscovery(), factory);

        SteamUiEvaluationResult first = await transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            "'first'",
            TimeSpan.FromSeconds(2));
        SteamUiEvaluationResult second = await transport.EvaluateAsync(
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
        IAsyncDisposable first = await transport.SubscribeAsync(
            SteamUiTargetRole.SharedJsContext);
        await factory.FirstConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await first.DisposeAsync();
        await using IAsyncDisposable replacement = await transport.SubscribeAsync(
            SteamUiTargetRole.SharedJsContext);
        factory.ReleaseFirstConnect.TrySetResult();

        SteamUiEvaluationResult result = await transport.EvaluateAsync(
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

        SteamUiEvaluationResult disabled = await transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            "'disabled'",
            TimeSpan.FromSeconds(2));
        Assert.Empty(factory.Wires);

        transport.SetEnabled(true);
        SteamUiEvaluationResult enabled = await transport.EvaluateAsync(
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
        await using IAsyncDisposable subscription = await transport.SubscribeAsync(
            SteamUiTargetRole.SharedJsContext);
        SteamUiEvaluationResult first = await transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            "'first'",
            TimeSpan.FromSeconds(2));

        transport.SetEnabled(false);
        await factory.Wires[0].Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        transport.SetEnabled(true);
        SteamUiEvaluationResult second = await transport.EvaluateAsync(
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
        await using IAsyncDisposable subscription = await transport.SubscribeAsync(
            SteamUiTargetRole.SharedJsContext);

        SteamUiEvaluationResult failed = await transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            "'first'",
            TimeSpan.FromSeconds(2));
        Assert.Equal(
            SteamUiTransportHealth.Incompatible,
            transport.GetSnapshots().Single(
                snapshot => snapshot.Role == SteamUiTargetRole.SharedJsContext).Health);

        SteamUiEvaluationResult recovered = await transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            "'second'",
            TimeSpan.FromSeconds(2));

        Assert.True(failed.Reachable);
        Assert.NotNull(failed.Error);
        Assert.True(recovered.Reachable);
        Assert.Equal(
            SteamUiTransportHealth.Ready,
            transport.GetSnapshots().Single(
                snapshot => snapshot.Role == SteamUiTargetRole.SharedJsContext).Health);
    }

    [Fact]
    public async Task ThrowingGenerationSubscriberDoesNotBlockOtherSubscribersOrChannel()
    {
        var factory = new ResponsiveWireFactory();
        await using var transport = new PersistentSteamUiTransport(
            new FixtureDiscovery(), factory);
        await using IAsyncDisposable subscription = await transport.SubscribeAsync(
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

        Task<SteamUiEvaluationResult> evaluation = transport.EvaluateAsync(
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
        IAsyncDisposable first = await transport.SubscribeAsync(SteamUiTargetRole.MainWindow);
        IAsyncDisposable second = await transport.SubscribeAsync(SteamUiTargetRole.MainWindow);
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

            CefEvalResult result = await SteamUiTransportSession.EvaluateAsync(
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
            int connect = Interlocked.Increment(ref _connectCount);
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
                PageEnableStarted,
                ReleasePageEnable);
            lock (Wires)
            {
                Wires.Add(wire);
            }
            return wire;
        }
    }

    private sealed class ResponsiveWire(
        bool blockPageEnable,
        bool failFirstEvaluation,
        TaskCompletionSource pageEnableStarted,
        TaskCompletionSource releasePageEnable) : ISteamUiCdpWire
    {
        private readonly Queue<byte[]> _messages = new();
        private readonly SemaphoreSlim _available = new(0);
        private int _evaluations;

        internal List<string> Methods { get; } = [];

        internal TaskCompletionSource Disposed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SendAsync(
            ReadOnlyMemory<byte> message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using JsonDocument request = JsonDocument.Parse(message);
            int id = request.RootElement.GetProperty("id").GetInt32();
            string method = request.RootElement.GetProperty("method").GetString()!;
            Methods.Add(method);
            if (blockPageEnable && method == "Page.enable")
            {
                pageEnableStarted.TrySetResult();
                await releasePageEnable.Task.WaitAsync(cancellationToken);
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
            Enqueue(Encoding.UTF8.GetBytes($"{{\"id\":{id},\"result\":{result}}}"));
        }

        public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
        {
            await _available.WaitAsync(cancellationToken);
            lock (_messages)
            {
                return _messages.Dequeue();
            }
        }

        internal void Notify(string method, string parameters) =>
            Enqueue(Encoding.UTF8.GetBytes(
                $"{{\"method\":{JsonSerializer.Serialize(method)},\"params\":{parameters}}}"));

        private void Enqueue(byte[] message)
        {
            lock (_messages)
            {
                _messages.Enqueue(message);
            }
            _available.Release();
        }

        public ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
