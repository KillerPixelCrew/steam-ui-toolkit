using System.Collections.Concurrent;
using System.Text.Json;

namespace SteamUiToolkit.Tests;

[CollectionDefinition("Steam UI diagnostics", DisableParallelization = true)]
public sealed class SteamUiDiagnosticsCollection
{
}

[Collection("Steam UI diagnostics")]
public sealed class SteamUiModuleRuntimeTests
{
    private static readonly SteamUiInjectedAsset Asset = new("(()=>__STEAM_UI_CONFIGURATION_JSON__)()", "TEST");

    [Theory]
    [InlineData("disabled", "The requested semantic service is not active.", 0)]
    [InlineData("missing", "No handler is registered for this command.", 0)]
    [InlineData("default", "no reason reported", 1)]
    [InlineData("throws", "fixture failure", 1)]
    public async Task RefusalsKeepTheirReasonAndPayloadShapeWithoutLoggingTheSecret(
        string mode, string expected, int calls)
    {
        var log = new RecordingLog();
        SteamUiLog.Use(log);
        try
        {
            await using var transport = new FakeSteamUiTransport();
            var started = 0;
            var module = new SteamUiModule("example", commands:
            [
                new SteamUiCommandHandler("fixture.commands", "work", (_, _) =>
                {
                    Interlocked.Increment(ref started);
                    if (mode == "throws")
                    {
                        throw new InvalidOperationException("fixture failure");
                    }

                    return Task.FromResult(default(SteamUiCommandResult));
                })
            ]);
            var modules = new SteamUiModuleSet([module]);
            var allowed = new Dictionary<string, IReadOnlyList<string>> { ["fixture.commands"] = ["work", "missing"] };
            await using var bridge = new SteamUiBridgeHost(transport, Asset, allowed);
            await using var patches = new SteamUiPatchManager(transport);
            await using var runtime = new SteamUiModuleRuntime(bridge, modules, patches,
                () => mode != "disabled", () => false);
            Assert.True(await bridge.BootstrapAsync());
            transport.EmitBindingPayload(Request(transport.Generations, 1, mode == "missing" ? "missing" : "work"));
            await TestJson.WaitUntilAsync(() => Deliveries(transport).Length == 1);

            Assert.Equal(calls, Volatile.Read(ref started));
            Assert.Contains(expected, Deliveries(transport)[0], StringComparison.OrdinalIgnoreCase);
            var line = Assert.Single(log.Lines, item => item.Message.Contains("did nothing", StringComparison.Ordinal));
            Assert.Contains("secret", line.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("private-password", line.Message, StringComparison.Ordinal);
            Assert.True(line.Warning);

            if (mode is "throws" or "default")
            {
                transport.EmitBindingPayload(Request(transport.Generations, 2, "work"));
                await TestJson.WaitUntilAsync(() => Deliveries(transport).Length == 2);
                Assert.Equal(mode == "throws" ? 1 : 2, Volatile.Read(ref started));
                Assert.Contains(mode == "throws" ? "This surface was turned off after an error." : expected,
                    Deliveries(transport)[1], StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            SteamUiLog.Use(null);
        }
    }

    [Fact]
    public async Task HandlerStartsStayInArrivalOrderAndThePumpCanCancelAnAwaitingRequest()
    {
        await using var transport = new FakeSteamUiTransport();
        var starts = new ConcurrentQueue<long>();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new SteamUiModule("example", commands:
        [
            new SteamUiCommandHandler("fixture.commands", "work", async (request, token) =>
            {
                starts.Enqueue(request.Sequence);
                if (request.Sequence == 1)
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled.TrySetResult();
                        throw;
                    }
                }

                return SteamUiCommandResult.Applied;
            })
        ]);
        var modules = new SteamUiModuleSet([module]);
        await using var bridge = new SteamUiBridgeHost(transport, Asset, modules.AllowedCommands);
        await using var patches = new SteamUiPatchManager(transport);
        await using var runtime = new SteamUiModuleRuntime(bridge, modules, patches, () => true, () => false);
        Assert.True(await bridge.BootstrapAsync());
        var first = Request(transport.Generations, 1, "work");
        transport.EmitBindingPayload(first);
        transport.EmitBindingPayload(first);
        transport.EmitBindingPayload(Request(transport.Generations, 2, "work"));
        await TestJson.WaitUntilAsync(() => Deliveries(transport).Length == 1);
        transport.EmitBindingPayload(Request(transport.Generations, 1, "work", type: "cancel"));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await runtime.ShutdownAsync(CancellationToken.None);

        Assert.Equal([1L, 2L], starts.ToArray());
        Assert.Single(Deliveries(transport));
    }

    [Fact]
    public async Task PatchDiagnosticsKeepFullSnapshotDetailAndBoundOnlyTheWarning()
    {
        var log = new RecordingLog();
        SteamUiLog.Use(log);
        try
        {
            await using var transport = new FakeSteamUiTransport();
            await using var manager = new SteamUiPatchManager(transport);
            var detail = new string('x', 5000);
            var patch = new SteamGatePatch("example.gate", "example", "v1", "probe", _ => true,
                "status.installed", "!status.installed", "Example");
            transport.OnEvaluate = _ => Task.FromResult(new SteamUiEvaluationResult(
                SteamUiDispatch.Answered, null, detail, transport.Generations));
            manager.Register(patch);
            await manager.SynchronizeAsync();

            var snapshot = Assert.Single(manager.GetSnapshots());
            Assert.Equal(SteamUiPatchState.Incompatible, snapshot.State);
            Assert.Equal(detail, snapshot.LastFailure);
            var warning = Assert.Single(log.Lines, item => item.Message.Contains("Incompatible", StringComparison.Ordinal));
            Assert.True(warning.Warning);
            Assert.Contains('…', warning.Message);
            Assert.DoesNotContain(detail, warning.Message, StringComparison.Ordinal);
        }
        finally
        {
            SteamUiLog.Use(null);
        }
    }

    [Fact]
    public async Task ARejectedResponseIsLoggedWithTheCommandAndIsNotRetried()
    {
        var log = new RecordingLog();
        SteamUiLog.Use(log);
        try
        {
            await using var transport = new FakeSteamUiTransport();
            var module = new SteamUiModule("example", commands:
            [
                new SteamUiCommandHandler("fixture.commands", "work", (_, _) => Task.FromResult(SteamUiCommandResult.Applied))
            ]);
            var modules = new SteamUiModuleSet([module]);
            await using var bridge = new SteamUiBridgeHost(transport, Asset, modules.AllowedCommands);
            await using var patches = new SteamUiPatchManager(transport);
            await using var runtime = new SteamUiModuleRuntime(bridge, modules, patches, () => true, () => false);
            Assert.True(await bridge.BootstrapAsync());
            transport.EvaluationValue = "{\"ok\":false}";
            transport.EmitBindingPayload(Request(transport.Generations, 1, "work"));
            await TestJson.WaitUntilAsync(() => log.Lines.Any(line => line.Message.Contains("was not accepted", StringComparison.Ordinal)));
            await runtime.ShutdownAsync(CancellationToken.None);

            Assert.Single(Deliveries(transport));
            Assert.Contains(log.Lines, line => line.Warning && line.Message.Contains("fixture.commands/work", StringComparison.Ordinal));
        }
        finally
        {
            SteamUiLog.Use(null);
        }
    }

    [Fact]
    public async Task BridgeBootstrapFailureKeepsThePagesUsefulError()
    {
        await using var transport = new FakeSteamUiTransport();
        var detail = new string('x', 5000) + " bootstrap cause";
        transport.OnEvaluate = _ => Task.FromResult(new SteamUiEvaluationResult(
            SteamUiDispatch.Answered, null, detail, transport.Generations));
        await using var bridge = new SteamUiBridgeHost(transport, Asset,
            new Dictionary<string, IReadOnlyList<string>>());
        var patch = new SteamUiBridgePatch(bridge);
        var result = await patch.ApplyAsync(new SteamUiPatchContext(transport, TimeSpan.FromSeconds(1)), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(detail, result.Diagnostic, StringComparison.Ordinal);
    }

    private static string Request(SteamUiGenerations generations, long sequence, string command, string type = "request")
    {
        return JsonSerializer.Serialize(new
        {
            version = SteamUiBridgeHost.SchemaVersion,
            type,
            patchId = "fixture.commands",
            command,
            sequence,
            actionGeneration = sequence,
            contextGeneration = generations.ExecutionContext,
            documentGeneration = generations.Document,
            payload = new { secret = "private-password", revision = 7 }
        });
    }

    private static string[] Deliveries(FakeSteamUiTransport transport)
    {
        return transport.Expressions.Where(expression => expression.Contains("b.deliver(", StringComparison.Ordinal)).ToArray();
    }

    private sealed class RecordingLog : ISteamUiLog
    {
        internal ConcurrentQueue<(string Message, bool Warning)> Lines { get; } = new();

        public void Info(string message) => Lines.Enqueue((message, false));

        public void Warn(string message) => Lines.Enqueue((message, true));

        public void Change(string key, string message, bool warning = false) => Lines.Enqueue((message, warning));
    }
}
