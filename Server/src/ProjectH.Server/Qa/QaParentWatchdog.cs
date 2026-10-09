using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace ProjectH.Server.Qa;

// QA-3: the server launched by a QA tool stops itself once that tool's process is gone (killed hard, crashed), so no
// orphan server keeps its ports or its database connections. A thread-pool timer checks every interval; the check is a
// process lookup, nothing on the game loop. The parent's start time is taken once at the start: a pid that the system
// reuses for another process later still counts as gone. stop is called once (IHostApplicationLifetime.StopApplication,
// the normal shutdown path). Owned by QaHttpService, which disposes it; Dispose stops the timer.
internal sealed class QaParentWatchdog : IDisposable
{
    private readonly int _pid;
    private readonly DateTime? _startTime;
    private readonly Action _stop;
    private readonly ILogger _logger;
    private readonly Timer _timer;
    private int _fired;

    // 기능: 부모 프로세스 감시를 시작한다(시작 시각을 한 번 기록하고 interval마다 검사).
    // 입력: pid - QA 도구의 프로세스 id, interval - 검사 주기, stop - 부모가 사라지면 한 번 부를 동작, logger - 로그.
    // 출력: 타이머가 도는 QaParentWatchdog. 부모가 이미 없으면 첫 검사에서 stop이 불린다.
    public QaParentWatchdog(int pid, TimeSpan interval, Action stop, ILogger logger)
    {
        _pid = pid;
        _stop = stop;
        _logger = logger;
        _startTime = StartTimeOf(pid);
        if (_startTime == null) _logger.LogWarning("QA parent process {Pid} is not running", pid);
        else _logger.LogInformation("QA parent watch: the server stops when process {Pid} ends", pid);
        _timer = new Timer(_ => Check(), null, TimeSpan.Zero, interval);
    }

    public bool Fired => Volatile.Read(ref _fired) != 0;

    // 기능: pid의 프로세스가 아직 시작 때 본 그 프로세스인지 본다.
    // 입력: pid - 프로세스 id, startTime - 시작 때 기록한 시작 시각(없었으면 null).
    // 출력: 같은 시작 시각으로 살아 있으면 true.
    // The process is still the one that was there at the start.
    public static bool IsAlive(int pid, DateTime? startTime)
    {
        if (startTime == null) return false;
        DateTime? now = StartTimeOf(pid);
        return now != null && now.Value == startTime.Value;
    }

    // 기능: 프로세스의 시작 시각을 읽는다.
    // 입력: pid - 프로세스 id.
    // 출력: 시작 시각, 없거나 끝났거나 접근할 수 없으면 null.
    private static DateTime? StartTimeOf(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.HasExited) return null;
            return process.StartTime;
        }
        catch (Exception)
        {
            // Not running (ArgumentException), or exited/not accessible between the calls: treated as gone.
            return null;
        }
    }

    // 기능: 타이머 콜백. 부모가 사라졌으면 한 번만 stop을 부른다(예외를 던지지 않음).
    // 입력: 없음.
    // 출력: 반환값 없음. 처음 사라진 것을 본 호출에서 _fired가 1이 되고 stop이 불린다.
    private void Check()
    {
        if (Fired || IsAlive(_pid, _startTime)) return;
        if (Interlocked.Exchange(ref _fired, 1) != 0) return;
        try
        {
            _logger.LogWarning("QA parent process {Pid} is gone: stopping the server", _pid);
            _stop();
        }
        catch (Exception ex)
        {
            // A timer callback must not throw (it would end the process).
            try { _logger.LogError(ex, "Stopping the server after the QA parent ended failed"); } catch { }
        }
    }

    // 기능: 타이머를 멈춘다.
    // 입력: 없음.
    // 출력: 반환값 없음. 더 이상 검사하지 않는다.
    public void Dispose() => _timer.Dispose();
}
