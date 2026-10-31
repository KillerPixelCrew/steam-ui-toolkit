using System.Text.Json;

namespace SteamUiToolkit.Tests;

public sealed class SteamUiCdpConnectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JavaScriptAndProtocolErrorsKeepTheFullDetail(bool protocol)
    {
        var detail = new string('x', 5000) + " the useful tail";
        var wire = new QueueWire();
        wire.Sent = request =>
        {
            var id = QueueWire.RequestId(request);
            wire.Enqueue(protocol
                ? JsonSerializer.Serialize(new { id, error = new { message = detail } })
                : JsonSerializer.Serialize(new { id, result = new { exceptionDetails = new { text = detail } } }));
        };
        await using var connection = new SteamUiCdpConnection(wire, (_, _) => { }, (_, _) => { });
        connection.Start();
        if (protocol)
        {
            var error = await Assert.ThrowsAsync<SteamUiUnansweredException>(() =>
                connection.EvaluateAsync("throw Error()", TimeSpan.FromSeconds(1), CancellationToken.None));
            var protocolError = Assert.IsType<InvalidDataException>(error.InnerException);
            Assert.Contains(detail, protocolError.Message, StringComparison.Ordinal);
        }
        else
        {
            var (_, error) = await connection.EvaluateAsync("throw Error()", TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.Contains(detail, error, StringComparison.Ordinal);
        }
    }

    private const string ConsoleNotification =
        "{\"method\":\"Runtime.consoleAPICalled\",\"params\":{}}";

    [Fact]
    public async Task SynchronousResultsDoNotEnterTheInspectorPromiseQueue()
    {
        var wire = new QueueWire();
        var methods = new List<string>();
        string? group = null;
        wire.Sent = request =>
        {
            using var document = JsonDocument.Parse(request);
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString()!;
            methods.Add(method);
            var id = QueueWire.RequestId(request);
            if (method == "Runtime.evaluate")
            {
                var parameters = root.GetProperty("params");
                Assert.False(parameters.GetProperty("awaitPromise").GetBoolean());
                group = parameters.GetProperty("objectGroup").GetString();
                wire.Enqueue(StringResult(id, "ready"));
            }
            else
            {
                Assert.Equal("Runtime.releaseObjectGroup", method);
                Assert.Equal(group, root.GetProperty("params").GetProperty("objectGroup").GetString());
                wire.Enqueue($"{{\"id\":{id},\"result\":{{}}}}");
            }
        };
        await using var connection = new SteamUiCdpConnection(wire, (_, _) => { }, (_, _) => { });
        connection.Start();

        Assert.Equal("ready", (await connection.EvaluateAsync("'ready'", TimeSpan.FromSeconds(1),
            CancellationToken.None)).Value);
        // A primitive result leaves nothing in the group, so there is nothing to release.
        Assert.Equal(["Runtime.evaluate"], methods);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedPromiseCompletionDoesNotReExecuteItsProducer(bool rejected)
    {
        var wire = new QueueWire();
        var evaluations = 0;
        var inspections = 0;
        var released = false;
        wire.Sent = request =>
        {
            using var document = JsonDocument.Parse(request);
            var method = document.RootElement.GetProperty("method").GetString();
            var id = QueueWire.RequestId(request);
            if (method == "Runtime.evaluate")
            {
                evaluations++;
                wire.Enqueue($"{{\"id\":{id},\"result\":{{\"result\":{{\"type\":\"object\",\"subtype\":\"promise\",\"objectId\":\"owned-promise\"}}}}}}");
            }
            else if (method == "Runtime.getProperties")
            {
                Assert.Equal("owned-promise", document.RootElement.GetProperty("params").GetProperty("objectId").GetString());
                var state = ++inspections == 1 ? "pending" : rejected ? "rejected" : "fulfilled";
                wire.Enqueue($"{{\"id\":{id},\"result\":{{\"internalProperties\":[{{\"name\":\"[[PromiseState]]\",\"value\":{{\"type\":\"string\",\"value\":\"{state}\"}}}},{{\"name\":\"[[PromiseResult]]\",\"value\":{{\"type\":\"string\",\"value\":\"completion detail\"}}}}]}}}}");
            }
            else
            {
                Assert.Equal("Runtime.releaseObjectGroup", method);
                released = true;
                wire.Enqueue($"{{\"id\":{id},\"result\":{{}}}}");
            }
        };
        await using var connection = new SteamUiCdpConnection(wire, (_, _) => { }, (_, _) => { });
        connection.Start();

        var (value, error) = await connection.EvaluateAsync("producer()", TimeSpan.FromSeconds(1),
            CancellationToken.None);
        if (rejected)
        {
            Assert.Null(value);
            Assert.Contains("completion detail", error, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("completion detail", value);
            Assert.Null(error);
        }

        Assert.Equal(1, evaluations);
        Assert.Equal(2, inspections);
        Assert.True(released);
    }

    [Fact]
    public async Task PendingPromiseTimeoutReleasesHandlesAndRemainsUnanswered()
    {
        var wire = new QueueWire();
        var evaluations = 0;
        var released = false;
        wire.Sent = request =>
        {
            using var document = JsonDocument.Parse(request);
            var method = document.RootElement.GetProperty("method").GetString();
            var id = QueueWire.RequestId(request);
            if (method == "Runtime.evaluate")
            {
                evaluations++;
                wire.Enqueue($"{{\"id\":{id},\"result\":{{\"result\":{{\"type\":\"object\",\"subtype\":\"promise\",\"objectId\":\"pending-promise\"}}}}}}");
            }
            else if (method == "Runtime.getProperties")
            {
                wire.Enqueue($"{{\"id\":{id},\"result\":{{\"internalProperties\":[{{\"name\":\"[[PromiseState]]\",\"value\":{{\"type\":\"string\",\"value\":\"pending\"}}}}]}}}}");
            }
            else
            {
                Assert.Equal("Runtime.releaseObjectGroup", method);
                released = true;
                wire.Enqueue($"{{\"id\":{id},\"result\":{{}}}}");
            }
        };
        await using var connection = new SteamUiCdpConnection(wire, (_, _) => { }, (_, _) => { });
        connection.Start();

        var failure = await Assert.ThrowsAsync<SteamUiUnansweredException>(() =>
            connection.EvaluateAsync("producer()", TimeSpan.FromMilliseconds(125), CancellationToken.None));
        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
        Assert.Equal(1, evaluations);
        Assert.True(released);
    }

    [Fact]
    public async Task EvaluationIgnoresOrphanAndCompletesMatchingRequest()
    {
        var wire = new QueueWire();
        wire.Sent = request =>
        {
            var id = QueueWire.RequestId(request);
            wire.Enqueue("{\"id\":999,\"result\":{}}");
            wire.Enqueue(StringResult(id, "ok"));
        };
        await using var connection = new SteamUiCdpConnection(
            wire, (_, _) => { }, (_, _) => { });
        connection.Start();

        var (value, _) = await connection.EvaluateAsync(
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

        var (value, _) = await connection.EvaluateAsync(
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

        // The first request was sent before the caller stopped waiting, so it may have run: that is
        // reported as unanswered, carrying the cancellation.
        var unanswered = await Assert.ThrowsAsync<SteamUiUnansweredException>(() => connection.EvaluateAsync(
            "'first'", TimeSpan.FromSeconds(1), cancellation.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(unanswered.InnerException);
        var (second, _) = await connection.EvaluateAsync(
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
        var evaluation = connection.EvaluateAsync(
            "'ok'", TimeSpan.FromSeconds(15), CancellationToken.None);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            Assert.Equal("ok", (await evaluation.WaitAsync(TimeSpan.FromSeconds(15))).Value);
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

        var (value, _) = await connection.EvaluateAsync(
            "'ok'", TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("ok", value);
    }

    private static string StringResult(int id, string value)
    {
        return $"{{\"id\":{id},\"result\":{{\"result\":{{\"type\":\"string\",\"value\":\"{value}\"}}}}}}";
    }
}
