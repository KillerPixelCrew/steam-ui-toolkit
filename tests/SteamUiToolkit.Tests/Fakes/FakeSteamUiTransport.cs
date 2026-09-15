using System.Text.Json;

namespace SteamUiToolkit.Tests.Fakes;

/// <summary>One evaluation a patch or host sent through the fake transport.</summary>
/// <param name="Role">The target the expression was sent to.</param>
/// <param name="Expression">The expression.</param>
/// <param name="Timeout">The deadline the caller passed.</param>
/// <param name="CancellationToken">The caller's cancellation.</param>
internal readonly record struct FakeEvaluation(
    SteamUiTargetRole Role,
    string Expression,
    TimeSpan Timeout,
    CancellationToken CancellationToken);

/// <summary>
/// A shared-context transport that records what is sent and answers evaluations from a delegate.
/// </summary>
/// <remarks>
/// Without a delegate every evaluation answers <see cref="EvaluationValue"/> under the current
/// generations. Tests that need content-dependent answers, blocking, or generation changes during a
/// call supply <see cref="OnEvaluate"/> and build the answer with <see cref="Reply"/>.
/// </remarks>
internal sealed class FakeSteamUiTransport : ISteamUiTransport
{
    private readonly object _sync = new();
    private readonly List<string> _expressions = [];
    private readonly List<bool> _bindingStates = [];
    private int _releasedSubscriptions;

    public event EventHandler<SteamUiNotification>? NotificationReceived;

    public event EventHandler<SteamUiTransportSnapshot>? GenerationChanged;

    internal SteamUiGenerations Generations { get; set; } = new(1, 1, 1, 1, 1, 1);

    internal SteamUiTransportHealth Health { get; set; } = SteamUiTransportHealth.Ready;

    internal string EvaluationValue { get; set; } = "{\"ok\":true}";

    internal Func<FakeEvaluation, Task<SteamUiEvaluationResult>>? OnEvaluate { get; set; }

    internal bool AdvanceGenerationOnInstall { get; init; }

    internal bool RejectSubscriptions { get; init; }

    internal bool RejectBindings { get; init; }

    internal IReadOnlyList<string> Expressions
    {
        get
        {
            lock (_sync)
            {
                return [.. _expressions];
            }
        }
    }

    internal IReadOnlyList<bool> BindingStates
    {
        get
        {
            lock (_sync)
            {
                return [.. _bindingStates];
            }
        }
    }

    internal int ReleasedSubscriptions => Volatile.Read(ref _releasedSubscriptions);

    public ValueTask<IAsyncDisposable> SubscribeAsync(
        SteamUiTargetRole role,
        CancellationToken cancellationToken = default)
    {
        if (RejectSubscriptions)
        {
            throw new InvalidOperationException("Must borrow the existing subscription");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IAsyncDisposable>(new Lease(this));
    }

    public Task<SteamUiEvaluationResult> EvaluateAsync(
        SteamUiTargetRole role,
        string expression,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _expressions.Add(expression);
        }
        return OnEvaluate?.Invoke(new FakeEvaluation(role, expression, timeout, cancellationToken))
            ?? Task.FromResult(Reply(EvaluationValue));
    }

    public Task SetRuntimeBindingAsync(
        SteamUiTargetRole role,
        string bindingName,
        bool installed,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (RejectBindings)
        {
            throw new InvalidOperationException("Must not install a binding");
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _bindingStates.Add(installed);
        }
        if (installed && AdvanceGenerationOnInstall)
        {
            AdvanceDocumentGeneration();
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots() => [Snapshot()];

    /// <summary>A reachable answer under the current generations.</summary>
    internal SteamUiEvaluationResult Reply(string? value) => new(true, value, null, Generations);

    internal void AdvanceDocumentGeneration()
    {
        AdvanceGenerationWithoutEvent();
        EmitCurrentGeneration();
    }

    internal void AdvanceGenerationWithoutEvent() =>
        Generations = Generations with
        {
            ExecutionContext = Generations.ExecutionContext + 1,
            Document = Generations.Document + 1,
        };

    internal void EmitCurrentGeneration() => GenerationChanged?.Invoke(this, Snapshot());

    internal void EmitBindingPayload(string payload, SteamUiGenerations? generations = null) =>
        EmitRawParameters(
            JsonSerializer.Serialize(new { name = SteamUiBridgeIdentity.BindingName, payload }),
            generations);

    internal void EmitRawParameters(string parameters, SteamUiGenerations? generations = null) =>
        NotificationReceived?.Invoke(
            this,
            new SteamUiNotification(
                SteamUiTargetRole.SharedJsContext,
                "Runtime.bindingCalled",
                parameters,
                generations ?? Generations));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private SteamUiTransportSnapshot Snapshot() => new(
        SteamUiTargetRole.SharedJsContext,
        Health,
        Generations,
        "fixture",
        null,
        0,
        1);

    private sealed class Lease(FakeSteamUiTransport owner) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Increment(ref owner._releasedSubscriptions);
            }
            return ValueTask.CompletedTask;
        }
    }
}
