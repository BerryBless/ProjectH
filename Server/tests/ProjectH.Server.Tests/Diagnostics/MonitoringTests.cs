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
        loop.StatsQueries.AddLimited();
        loop.StatsQueries.AddLimited();
        loop.Health.AddNetworkError();
        loop.Health.AddStallExit();
        loop.Health.AddPlayerFailure();
        loop.Health.AddConnectRateReject();
        loop.Health.AddConnectRateReject();
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
                     "rejects full=1", "badRequest=0", "version=0 connectRate=2",   // server review M2
                     "kicks kicked=0", "joinTimeout=0", "inputTimeout=1", "serverError=0", "congested=0",   // Phase 13 final review A4
                     "badPackets unknownId=0", "malformed=", "beforeJoin=", "duplicateJoin=", "inputRate=", "wrongDirection=1", "handlerException=",
                     "tickFailures=0", "loopFailures=0", "matchResets=0", "stalls=0", "movementAnomalies=0",
                     "buildRate=0",   // Phase 13 D8
                     "build pieces=0 cells=0 requests=0 accepted=0 destroyed=0 collapsed=0 duplicates=0", // Phase 13 D18
                     "buildRejects noResource=0 outOfRange=0 blocked=0 unsupported=0 occupied=0 rateLimited=0 invalidState=0 invalidRequest=0 budgetFull=0",
                     "harvest hits=0 envDestroyed=0", "syncDeferred=0", "buildInboxDrops=0",
                     "db saved=3 failed=1 discarded=2 dropped=4",
                     "stats requests=0 limited=2 busy=0 unavailable=0 undelivered=0",
                     "networkErrors=1",   // server review M1
                     "stallExits=1",   // server review M8
                     "playerFailures=1",   // server review M7
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
        var health = new HealthCounters
        {
            Persistence = () => new PersistenceCounts(5, 0, 0, 1),
            StatsQueries = () => new StatsQueryCounts(Requests: 6, Limited: 2, Busy: 1, Unavailable: 3, Undelivered: 4),
        };
        health.AddConnection();
        health.AddConnection();
        health.AddKick(DisconnectCode.Kicked);
        health.AddBadPacket(BadPacketReason.Malformed);
        health.AddDisconnect(timeout: true);
        health.SetGauges(peers: 3, players: 2, graced: 1, MatchFlowState.Playing);
        health.AddGraceExpiry();
        health.AddMovementAnomaly();
        health.AddBuildInboxDrop();
        health.AddBuildInboxDrop();
        health.AddNetworkError();
        health.AddStallExit();
        health.AddPlayerFailure();
        health.AddConnectRateReject();
        health.SetBuild(new BuildCounts(7, 3, 8, 5, 2, 10, 4, 1, 9, 2, 0, 0, 6), code => code == BuildResultCode.Occupied ? 2 : 0);

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
        Assert.Contains(("projecth.movement_anomalies", 1L, ""), seen);   // Phase 12 D12
        Assert.Contains(("projecth.build.pieces", 7L, ""), seen);         // Phase 13 D18
        Assert.Contains(("projecth.build.cells", 3L, ""), seen);
        Assert.Contains(("projecth.build.requests", 5L, "result=Ok"), seen);
        Assert.Contains(("projecth.build.requests", 2L, "result=Occupied"), seen);
        Assert.Contains(("projecth.build.destroyed", 4L, "cause=collapse"), seen);
        Assert.Contains(("projecth.build.destroyed", 6L, "cause=damage"), seen);
        Assert.Contains(("projecth.build.inbox_drops", 2L, ""), seen);
        Assert.Contains(("projecth.harvest.hits", 9L, ""), seen);
        Assert.Contains(("projecth.network_errors", 1L, ""), seen);   // server review M1
        Assert.Contains(("projecth.stall_exits", 1L, ""), seen);   // server review M8
        Assert.Contains(("projecth.player_failures", 1L, ""), seen);   // server review M7
        Assert.Contains(("projecth.rejects", 1L, "reason=ConnectRate"), seen);   // server review M2
        Assert.Contains(("projecth.stats_queries", 6L, "result=requests"), seen);
        Assert.Contains(("projecth.stats_queries", 2L, "result=limited"), seen);
        Assert.Contains(("projecth.stats_queries", 1L, "result=busy"), seen);
        Assert.Contains(("projecth.stats_queries", 3L, "result=unavailable"), seen);
        Assert.Contains(("projecth.stats_queries", 4L, "result=undelivered"), seen);
        // B7: a shutdown is not a kick, so it has no series.
        Assert.DoesNotContain(seen, s => s.Name == "projecth.kicks" && s.Tags.Contains(nameof(DisconnectCode.ServerShutdown)));
        Assert.Equal(5, seen.Count(s => s.Name == "projecth.kicks"));   // Phase 13 final review A4: Congested
        Assert.Contains(("projecth.kicks", 0L, "code=Congested"), seen);
    }

    // Final review B12: a match reset starts the match's numbers over; the counters carry the old ones, so they never go
    // back. The gauges (pieces, cells) are the new match's.
    [Fact]
    public void BuildCounters_SurviveAMatchReset()
    {
        var health = new HealthCounters();
        health.SetBuild(new BuildCounts(7, 3, 8, 5, 2, 10, 4, 1, 9, 2, 6, 11, 6, 1), code => code == BuildResultCode.Occupied ? 2 : 0);
        health.CarryBuildTotals();
        health.SetBuild(new BuildCounts(1, 1, 1, 1, 0, 0, 0, 0, 1, 0, 1, 1, 0, 0), code => code == BuildResultCode.Occupied ? 1 : 0);
        BuildCounts b = health.Build;
        Assert.Equal(1, b.Pieces);
        Assert.Equal(1, b.Cells);
        Assert.Equal(9, b.Requests);
        Assert.Equal(6, b.Accepted);
        Assert.Equal(10, b.Destroyed);
        Assert.Equal(10, b.HarvestHits);
        Assert.Equal(12, b.SyncPackets);
        Assert.Equal(1, b.SyncDeferred);
        Assert.Equal(3, health.BuildRejects(BuildResultCode.Occupied));
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

    // Server review M8: a stall that lasts FatalStallSeconds stops the server once (the callback: stop taking connections,
    // exit code 1), counted as a stall exit; 0 turns that off.
    [Fact]
    public void AStallPastTheFatalLimit_StopsTheServerOnce()
    {
        var time = new ManualTime();
        var log = new ListLogger();
        var health = new HealthCounters();
        long lastTick = time.GetTimestamp();
        int fatal = 0;
        using var watchdog = new StallWatchdog(() => lastTick, time, health, log, fatalAfter: TimeSpan.FromSeconds(30), onFatalStall: () => fatal++);

        for (int second = 1; second <= 30; second++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            watchdog.Check();
        }
        Assert.Equal(0, fatal);                      // exactly 30 s: not past the limit yet
        Assert.Equal(1, health.Stalls);

        time.Advance(TimeSpan.FromSeconds(1));
        watchdog.Check();
        Assert.Equal(1, fatal);
        Assert.Equal(1, health.StallExits);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("stopping the server"));

        for (int i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            watchdog.Check();
        }
        lastTick = time.GetTimestamp();              // even a recovery and a second long stall do not stop it again
        watchdog.Check();
        time.Advance(TimeSpan.FromSeconds(60));
        watchdog.Check();
        Assert.Equal(1, fatal);
        Assert.Equal(1, health.StallExits);
        Assert.Equal(2, health.Stalls);
    }

    [Fact]
    public void FatalStallZero_NeverStopsTheServer()
    {
        var time = new ManualTime();
        var health = new HealthCounters();
        long lastTick = time.GetTimestamp();
        int fatal = 0;
        using var watchdog = new StallWatchdog(() => lastTick, time, health, new ListLogger(), fatalAfter: TimeSpan.Zero, onFatalStall: () => fatal++);
        for (int i = 0; i < 300; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            watchdog.Check();
        }
        Assert.Equal(0, fatal);
        Assert.Equal(0, health.StallExits);
        Assert.Equal(1, health.Stalls);
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
