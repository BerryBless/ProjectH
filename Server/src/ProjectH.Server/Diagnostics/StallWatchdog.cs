using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace ProjectH.Server.Diagnostics;

// Phase 10 D9: notices a game loop that stopped ticking (a deadlock, an endless loop, a blocking call) even when
// nothing throws. A timer checks once per second when the loop last finished a tick; past the threshold it logs one
// Critical and counts a stall, and when ticks come back it logs how long the stall lasted.
// Owned by GameServerService (created after the loop starts, disposed before it stops). Check runs on timer threads.
// Callbacks can overlap (a starved thread pool can fire the next one before the last returns), so a check that finds
// another one running returns at once (Interlocked flag, no lock); _stalled and _stalledFrom are only touched inside
// it. The loop's timestamp is read with Volatile (the delegate).
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

    public StallWatchdog(Func<long> lastTickTimestamp, TimeProvider time, HealthCounters health, ILogger logger, TimeSpan? threshold = null)
    {
        _lastTickTimestamp = lastTickTimestamp;
        _time = time;
        _health = health;
        _logger = logger;
        _threshold = threshold ?? DefaultThreshold;
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
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private void CheckOnce()
    {
        long last = _lastTickTimestamp();
        TimeSpan since = _time.GetElapsedTime(last);
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
    }

    public void Dispose() => _timer?.Dispose();
}
