using System;
using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Shared.Protocol
{
    // Phase 15 D7: what a map marker request asks for. Values are the wire format. 0-3 are team pings (also the kinds a
    // TeamMarkers ping carries), 4-5 set or clear the sender's own waypoint.
    public enum MapMarkerKind : byte
    {
        Location = 0,        // a place (terrain, a building, a piece)
        Enemy = 1,           // TargetId = an enemy's entity id; the server checks it (a failed check becomes Location)
        Item = 2,            // TargetId = a world item's id (WorldItemData.ItemId); a failed check drops the request
        Danger = 3,          // a place to stay away from
        WaypointSet = 4,     // the sender's one waypoint (moved if it already has one)
        WaypointClear = 5,   // remove the sender's waypoint (the position is ignored)
    }

    // Phase 15 D7, D9, D10: the map marker limits both sides check packets against, and the wire form of a position.
    public static class MapMarkerConstants
    {
        public const int MaxTeamPings = 8;                            // D9: a team's active pings (TeamMarkers)
        public const int MaxWaypoints = SquadConstants.MaxTeamSize;   // D5: one per team member
        public const float CoordinateScale = 100f;                    // 1/100 m: int16 covers +-327.67 m
        // TeamMarkers positions stay inside the map (the server refuses anything outside it); one unit of slack for rounding.
        public const float MaxHorizontal = GameMap.HalfSize + 0.01f;

        // 기능: 월드 좌표 한 축을 1/100 m int16으로 바꾼다(범위 밖은 끝값으로 자르고, NaN·무한대는 0).
        // 입력: value - 미터 단위 좌표.
        // 출력: 보낼 int16 값.
        public static short ToWire(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0;
            float scaled = (float)Math.Round(value * CoordinateScale);
            if (scaled > short.MaxValue) return short.MaxValue;
            if (scaled < short.MinValue) return short.MinValue;
            return (short)scaled;
        }

        // 기능: 1/100 m int16을 미터로 바꾼다.
        // 입력: raw - 받은 값.
        // 출력: 미터 단위 좌표.
        public static float FromWire(short raw) => raw / CoordinateScale;

        // 기능: 받는 쪽이 읽게 될 값으로 좌표를 맞춘다(테스트·서버가 보낸 값과 같은 위치를 쓰려고).
        // 입력: value - 미터 단위 좌표.
        // 출력: 왕복한 좌표.
        public static float Quantize(float value) => FromWire(ToWire(value));

        // 기능: 한 위치를 PacketWriter에 x·y·z int16으로 쓴다.
        // 입력: writer - 대상, position - 월드 위치.
        // 출력: 반환값 없음. 6바이트가 쓰인다.
        public static void WritePosition(ref PacketWriter writer, Vector3 position)
        {
            writer.WriteUInt16(unchecked((ushort)ToWire(position.X)));
            writer.WriteUInt16(unchecked((ushort)ToWire(position.Y)));
            writer.WriteUInt16(unchecked((ushort)ToWire(position.Z)));
        }

        // 기능: x·y·z int16 위치를 읽는다(호출자가 길이를 먼저 확인한다).
        // 입력: reader - 본문.
        // 출력: 읽은 위치.
        public static Vector3 ReadPosition(ref PacketReader reader)
        {
            reader.TryReadUInt16(out ushort x);
            reader.TryReadUInt16(out ushort y);
            reader.TryReadUInt16(out ushort z);
            return new Vector3(FromWire(unchecked((short)x)), FromWire(unchecked((short)y)), FromWire(unchecked((short)z)));
        }

        // 기능: 종류가 팀 Ping(Location·Enemy·Item·Danger)인지 본다.
        // 입력: kind - 종류.
        // 출력: Ping 종류면 true(Waypoint 설정·삭제는 false).
        public static bool IsPing(MapMarkerKind kind) => kind <= MapMarkerKind.Danger;
    }

    // Phase 15 D7: C->S, ReliableOrdered on channel 0: one ping, or setting or clearing the sender's waypoint. The position
    // is in 1/100 m (int16, so at most +-327 m: a client cannot send a huge coordinate); the server checks it lies inside
    // the map and clamps the height. TargetId: Enemy = the entity id, Item = the item id, every other kind 0.
    // Layout: [PacketId 1][Kind 1][X 2][Y 2][Z 2][TargetId 2] = 10 bytes (the spec's "11 B" counted one byte too many).
    public struct MapMarker
    {
        public const int Size = 10;   // with the packet id

        public MapMarkerKind Kind;
        public Vector3 Position;
        public ushort TargetId;

        // 기능: MapMarker 패킷을 쓴다(좌표는 1/100 m로 자른다).
        // 입력: writer - 대상, m - 요청.
        // 출력: 반환값 없음. writer에 10바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in MapMarker m)
        {
            writer.WriteByte((byte)PacketId.MapMarker);
            writer.WriteByte((byte)m.Kind);
            MapMarkerConstants.WritePosition(ref writer, m.Position);
            writer.WriteUInt16(m.TargetId);
        }

        // 기능: MapMarker 본문(PacketId 뒤)을 읽고 형식만 확인한다. 맵 안인지·대상이 맞는지는 서버(Match)가 본다.
        // 입력: reader - 본문.
        // 출력: 성공하면 true와 요청. 길이가 정확히 9바이트가 아니거나, 모르는 종류이거나, Enemy·Item인데 대상 0이거나
        //   다른 종류인데 대상이 0이 아니면 false.
        public static bool TryRead(ref PacketReader reader, out MapMarker m)
        {
            m = default;
            if (reader.Remaining != Size - 1) return false;
            reader.TryReadByte(out byte kind);
            m.Position = MapMarkerConstants.ReadPosition(ref reader);
            reader.TryReadUInt16(out m.TargetId);
            if (kind > (byte)MapMarkerKind.WaypointClear) return false;
            m.Kind = (MapMarkerKind)kind;
            bool targeted = m.Kind == MapMarkerKind.Enemy || m.Kind == MapMarkerKind.Item;
            return targeted ? m.TargetId != 0 : m.TargetId == 0;
        }
    }

    // Phase 15 D10: one active team ping as TeamMarkers carries it. Id is the team's serial number for the ping (1-255,
    // wrapping; it tells a new ping from an old one), EndTick the server tick it disappears at. TargetId: the Enemy's
    // entity id or the Item's id it was placed on (0 for Location and Danger, and for an Enemy check that failed).
    // 16 bytes on the wire (the spec's "15 B" counted one byte too few).
    public struct MarkerPing
    {
        public const int Size = 16;

        public byte Id;
        public MapMarkerKind Kind;
        public ushort OwnerId;
        public Vector3 Position;
        public uint EndTick;
        public ushort TargetId;
    }

    // Phase 15 D5, D10: a team member's waypoint.
    public struct MarkerWaypoint
    {
        public const int Size = 8;

        public ushort OwnerId;
        public Vector3 Position;
    }

    // Phase 15 D10: S->C, ReliableOrdered on channel 0, to each team member: every active ping and waypoint of its own team
    // (never another team's). Sent at the end of a tick in which the team's markers changed (a ping added or expired, a
    // waypoint set, moved or cleared, a member left), and at a join or resume. The client replaces what it had with the
    // list (idempotent, order does not matter).
    // Layout: [PacketId 1][PingCount 1] PingCount x [Id 1][Kind 1][OwnerId 2][X 2][Y 2][Z 2][EndTick 4][TargetId 2]
    //         [WaypointCount 1] WaypointCount x [OwnerId 2][X 2][Y 2][Z 2].
    public static class TeamMarkersPacket
    {
        public const int MaxSize = 3 + MapMarkerConstants.MaxTeamPings * MarkerPing.Size + MapMarkerConstants.MaxWaypoints * MarkerWaypoint.Size;   // 163

        // 기능: TeamMarkers 패킷을 쓴다(목록이 상한보다 길면 상한까지만).
        // 입력: writer - 대상, pings - 팀의 활성 Ping, waypoints - 팀원의 Waypoint.
        // 출력: 반환값 없음. writer에 패킷이 쓰인다(최대 MaxSize = 163바이트).
        public static void Write(ref PacketWriter writer, ReadOnlySpan<MarkerPing> pings, ReadOnlySpan<MarkerWaypoint> waypoints)
        {
            int pingCount = Math.Min(pings.Length, MapMarkerConstants.MaxTeamPings);
            int waypointCount = Math.Min(waypoints.Length, MapMarkerConstants.MaxWaypoints);
            writer.WriteByte((byte)PacketId.TeamMarkers);
            writer.WriteByte((byte)pingCount);
            for (int i = 0; i < pingCount; i++)
            {
                MarkerPing p = pings[i];
                writer.WriteByte(p.Id);
                writer.WriteByte((byte)p.Kind);
                writer.WriteUInt16(p.OwnerId);
                MapMarkerConstants.WritePosition(ref writer, p.Position);
                writer.WriteUInt32(p.EndTick);
                writer.WriteUInt16(p.TargetId);
            }
            writer.WriteByte((byte)waypointCount);
            for (int i = 0; i < waypointCount; i++)
            {
                writer.WriteUInt16(waypoints[i].OwnerId);
                MapMarkerConstants.WritePosition(ref writer, waypoints[i].Position);
            }
        }

        // 기능: TeamMarkers 본문(PacketId 뒤)을 호출자가 가진 고정 배열에 읽는다(할당 없음). 서버가 보내지 않는 값은 거절한다.
        // 입력: reader - 본문, pings - MaxTeamPings 칸 이상, waypoints - MaxWaypoints 칸 이상.
        // 출력: 성공하면 true와 pingCount·waypointCount(앞쪽 칸에 채워진다). 짧거나 길거나, 수가 상한·배열을 넘거나,
        //   Ping 종류가 아니거나, 주인 id 0이거나, 맵 밖 좌표면 false(배열 내용은 쓰다 만 상태일 수 있다).
        public static bool TryRead(ref PacketReader reader, Span<MarkerPing> pings, Span<MarkerWaypoint> waypoints, out int pingCount,
            out int waypointCount)
        {
            pingCount = 0;
            waypointCount = 0;
            if (!reader.TryReadByte(out byte pc) || pc > MapMarkerConstants.MaxTeamPings || pc > pings.Length) return false;
            if (reader.Remaining < pc * MarkerPing.Size + 1) return false;
            for (int i = 0; i < pc; i++)
            {
                var p = new MarkerPing();
                reader.TryReadByte(out p.Id);
                reader.TryReadByte(out byte kind);
                reader.TryReadUInt16(out p.OwnerId);
                p.Position = MapMarkerConstants.ReadPosition(ref reader);
                reader.TryReadUInt32(out p.EndTick);
                reader.TryReadUInt16(out p.TargetId);
                if (kind > (byte)MapMarkerKind.Danger || p.OwnerId == 0 || !InMap(p.Position)) return false;
                p.Kind = (MapMarkerKind)kind;
                pings[i] = p;
            }
            reader.TryReadByte(out byte wc);
            if (wc > MapMarkerConstants.MaxWaypoints || wc > waypoints.Length || reader.Remaining != wc * MarkerWaypoint.Size) return false;
            for (int i = 0; i < wc; i++)
            {
                var w = new MarkerWaypoint();
                reader.TryReadUInt16(out w.OwnerId);
                w.Position = MapMarkerConstants.ReadPosition(ref reader);
                if (w.OwnerId == 0 || !InMap(w.Position)) return false;
                waypoints[i] = w;
            }
            pingCount = pc;
            waypointCount = wc;
            return true;
        }

        // 기능: 수평 좌표가 맵 안(±HalfSize, 반올림 여유 포함)인지 본다.
        // 입력: position - 위치.
        // 출력: 안이면 true.
        private static bool InMap(Vector3 position) =>
            Math.Abs(position.X) <= MapMarkerConstants.MaxHorizontal && Math.Abs(position.Z) <= MapMarkerConstants.MaxHorizontal;
    }
}
