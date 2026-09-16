using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SteamUiToolkit;

/// <summary>Small guards the transport, connection, patch manager and module runtime share.</summary>
internal static class SteamUiShared
{
    /// <summary>The longest single Steam UI operation any caller may request.</summary>
    internal static readonly TimeSpan MaximumOperationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Truncates a diagnostic, marking that it was cut.</summary>
    /// <param name="value">The diagnostic, or null.</param>
    /// <param name="maximumLength">The longest retained prefix.</param>
    /// <returns>The value itself when it fits, otherwise its prefix followed by "...".</returns>
    [return: NotNullIfNotNull(nameof(value))]
    internal static string? Bound(string? value, int maximumLength)
    {
        return value is null || value.Length <= maximumLength
            ? value
            : value[..maximumLength] + "...";
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
