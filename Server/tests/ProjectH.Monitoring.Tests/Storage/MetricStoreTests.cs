using ProjectH.Monitoring.Contracts;
using ProjectH.Monitoring.Storage;
using ProjectH.Monitoring.Tests.Ingest;

namespace ProjectH.Monitoring.Tests.Storage;

public class MetricStoreTests
{
    // 기능: 작은 링(1분 / 30초 = 2개)과 60초 Offline 기준의 설정을 만든다.
    // 입력: maxServers - 등록할 수 있는 서버 수.
    // 출력: 유효한 MonitoringServerOptions.
    private static MonitoringServerOptions Small(int maxServers = 32) =>
        new() { HistoryMinutes = 1, ExpectedIntervalSeconds = 30, OfflineThresholdSeconds = 60, MaxServers = maxServers };

    // 기능: 테스트 시계의 지금 관측된 유효한 Snapshot을 만든다.
    // 입력: time - 테스트 시계, id - 서버 이름, p95 - Tick P95(ms).
    // 출력: ObservedAt = 지금, StartedAt = 1분 전인 Snapshot.
    private static ServerMonitoringSnapshot At(ManualTime time, string id = "s1", double p95 = 0.4) =>
        SnapshotValidatorTests.Valid(id, time.GetUtcNow()) with { StartedAt = time.GetUtcNow().AddMinutes(-1), TickP95Ms = p95 };

    [Fact]
    public void FirstSnapshot_RegistersTheServer_AndIsTheLatest()
    {
        var time = new ManualTime();
        var store = new MetricStore(Small());
        Assert.Equal(IngestOutcome.FirstSeen, store.Add(At(time), time.GetUtcNow()));
        Assert.Equal(1, store.Count);
        ServerSummary s = Assert.Single(store.List(time.GetUtcNow()));
        Assert.Equal("s1", s.ServerId);
        Assert.Equal(ServerState.Online, s.Status);
        Assert.Equal(time.GetUtcNow(), s.LastReceivedAt);
        Assert.Empty(s.Warnings);
    }

