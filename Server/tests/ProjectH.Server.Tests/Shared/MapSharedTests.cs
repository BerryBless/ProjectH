using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 15 (spec "검증 계획", Shared): MapMarker and TeamMarkers round trips, limits and refusals.
public class MapSharedTests
{
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];

    // 기능: MapMarker를 쓰고 PacketId를 확인한 뒤 본문을 읽는다.
    // 입력: m - 보낼 요청.
    // 출력: 읽기 성공 여부와 읽은 요청, 쓴 길이.
    private (bool Ok, MapMarker Read, int Length) RoundTrip(in MapMarker m)
    {
        var writer = new PacketWriter(_buffer);
        MapMarker.Write(ref writer, m);
        var reader = new PacketReader(writer.WrittenSpan);
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(PacketId.MapMarker, id);
        bool ok = MapMarker.TryRead(ref reader, out MapMarker read);
        return (ok, read, writer.Length);
    }

    [Fact]
    public void MapMarker_RoundTrips_In10Bytes()
    {
        var m = new MapMarker { Kind = MapMarkerKind.Enemy, Position = new Vector3(-79.994f, 12.346f, 80f), TargetId = 7 };
        var (ok, read, length) = RoundTrip(m);
        Assert.True(ok);
        Assert.Equal(MapMarker.Size, length);
        Assert.Equal(10, MapMarker.Size);
        Assert.Equal(MapMarkerKind.Enemy, read.Kind);
        Assert.Equal((ushort)7, read.TargetId);
        Assert.Equal(-79.99f, read.Position.X, 3);
        Assert.Equal(12.35f, read.Position.Y, 3);   // 1/100 m
        Assert.Equal(80f, read.Position.Z, 3);
    }

    [Theory]
    [InlineData(MapMarkerKind.Location)]
    [InlineData(MapMarkerKind.Danger)]
    [InlineData(MapMarkerKind.WaypointSet)]
    [InlineData(MapMarkerKind.WaypointClear)]
    public void MapMarker_UntargetedKinds_RoundTrip_WithTargetZero(MapMarkerKind kind)
    {
        var (ok, read, _) = RoundTrip(new MapMarker { Kind = kind, Position = new Vector3(1f, 2f, 3f) });
        Assert.True(ok);
        Assert.Equal(kind, read.Kind);
    }

    [Theory]
    [InlineData(MapMarkerKind.Location, 5)]   // a target on a kind that has none
    [InlineData(MapMarkerKind.WaypointSet, 1)]
    [InlineData(MapMarkerKind.Enemy, 0)]      // no target on a kind that needs one
    [InlineData(MapMarkerKind.Item, 0)]
    public void MapMarker_WrongTarget_IsRefused(MapMarkerKind kind, ushort target)
    {
        Assert.False(RoundTrip(new MapMarker { Kind = kind, TargetId = target }).Ok);
    }

    [Fact]
    public void MapMarker_UnknownKind_IsRefused()
    {
        Assert.False(RoundTrip(new MapMarker { Kind = (MapMarkerKind)6 }).Ok);
        Assert.False(RoundTrip(new MapMarker { Kind = (MapMarkerKind)255 }).Ok);
    }

    [Fact]
    public void MapMarker_WrongLength_IsRefused()
    {
        var writer = new PacketWriter(_buffer);
        MapMarker.Write(ref writer, new MapMarker { Kind = MapMarkerKind.Location });
        byte[] body = writer.WrittenSpan.Slice(1).ToArray();
        var shortReader = new PacketReader(body.AsSpan(0, body.Length - 1));
        Assert.False(MapMarker.TryRead(ref shortReader, out _));
        byte[] longer = new byte[body.Length + 1];
        body.CopyTo(longer, 0);
        var longReader = new PacketReader(longer);
        Assert.False(MapMarker.TryRead(ref longReader, out _));
    }

    [Fact]
    public void Coordinates_ClampToInt16_AndNonFiniteIsZero()
    {
        Assert.Equal(short.MaxValue, MapMarkerConstants.ToWire(1e6f));
        Assert.Equal(short.MinValue, MapMarkerConstants.ToWire(-1e6f));
        Assert.Equal((short)0, MapMarkerConstants.ToWire(float.NaN));
        Assert.Equal((short)0, MapMarkerConstants.ToWire(float.PositiveInfinity));
        Assert.Equal(327.67f, MapMarkerConstants.FromWire(short.MaxValue), 3);
        Assert.Equal(-12.34f, MapMarkerConstants.Quantize(-12.3401f), 3);
    }

    // 기능: TeamMarkers를 쓰고 PacketId를 확인한 뒤 본문을 고정 배열에 읽는다.
    // 입력: pings, waypoints - 보낼 목록, outPings·outWaypoints - 읽을 배열.
    // 출력: 읽기 성공 여부, 읽은 수 두 개, 쓴 길이.
    private (bool Ok, int Pings, int Waypoints, int Length) RoundTrip(MarkerPing[] pings, MarkerWaypoint[] waypoints, MarkerPing[] outPings,
        MarkerWaypoint[] outWaypoints)
    {
        var writer = new PacketWriter(_buffer);
        TeamMarkersPacket.Write(ref writer, pings, waypoints);
        Assert.False(writer.Overflowed);
        var reader = new PacketReader(writer.WrittenSpan);
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(PacketId.TeamMarkers, id);
        bool ok = TeamMarkersPacket.TryRead(ref reader, outPings, outWaypoints, out int pc, out int wc);
        return (ok, pc, wc, writer.Length);
    }

    // 기능: 상한까지 채운 Ping·Waypoint 목록을 만든다.
    // 입력: 없음.
    // 출력: Ping 8개, Waypoint 4개.
    private static (MarkerPing[], MarkerWaypoint[]) FullLists()
    {
        var pings = new MarkerPing[MapMarkerConstants.MaxTeamPings];
        for (int i = 0; i < pings.Length; i++)
        {
            pings[i] = new MarkerPing
            {
                Id = (byte)(250 + i), Kind = (MapMarkerKind)(i % 4), OwnerId = (ushort)(i + 1), Position = new Vector3(-80f + i, 3.5f, 80f - i),
                EndTick = 100_000u + (uint)i, TargetId = (ushort)(i % 4 == 1 ? 9 : 0),
            };
        }
        var waypoints = new MarkerWaypoint[MapMarkerConstants.MaxWaypoints];
        for (int i = 0; i < waypoints.Length; i++) waypoints[i] = new MarkerWaypoint { OwnerId = (ushort)(10 + i), Position = new Vector3(i, -1f, -i) };
        return (pings, waypoints);
    }

    [Fact]
    public void TeamMarkers_Full_RoundTrips_AtMaxSize()
    {
        var (pings, waypoints) = FullLists();
        var outPings = new MarkerPing[MapMarkerConstants.MaxTeamPings];
        var outWaypoints = new MarkerWaypoint[MapMarkerConstants.MaxWaypoints];
        var (ok, pc, wc, length) = RoundTrip(pings, waypoints, outPings, outWaypoints);
        Assert.True(ok);
        Assert.Equal(163, TeamMarkersPacket.MaxSize);
        Assert.Equal(16, MarkerPing.Size);
        Assert.Equal(TeamMarkersPacket.MaxSize, length);
        Assert.Equal(8, pc);
        Assert.Equal(4, wc);
        for (int i = 0; i < pc; i++)
        {
            Assert.Equal(pings[i].Id, outPings[i].Id);
            Assert.Equal(pings[i].Kind, outPings[i].Kind);
            Assert.Equal(pings[i].OwnerId, outPings[i].OwnerId);
            Assert.Equal(pings[i].EndTick, outPings[i].EndTick);
            Assert.Equal(pings[i].TargetId, outPings[i].TargetId);
            Assert.Equal(pings[i].Position.X, outPings[i].Position.X, 3);
            Assert.Equal(pings[i].Position.Y, outPings[i].Position.Y, 3);
            Assert.Equal(pings[i].Position.Z, outPings[i].Position.Z, 3);
        }
        for (int i = 0; i < wc; i++)
        {
            Assert.Equal(waypoints[i].OwnerId, outWaypoints[i].OwnerId);
            Assert.Equal(waypoints[i].Position.Z, outWaypoints[i].Position.Z, 3);
        }
    }

    [Fact]
    public void TeamMarkers_Empty_Is3Bytes()
    {
        var (ok, pc, wc, length) = RoundTrip(Array.Empty<MarkerPing>(), Array.Empty<MarkerWaypoint>(), new MarkerPing[8], new MarkerWaypoint[4]);
        Assert.True(ok);
        Assert.Equal(3, length);
        Assert.Equal(0, pc);
        Assert.Equal(0, wc);
    }

    [Fact]
    public void TeamMarkers_Write_StopsAtTheLimits()
    {
        var pings = new MarkerPing[12];
        for (int i = 0; i < pings.Length; i++) pings[i] = new MarkerPing { OwnerId = 1 };
        var waypoints = new MarkerWaypoint[6];
        for (int i = 0; i < waypoints.Length; i++) waypoints[i] = new MarkerWaypoint { OwnerId = 1 };
        var (ok, pc, wc, length) = RoundTrip(pings, waypoints, new MarkerPing[8], new MarkerWaypoint[4]);
        Assert.True(ok);
        Assert.Equal(8, pc);
        Assert.Equal(4, wc);
        Assert.Equal(TeamMarkersPacket.MaxSize, length);
    }

    // 기능: 손으로 만든 TeamMarkers 본문을 읽는다.
    // 입력: body - PacketId 뒤 바이트, pingRoom·waypointRoom - 읽을 배열 크기.
    // 출력: 읽기 성공 여부.
    private static bool ReadBody(byte[] body, int pingRoom = 8, int waypointRoom = 4)
    {
        var reader = new PacketReader(body);
        return TeamMarkersPacket.TryRead(ref reader, new MarkerPing[pingRoom], new MarkerWaypoint[waypointRoom], out _, out _);
    }

    // 기능: Ping 하나와 Waypoint 0개인 본문을 만든다.
    // 입력: kind - Ping 종류 바이트, owner - 주인 id, x - X 좌표(1/100 m 단위 정수).
    // 출력: 본문 바이트.
    private static byte[] OnePing(byte kind, ushort owner, short x)
    {
        var b = new byte[1 + MarkerPing.Size + 1];
        b[0] = 1;
        b[1] = 3;   // id
        b[2] = kind;
        b[3] = (byte)owner;
        b[4] = (byte)(owner >> 8);
        b[5] = (byte)x;
        b[6] = (byte)(x >> 8);
        return b;   // y, z, end tick, target 0; waypoint count 0
    }

    [Fact]
    public void TeamMarkers_RefusesWhatTheServerNeverSends()
    {
        Assert.True(ReadBody(OnePing(0, 5, 100)));
        Assert.False(ReadBody(OnePing((byte)MapMarkerKind.WaypointSet, 5, 100)));   // not a ping kind
        Assert.False(ReadBody(OnePing(0, 0, 100)));                                  // no owner
        Assert.False(ReadBody(OnePing(0, 5, 8100)));                                 // 81 m: outside the map
        Assert.False(ReadBody(new byte[] { 9, 0 }));                                 // more pings than the limit
        Assert.False(ReadBody(new byte[] { 0, 5 }));                                 // more waypoints than the limit
        Assert.False(ReadBody(new byte[] { 0, 1, 1, 0 }));                           // a waypoint cut short
        Assert.False(ReadBody(new byte[] { 0, 0, 0 }));                              // trailing bytes
        Assert.False(ReadBody(new byte[] { 0 }));                                    // no waypoint count
        Assert.False(ReadBody(OnePing(0, 5, 100), pingRoom: 0));                     // more than the caller has room for
    }
}
