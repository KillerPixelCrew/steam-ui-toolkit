using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SteamUiToolkit;

// Native CDP diagnostics share the existing connection; no console wrappers or page listeners.
internal sealed class SteamUiCefDiagnostics : IAsyncDisposable
{
    private const int MaximumMessageLength = 8192;
    private readonly SteamUiCdpConnection _connection;

    private readonly Channel<(string Method, string Parameters)> _messages =
        Channel.CreateBounded<(string Method, string Parameters)>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly Task _pump;
    private readonly SteamUiTargetRole _role;
    private readonly CancellationTokenSource _shutdown = new();
    private int _dropped;

    internal SteamUiCefDiagnostics(SteamUiTargetRole role, SteamUiCdpConnection connection)
    {
        _role = role;
        _connection = connection;
        _pump = PumpAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _messages.Writer.TryComplete();
        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
        }
    }

    internal bool Post(string method, string parameters, JsonElement data)
    {
        if (method is not ("Runtime.consoleAPICalled" or "Runtime.exceptionThrown" or "Log.entryAdded"))
        {
            return false;
        }

        if (!SteamUiLog.ConsoleEnabled)
        {
            return true;
        }

        if (!SteamUiLog.ConsoleVerboseEnabled && method != "Runtime.exceptionThrown")
        {
            var level = method == "Log.entryAdded" && data.ValueKind == JsonValueKind.Object
                                                   && data.TryGetProperty("entry", out var entry)
                ? Text(entry, "level")
                : Text(data, "type");
            if (level is not ("error" or "assert" or "warning" or "warn"))
            {
                return true;
            }
        }

        if (parameters.Length > 64 * 1024 || !_messages.Writer.TryWrite((method, parameters)))
        {
            Interlocked.Increment(ref _dropped);
        }

        return true;
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var (method, parameters) in _messages.Reader.ReadAllAsync(_shutdown.Token)
                               .ConfigureAwait(false))
            {
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped != 0)
                {
                    SteamUiLog.Console($"steam.ui.console.{_role}.overflow",
                        $"Steam CEF {_role}: dropped {dropped} console messages because the diagnostic queue was full or the payload was oversized.",
                        SteamUiConsoleLevel.Warning);
                }

                try
                {
                    await WriteAsync(method, parameters).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    SteamUiLog.Change($"steam.ui.console.{_role}.parse",
                        $"Steam CEF {_role}: diagnostic could not be read: {Bound(error.Message, 256)}", true);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task WriteAsync(string method, string parameters)
    {
        using var document = JsonDocument.Parse(parameters);
        var data = document.RootElement;
        var exception = method == "Runtime.exceptionThrown";
        if (exception)
        {
            data = data.GetProperty("exceptionDetails");
        }
        else if (method == "Log.entryAdded")
        {
            data = data.GetProperty("entry");
        }

        var type = exception ? "exception" : Text(data, method == "Log.entryAdded" ? "level" : "type");
        var level = exception || type is "error" or "assert"
            ? SteamUiConsoleLevel.Error
            : type is "warning" or "warn"
                ? SteamUiConsoleLevel.Warning
                : SteamUiConsoleLevel.Debug;
        if (level == SteamUiConsoleLevel.Debug && !SteamUiLog.ConsoleVerboseEnabled)
        {
            return;
        }

        var text = new StringBuilder($"Steam CEF {_role} {type}: ");
        if (exception)
        {
            text.Append(Text(data, "text"));
            if (data.TryGetProperty("exception", out var reason))
            {
                text.Append(' ');
                await AppendObjectAsync(text, reason, true).ConfigureAwait(false);
            }
        }
        else if (data.TryGetProperty("args", out var arguments) && arguments.ValueKind == JsonValueKind.Array)
        {
            var count = 0;
            foreach (var argument in arguments.EnumerateArray())
            {
                if (count++ == 8)
                {
                    text.Append(" …");
                    break;
                }

                if (count > 1)
                {
                    text.Append(' ');
                }

                await AppendObjectAsync(text, argument, level != SteamUiConsoleLevel.Debug && count <= 4)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            text.Append(Bound(Text(data, "text"), 2048));
        }

        var url = Text(data, "url");
        if (url.Length > 0)
        {
            text.Append(" | source: ").Append(SourceUrl(url));
            if (data.TryGetProperty("lineNumber", out var line) && line.TryGetInt32(out var number))
            {
                text.Append(':').Append(number + 1);
            }
        }

        if (data.TryGetProperty("stackTrace", out var stack))
        {
            AppendStack(text, stack, 0);
        }

        if (!_shutdown.IsCancellationRequested)
        {
            SteamUiLog.Console($"steam.ui.console.{_role}.{type}", Bound(text.ToString(), MaximumMessageLength), level);
        }
    }

    private async Task AppendObjectAsync(StringBuilder text, JsonElement value, bool inspect)
    {
        if (value.TryGetProperty("value", out var primitive))
        {
            text.Append(Bound(
                primitive.ValueKind == JsonValueKind.String ? primitive.GetString()! : primitive.GetRawText(), 2048));
            return;
        }

        var description = Text(value, "description");
        text.Append(Bound(description.Length == 0 ? Text(value, "type") : description, 2048));
        var objectId = Text(value, "objectId");
        if (!inspect || objectId.Length == 0)
        {
            if (value.TryGetProperty("preview", out var preview))
            {
                text.Append(' ').Append(Bound(preview.GetRawText(), 1024));
            }

            return;
        }

        var handles = new HashSet<string>(StringComparer.Ordinal) { objectId };
        try
        {
            var response = await _connection.InvokeAsync("Runtime.getProperties", writer =>
            {
                writer.WriteString("objectId", objectId);
                writer.WriteBoolean("ownProperties", true);
                writer.WriteBoolean("generatePreview", false);
                writer.WriteBoolean("nonIndexedPropertiesOnly", true);
            }, TimeSpan.FromMilliseconds(750), _shutdown.Token).ConfigureAwait(false);
            foreach (var field in new[] { "result", "internalProperties", "privateProperties" })
            {
                if (!response.TryGetProperty(field, out var descriptors) ||
                    descriptors.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var descriptor in descriptors.EnumerateArray())
                {
                    foreach (var memberName in new[] { "value", "get", "set" })
                    {
                        if (descriptor.TryGetProperty(memberName, out var remote))
                        {
                            var handle = Text(remote, "objectId");
                            if (handle.Length > 0)
                            {
                                handles.Add(handle);
                            }
                        }
                    }
                }
            }

            if (response.TryGetProperty("result", out var properties) && properties.ValueKind == JsonValueKind.Array)
            {
                text.Append(" {");
                var count = 0;
                foreach (var property in properties.EnumerateArray())
                {
                    if (property.TryGetProperty("value", out var member))
                    {
                        var childId = Text(member, "objectId");
                        if (childId.Length > 0)
                        {
                            handles.Add(childId);
                        }

                        if (count++ >= 12)
                        {
                            continue;
                        }

                        if (count > 1)
                        {
                            text.Append(", ");
                        }

                        var name = Text(property, "name");
                        text.Append(Bound(name, 128)).Append(": ");
                        if (name.Contains("token", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("password", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("cookie", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("authorization", StringComparison.OrdinalIgnoreCase))
                        {
                            text.Append("[redacted]");
                        }
                        else if (member.TryGetProperty("value", out var scalar))
                        {
                            text.Append(Bound(scalar.GetRawText(), 512));
                        }
                        else
                        {
                            text.Append(Bound(Text(member, "description"), 512));
                        }
                    }
                    // Accessor descriptors are deliberately not invoked.
                }

                text.Append('}');
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            text.Append(" [object details unavailable: ").Append(Bound(error.Message, 128)).Append(']');
        }
        finally
        {
            foreach (var handle in handles)
            {
                if (_shutdown.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await _connection.InvokeAsync("Runtime.releaseObject",
                        writer => writer.WriteString("objectId", handle),
                        TimeSpan.FromMilliseconds(250), _shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A vanished context or closing connection already released its handles.
                }
            }
        }
    }

    private static void AppendStack(StringBuilder text, JsonElement stack, int depth)
    {
        if (depth > 3 || text.Length >= MaximumMessageLength)
        {
            return;
        }

        if (stack.TryGetProperty("callFrames", out var frames) && frames.ValueKind == JsonValueKind.Array)
        {
            var count = 0;
            foreach (var frame in frames.EnumerateArray())
            {
                if (count++ >= 16)
                {
                    break;
                }

                text.Append("\n  at ").Append(Bound(Text(frame, "functionName"), 128))
                    .Append(" (").Append(SourceUrl(Text(frame, "url"))).Append(':');
                text.Append(frame.TryGetProperty("lineNumber", out var line) && line.TryGetInt32(out var number)
                        ? number + 1
                        : 0)
                    .Append(':');
                text.Append(frame.TryGetProperty("columnNumber", out var column) && column.TryGetInt32(out var position)
                        ? position + 1
                        : 0)
                    .Append(')');
            }
        }

        if (stack.TryGetProperty("parent", out var parent))
        {
            text.Append("\n  async: ").Append(Bound(Text(parent, "description"), 128));
            AppendStack(text, parent, depth + 1);
        }
    }

    private static string Text(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
                                                       && property.ValueKind == JsonValueKind.String
            ? property.GetString()!
            : string.Empty;
    }

    private static string SourceUrl(string value)
    {
        var query = value.IndexOfAny(['?', '#']);
        return Bound(query < 0 ? value : value[..query], 512);
    }

    private static string Bound(string value, int length)
    {
        return value.Length <= length ? value : value[..length] + "…";
    }
}
