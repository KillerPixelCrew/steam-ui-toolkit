namespace SteamUiToolkit.Tests;

public sealed class SteamUiCdpConnectionTests
{
    private const string ConsoleNotification =
        "{\"method\":\"Runtime.consoleAPICalled\",\"params\":{}}";

    [Fact]
    public async Task EvaluationIgnoresOrphanAndCompletesMatchingRequest()
    {
        var wire = new QueueWire();
        wire.Sent = request =>
        {
            int id = QueueWire.RequestId(request);
            wire.Enqueue("{\"id\":999,\"result\":{}}");
            wire.Enqueue(StringResult(id, "ok"));
        };
        await using var connection = new SteamUiCdpConnection(
            wire, (_, _) => { }, (_, _) => { });
        connection.Start();

        var value = await connection.EvaluateAsync(
            "JSON.stringify({ok:true})", TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("ok", value);
    }

    [Fact]
    public async Task MalformedFramesAreDroppedWithoutClosingTheChannel()
    {
        var oversized = "{\"method\":\"Page.frameNavigated\",\"params\":{\"frame\":\""
            + new string('x', 1024 * 1024) + "\"}}";
        var wire = new QueueWire();
        wire.Sent = request =>
        {
            wire.Enqueue("[]");
            wire.Enqueue("not json");
            wire.Enqueue("{\"params\":{}}");
            wire.Enqueue(oversized);
            wire.Enqueue(StringResult(QueueWire.RequestId(request), "ok"));
        };
        var delivered = new TaskCompletionSource<(string Method, string Parameters)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var closes = 0;
        await using var connection = new SteamUiCdpConnection(
            wire,
            (method, parameters) => delivered.TrySetResult((method, parameters)),
            (_, _) => Interlocked.Increment(ref closes));
        connection.Start();

        var value = await connection.EvaluateAsync(
            "'x'", TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("ok", value);
        Assert.False(connection.Completion.IsCompleted);
        Assert.Equal(0, closes);
        // The oversized notification still counts as a navigation, without its parameters, and is
        // the only one that reaches the handler.
        Assert.Equal(
            ("Page.frameNavigated", "{}"),
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task CallerCancellationDoesNotPoisonPersistentChannel()
    {
        var wire = new QueueWire();
        var sends = 0;
        wire.Sent = request =>
        {
            sends++;
            if (sends == 1)
            {
                return;
            }
            wire.Enqueue(StringResult(QueueWire.RequestId(request), "second"));
        };
        await using var connection = new SteamUiCdpConnection(
            wire, (_, _) => { }, (_, _) => { });
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
        var wire = new QueueWire();
        var handlerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        wire.Sent = request =>
        {
            wire.Enqueue(ConsoleNotification);
            wire.Enqueue(StringResult(QueueWire.RequestId(request), "ok"));
        };
        await using var connection = new SteamUiCdpConnection(
            wire,
            (_, _) =>
            {
                handlerStarted.TrySetResult();
                releaseHandler.Task.GetAwaiter().GetResult();
            },
            (_, _) => { });
        connection.Start();

        // Generous budgets: the handler parks a pool thread, and a busy CI runner can take a
        // while to start the next one. The contract is only that the reply arrives while the
        // handler is still blocked.
        Task<string?> evaluation = connection.EvaluateAsync(
            "'ok'", TimeSpan.FromSeconds(15), CancellationToken.None);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            Assert.Equal("ok", await evaluation.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.False(releaseHandler.Task.IsCompleted);
        }
        finally
        {
            releaseHandler.TrySetResult();
        }
    }

    [Fact]
    public async Task NotificationHandlerFailureDoesNotPoisonChannel()
    {
        var wire = new QueueWire();
        wire.Sent = request =>
        {
            wire.Enqueue(ConsoleNotification);
            wire.Enqueue(StringResult(QueueWire.RequestId(request), "ok"));
        };
        await using var connection = new SteamUiCdpConnection(
            wire,
            (_, _) => throw new InvalidOperationException("fixture failure"),
            (_, _) => { });
        connection.Start();

        string? value = await connection.EvaluateAsync(
            "'ok'", TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("ok", value);
    }

    private static string StringResult(int id, string value) =>
        $"{{\"id\":{id},\"result\":{{\"result\":{{\"type\":\"string\",\"value\":\"{value}\"}}}}}}";
}
