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
    public async Task MalformedFrameFaultsPendingRequestAndChannel()
    {
        var wire = new QueueWire();
        wire.Sent = _ => wire.Enqueue("[]");
        Exception? closed = null;
        await using var connection = new SteamUiCdpConnection(
            wire, (_, _) => { }, (_, error) => closed = error);
        connection.Start();

        await Assert.ThrowsAnyAsync<Exception>(() => connection.EvaluateAsync(
            "'x'", TimeSpan.FromSeconds(1), CancellationToken.None));
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsType<InvalidDataException>(closed);
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
