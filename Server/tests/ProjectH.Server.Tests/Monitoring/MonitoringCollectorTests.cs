using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Monitoring.Contracts;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Monitoring;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Tests.Monitoring;

// Monitoring D3: the snapshot is built on the game loop thread from values the loop already has.
public class MonitoringCollectorTests
{
    // 기능: 켜진 설정(30 Hz)으로 수집기를 만든다.
    // 입력: time - 시계, slot - 보낼 슬롯, interval - 주기(초), db - DB 큐 (수, 용량)(null = (0, 16)).
    // 출력: StartedAt = time의 지금인 MonitoringCollector.
    private static MonitoringCollector Collector(ManualTime time, MonitoringSlot slot, int interval = 5, Func<(int, int)>? db = null) =>
        new(new MonitoringOptions { Enabled = true, ServerId = "dev-server-01", IntervalSeconds = interval }, simHz: 30, slot, time, db ?? (() => (0, 16)));

    // 기능: 게이지는 고정값, 네트워크 누적값과 Health는 인자로 받은 루프 입력을 만든다.
    // 입력: health - 오류 카운터, packetsIn·packetsOut·bytesIn·bytesOut - 시작부터의 누적값.
    // 출력: Peers 3, Players 2, Graced 1, Playing #4인 MonitoringSource.
    private static MonitoringSource Source(HealthCounters health, long packetsIn = 0, long packetsOut = 0, long bytesIn = 0, long bytesOut = 0) =>
        new(ConnectedPeers: 3, Players: 2, Graced: 1, MatchFlowState.Playing, Round: 4, packetsIn, packetsOut, bytesIn, bytesOut, health);

    [Fact]
    public void Publish_FillsEveryField_FromTheSources()
    {
        var time = new ManualTime();
        var slot = new MonitoringSlot();
        var health = new HealthCounters();
        health.AddTickFailure(); health.AddTickFailure(); health.AddLoopFailure(); health.AddPlayerFailure(); health.AddCallbackError();
        health.AddBadPacket(BadPacketReason.Malformed); health.AddBadPacket(BadPacketReason.UnknownId);
        health.AddDisconnect(timeout: true); health.AddDisconnect(timeout: false); health.AddDisconnect(timeout: false);
        var c = Collector(time, slot, db: () => (3, 16));
        foreach (double ms in new[] { 1.0, 2.0, 3.0, 4.0 }) c.RecordTick(ms);
        time.Advance(TimeSpan.FromSeconds(5));

        ServerMonitoringSnapshot s = c.Publish(Source(health, packetsIn: 500, packetsOut: 250, bytesIn: 50_000, bytesOut: 100_000));

        Assert.Same(s, slot.Take());
        Assert.Equal("dev-server-01", s.ServerId);
        Assert.Equal(c.Version, s.Version);
        Assert.NotEqual("", s.Version);
        Assert.Equal(ProtocolConstants.ProtocolVersion, s.ProtocolVersion);
        Assert.Equal(time.GetUtcNow().AddSeconds(-5), s.StartedAt);
        Assert.Equal(time.GetUtcNow(), s.ObservedAt);
        Assert.Equal(5, s.UptimeSeconds, 3);
        Assert.Equal(5, s.WindowSeconds, 3);
        Assert.Equal(3, s.ConnectedPeers); Assert.Equal(2, s.Players); Assert.Equal(1, s.Graced);
        Assert.Equal("Playing", s.MatchState); Assert.Equal(4, s.Round);
        Assert.Equal(4, s.TickSamples);
        Assert.Equal(2, s.TickP50Ms); Assert.Equal(4, s.TickP95Ms); Assert.Equal(4, s.TickP99Ms); Assert.Equal(4, s.TickMaxMs);   // nearest rank, as TickMetrics
        Assert.Equal(100, s.PacketsInPerSecond, 6); Assert.Equal(50, s.PacketsOutPerSecond, 6);
        Assert.Equal(10_000, s.BytesInPerSecond, 6); Assert.Equal(20_000, s.BytesOutPerSecond, 6);
        Assert.Equal(5, s.Exceptions);        // 2 tick + 1 loop + 1 player + 1 callback
        Assert.Equal(2, s.InvalidPackets);
        Assert.Equal(3, s.Disconnects);
        Assert.Equal(3, s.DbQueueCount); Assert.Equal(16, s.DbQueueCapacity);
        Assert.True(s.ManagedMemoryBytes > 0); Assert.True(s.WorkingSetBytes > 0);
        Assert.True(s.GcGen0 >= s.GcGen1 && s.GcGen1 >= s.GcGen2);
        Assert.True(double.IsFinite(s.CpuPercent) && s.CpuPercent >= 0);
    }