    [Fact]
    public void SecondSnapshot_IsStored_AndReplacesTheLatest()
    {
        var time = new ManualTime();
        var store = new MetricStore(Small());
        store.Add(At(time), time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(IngestOutcome.Stored, store.Add(At(time) with { Players = 7 }, time.GetUtcNow()));
        Assert.Equal(7, store.Get("s1", time.GetUtcNow())!.Latest.Players);
    }

    [Fact]
    public void Ring_KeepsOnlyTheNewestCapacitySamples_InOrder()
    {
        var time = new ManualTime();
        var store = new MetricStore(Small());   // capacity 2
        for (int i = 1; i <= 3; i++)
        {
            store.Add(At(time) with { Round = i }, time.GetUtcNow());
            time.Advance(TimeSpan.FromSeconds(10));
        }
        IReadOnlyList<MetricSample> h = store.History("s1", 1, time.GetUtcNow())!;
        Assert.Equal(new[] { 2, 3 }, h.Select(s => s.Snapshot.Round));
        Assert.True(h[0].ReceivedAt < h[1].ReceivedAt);
    }

    [Fact]
    public void History_ReturnsOnlyTheRequestedMinutes_AndClampsTheRequest()
    {
        var time = new ManualTime();
        var store = new MetricStore(new MonitoringServerOptions { HistoryMinutes = 10, ExpectedIntervalSeconds = 5, OfflineThresholdSeconds = 15 });
        for (int i = 0; i < 12; i++)   // one per minute for 11 minutes
        {
            store.Add(At(time) with { Round = i }, time.GetUtcNow());
            time.Advance(TimeSpan.FromMinutes(1));
        }
        // now = 12 min; the last sample was at 11 min. 2 minutes back = samples at 10 and 11.
        Assert.Equal(new[] { 10, 11 }, store.History("s1", 2, time.GetUtcNow())!.Select(s => s.Snapshot.Round));
        // 9999 clamps to HistoryMinutes (10): samples at 2..11.
        Assert.Equal(10, store.History("s1", 9999, time.GetUtcNow())!.Count);
        // 0 clamps to 1: the sample at 11 only.
        Assert.Equal(new[] { 11 }, store.History("s1", 0, time.GetUtcNow())!.Select(s => s.Snapshot.Round));
        Assert.Null(store.History("unknown", 1, time.GetUtcNow()));
    }

    [Fact]
    public void Servers_AreSeparated_AndListedByName()
    {
        var time = new ManualTime();
        var store = new MetricStore(Small());
        store.Add(At(time, "zeta") with { Players = 1 }, time.GetUtcNow());
        store.Add(At(time, "alpha") with { Players = 2 }, time.GetUtcNow());
        IReadOnlyList<ServerSummary> list = store.List(time.GetUtcNow());
        Assert.Equal(new[] { "alpha", "zeta" }, list.Select(s => s.ServerId));
        Assert.Equal(2, store.Get("alpha", time.GetUtcNow())!.Latest.Players);
        Assert.Equal(1, store.Get("zeta", time.GetUtcNow())!.Latest.Players);
        Assert.Single(store.History("alpha", 1, time.GetUtcNow())!);
    }

    [Fact]
    public void MaxServers_RefusesANewServer_ButNotAKnownOne()
    {
        var time = new ManualTime();
        var store = new MetricStore(Small(maxServers: 1));
        Assert.Equal(IngestOutcome.FirstSeen, store.Add(At(time, "s1"), time.GetUtcNow()));
        Assert.Equal(IngestOutcome.TooManyServers, store.Add(At(time, "s2"), time.GetUtcNow()));
        Assert.Equal(IngestOutcome.Stored, store.Add(At(time, "s1"), time.GetUtcNow()));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Offline_AfterTheThreshold_WithLastSeen_AndSweepReportsItOnce()
    {
        var time = new ManualTime();
        var store = new MetricStore(Small());
        store.Add(At(time), time.GetUtcNow());
        DateTimeOffset lastSeen = time.GetUtcNow();
        time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(ServerState.Online, store.Get("s1", time.GetUtcNow())!.Status);   // exactly the threshold: still online
        Assert.Empty(store.SweepOffline(time.GetUtcNow()));
        time.Advance(TimeSpan.FromSeconds(1));
        ServerSummary s = store.Get("s1", time.GetUtcNow())!;
        Assert.Equal(ServerState.Offline, s.Status);
        Assert.Equal(lastSeen, s.LastReceivedAt);
        Assert.Contains(s.Warnings, w => w.StartsWith("offline"));
        var swept = Assert.Single(store.SweepOffline(time.GetUtcNow()));
        Assert.Equal(("s1", lastSeen), swept);
        Assert.Empty(store.SweepOffline(time.GetUtcNow()));   // logged once
    }

    [Fact]
    public void ASnapshotAfterOffline_IsReturned_AndSweepCanFireAgainLater()
    {
        var time = new ManualTime();
        var store = new MetricStore(Small());
        store.Add(At(time), time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(61));
        store.SweepOffline(time.GetUtcNow());
        Assert.Equal(IngestOutcome.Returned, store.Add(At(time), time.GetUtcNow()));
        Assert.Equal(ServerState.Online, store.Get("s1", time.GetUtcNow())!.Status);
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Single(store.SweepOffline(time.GetUtcNow()));
    }

    [Fact]
    public void Returned_IsDecidedByTheGap_EvenWithoutASweep()
    {
        var time = new ManualTime();
        var store = new MetricStore(Small());
        store.Add(At(time), time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(IngestOutcome.Returned, store.Add(At(time), time.GetUtcNow()));
    }

    [Fact]
    public void Warning_WhenTickP95OrMemoryReachesTheThreshold()
    {
        var time = new ManualTime();
        var o = Small();
        o.TickP95WarningMs = 16.7;
        o.MemoryWarningBytes = 1_000_000_000;
        var store = new MetricStore(o);
        store.Add(At(time, p95: 16.7), time.GetUtcNow());
        ServerSummary s = store.Get("s1", time.GetUtcNow())!;
        Assert.Equal(ServerState.Warning, s.Status);
        Assert.Contains(s.Warnings, w => w.Contains("tick p95"));
        store.Add(At(time, p95: 1) with { WorkingSetBytes = 1_000_000_000 }, time.GetUtcNow());
        s = store.Get("s1", time.GetUtcNow())!;
        Assert.Equal(ServerState.Warning, s.Status);
        Assert.Contains(s.Warnings, w => w.Contains("working set"));
        store.Add(At(time, p95: 1), time.GetUtcNow());
        Assert.Equal(ServerState.Online, store.Get("s1", time.GetUtcNow())!.Status);
    }

    [Fact]
    public void MemoryWarningOff_NeverWarnsOnMemory()
    {
        var time = new ManualTime();
        var store = new MetricStore(Small());   // MemoryWarningBytes 0
        store.Add(At(time) with { WorkingSetBytes = long.MaxValue }, time.GetUtcNow());
        Assert.Equal(ServerState.Online, store.Get("s1", time.GetUtcNow())!.Status);
    }

    [Fact]
    public void ConcurrentAdds_FromManyServers_LoseNothing()
    {
        var time = new ManualTime();
        var store = new MetricStore(new MonitoringServerOptions { HistoryMinutes = 10, ExpectedIntervalSeconds = 1, OfflineThresholdSeconds = 15, MaxServers = 64 });
        Parallel.For(0, 32, server =>
        {
            for (int i = 0; i < 100; i++) store.Add(At(time, $"s{server}") with { Round = i }, time.GetUtcNow());
        });
        Assert.Equal(32, store.Count);
        foreach (ServerSummary s in store.List(time.GetUtcNow()))
        {
            Assert.Equal(99, s.Latest.Round);
            Assert.Equal(100, store.History(s.ServerId, 10, time.GetUtcNow())!.Count);
        }
    }
}
