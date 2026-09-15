using System.Text;
using System.Text.Json;

namespace SteamUiToolkit.Tests.Fakes;

/// <summary>A CDP wire that hands the connection whatever the test queued, in order.</summary>
internal class QueueWire : ISteamUiCdpWire
{
    private readonly Queue<byte[]> _messages = new();
    private readonly SemaphoreSlim _available = new(0);

    /// <summary>Observes each sent request; replies are queued with <see cref="Enqueue(string)"/>.</summary>
    internal Action<byte[]>? Sent { get; set; }

    internal TaskCompletionSource Disposed { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal static int RequestId(ReadOnlyMemory<byte> request)
    {
        using JsonDocument document = JsonDocument.Parse(request);
        return document.RootElement.GetProperty("id").GetInt32();
    }

    internal void Enqueue(string message) => Enqueue(Encoding.UTF8.GetBytes(message));

    internal void Enqueue(byte[] message)
    {
        lock (_messages)
        {
            _messages.Enqueue(message);
        }
        _available.Release();
    }

    internal void Notify(string method, string parameters) =>
        Enqueue($"{{\"method\":{JsonSerializer.Serialize(method)},\"params\":{parameters}}}");

    public virtual Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
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
        Disposed.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
