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

        // Finite and inside the snapshot range (NaN fails both comparisons).
        private static bool InRange(float value) => value >= -MaxCoordinate && value <= MaxCoordinate;
    }

    // Phase 12 D9, D11: S->C, ReliableOrdered: which doors are open (bit i = GameMap.Doors[i]). To everyone when a door
    // changes (at most once per tick) and at the round start (all closed), and to a newcomer or a resumed player.
    public static class DoorStatesPacket
    {
        public const int Size = 2;   // with the packet id

        public static void Write(ref PacketWriter writer, byte openMask)
        {
            writer.WriteByte((byte)PacketId.DoorStates);
            writer.WriteByte(openMask);
        }

        // A bit for a door the map does not have is refused.
        public static bool TryRead(ref PacketReader reader, out byte openMask)
        {
            return reader.TryReadByte(out openMask) && openMask >> GameMap.DoorCount == 0;
        }
    }
}