    [Fact]
    public void Rates_AreTheDeltaSinceThePreviousPublish_AndTheWindowResets()
    {
        var time = new ManualTime();
        var slot = new MonitoringSlot();
        var health = new HealthCounters();
        var c = Collector(time, slot);
        time.Advance(TimeSpan.FromSeconds(5));
        c.Publish(Source(health, packetsIn: 500));
        c.RecordTick(7);
        time.Advance(TimeSpan.FromSeconds(10));
        ServerMonitoringSnapshot s = c.Publish(Source(health, packetsIn: 600));
        Assert.Equal(10, s.WindowSeconds, 3);
        Assert.Equal(10, s.PacketsInPerSecond, 6);   // (600 - 500) / 10
        Assert.Equal(1, s.TickSamples);
        Assert.Equal(7, s.TickMaxMs);
        Assert.Equal(15, s.UptimeSeconds, 3);
    }

    [Fact]
    public void ZeroWindow_NoSamples_AndNegativeQueueCount_StillGiveFiniteNonNegativeValues()
    {
        var time = new ManualTime();
        var slot = new MonitoringSlot();
        var c = Collector(time, slot, db: () => (-1, 16));   // Channel.Reader.CanCount false → -1
        ServerMonitoringSnapshot s = c.Publish(Source(new HealthCounters()));   // no time passed, no ticks
        Assert.Equal(0, s.WindowSeconds);
        Assert.Equal(0, s.TickSamples);
        Assert.Equal(0, s.DbQueueCount);
        foreach (double v in new[] { s.CpuPercent, s.PacketsInPerSecond, s.PacketsOutPerSecond, s.BytesInPerSecond, s.BytesOutPerSecond,
                     s.TickP50Ms, s.TickP95Ms, s.TickP99Ms, s.TickMaxMs, s.UptimeSeconds })
            Assert.True(double.IsFinite(v) && v >= 0, $"{v}");
        // The monitoring server's validator (other test project) rejects NaN/Infinity; the JSON must never carry them.
        string json = System.Text.Json.JsonSerializer.Serialize(s, System.Text.Json.JsonSerializerOptions.Web);
        Assert.DoesNotContain("NaN", json);
        Assert.DoesNotContain("Infinity", json);
    }

    [Fact]
    public void IsDue_AfterTheInterval_NotBefore()
    {
        var time = new ManualTime();
        var c = Collector(time, new MonitoringSlot(), interval: 5);
        Assert.False(c.IsDue());
        time.Advance(TimeSpan.FromSeconds(4.9));
        Assert.False(c.IsDue());
        time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.True(c.IsDue());
        c.Publish(Source(new HealthCounters()));
        Assert.False(c.IsDue());
    }

