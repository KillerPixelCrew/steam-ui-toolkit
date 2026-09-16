using System.Text.Json;

namespace SteamUiToolkit.Tests;

public sealed class SteamUiBridgeHostTests
{
    // The bridge substitutes the configuration into whatever it is given and evaluates it; these
    // tests exercise the envelope handling around that, not the script, so the smallest asset that
    // still carries the placeholder is the honest fixture.
    private static readonly SteamUiInjectedAsset TestAsset =
        new("(()=>{return __STEAM_UI_CONFIGURATION_JSON__;})()", "TESTASSETHASH");

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> TestVocabulary =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["example.performance"] = ["setLimit"]
        };

    [Fact]
    public async Task CurrentRequestIsDeliveredOnceAndReplayIsRejected()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var host = new SteamUiBridgeHost(transport, TestAsset, TestVocabulary);
        var received = new List<SteamUiBridgeRequest>();
        host.RequestReceived += (_, request) => received.Add(request);
        Assert.True(await host.BootstrapAsync());

        var request = RequestJson(transport.Generations, 1, 1);
        transport.EmitBindingPayload(request);
        transport.EmitBindingPayload(request);
        transport.EmitBindingPayload(RequestJson(
            transport.Generations,
            2,
            2));

        await TestJson.WaitUntilAsync(() => received.Count == 2);
        Assert.Equal([1L, 2L], received.Select(item => item.Sequence));
    }

    [Fact]
    public async Task MalformedAndNonBindingNotificationsNeverReachTheRouter()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var host = new SteamUiBridgeHost(transport, TestAsset, TestVocabulary);
        var received = 0;
        host.RequestReceived += (_, _) => received++;
        Assert.True(await host.BootstrapAsync());

        transport.EmitRawParameters("[");
        transport.EmitBindingPayload("{");
        transport.EmitRawParameters("{\"name\":\"somebody-elses-binding\",\"payload\":\"{}\"}");
        transport.EmitRawParameters("{\"name\":\"__steamUiBridge_v1_7b24d11c\"}");
        transport.EmitBindingPayload(new string('x', SteamUiBridgeHost.MaximumPayloadCharacters + 1));

        Assert.Equal(0, received);
    }

    [Fact]
    public async Task GenerationReplacementSuppressesTrafficUntilTheBridgeIsBootstrappedAgain()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var host = new SteamUiBridgeHost(transport, TestAsset, TestVocabulary);
        var received = 0;
        host.RequestReceived += (_, _) => received++;
        Assert.True(await host.BootstrapAsync());

        var previous = transport.Generations;
        transport.AdvanceDocumentGeneration();
        var evaluationsAfterReplacement = transport.Expressions.Count;

        Assert.False(host.IsReady);
        Assert.False(await host.PublishStateAsync(
            "example.performance",
            TestJson.Parse("{\"watts\":15}")));
        Assert.False(await host.RespondAsync(
            Request(previous, 1, 1),
            true,
            null,
            null));
        transport.EmitBindingPayload(RequestJson(
                previous,
                1,
                1),
            previous);
        transport.EmitBindingPayload(RequestJson(
            transport.Generations,
            1,
            1));

        Assert.Equal(evaluationsAfterReplacement, transport.Expressions.Count);
        Assert.Equal(0, received);

        Assert.True(await host.BootstrapAsync());
        transport.EmitBindingPayload(RequestJson(
            transport.Generations,
            1,
            1));

        await TestJson.WaitUntilAsync(() => received == 1);
        Assert.Equal(1, received);
    }

    [Fact]
    public async Task StateAndResponsesRequireAReadyBridgeAndAnAllowlistedStateIdentity()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var host = new SteamUiBridgeHost(transport, TestAsset, TestVocabulary);
        var request = Request(
            transport.Generations,
            1,
            1);

        Assert.False(await host.PublishStateAsync("example.performance", TestJson.Parse("{}")));
        Assert.False(await host.RespondAsync(request, true, null, null));
        Assert.Empty(transport.Expressions);

        Assert.True(await host.BootstrapAsync());
        var afterBootstrap = transport.Expressions.Count;
        Assert.False(await host.PublishStateAsync("not.allowlisted", TestJson.Parse("{}")));
        Assert.False(await host.PublishStateAsync(
            "example.performance",
            TestJson.Parse("{\"value\":\"" + new string('x', SteamUiBridgeHost.MaximumPayloadCharacters)
                                           + "\"}")));
        Assert.Equal(afterBootstrap, transport.Expressions.Count);

        Assert.True(await host.PublishStateAsync(
            "example.performance",
            TestJson.Parse("{\"watts\":15}")));
        Assert.True(await host.RespondAsync(request, false, null, "refused"));
        Assert.Equal(afterBootstrap + 2, transport.Expressions.Count);
        Assert.All(
            transport.Expressions.Skip(afterBootstrap),
            expression => Assert.Contains("deliver", expression, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeliveryAcknowledgementMustBeStructuredJson()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var host = new SteamUiBridgeHost(transport, TestAsset, TestVocabulary);
        Assert.True(await host.BootstrapAsync());
        transport.EvaluationValue = "{\"message\":\"\\\"ok\\\":true\"}";

        Assert.False(await host.PublishStateAsync("example.performance", TestJson.Parse("{}")));

        transport.EvaluationValue = "not json";
        Assert.False(await host.PublishStateAsync("example.performance", TestJson.Parse("{}")));
    }

    [Fact]
    public async Task ResponsePayloadUsesTheSameBoundAsPublishedState()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var host = new SteamUiBridgeHost(transport, TestAsset, TestVocabulary);
        Assert.True(await host.BootstrapAsync());
        var afterBootstrap = transport.Expressions.Count;
        var oversized = TestJson.Parse(
            "{\"value\":\"" + new string('x', SteamUiBridgeHost.MaximumPayloadCharacters)
                            + "\"}");

        Assert.False(await host.RespondAsync(
            Request(transport.Generations, 1, 1), true, oversized, null));
        Assert.Equal(afterBootstrap, transport.Expressions.Count);
    }

    [Fact]
    public async Task HandlerFailureDoesNotBlockTheNextBridgeSubscriber()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var host = new SteamUiBridgeHost(transport, TestAsset, TestVocabulary);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.RequestReceived += (_, _) => throw new InvalidOperationException("fixture failure");
        host.RequestReceived += (_, _) => received.TrySetResult();
        Assert.True(await host.BootstrapAsync());

        transport.EmitBindingPayload(RequestJson(transport.Generations, 1, 1));

        await received.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(host.IsReady);
    }

    [Fact]
    public async Task DisposalWaitsForAnInProgressBootstrapBeforeRetracting()
    {
        await using var transport = new FakeSteamUiTransport();
        var evaluationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEvaluation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.OnEvaluate = async call =>
        {
            // Only the first evaluation, the bootstrap, is held.
            if (!evaluationStarted.Task.IsCompleted)
            {
                evaluationStarted.TrySetResult();
                await releaseEvaluation.Task.WaitAsync(call.CancellationToken);
            }

            return transport.Reply(transport.EvaluationValue);
        };
        var host = new SteamUiBridgeHost(transport, TestAsset, TestVocabulary);

        var bootstrap = host.BootstrapAsync();
        await evaluationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var dispose = host.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);

        releaseEvaluation.TrySetResult();
        _ = await bootstrap;
        await dispose;

        Assert.Equal([true, false], transport.BindingStates);
        Assert.False(host.IsReady);
    }

    [Fact]
    public async Task ConnectionGenerationRaisedDuringBindingInstallBecomesBootstrapBaseline()
    {
        await using var transport = new FakeSteamUiTransport { AdvanceGenerationOnInstall = true };
        await using var host = new SteamUiBridgeHost(transport, TestAsset, TestVocabulary);

        Assert.True(await host.BootstrapAsync());
        Assert.True(host.IsReady);
    }

    [Fact]
    public async Task DisposalRetractsTheBindingAndDetachesNotifications()
    {
        await using var transport = new FakeSteamUiTransport();
        var host = new SteamUiBridgeHost(transport, TestAsset, TestVocabulary);
        var received = 0;
        host.RequestReceived += (_, _) => received++;
        Assert.True(await host.BootstrapAsync());

        await host.DisposeAsync();
        await host.DisposeAsync();
        transport.EmitBindingPayload(RequestJson(
            transport.Generations,
            1,
            1));

        Assert.Equal([true, false], transport.BindingStates);
        Assert.Equal(0, received);
        Assert.False(host.IsReady);
    }

    private static SteamUiBridgeRequest Request(
        SteamUiGenerations generations,
        long sequence,
        long actionGeneration)
    {
        return new SteamUiBridgeRequest(
            SteamUiBridgeHost.SchemaVersion,
            "request",
            "example.performance",
            "setLimit",
            sequence,
            actionGeneration,
            generations.ExecutionContext,
            generations.Document,
            TestJson.Parse("{\"watts\":15,\"enabled\":true}"));
    }

    private static string RequestJson(
        SteamUiGenerations generations,
        long sequence,
        long actionGeneration)
    {
        return JsonSerializer.Serialize(new
        {
            version = SteamUiBridgeHost.SchemaVersion,
            type = "request",
            patchId = "example.performance",
            command = "setLimit",
            sequence,
            actionGeneration,
            contextGeneration = generations.ExecutionContext,
            documentGeneration = generations.Document,
            payload = new { watts = 15, enabled = true }
        });
    }
}
