using System.Text.Json;

namespace SteamUiToolkit.Tests;

public sealed class SteamUiModuleRuntimeTests
{
    private static readonly SteamUiInjectedAsset Asset = new(
        "(()=>__WSGM_CONFIGURATION_JSON__)()",
        "FIXTUREHASH");

    [Fact]
    public async Task FailingPublicationDoesNotPreventIndependentPublication()
    {
        await using var transport = new RuntimeTransport();
        SteamUiModuleSet modules = new(
        [
            new SteamUiModule(
                "fixture",
                publications:
                [
                    new SteamUiStatePublication(
                        "fixture.bad",
                        () => true,
                        () => throw new InvalidOperationException("fixture failure")),
                    new SteamUiStatePublication(
                        "fixture.good",
                        () => true,
                        () => ValueTask.FromResult<JsonElement?>(Json("{\"value\":1}"))),
                ]),
        ]);
        await using var bridge = new SteamUiBridgeHost(
            transport,
            Asset,
            modules.AllowedCommands);
        Assert.True(await bridge.BootstrapAsync());
        await using var runtime = new SteamUiModuleRuntime(
            bridge,
            modules,
            () => true,
            () => true);

        runtime.QueuePublication();

        await WaitUntilAsync(() => transport.DeliveryCount == 1);
        Assert.Contains("fixture.good", transport.GetDelivery(0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedPublicationDoesNotPreventTheNextPublication()
    {
        await using var transport = new RuntimeTransport();
        transport.DeliveryAcknowledgements.Enqueue("{\"ok\":false}");
        transport.DeliveryAcknowledgements.Enqueue("{\"ok\":true}");
        SteamUiModuleSet modules = new(
        [
            new SteamUiModule(
                "fixture",
                publications:
                [
                    Publication("fixture.first", 1),
                    Publication("fixture.second", 2),
                ]),
        ]);
        await using var bridge = new SteamUiBridgeHost(
            transport,
            Asset,
            modules.AllowedCommands);
        Assert.True(await bridge.BootstrapAsync());
        await using var runtime = new SteamUiModuleRuntime(
            bridge,
            modules,
            () => true,
            () => true);

        runtime.QueuePublication();

        await WaitUntilAsync(() => transport.DeliveryCount == 2);
        Assert.Contains("fixture.first", transport.GetDelivery(0), StringComparison.Ordinal);
        Assert.Contains("fixture.second", transport.GetDelivery(1), StringComparison.Ordinal);
    }

    private static SteamUiStatePublication Publication(string patchId, int value) => new(
        patchId,
        () => true,
        () => ValueTask.FromResult<JsonElement?>(Json($"{{\"value\":{value}}}")));

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class RuntimeTransport : ISteamUiTransport
    {
        private readonly object _sync = new();
        private readonly SteamUiGenerations _generations = new(1, 1, 1, 1, 1, 1);

        internal Queue<string> DeliveryAcknowledgements { get; } = new();

        private List<string> DeliveryExpressions { get; } = [];

        internal int DeliveryCount
        {
            get
            {
                lock (_sync)
                {
                    return DeliveryExpressions.Count;
                }
            }
        }

        internal string GetDelivery(int index)
        {
            lock (_sync)
            {
                return DeliveryExpressions[index];
            }
        }

        public event EventHandler<SteamUiNotification>? NotificationReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<SteamUiTransportSnapshot>? GenerationChanged
        {
            add { }
            remove { }
        }

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            SteamUiTargetRole role,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IAsyncDisposable>(new Lease());
        }

        public Task<SteamUiEvaluationResult> EvaluateAsync(
            SteamUiTargetRole role,
            string expression,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string acknowledgement = "{\"ok\":true}";
            lock (_sync)
            {
                if (expression.Contains("deliver", StringComparison.Ordinal))
                {
                    DeliveryExpressions.Add(expression);
                    if (DeliveryAcknowledgements.Count > 0)
                    {
                        acknowledgement = DeliveryAcknowledgements.Dequeue();
                    }
                }
            }
            return Task.FromResult(new SteamUiEvaluationResult(
                true,
                acknowledgement,
                null,
                _generations));
        }

        public Task SetRuntimeBindingAsync(
            SteamUiTargetRole role,
            string bindingName,
            bool installed,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots() =>
        [
            new SteamUiTransportSnapshot(
                SteamUiTargetRole.SharedJsContext,
                SteamUiTransportHealth.Ready,
                _generations,
                "fixture",
                null,
                0,
                1),
        ];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
