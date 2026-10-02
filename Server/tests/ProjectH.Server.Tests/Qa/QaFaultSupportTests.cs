using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Server.Persistence;
using ProjectH.Server.Qa;
using Xunit;

namespace ProjectH.Server.Tests.Qa;

// QA-3: the parent watch (no orphan server) and the database numbers the fault scenarios read.
public sealed class QaFaultSupportTests
{
    private static int ExitedProcessId()
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", "--version")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.Id;
    }

    [Fact]
    public async Task ParentWatch_StopsOnce_WhenTheParentIsGone()
    {
        int stops = 0;
        using var watch = new QaParentWatchdog(ExitedProcessId(), TimeSpan.FromMilliseconds(20), () => Interlocked.Increment(ref stops),
            NullLogger.Instance);
        for (int i = 0; i < 200 && Volatile.Read(ref stops) == 0; i++) await Task.Delay(10);
        await Task.Delay(100);   // more checks run; still one stop
        Assert.Equal(1, Volatile.Read(ref stops));
        Assert.True(watch.Fired);
    }

    [Fact]
    public async Task ParentWatch_KeepsRunning_WhileTheParentLives()
    {
        int stops = 0;
        using (var watch = new QaParentWatchdog(Environment.ProcessId, TimeSpan.FromMilliseconds(20), () => Interlocked.Increment(ref stops),
                   NullLogger.Instance))
        {
            await Task.Delay(200);
            Assert.False(watch.Fired);
        }
        Assert.Equal(0, stops);
    }

    [Fact]
    public void ParentWatch_AReusedPid_CountsAsGone()
    {
        DateTime start = Process.GetCurrentProcess().StartTime;
        Assert.True(QaParentWatchdog.IsAlive(Environment.ProcessId, start));
        Assert.False(QaParentWatchdog.IsAlive(Environment.ProcessId, start.AddSeconds(-1)));
        Assert.False(QaParentWatchdog.IsAlive(Environment.ProcessId, null));
    }

    [Fact]
    public void ParentPid_Validation()
    {
        Assert.Null(new QaOptions { ParentPid = 1234 }.Validate());
        Assert.NotNull(new QaOptions { ParentPid = -5 }.Validate());
    }

    [Fact]
    public void HistoryQueue_CountsWaitingRecords()
    {
        var queue = new MatchHistoryQueue(2);
        Assert.Equal(0, queue.Count);
        var record = new MatchRecord(1, DateTime.UtcNow, DateTime.UtcNow, null, Array.Empty<PlayerRecord>());
        queue.TryEnqueue(record);
        queue.TryEnqueue(record);
        queue.TryEnqueue(record);
        Assert.Equal(2, queue.Count);
        Assert.Equal(1, queue.Dropped);
        queue.Reader.TryRead(out _);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task HealthAndMetrics_ReportTheDatabase()
    {
        using var h = new QaHarness();
        h.Qa.Database = () => new QaDbStatus(true, 3, 1, 0, 2, 5);
        var (_, health) = await h.Run(t => QaResult.Data(QaQueries.Health(t)));
        Assert.Equal(5, health.GetProperty("dbQueueLength").GetInt32());
        Assert.Equal(3, health.GetProperty("db").GetProperty("saved").GetInt64());
        Assert.True(health.GetProperty("db").GetProperty("enabled").GetBoolean());
    }
}
