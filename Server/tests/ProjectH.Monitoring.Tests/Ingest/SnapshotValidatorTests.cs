using ProjectH.Monitoring.Contracts;
using ProjectH.Monitoring.Ingest;

namespace ProjectH.Monitoring.Tests.Ingest;

public class SnapshotValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    // 기능: 검증을 통과하는 Snapshot을 만든다(테스트는 필드 하나만 바꾼다).
    // 입력: serverId - 서버 이름, observedAt - 관측 시각(null = Now).
    // 출력: 모든 값이 유효한 ServerMonitoringSnapshot.
    internal static ServerMonitoringSnapshot Valid(string serverId = "dev-server-01", DateTimeOffset? observedAt = null) => new()
    {
        ServerId = serverId, Version = "1.0.0", ProtocolVersion = 18, StartedAt = Now.AddMinutes(-10), ObservedAt = observedAt ?? Now,
        UptimeSeconds = 600, WindowSeconds = 5, ConnectedPeers = 2, Players = 2, Graced = 0, MatchState = "Playing", Round = 1,
        TickSamples = 150, TickP50Ms = 0.2, TickP95Ms = 0.4, TickP99Ms = 0.8, TickMaxMs = 2, CpuPercent = 3, ManagedMemoryBytes = 50_000_000,
        WorkingSetBytes = 120_000_000, GcGen0 = 10, GcGen1 = 2, GcGen2 = 0, PacketsInPerSecond = 60, PacketsOutPerSecond = 30,
        BytesInPerSecond = 3000, BytesOutPerSecond = 9000, Exceptions = 0, InvalidPackets = 0, Disconnects = 1, DbQueueCount = 0, DbQueueCapacity = 16,
    };

    [Fact]
    public void Valid_ReturnsNull() => Assert.Null(SnapshotValidator.Validate(Valid(), Now, 300));

    [Theory]
    [InlineData("")]
    [InlineData("bad id")]
    public void BadServerId_IsRejected(string id) => Assert.Contains("serverId", SnapshotValidator.Validate(Valid(id), Now, 300));

    [Fact]
    public void MissingObservedAt_IsRejected() => Assert.Contains("observedAt", SnapshotValidator.Validate(Valid() with { ObservedAt = default }, Now, 300));

    [Theory]
    [InlineData(301)]
    [InlineData(-301)]
    public void ObservedAtOutsideClockSkew_IsRejected(int seconds) =>
        Assert.Contains("observedAt", SnapshotValidator.Validate(Valid(observedAt: Now.AddSeconds(seconds)), Now, 300));

    [Fact]
    public void ObservedAtInsideClockSkew_IsAccepted() => Assert.Null(SnapshotValidator.Validate(Valid(observedAt: Now.AddSeconds(-299)), Now, 300));

    [Fact]
    public void StartedAtAfterObservedAt_IsRejected() => Assert.Contains("startedAt", SnapshotValidator.Validate(Valid() with { StartedAt = Now.AddSeconds(1) }, Now, 300));

    [Fact]
    public void NegativeCount_IsRejected()
    {
        Assert.NotNull(SnapshotValidator.Validate(Valid() with { Players = -1 }, Now, 300));
        Assert.NotNull(SnapshotValidator.Validate(Valid() with { DbQueueCount = -1 }, Now, 300));
        Assert.NotNull(SnapshotValidator.Validate(Valid() with { WorkingSetBytes = -1 }, Now, 300));
        Assert.NotNull(SnapshotValidator.Validate(Valid() with { Exceptions = -1 }, Now, 300));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.5)]
    public void NonFiniteOrNegativeDouble_IsRejected(double value)
    {
        Assert.Contains("tickP95Ms", SnapshotValidator.Validate(Valid() with { TickP95Ms = value }, Now, 300));
        Assert.Contains("cpuPercent", SnapshotValidator.Validate(Valid() with { CpuPercent = value }, Now, 300));
        Assert.Contains("bytesOutPerSecond", SnapshotValidator.Validate(Valid() with { BytesOutPerSecond = value }, Now, 300));
    }

    [Fact]
    public void CpuAbove100_IsAccepted() => Assert.Null(SnapshotValidator.Validate(Valid() with { CpuPercent = 100.4 }, Now, 300));

    [Fact]
    public void LongText_IsRejected()
    {
        Assert.Contains("version", SnapshotValidator.Validate(Valid() with { Version = new string('v', 65) }, Now, 300));
        Assert.Contains("matchState", SnapshotValidator.Validate(Valid() with { MatchState = new string('m', 65) }, Now, 300));
    }
}
