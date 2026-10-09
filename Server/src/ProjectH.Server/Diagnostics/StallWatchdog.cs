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

    // 기능: Game Loop 정지 감시기를 만든다(Timer는 Start에서 시작).
    // 입력: lastTickTimestamp - 마지막 Tick이 끝난 TimeProvider Timestamp를 읽는 함수, time - 시계, health - 정지·치명 정지·콜백 오류를 셀 합계, logger - 로그, threshold - 정지로 보는 무Tick 시간(null이면 2초), fatalAfter - 이 시간을 넘긴 정지면 onFatalStall을 부른다(Zero면 끔), onFatalStall - 치명 정지 때 한 번 부를 콜백.
    // 출력: 아직 감시를 시작하지 않은 StallWatchdog.
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

    // 기능: 1초마다 Check를 부르는 Timer를 시작한다. 이미 시작했으면 아무것도 하지 않는다.
    // 입력: 없음.
    // 출력: 반환값 없음. Timer 스레드에서 Check가 주기적으로 돈다.
    public void Start() => _timer ??= _time.CreateTimer(_ => Check(), null, Period, Period);

    // 기능: 정지 검사 한 번을 실행한다. 다른 검사가 돌고 있으면 바로 돌아가고, 예외는 OnError로 삼킨다. 테스트가 수동 시계로 직접 부를 수 있게 public이다.
    // 입력: 없음.
    // 출력: 반환값 없음. 정지 상태·합계·로그가 갱신되고, 치명 정지면 onFatalStall이 불린다.
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

    // 기능: 마지막 Tick 이후 시간으로 정지 시작·회복을 판정해 로그와 합계를 남기고, 이전 검사에서 본 정지가 fatalAfter를 넘겼으면 한 번만 치명 정지 처리를 한다.
    // 입력: 없음.
    // 출력: 반환값 없음. _stalled·_stalledFrom·_fatalDone이 바뀌고 필요하면 onFatalStall이 불린다.
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

    // 기능: 검사 중 잡은 예외를 콜백 오류로 세고 ErrorLogInterval(10초)에 한 번만 Error 로그를 남긴다. 절대 던지지 않는다.
    // 입력: ex - 잡은 예외.
    // 출력: 반환값 없음.
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

    // 기능: 감시 Timer를 멈추고 해제한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 더는 Check가 예약되지 않는다.
    public void Dispose() => _timer?.Dispose();
}
