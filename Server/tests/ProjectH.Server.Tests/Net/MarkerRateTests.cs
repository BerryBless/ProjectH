using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Net;
using ProjectH.Server.Tests.Integration;
using ProjectH.Shared.Protocol;
using Xunit;
using static ProjectH.Server.Tests.Integration.HardeningIntegrationTests;

namespace ProjectH.Server.Tests.Net;

// Phase 15 D7, D13: MapMarker on the receive path: the per-connection token bucket (2 per second, 4 at once; more are dropped
// and counted, never kicked), the 1-second count above which packets are invalid, the join check and the Marker channel.
public class MarkerRateTests
{
    [Fact]
    public void TheBucket_StartsFull_ThenLetsTwoPerSecondThrough()
    {
        var state = new PeerState("p");
        for (int i = 0; i < 4; i++) Assert.True(state.TryTakeMarkerToken(1000, 2, 4), $"marker {i + 1}");
        Assert.False(state.TryTakeMarkerToken(1000, 2, 4));
        Assert.False(state.TryTakeMarkerToken(1499, 2, 4));
        Assert.True(state.TryTakeMarkerToken(1500, 2, 4));   // 2 per second: one every 500 ms
        Assert.False(state.TryTakeMarkerToken(1500, 2, 4));
        int taken = 0;
        for (int i = 0; i < 10; i++) if (state.TryTakeMarkerToken(60_000, 2, 4)) taken++;
        Assert.Equal(4, taken);   // a long pause refills to the burst, not beyond
    }

    [Fact]
    public void TheWindow_CountsEveryPacket_InOneSecond()
    {
        var state = new PeerState("p");
        for (int i = 0; i < 20; i++) Assert.True(state.TryCountMarkerPacket(5000, 20));
        Assert.False(state.TryCountMarkerPacket(5999, 20));
        Assert.True(state.TryCountMarkerPacket(6000, 20));   // a new window
    }

    [Fact]
    public void AFullMarkerChannel_CountsTheDroppedRequests()
    {
        var health = new HealthCounters();
        var options = new ServerOptions { MaxPlayers = 1 };
        var channels = new InboundChannels(options, new ServerStats(), null, health.AddMarkerInboxDrop);
        for (int i = 0; i < InboundChannels.MarkersPerPlayer + 2; i++) Assert.True(channels.Marker.Writer.TryWrite(default));
        Assert.Equal(2, health.MarkerInboxDrops);
    }

    // 기능: 개발 모드 서버를 띄운다(입장하면 팀이 생겨 Ping이 바로 받아들여진다).
    // 입력: 없음.
    // 출력: 시작된 GameLoop.
    private static GameLoop StartSandbox()
    {
        var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4, DevRespawn = true, StatsIntervalSeconds = 60 },
            TestGameData.Create(), NullLogger.Instance, TestGameData.CombatLoadout);
        loop.Start();
        return loop;
    }

    private static MapMarker Ping(float x) => new() { Kind = MapMarkerKind.Location, Position = new Vector3(x, 0f, 5f) };

    [Fact]
    public void APing_TravelsToTheMatch_AndTeamMarkersComeBack()
    {
        using GameLoop server = StartSandbox();
        using HeadlessClient client = Join(server, "pinger");
        client.SendMapMarker(Ping(3f));
        Assert.True(Pump.Until(() => client.TeamMarkers.Exists(m => m.Pings == 1), 3000, client), "TeamMarkers with the ping");
        Assert.Equal(3f, client.LastPings[0].Position.X, 2);
        Assert.Equal(client.MyEntityId, client.LastPings[0].OwnerId);
        Assert.Equal(0, server.Health.BadPacketsTotal);   // never WrongDirection: the listener has a MapMarker case
    }

    [Fact]
    public void ABurstOfSix_LetsFourThrough_AndDropsTwo_WithoutABadPacket()
    {
        using GameLoop server = StartSandbox();
        using HeadlessClient client = Join(server, "burst");
        for (int i = 0; i < 6; i++) client.SendMapMarker(Ping(i));
        Assert.True(Pump.Until(() => server.Health.MarkerDrops == 2, 3000, client), "two dropped");
        Assert.True(Pump.Until(() => server.Health.Map.Pings == 4, 3000, client), "four taken");
        Assert.Equal(0, server.Health.BadPackets(BadPacketReason.MarkerRate));
        Assert.Equal(1, server.Health.Map.Replaced);   // the fourth replaced the player's oldest (3 per player)
    }

    [Fact]
    public void MoreThanTwentyInOneSecond_AreInvalidPackets()
    {
        using GameLoop server = StartSandbox();
        using HeadlessClient client = Join(server, "flood");
        for (int i = 0; i < 25; i++) client.SendMapMarker(Ping(i % 10));
        Assert.True(Pump.Until(() => server.Health.BadPackets(BadPacketReason.MarkerRate) == 5, 3000, client), "five invalid");
        Assert.True(Pump.Until(() => server.Health.MarkerDrops == 16, 3000, client), "sixteen dropped by the bucket");
        Assert.False(client.Disconnected);   // 5 is below the kick threshold
    }

    [Fact]
    public void AMarkerBeforeTheJoin_AndAMalformedOne_AreInvalid()
    {
        using GameLoop server = StartSandbox();
        var early = new HeadlessClient();
        early.Connect(server.LocalPort, "early");
        Assert.True(Pump.Until(() => early.Connected, 3000, early));
        early.SendMapMarker(Ping(1f));
        Assert.True(Pump.Until(() => server.Health.BadPackets(BadPacketReason.InputBeforeJoin) == 1, 3000, early), "before join");
        early.Dispose();

        using HeadlessClient client = Join(server, "bad");
        client.SendRaw(new byte[] { (byte)PacketId.MapMarker, 0, 1, 2 });                                       // short
        client.SendRaw(new byte[] { (byte)PacketId.MapMarker, (byte)MapMarkerKind.Location, 0, 0, 0, 0, 0, 0, 7, 0 });   // target on a Location
        Assert.True(Pump.Until(() => server.Health.BadPackets(BadPacketReason.Malformed) == 2, 3000, client), "malformed");
        Assert.Equal(0, server.Health.BadPackets(BadPacketReason.WrongDirection));
    }
}
