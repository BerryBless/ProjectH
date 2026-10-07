using System.Numerics;
using ProjectH.Bots;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Tests.Bots;

// Phase 15 D10 (review Low): the bots' connection replaces its team's marker lists only with a TeamMarkers that parsed in
// full; a refused packet leaves the last lists and counts as they were.
public sealed class BotMarkerTests
{
    // 기능: TeamMarkers 본문(PacketId 뒤)을 만든다.
    // 입력: pings - Ping 목록, waypoints - Waypoint 목록.
    // 출력: 본문 바이트.
    private static byte[] Body(MarkerPing[] pings, MarkerWaypoint[] waypoints)
    {
        var buffer = new byte[ProtocolConstants.MaxPacketSize];
        var writer = new PacketWriter(buffer);
        TeamMarkersPacket.Write(ref writer, pings, waypoints);
        return writer.WrittenSpan.Slice(1).ToArray();
    }

    [Fact]
    public void ARefusedTeamMarkers_KeepsTheLastLists()
    {
        var view = new BotView();
        var pings = new MarkerPing[MapMarkerConstants.MaxTeamPings];
        var waypoints = new MarkerWaypoint[MapMarkerConstants.MaxWaypoints];
        byte[] good = Body(
            new[] { new MarkerPing { Id = 1, Kind = MapMarkerKind.Danger, OwnerId = 7, Position = new Vector3(5f, 0f, 5f) } },
            new[] { new MarkerWaypoint { OwnerId = 7, Position = new Vector3(-5f, 0f, 5f) } });
        var reader = new PacketReader(good);
        Assert.True(BotConnection.ApplyTeamMarkers(ref reader, view, pings, waypoints));

        // Two pings that parse, then a third outside the map: refused after the first two were read.
        byte[] bad = Body(
            new[]
            {
                new MarkerPing { Id = 2, Kind = MapMarkerKind.Location, OwnerId = 8, Position = new Vector3(1f, 0f, 1f) },
                new MarkerPing { Id = 3, Kind = MapMarkerKind.Location, OwnerId = 8, Position = new Vector3(2f, 0f, 2f) },
                new MarkerPing { Id = 4, Kind = MapMarkerKind.Location, OwnerId = 8, Position = new Vector3(90f, 0f, 2f) },
            },
            System.Array.Empty<MarkerWaypoint>());
        reader = new PacketReader(bad);
        Assert.False(BotConnection.ApplyTeamMarkers(ref reader, view, pings, waypoints));

        Assert.Equal(1, view.PingCount);
        Assert.Equal(1, view.WaypointCount);
        Assert.Equal(1, view.TeamMarkersReceived);
        Assert.Equal((byte)1, view.Pings[0].Id);                  // not overwritten by the refused packet
        Assert.Equal(MapMarkerKind.Danger, view.Pings[0].Kind);
        Assert.Equal((ushort)7, view.Waypoints[0].OwnerId);
    }
}
