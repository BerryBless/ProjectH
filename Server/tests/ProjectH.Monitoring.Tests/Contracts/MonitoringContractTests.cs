using ProjectH.Monitoring.Contracts;

namespace ProjectH.Monitoring.Tests.Contracts;

public class MonitoringContractTests
{
    [Theory]
    [InlineData("dev-server-01")]
    [InlineData("a")]
    [InlineData("GameServer_02.eu")]
    public void IsValidServerId_AcceptsLettersDigitsDotUnderscoreDash(string id) => Assert.True(MonitoringContract.IsValidServerId(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/id")]
    [InlineData("한글")]
    public void IsValidServerId_RejectsEmptyAndOtherCharacters(string? id) => Assert.False(MonitoringContract.IsValidServerId(id));

    [Fact]
    public void IsValidServerId_RejectsOver64Characters()
    {
        Assert.True(MonitoringContract.IsValidServerId(new string('a', 64)));
        Assert.False(MonitoringContract.IsValidServerId(new string('a', 65)));
    }

    [Fact]
    public void Snapshot_RoundTripsThroughJson_WithEveryField()
    {
        var s = new ServerMonitoringSnapshot { ServerId = "dev-server-01", Version = "1.0.0", ProtocolVersion = 18, StartedAt = DateTimeOffset.UnixEpoch,
            ObservedAt = DateTimeOffset.UnixEpoch.AddSeconds(5), UptimeSeconds = 5, WindowSeconds = 5, ConnectedPeers = 3, Players = 2, Graced = 1,
            MatchState = "Playing", Round = 4, TickSamples = 150, TickP50Ms = 0.2, TickP95Ms = 0.5, TickP99Ms = 0.9, TickMaxMs = 3.1, CpuPercent = 4.2,
            ManagedMemoryBytes = 1, WorkingSetBytes = 2, GcGen0 = 3, GcGen1 = 4, GcGen2 = 5, PacketsInPerSecond = 6, PacketsOutPerSecond = 7,
            BytesInPerSecond = 8, BytesOutPerSecond = 9, Exceptions = 10, InvalidPackets = 11, Disconnects = 12, DbQueueCount = 1, DbQueueCapacity = 16 };
        string json = System.Text.Json.JsonSerializer.Serialize(s, System.Text.Json.JsonSerializerOptions.Web);
        var back = System.Text.Json.JsonSerializer.Deserialize<ServerMonitoringSnapshot>(json, System.Text.Json.JsonSerializerOptions.Web);
        Assert.Equal(s, back);
        Assert.True(json.Length < MonitoringContract.MaxBodyBytes / 8, $"a snapshot must stay small; this one is {json.Length} bytes");
    }

    [Fact]
    public void Snapshot_WithoutServerId_FailsToDeserialize()
    {
        Assert.Throws<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<ServerMonitoringSnapshot>("{\"players\":1}", System.Text.Json.JsonSerializerOptions.Web));
    }
}
