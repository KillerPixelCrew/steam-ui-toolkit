using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace SteamUiToolkit;

/// <summary>Small guards the transport, connection, patch manager and module runtime share.</summary>
internal static class SteamUiShared
{
    /// <summary>The longest diagnostic written in a log line; full operation details remain available.</summary>
    internal const int MaximumDiagnosticLength = 2048;

    /// <summary>The longest single Steam UI operation any caller may request.</summary>
    internal static readonly TimeSpan MaximumOperationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Truncates a diagnostic, marking that it was cut.</summary>
    /// <param name="value">The diagnostic, or null.</param>
    /// <param name="maximumLength">The longest result, the marker included.</param>
    /// <returns>
    ///     The value itself when it fits, otherwise a prefix followed by "…" that is at most
    ///     <paramref name="maximumLength" /> characters and never ends on half a surrogate pair.
    /// </returns>
    [return: NotNullIfNotNull(nameof(value))]
    internal static string? Bound(string? value, int maximumLength)
    {
        if (value is null || value.Length <= maximumLength)
        {
            return value;
        }

        var kept = Math.Max(0, maximumLength - 1);
        if (kept > 0 && char.IsHighSurrogate(value[kept - 1]))
        {
            kept--;
        }

        return value[..kept] + "…";
    }

    /// <summary>Describes a JSON payload for a log line without any of its string values.</summary>
    /// <param name="json">The payload as received, or null.</param>
    /// <returns>
    ///     The same structure with every string replaced by its length, or a note saying the text was
    ///     not JSON. Property names, numbers, booleans and nulls are kept, so a refusal still shows which
    ///     field was missing or of the wrong kind.
    /// </returns>
    /// <remarks>
    ///     Plugin secrets travel as command payloads and testers paste wsgm.log, so a payload is never
    ///     written as it arrived.
    /// </remarks>
    internal static string DescribePayload(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return "<empty>";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteRedacted(writer, document.RootElement);
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            return $"<unparseable, {json.Length} chars>";
        }
    }

    private static void WriteRedacted(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRedacted(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedacted(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue($"…({element.GetString()?.Length ?? 0})");
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>Rejects a timeout that is not positive or exceeds the operation bound.</summary>
    /// <param name="timeout">The requested timeout.</param>
    /// <param name="message">The rejection message.</param>
    /// <param name="paramName">The caller's parameter name.</param>
    internal static void ThrowIfInvalidTimeout(
        TimeSpan timeout,
        string message = "Steam UI operations require a positive timeout no greater than 30 seconds.",
        [CallerArgumentExpression(nameof(timeout))]
        string? paramName = null)
    {
        if (timeout <= TimeSpan.Zero || timeout > MaximumOperationTimeout)
        {
            throw new ArgumentOutOfRangeException(paramName, message);
        }
    }

    /// <summary>Cancels a source that may already have been disposed by its owner.</summary>
    /// <param name="cancellation">The source, or null.</param>
    internal static void CancelSafely(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation completed between the bounded lookup and cancellation.
        }
    }
}
