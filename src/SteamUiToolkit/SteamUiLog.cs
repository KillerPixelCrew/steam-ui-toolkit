namespace SteamUiToolkit;

/// <summary>The severity of a console or exception message reported by Steam's CEF runtime.</summary>
public enum SteamUiConsoleLevel
{
    /// <summary>A development trace or informational console message.</summary>
    Debug,

    /// <summary>A console warning.</summary>
    Warning,

    /// <summary>A console error or uncaught JavaScript exception.</summary>
    Error
}

/// <summary>Where the Steam UI machinery writes its diagnostics.</summary>
/// <remarks>
///     The host supplies this. Without it the machinery would either depend on one application's
///     logger or say nothing at all, and saying nothing is not an option here: remote diagnosis of a
///     CEF surface is a pasted log, and the lines below are frequently the only evidence that a control
///     the user operated did nothing.
/// </remarks>
public interface ISteamUiLog
{
    /// <summary>Whether informational CEF console messages should be captured as development diagnostics.</summary>
    bool ConsoleVerboseEnabled => false;

    /// <summary>Records a CEF message, including its source and available stack.</summary>
    /// <param name="key">Stable target and severity key for repeated-message suppression.</param>
    /// <param name="message">Bounded console text, object properties and stack frames.</param>
    /// <param name="level">The console severity.</param>
    /// <remarks>The default forwards warnings and errors through the existing change-aware sink.</remarks>
    void Console(string key, string message, SteamUiConsoleLevel level)
    {
        if (level == SteamUiConsoleLevel.Debug)
        {
            Info(message);
        }
        else
        {
            Change(key, message, true);
        }
    }

    /// <summary>Records something that happened.</summary>
    /// <param name="message">The line to write.</param>
    void Info(string message);

    /// <summary>Records a failure the caller recovered from.</summary>
    /// <param name="message">The line to write.</param>
    void Warn(string message);

    /// <summary>Records a line only when it differs from the last one written under this key.</summary>
    /// <param name="key">
    ///     Identifies the state being reported, so unrelated lines do not suppress
    ///     each other.
    /// </param>
    /// <param name="message">The line to write.</param>
    /// <param name="warning">Whether this is a failure rather than an observation.</param>
    /// <remarks>
    ///     For anything on a poll or a repeating gate. A patch can repeat a refused write on its own
    ///     schedule, and writing every repeat buries the transition that matters in thousands of
    ///     identical lines. Suppressed repeats must be counted rather than dropped: a stalled timer and
    ///     a steady state have to stay distinguishable in the log.
    /// </remarks>
    void Change(string key, string message, bool warning = false);
}

/// <summary>The sink the Steam UI machinery writes to.</summary>
/// <remarks>
///     A settable static rather than a constructor parameter on a dozen types. The alternative threads
///     a logger through the transport, the bridge, the patch manager and every patch context, which is
///     churn that buys nothing: there is one sink per process and it is set before anything starts.
///     <para>
///         It defaults to discarding, so a consumer that never sets one still runs — and a test that never
///         sets one writes nothing, which is what this repository's tests require.
///     </para>
/// </remarks>
public static class SteamUiLog
{
    private static readonly ISteamUiLog Discarding = new Discard();

    // Volatile: the host sets it once at startup and every thread that writes a line reads it.
    private static volatile ISteamUiLog _sink = Discarding;

    internal static bool ConsoleEnabled => !ReferenceEquals(_sink, Discarding);

    internal static bool ConsoleVerboseEnabled => _sink.ConsoleVerboseEnabled;

    internal static void Console(string key, string message, SteamUiConsoleLevel level)
    {
        _sink.Console(key, message, level);
    }

    /// <summary>Directs the machinery's diagnostics at the host's logger.</summary>
    /// <param name="sink">The host's sink, or <see langword="null" /> to discard.</param>
    public static void Use(ISteamUiLog? sink)
    {
        _sink = sink ?? Discarding;
    }

    /// <summary>Records something that happened.</summary>
    /// <param name="message">The line to write.</param>
    public static void Info(string message)
    {
        _sink.Info(message);
    }

    /// <summary>Records a failure the caller recovered from.</summary>
    /// <param name="message">The line to write.</param>
    public static void Warn(string message)
    {
        _sink.Warn(message);
    }

    /// <summary>Records a line only when it differs from the last one under this key.</summary>
    /// <param name="key">Identifies the state being reported.</param>
    /// <param name="message">The line to write.</param>
    /// <param name="warning">Whether this is a failure rather than an observation.</param>
    public static void Change(string key, string message, bool warning = false)
    {
        _sink.Change(key, message, warning);
    }

    private sealed class Discard : ISteamUiLog
    {
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
