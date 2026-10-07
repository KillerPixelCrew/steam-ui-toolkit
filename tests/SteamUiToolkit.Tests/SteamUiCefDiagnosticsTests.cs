using System.Collections.Concurrent;
using System.Text.Json;

namespace SteamUiToolkit.Tests;

[CollectionDefinition("CEF diagnostic log", DisableParallelization = true)]
public sealed class CefDiagnosticLogCollection;

[Collection("CEF diagnostic log")]
public sealed class SteamUiCefDiagnosticsTests
{
    [Fact]
    public async Task PlainRejectedObjectLogsItsDataWithoutInvokingAccessors()
    {
        var log = new RecordingLog();
        SteamUiLog.Use(log);
        try
        {
            var methods = new ConcurrentQueue<string>();
            var wire = Wire(methods);
            await using var connection = new SteamUiCdpConnection(wire, (_, _) => { }, (_, _) => { },
                SteamUiTargetRole.SharedJsContext);
            connection.Start();
            wire.Notify("Runtime.exceptionThrown", """
                                                   {"exceptionDetails":{"text":"Uncaught (in promise)","url":"https://steamloopback.host/routes/library/home?session=private",
                                                    "exception":{"type":"object","description":"Object","objectId":"reason"},
                                                    "stackTrace":{"callFrames":[{"functionName":"loadGame","url":"https://steamloopback.host/library.js?token=private","lineNumber":6,"columnNumber":8}]}}}
                                                   """);
            var message = await log.Message.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(SteamUiConsoleLevel.Error, message.Level);
            Assert.Contains("result: 2", message.Text);
            Assert.Contains("CVRPathHelpers not found", message.Text);
            Assert.Contains("at loadGame (https://steamloopback.host/library.js:7:9)", message.Text);
            Assert.Contains("[redacted]", message.Text);
            Assert.DoesNotContain("private", message.Text);
            Assert.DoesNotContain("secret", message.Text);
            Assert.Contains("Runtime.getProperties", methods);
            Assert.DoesNotContain("Runtime.evaluate", methods);
            Assert.DoesNotContain("Runtime.callFunctionOn", methods);
        }
        finally
        {
            SteamUiLog.Use(null);
        }
    }

    [Fact]
    public async Task ConsoleWarningsKeepStacksAndInformationalMessagesRequireVerbose()
    {
        var log = new RecordingLog();
        SteamUiLog.Use(log);
        try
        {
            var wire = Wire(new ConcurrentQueue<string>());
            await using var connection = new SteamUiCdpConnection(wire, (_, _) => { }, (_, _) => { },
                SteamUiTargetRole.MainWindow);
            connection.Start();
            wire.Notify("Runtime.consoleAPICalled", """
                                                    {"type":"log","args":[{"type":"string","value":"development only"}]}
                                                    """);
            wire.Notify("Runtime.consoleAPICalled", """
                                                    {"type":"warning","args":[{"type":"string","value":"layout warning"}],
                                                     "stackTrace":{"callFrames":[{"functionName":"render","url":"https://steamloopback.host/library.js","lineNumber":10,"columnNumber":2}]}}
                                                    """);
            var message = await log.Message.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(SteamUiConsoleLevel.Warning, message.Level);
            Assert.Contains("layout warning", message.Text);
            Assert.Contains("library.js:11:3", message.Text);
            Assert.DoesNotContain(log.Messages,
                entry => entry.Text.Contains("development only", StringComparison.Ordinal));
            log.Verbose = true;
            var next = new TaskCompletionSource<(string Text, SteamUiConsoleLevel Level)>(TaskCreationOptions
                .RunContinuationsAsynchronously);
            log.Message = next;
            wire.Notify("Log.entryAdded", """
                                          {"entry":{"level":"info","text":"browser development message","url":"https://steamloopback.host/library.js"}}
                                          """);
            Assert.Equal(SteamUiConsoleLevel.Debug, (await next.Task.WaitAsync(TimeSpan.FromSeconds(2))).Level);
        }
        finally
        {
            SteamUiLog.Use(null);
        }
    }

    [Fact]
    public async Task ConsoleFloodDoesNotBlockBindingsOrEvaluationReplies()
    {
        var log = new RecordingLog();
        SteamUiLog.Use(log);
        try
        {
            var wire = Wire(new ConcurrentQueue<string>());
            var binding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var connection = new SteamUiCdpConnection(wire, (method, _) =>
            {
                if (method == "Runtime.bindingCalled")
                {
                    binding.TrySetResult();
                }
            }, (_, _) => { }, SteamUiTargetRole.SharedJsContext);
            connection.Start();
            for (var index = 0; index < 1000; index++)
            {
                wire.Notify("Runtime.consoleAPICalled", """{"type":"log","args":[]} """);
            }

            wire.Notify("Runtime.bindingCalled", "{}");
            var response = await connection.EvaluateAsync("'ready'", TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.Equal("ready", response.Value);
            await binding.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(connection.Completion.IsCompleted);
        }
        finally
        {
            SteamUiLog.Use(null);
        }
    }

    private static QueueWire Wire(ConcurrentQueue<string> methods)
    {
        var wire = new QueueWire();
        wire.Sent = bytes =>
        {
            using var request = JsonDocument.Parse(bytes);
            var method = request.RootElement.GetProperty("method").GetString()!;
            methods.Enqueue(method);
            var result = method switch
            {
                "Runtime.getProperties" => """
                                           {"result":[{"name":"result","value":{"type":"number","value":2}},
                                           {"name":"message","value":{"type":"string","value":"CVRPathHelpers not found"}},
                                           {"name":"authorization","value":{"type":"string","value":"secret"}},
                                           {"name":"getter","get":{"type":"function","objectId":"getter"}}]}
                                           """,
                "Runtime.evaluate" => """{"result":{"type":"string","value":"ready"}}""",
                _ => "{}"
            };
            wire.Enqueue($"{{\"id\":{QueueWire.RequestId(bytes)},\"result\":{result}}}");
        };
        return wire;
    }

    private sealed class RecordingLog : ISteamUiLog
    {
        internal TaskCompletionSource<(string Text, SteamUiConsoleLevel Level)> Message =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Verbose;
        internal ConcurrentQueue<(string Text, SteamUiConsoleLevel Level)> Messages { get; } = new();
        public bool ConsoleVerboseEnabled => Verbose;

        public void Console(string key, string message, SteamUiConsoleLevel level)
        {
            Messages.Enqueue((message, level));
            Message.TrySetResult((message, level));
        }

        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Change(string key, string message, bool warning = false)
        {
        }
    }
}