    [Fact]
    public void TheDeadline_IsFixedRate_ALatePublishDoesNotDelayTheNext_AndAStallDoesNotBurst()
    {
        // Monitoring D3 ("nextStats와 같은 방식"): the game loop sees the deadline only at its next tick, so each publish is up
        // to one tick late. A deadline measured from the previous publish would slip by that much every interval and slide
        // past the sender's fixed-rate timer (now and then a sender tick finds the slot empty: a 10 s gap at the server).
        var time = new ManualTime();
        var c = Collector(time, new MonitoringSlot(), interval: 5);
        time.Advance(TimeSpan.FromSeconds(5.03));                   // one tick late
        c.Publish(Source(new HealthCounters()));
        time.Advance(TimeSpan.FromSeconds(4.97));                   // t = 10 s: the next deadline, not 10.03 s
        Assert.True(c.IsDue());
        c.Publish(Source(new HealthCounters()));
        Assert.False(c.IsDue());

        time.Advance(TimeSpan.FromSeconds(17));                     // t = 27 s: a stall past three deadlines
        Assert.True(c.IsDue());
        Assert.Equal(17, c.Publish(Source(new HealthCounters())).WindowSeconds, 3);
        Assert.False(c.IsDue());                                    // one publish, no catch-up burst
        time.Advance(TimeSpan.FromSeconds(4.9));
        Assert.False(c.IsDue());
        time.Advance(TimeSpan.FromSeconds(0.1));                    // t = 32 s: a full interval after the stall
        Assert.True(c.IsDue());
    }

    [Fact]
    public void AFullInterval_OfTicks_PlusJitter_KeepsTheFirstTick()
    {
        // 30 Hz × 5 s = 150 ticks, but the deadline is checked once per tick, so a window can hold a few more. The ring has
        // one second of headroom: the first (here the slowest) tick of the window is still in TickMaxMs.
        var time = new ManualTime();
        var c = Collector(time, new MonitoringSlot(), interval: 5);
        c.RecordTick(50);
        for (int i = 0; i < 155; i++) c.RecordTick(1);
        time.Advance(TimeSpan.FromSeconds(5));
        ServerMonitoringSnapshot s = c.Publish(Source(new HealthCounters()));
        Assert.Equal(156, s.TickSamples);
        Assert.Equal(50, s.TickMaxMs);
    }

    [Fact]
    public void TheGameLoop_PublishesEveryInterval_FromItsOwnState_AndNothingWhenOff()
    {
        var time = new ManualTime();
        var slot = new MonitoringSlot();
        var c = Collector(time, slot);
        using var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestGameData.Create(), NullLogger.Instance, time: time, monitoring: c);
        Assert.Same(c, loop.Monitoring);
        loop.RunTickGuarded();
        Assert.Null(slot.Take());                       // not due yet
        time.Advance(TimeSpan.FromSeconds(5));
        loop.RunTickGuarded();
        ServerMonitoringSnapshot s = slot.Take()!;
        Assert.Equal(0, s.ConnectedPeers);
        Assert.Equal("WaitingForPlayers", s.MatchState);
        Assert.Equal(0, s.TickSamples);                 // RecordTick lives in Run (which times the tick); RunTickGuarded does not
        loop.RunTickGuarded();
        Assert.Null(slot.Take());                       // once per interval

        using var off = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestGameData.Create(), NullLogger.Instance, time: time);
        Assert.Null(off.Monitoring);
    }

    [Fact]
    public void AThrowingPublish_IsALoopFailure_OncePerInterval_NeverATickFailure()
    {
        // Request §7: a monitoring fault must not reach the game. A tick failure would count towards a match reset (and three
        // resets stop the server); the snapshot is skipped instead, counted as a loop failure, and not retried every tick.
        var time = new ManualTime();
        var slot = new MonitoringSlot();
        var c = Collector(time, slot, db: () => throw new InvalidOperationException("db queue probe failed"));
        using var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestGameData.Create(), NullLogger.Instance, time: time, monitoring: c);
        time.Advance(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 3; i++) loop.RunTickGuarded();
        Assert.Equal(0, loop.Health.TickFailures);
        Assert.Equal(1, loop.Health.LoopFailures);
        Assert.Null(slot.Take());
        Assert.False(c.IsDue());                        // the next try is one interval later
    }
}
