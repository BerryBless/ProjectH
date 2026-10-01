using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Diagnostics;

// Records every log entry (level and formatted message). Thread-safe: the loop and timer threads may log.
public sealed class ListLogger : ILogger
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate) _entries.Add((logLevel, formatter(state, exception)));
    }
}

// Phase 10 D9: the Health line, the Meter and the stall watchdog.
public class MonitoringTests
{
    [Fact]
    public void TheHealthLine_FollowsTheStatsLine_WithEveryD9Item()
    {
        var log = new ListLogger();
        using var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestGameData.Create(), log);
        loop.Health.Persistence = () => new PersistenceCounts(3, 1, 2, 4);
        loop.Health.AddReject(RejectReason.ServerFull);
        loop.Health.AddKick(DisconnectCode.InputTimeout);
        loop.Health.AddBadPacket(BadPacketReason.WrongDirection);
        loop.RunTickGuarded();

        loop.LogPeriodic();

        List<string> lines = log.Entries.Select(e => e.Message).ToList();
        int stats = lines.FindIndex(l => l.StartsWith("Stats "));
        int health = lines.FindIndex(l => l.StartsWith("Health "));
        Assert.True(stats >= 0 && health == stats + 1, string.Join("\n", lines));
        string line = lines[health];
        foreach (string item in new[]
                 {
                     "peers=0", "players=0", "graced=0", "match=WaitingForPlayers#1",
                     "connections=", "joins=", "resumed=", "graceStarts=", "graceExpiries=0", "disconnects timeout=", "other=",
                     "rejects full=1", "badRequest=0", "version=0",
                     "kicks kicked=0", "joinTimeout=0", "inputTimeout=1", "serverError=0",
                     "badPackets unknownId=0", "malformed=", "beforeJoin=", "duplicateJoin=", "inputRate=", "wrongDirection=1", "handlerException=",
                     "tickFailures=0", "loopFailures=0", "matchResets=0", "stalls=0",
                     "db saved=3 failed=1 discarded=2 dropped=4",
                 })
        {
            Assert.Contains(item, line);
        }
        // B8: "badPackets" names only the per-reason group.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(line, "badPackets"));
    }

    [Fact]
    public void TheMeter_PublishesTheHealthCounters()
    {
        var health = new HealthCounters { Persistence = () => new PersistenceCounts(5, 0, 0, 1) };
        health.AddConnection();
        health.AddConnection();
        health.AddKick(DisconnectCode.Kicked);
        health.AddBadPacket(BadPacketReason.Malformed);
        health.AddDisconnect(timeout: true);
        health.SetGauges(peers: 3, players: 2, graced: 1, MatchFlowState.Playing);
        health.AddGraceExpiry();

        using var meter = new ServerMeter(health);
        var seen = new List<(string Name, long Value, string Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ServerMeter.Name) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            seen.Add((instrument.Name, value, string.Join(",", tags.ToArray().Select(t => $"{t.Key}={t.Value}")))));
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => seen.Add((instrument.Name, value, "")));
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Contains(("projecth.connections", 2L, ""), seen);
        Assert.Contains(("projecth.peers", 3L, ""), seen);
        Assert.Contains(("projecth.graced", 1L, ""), seen);
        Assert.Contains(("projecth.kicks", 1L, "code=Kicked"), seen);
        Assert.Contains(("projecth.bad_packets", 1L, "reason=Malformed"), seen);
        Assert.Contains(("projecth.disconnects", 1L, "reason=timeout"), seen);
        Assert.Contains(("projecth.db_records", 5L, "result=saved"), seen);
        Assert.Contains(("projecth.db_records", 1L, "result=dropped"), seen);
        Assert.Contains(("projecth.match_state", (long)MatchFlowState.Playing, ""), seen);
        Assert.Contains(("projecth.grace_expiries", 1L, ""), seen);
        // B7: a shutdown is not a kick, so it has no series.
        Assert.DoesNotContain(seen, s => s.Name == "projecth.kicks" && s.Tags.Contains(nameof(DisconnectCode.ServerShutdown)));
        Assert.Equal(4, seen.Count(s => s.Name == "projecth.kicks"));
    }

    [Fact]
    public void TheWatchdog_ReportsAStallOnce_AndTheRecovery()
    {
        var time = new ManualTime();
        var log = new ListLogger();
        var health = new HealthCounters();
        long lastTick = time.GetTimestamp();
        using var watchdog = new StallWatchdog(() => lastTick, time, health, log);

        time.Advance(TimeSpan.FromSeconds(1));
        watchdog.Check();
        Assert.Equal(0, health.Stalls);

        time.Advance(TimeSpan.FromSeconds(1.5));   // 2.5 s without a tick
        watchdog.Check();
        time.Advance(TimeSpan.FromSeconds(3));
        watchdog.Check();                           // still stalled: no second report
        Assert.Equal(1, health.Stalls);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("stalled"));

        lastTick = time.GetTimestamp();             // ticks again, 5.5 s after the last one
        watchdog.Check();
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("recovered after a stall of 5500 ms"));

        time.Advance(TimeSpan.FromSeconds(0.5));
        lastTick = time.GetTimestamp();
        watchdog.Check();
        Assert.Equal(1, health.Stalls);
        Assert.Equal(2, log.Entries.Count);
    }

    // B9: a check that starts while another runs (overlapping timer callbacks) returns at once, so a stall is reported
    // and counted once. The overlap is made deterministic by re-entering from the timestamp read.
    [Fact]
    public void TheWatchdog_IgnoresAnOverlappingCheck()
    {
        var time = new ManualTime();
        var log = new ListLogger();
        var health = new HealthCounters();
        long lastTick = time.GetTimestamp();
        StallWatchdog? watchdog = null;
        int reads = 0;
        watchdog = new StallWatchdog(() =>
        {
            if (++reads == 1) watchdog!.Check();   // a second callback while the first one runs
            return lastTick;
        }, time, health, log);

        time.Advance(TimeSpan.FromSeconds(3));
        watchdog.Check();
        Assert.Equal(1, reads);   // the overlapping check did not even read the clock
        Assert.Equal(1, health.Stalls);
        Assert.Single(log.Entries);
        watchdog.Dispose();
    }

    [Fact]
    public void TheShippedSettings_HaveTheHardeningValues_AndTimestamps()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile(System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build();
        var options = new ServerOptions();
        config.GetSection("Server").Bind(options);
        Assert.Null(options.Validate());
        Assert.Equal(10, options.ReconnectGraceSeconds);
        Assert.Equal(5, options.JoinTimeoutSeconds);
        Assert.Equal(10, options.InputTimeoutSeconds);
        Assert.False(string.IsNullOrEmpty(config["Logging:Console:FormatterOptions:TimestampFormat"]));
    }
}
