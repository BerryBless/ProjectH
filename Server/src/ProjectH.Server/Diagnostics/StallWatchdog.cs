using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace ProjectH.Server.Diagnostics;

// Phase 10 D9: notices a game loop that stopped ticking (a deadlock, an endless loop, a blocking call) even when
// nothing throws. A timer checks once per second when the loop last finished a tick; past the threshold it logs one
// Critical and counts a stall, and when ticks come back it logs how long the stall lasted.
// Server review M8: a stall that lasts fatalAfter (FatalStallSeconds) is taken as a hang that will not end: once per
// process it counts a stall exit, logs a Critical and calls onFatalStall (GameServerService: refuse new connections,
// exit code 1, stop the host), so the process does not stay up as a zombie that accepts connections and never ticks.
// Owned by GameServerService (created after the loop starts, disposed before it stops). Check runs on timer threads.
// Callbacks can overlap (a starved thread pool can fire the next one before the last returns), so a check that finds
// another one running returns at once (Interlocked flag, no lock); _stalled, _stalledFrom and _fatalDone are only
// touched inside it. The loop's timestamp is read with Volatile (the delegate).
public sealed class StallWatchdog : IDisposable
{
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(1);

    private readonly Func<long> _lastTickTimestamp;
    private readonly TimeProvider _time;
    private readonly HealthCounters _health;
    private readonly ILogger _logger;
    private readonly TimeSpan _threshold;
    private ITimer? _timer;
    private int _checking;   // 1 while a Check runs
    private bool _stalled;
    private long _stalledFrom;   // the last tick before the stall (TimeProvider timestamp)
    private readonly TimeSpan _fatalAfter;   // Zero = off
    private readonly Action? _onFatalStall;
    private bool _fatalDone;   // set once, never cleared: the server is stopping
    // Server review L9: when a caught exception was last logged (TimeProvider timestamp; inside Check only). The watchdog
    // has no stats line to reset a flag, so it logs at most once per ErrorLogInterval (the default stats interval).
    private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromSeconds(10);
    private bool _errorLogged;
    private long _errorLoggedAt;

    public StallWatchdog(Func<long> lastTickTimestamp, TimeProvider time, HealthCounters health, ILogger logger, TimeSpan? threshold = null,
        TimeSpan fatalAfter = default, Action? onFatalStall = null)
    {
        _lastTickTimestamp = lastTickTimestamp;
        _time = time;
        _health = health;
        _logger = logger;
        _threshold = threshold ?? DefaultThreshold;
        _fatalAfter = fatalAfter;
        _onFatalStall = onFatalStall;
    }

    public void Start() => _timer ??= _time.CreateTimer(_ => Check(), null, Period, Period);

    // One check; public so tests can drive it with a manual clock.
    public void Check()
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0) return;
        try
        {
            CheckOnce();
        }
        catch (Exception ex)
        {
            // Server review L9: an exception escaping a timer callback would end the process.
            OnError(ex);
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private void CheckOnce()
    {
        long last = _lastTickTimestamp();
        TimeSpan since = _time.GetElapsedTime(last);
        // Server review M8: the fatal stop needs a stall an earlier check already saw and that still goes on. One check
        // after a long gap (a debugger break or a suspended process: the overdue timer may read the old tick time before
        // the loop ticks again) only reports it. A real hang is stopped at most one Period later.
        bool wasStalled = _stalled;
        if (!_stalled && since > _threshold)
        {
            _stalled = true;
            _stalledFrom = last;
            _health.AddStall();
            _logger.LogCritical("Game loop stalled: no tick for {Ms:F0} ms", since.TotalMilliseconds);
        }
        else if (_stalled && last != _stalledFrom)
        {
            _stalled = false;
            _logger.LogWarning("Game loop recovered after a stall of {Ms:F0} ms", _time.GetElapsedTime(_stalledFrom, last).TotalMilliseconds);
        }

        if (wasStalled && _stalled && !_fatalDone && _fatalAfter > TimeSpan.Zero && since > _fatalAfter)
        {
            _fatalDone = true;
            _health.AddStallExit();
            _logger.LogCritical("Game loop stalled for {Seconds:F0} s: stopping the server (exit code 1)", since.TotalSeconds);
            _onFatalStall?.Invoke();
        }
    }

    // Never throws (the log call is guarded too).
    private void OnError(Exception ex)
    {
        _health.AddCallbackError();
        try
        {
            long now = _time.GetTimestamp();
            if (_errorLogged && _time.GetElapsedTime(_errorLoggedAt, now) < ErrorLogInterval) return;
            _errorLogged = true;
            _errorLoggedAt = now;
            _logger.LogError(ex, "Exception in the stall watchdog's check (counted as callbackErrors; logged at most every {Seconds} s)",
                ErrorLogInterval.TotalSeconds);
        }
        catch
        {
            // Counted above; nothing left to report it with.
        }
    }

    public void Dispose() => _timer?.Dispose();
}
