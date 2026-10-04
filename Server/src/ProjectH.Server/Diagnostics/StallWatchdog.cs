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

    // 기능: Game Loop 멈춤 감시기를 만든다. Timer는 Start를 호출해야 시작된다.
    // 입력: lastTickTimestamp - Loop가 마지막으로 Tick을 끝낸 시각(TimeProvider Timestamp)을 돌려주는 함수, time - 시각과 Timer 공급자, health - 멈춤 수를 기록할 Health Counter, logger - 로그 출력 대상, threshold - 멈춤으로 판정할 Tick 공백(null이면 2초), fatalAfter - 서버를 멈출 멈춤 지속 시간(0이면 끔), onFatalStall - 치명적 멈춤 때 한 번 호출할 동작.
    // 출력: 멈춤이 없는 상태로 초기화된 StallWatchdog 객체.
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

    // 기능: 1초마다 Check를 실행하는 Timer를 만든다. 이미 시작했으면 아무것도 하지 않는다.
    // 입력: 없음.
    // 출력: 반환값 없음. 감시 Timer가 시작된다.
    public void Start() => _timer ??= _time.CreateTimer(_ => Check(), null, Period, Period);

    // 기능: 멈춤 검사를 한 번 실행한다. 다른 검사가 실행 중이면 바로 돌아가고, 검사 중 예외는 잡아서 센다. Timer Thread에서 실행된다.
    // 입력: 없음.
    // 출력: 반환값 없음. 멈춤 상태·멈춤 Counter·로그가 갱신되고, 치명적 멈춤이면 onFatalStall이 호출된다.
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

    // 기능: 마지막 Tick 이후 경과 시간으로 멈춤 시작·회복을 판정하고, 이전 검사부터 이어진 멈춤이 fatalAfter를 넘으면 서버 정지를 요청한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 멈춤 상태가 바뀌면 Counter와 로그가 기록되고, 치명적 멈춤이면 Process당 한 번 onFatalStall이 호출된다.
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

    // 기능: 검사 중 잡힌 예외를 Callback 오류로 세고, ErrorLogInterval마다 최대 한 번 로그로 남긴다.
    // 입력: ex - 검사 중 발생한 예외.
    // 출력: 반환값 없음. Callback 오류 수가 증가하고 필요하면 오류 로그가 기록된다.
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

    // 기능: Stall 점검 Timer를 해제한다. Start 전이면 아무것도 하지 않는다.
    // 입력: 없음.
    // 출력: 반환값 없음. Timer가 있으면 해제되어 더 이상 점검하지 않는다.
    public void Dispose() => _timer?.Dispose();
}
