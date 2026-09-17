using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>An app Steam started or stopped.</summary>
public sealed class SteamAppLifetimeEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    /// <param name="appId">The app id.</param>
    /// <param name="timestamp">When Steam reported the change, or when it was noticed.</param>
    /// <param name="resynchronized">Whether the change was inferred from the running set.</param>
    public SteamAppLifetimeEventArgs(uint appId, DateTimeOffset timestamp, bool resynchronized)
    {
        AppId = appId;
        Timestamp = timestamp;
        Resynchronized = resynchronized;
    }

    /// <summary>The app id.</summary>
    public uint AppId { get; }

    /// <summary>Whether the app is a non-Steam shortcut.</summary>
    public bool IsShortcut => SteamApps.IsShortcutAppId(AppId);

    /// <summary>
    ///     When Steam delivered the notification. For a resynchronized change, when the monitor noticed it.
    /// </summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    ///     True when the monitor did not see Steam's notification and derived the change by comparing
    ///     running sets instead: on the first reading (every app already running is reported as started),
    ///     after Steam replaced its context, or after more changes than the observer's log retains. The
    ///     change is real, but its timestamp is approximate and quick start-stop pairs in the gap are lost.
    /// </summary>
    public bool Resynchronized { get; }
}

/// <summary>Whether the monitor can currently see Steam's running apps.</summary>
public sealed class SteamRunningAppsAvailabilityEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    /// <param name="available">Whether readings are arriving.</param>
    /// <param name="diagnostic">Why they stopped, when they did.</param>
    public SteamRunningAppsAvailabilityEventArgs(bool available, string? diagnostic)
    {
        Available = available;
        Diagnostic = diagnostic;
    }

    /// <summary>Whether readings are arriving.</summary>
    public bool Available { get; }

    /// <summary>Why readings stopped, for the log. Null while available.</summary>
    public string? Diagnostic { get; }
}

/// <summary>
///     Raises events when Steam starts or stops an app, from Steam's own lifetime notifications.
/// </summary>
/// <remarks>
///     <para>
///         The monitor polls a <see cref="SteamRunningAppsProbe" /> and reads the observer's numbered event
///         log, so a game that starts and stops between two polls still produces both events, in order.
///         Latency is at most one poll interval.
///     </para>
///     <para>
///         While Steam is unreachable (not running, CEF disabled, or the transport closed during a Big
///         Picture transition) the monitor raises <see cref="AvailabilityChanged" /> and keeps the last
///         known running set. It raises no stop events for that outage: an unreachable client is not a
///         closed game. When readings return, differences are reported with
///         <see cref="SteamAppLifetimeEventArgs.Resynchronized" /> set.
///     </para>
///     <para>
///         Handlers run on the monitor's worker thread, one at a time and in order. They must not block;
///         marshal to a UI thread where needed. An exception from a handler is logged and does not stop
///         the monitor.
///     </para>
/// </remarks>
public sealed class SteamAppLifetimeMonitor : IAsyncDisposable
{
    /// <summary>The poll interval used when none is given.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan MinimumPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly TimeSpan _pollInterval;
    private readonly SteamRunningAppsProbe _probe;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _sync = new();
    private readonly SteamAppLifetimeTracker _tracker = new();
    private int _disposed;
    private Task? _loop;

