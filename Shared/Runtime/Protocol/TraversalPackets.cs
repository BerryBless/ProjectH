using ProjectH.Shared.Simulation;

namespace ProjectH.Shared.Protocol
{
    // Phase 12 D5, D11: S->C, ReliableOrdered: this match's drop transport route, once at the match start (before the
    // PlayerRespawned events that put everyone aboard) and to a newcomer or a resumed player during the match. Both
    // sides compute the transport's position from it (DropRoute.PositionAt); it is never sent per tick.
    public static class TransportRoutePacket
    {
        public const int Size = 29;   // with the packet id: 5 floats + 2 x uint32
        // Sanity limits for the client: the route stays well inside the snapshot's +-128 m range and lasts at most 10 min.
        public const float MaxCoordinate = 127f;
        public const uint MaxDurationTicks = 128 * 600;

        // 기능: 수송기 경로를 TransportRoute 패킷(id + 5 float + 2 uint32)으로 쓴다.
        // 입력: writer - 쓸 Writer, route - 이 매치의 투입 경로.
        // 출력: 반환값 없음. writer에 Size바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in DropRoute route)
        {
            writer.WriteByte((byte)PacketId.TransportRoute);
            writer.WriteSingle(route.StartX);
            writer.WriteSingle(route.StartZ);
            writer.WriteSingle(route.EndX);
            writer.WriteSingle(route.EndZ);
            writer.WriteSingle(route.Altitude);
            writer.WriteUInt32(route.StartTick);
            writer.WriteUInt32(route.DurationTicks);
        }

        // 기능: 패킷 id 다음부터 TransportRoute 본문을 읽고 Client가 믿어도 되는 범위인지 검사한다.
        // 입력: reader - 패킷 id를 지난 Reader, route - 읽은 경로를 받을 변수.
        // 출력: 본문이 충분하고 좌표가 ±MaxCoordinate 안, 고도 0 이상, 길이 1..MaxDurationTicks면 true와 경로, 아니면 false.
        public static bool TryRead(ref PacketReader reader, out DropRoute route)
        {
            route = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadSingle(out route.StartX);
            reader.TryReadSingle(out route.StartZ);
            reader.TryReadSingle(out route.EndX);
            reader.TryReadSingle(out route.EndZ);
            reader.TryReadSingle(out route.Altitude);
            reader.TryReadUInt32(out route.StartTick);
            reader.TryReadUInt32(out route.DurationTicks);
            return InRange(route.StartX) && InRange(route.StartZ) && InRange(route.EndX) && InRange(route.EndZ) &&
                   InRange(route.Altitude) && route.Altitude >= 0f &&
                   route.DurationTicks >= 1 && route.DurationTicks <= MaxDurationTicks;
        }

        // 기능: 좌표가 유한하고 Snapshot 범위(±MaxCoordinate) 안인지 본다.
        // 입력: value - 검사할 좌표.
        // 출력: 범위 안이면 true(NaN·Infinity는 false).
        // Finite and inside the snapshot range (NaN fails both comparisons).
        private static bool InRange(float value) => value >= -MaxCoordinate && value <= MaxCoordinate;
    }

    // Phase 12 D9, D11: S->C, ReliableOrdered: which doors are open (bit i = GameMap.Doors[i]). To everyone when a door
    // changes (at most once per tick) and at the round start (all closed), and to a newcomer or a resumed player.
    public static class DoorStatesPacket
    {
        public const int Size = 2;   // with the packet id

        // 기능: 열린 문 비트 마스크를 DoorStates 패킷(id + 1바이트)으로 쓴다.
        // 입력: writer - 쓸 Writer, openMask - 비트 i가 GameMap.Doors[i]의 열림 여부.
        // 출력: 반환값 없음. writer에 2바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, byte openMask)
        {
            writer.WriteByte((byte)PacketId.DoorStates);
            writer.WriteByte(openMask);
        }

        // 기능: 패킷 id 다음의 열린 문 비트 마스크를 읽는다.
        // 입력: reader - 패킷 id를 지난 Reader, openMask - 읽은 마스크를 받을 변수.
        // 출력: 바이트가 있고 맵에 없는 문의 비트가 꺼져 있으면 true와 마스크, 아니면 false.
        // A bit for a door the map does not have is refused.
        public static bool TryRead(ref PacketReader reader, out byte openMask)
        {
            return reader.TryReadByte(out openMask) && openMask >> GameMap.DoorCount == 0;
        }
    }
}
