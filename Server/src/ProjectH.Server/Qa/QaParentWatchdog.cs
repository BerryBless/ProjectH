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

    // The process is still the one that was there at the start.
    public static bool IsAlive(int pid, DateTime? startTime)
    {
        if (startTime == null) return false;
        DateTime? now = StartTimeOf(pid);
        return now != null && now.Value == startTime.Value;
    }

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

    public void Dispose() => _timer.Dispose();
}