    /// <summary>Creates a monitor over the host's transport. Call <see cref="Start" /> to begin.</summary>
    /// <param name="transport">The session's transport.</param>
    /// <param name="pollInterval">How often to read; at least 250 ms. Null for one second.</param>
    public SteamAppLifetimeMonitor(ISteamUiTransport transport, TimeSpan? pollInterval = null)
    {
        _probe = new SteamRunningAppsProbe(transport);
        _pollInterval = pollInterval ?? DefaultPollInterval;
        if (_pollInterval < MinimumPollInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollInterval),
                pollInterval,
                "The poll interval must be at least 250 ms.");
        }
    }

    /// <summary>Whether readings are currently arriving from Steam.</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_sync)
            {
                return _tracker.Available;
            }
        }
    }

    /// <summary>The apps the monitor last knew to be running, in no particular order.</summary>
    public IReadOnlyList<uint> RunningApps
    {
        get
        {
            lock (_sync)
            {
                return [.. _tracker.Running];
            }
        }
    }

    /// <summary>Stops the monitor and removes its in-page observer.</summary>
    /// <returns>A task that completes when the worker has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task? loop;
        lock (_sync)
        {
            loop = _loop;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
    }

    /// <summary>Raised when Steam starts an app.</summary>
    public event EventHandler<SteamAppLifetimeEventArgs>? AppStarted;

    /// <summary>Raised when Steam stops an app.</summary>
    public event EventHandler<SteamAppLifetimeEventArgs>? AppStopped;

    /// <summary>Raised when readings from Steam stop or resume.</summary>
    public event EventHandler<SteamRunningAppsAvailabilityEventArgs>? AvailabilityChanged;

    /// <summary>Starts polling. Calling it again does nothing.</summary>
    /// <exception cref="ObjectDisposedException">The monitor was disposed.</exception>
    public void Start()
    {
        lock (_sync)
        {
            // Checked under the lock so a concurrent dispose either sees this loop or refuses it.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _loop ??= Task.Run(() => RunAsync(_shutdown.Token));
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IAsyncDisposable lease;
            try
            {
                lease = await _probe.SubscribeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Apply(new SteamRunningAppsObservation(false, [], 0, ex.Message));
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await using (lease.ConfigureAwait(false))
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    long? eventsAfter;
                    lock (_sync)
                    {
                        eventsAfter = _tracker.EventsAfter;
                    }

                    SteamRunningAppsObservation observation;
                    try
                    {
                        observation = await _probe.ObserveAsync(eventsAfter, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        observation = new SteamRunningAppsObservation(false, [], 0, ex.Message);
                    }

                    Apply(observation);
                    await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private void Apply(SteamRunningAppsObservation observation)
    {
        SteamAppLifetimeTracker.Update update;
        lock (_sync)
        {
            update = _tracker.Apply(observation, DateTimeOffset.UtcNow);
        }

        if (update.AvailabilityChanged)
        {
            SteamUiLog.Info(update.Available
                ? "Steam app lifetime monitor: readings available."
                : $"Steam app lifetime monitor: readings unavailable ({update.Diagnostic}).");
            Raise(AvailabilityChanged, new SteamRunningAppsAvailabilityEventArgs(update.Available, update.Diagnostic));
        }

        foreach (var change in update.Changes)
        {
            var args = new SteamAppLifetimeEventArgs(change.AppId, change.Timestamp, change.Resynchronized);
            Raise(change.Running ? AppStarted : AppStopped, args);
        }
    }

    private void Raise<T>(EventHandler<T>? handlers, T args)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                SteamUiLog.Warn($"Steam app lifetime handler failed: {ex.Message}");
            }
        }
    }
}

/// <summary>Turns successive readings into ordered start and stop changes. Not thread-safe.</summary>
internal sealed class SteamAppLifetimeTracker
{
    private readonly HashSet<uint> _running = [];
    private string? _observerId;
    private long _sequence;

    internal bool Available { get; private set; }

    internal IReadOnlyCollection<uint> Running => _running;

    /// <summary>The sequence to request events after, or null until an observer is known.</summary>
    internal long? EventsAfter => _observerId is null ? null : _sequence;

    /// <summary>Applies one reading.</summary>
    /// <param name="observation">The reading.</param>
    /// <param name="now">The time to stamp inferred changes with.</param>
    internal Update Apply(SteamRunningAppsObservation observation, DateTimeOffset now)
    {
        // A disabled transport answers as reachable with no observer. For a consumer that wants
        // lifetime events that is as blind as a failure.
        if (!observation.Reachable || observation.ObserverId is null)
        {
            var diagnostic = observation.Diagnostic ?? "Steam app observation is disabled.";
            if (!Available)
            {
                return new Update(false, false, diagnostic, []);
            }

            Available = false;
            return new Update(true, false, diagnostic, []);
        }

        var availabilityChanged = !Available;
        Available = true;
        List<Change> changes = [];
        var sameObserver = string.Equals(observation.ObserverId, _observerId, StringComparison.Ordinal);
        if (sameObserver && observation.EventsComplete && observation.Events is { } events)
        {
            foreach (var lifetime in events)
            {
                if (lifetime.Sequence <= _sequence)
                {
                    continue;
                }

                var changed = lifetime.Running ? _running.Add(lifetime.AppId) : _running.Remove(lifetime.AppId);
                if (changed)
                {
                    changes.Add(new Change(lifetime.AppId, lifetime.Running, lifetime.Timestamp, false));
                }
            }
        }

        // The running set is authoritative. After a replaced context or a truncated log this is the
        // whole update; otherwise it only catches what the log could not explain.
        Reconcile(observation.AppIds, now, changes);
        _observerId = observation.ObserverId;
        _sequence = Math.Max(observation.Sequence, sameObserver ? _sequence : 0);
        return new Update(availabilityChanged, true, null, changes);
    }

    private void Reconcile(IReadOnlyList<uint> reported, DateTimeOffset now, List<Change> changes)
    {
        foreach (var appId in reported)
        {
            if (_running.Add(appId))
            {
                changes.Add(new Change(appId, true, now, true));
            }
        }

        // A full reading may be truncated, so an app missing from it is not proof that it stopped.
        if (reported.Count >= SteamRunningAppsProbe.MaxReportedApps)
        {
            return;
        }

        var stopped = _running.Where(appId => !reported.Contains(appId)).ToList();
        foreach (var appId in stopped)
        {
            _running.Remove(appId);
            changes.Add(new Change(appId, false, now, true));
        }
    }

    /// <summary>One start or stop to raise.</summary>
    internal readonly record struct Change(uint AppId, bool Running, DateTimeOffset Timestamp, bool Resynchronized);

    /// <summary>What one reading changed.</summary>
    internal readonly record struct Update(
        bool AvailabilityChanged,
        bool Available,
        string? Diagnostic,
        IReadOnlyList<Change> Changes);
}
