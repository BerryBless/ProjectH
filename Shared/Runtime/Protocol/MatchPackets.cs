namespace ProjectH.Shared.Protocol
{
    // Phase 5 (D1): the match state machine. The values are the wire format.
    public enum MatchFlowState : byte
    {
        WaitingForPlayers = 0,
        Starting = 1,
        Playing = 2,
        FinalPhase = 3,
        Finished = 4,
        Closing = 5,
    }

    // S->C, ReliableOrdered, to everyone whenever a field changes, and to a newcomer at join (D11).
    // Before the match (WaitingForPlayers, Starting) Alive and Participants are the connected player count;
    // from Playing on they are the living participants and the participants at the start.
    // StateEndTick is the server tick the state ends at (Starting, Finished), 0 when it has no timer.
    // MinPlayers lets the client show "Waiting for players 1/2".
    public struct MatchState
    {
        public const int Size = 11;   // with the packet id

        public MatchFlowState State;
        public uint StateEndTick;
        public byte Alive;
        public byte Participants;
        public ushort Round;
        public byte MinPlayers;

        public static void Write(ref PacketWriter writer, in MatchState s)
        {
            writer.WriteByte((byte)PacketId.MatchState);
            writer.WriteByte((byte)s.State);
            writer.WriteUInt32(s.StateEndTick);
            writer.WriteByte(s.Alive);
            writer.WriteByte(s.Participants);
            writer.WriteUInt16(s.Round);
            writer.WriteByte(s.MinPlayers);
        }

        public static bool TryRead(ref PacketReader reader, out MatchState s)
        {
            s = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadByte(out byte state);
            if (state > (byte)MatchFlowState.Closing) return false;
            s.State = (MatchFlowState)state;
            reader.TryReadUInt32(out s.StateEndTick);
            reader.TryReadByte(out s.Alive);
            reader.TryReadByte(out s.Participants);
            reader.TryReadUInt16(out s.Round);
            reader.TryReadByte(out s.MinPlayers);
            return s.Alive <= s.Participants;
        }

        public bool SameAs(in MatchState other) =>
            State == other.State && StateEndTick == other.StateEndTick && Alive == other.Alive &&
            Participants == other.Participants && Round == other.Round && MinPlayers == other.MinPlayers;
    }

    // S->C, ReliableOrdered, to everyone when the zone phase changes, and to a newcomer at join (D11).
    // Phase 0 = no zone (before the match): From = To = the first circle. During phase p (1-based) the circle
    // is From until ShrinkStartTick, moves linearly to To until ShrinkEndTick, then stays To. Both sides
    // interpolate with the same formula (server SafeZone.Sample, client ZoneMath.Sample), so the circle is
    // never sent per snapshot. Centers are on the ground plane: X and Z.
    public struct ZoneState
    {
        public const int Size = 36;   // with the packet id

        public byte Phase;
        public float FromX;
        public float FromZ;
        public float FromRadius;
        public float ToX;
        public float ToZ;
        public float ToRadius;
        public uint ShrinkStartTick;
        public uint ShrinkEndTick;
        public ushort DamagePerSecond;

        public static void Write(ref PacketWriter writer, in ZoneState z)
        {
            writer.WriteByte((byte)PacketId.ZoneState);
            writer.WriteByte(z.Phase);
            writer.WriteSingle(z.FromX);
            writer.WriteSingle(z.FromZ);
            writer.WriteSingle(z.FromRadius);
            writer.WriteSingle(z.ToX);
            writer.WriteSingle(z.ToZ);
            writer.WriteSingle(z.ToRadius);
            writer.WriteUInt32(z.ShrinkStartTick);
            writer.WriteUInt32(z.ShrinkEndTick);
            writer.WriteUInt16(z.DamagePerSecond);
        }

        public static bool TryRead(ref PacketReader reader, out ZoneState z)
        {
            z = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadByte(out z.Phase);
            reader.TryReadSingle(out z.FromX);
            reader.TryReadSingle(out z.FromZ);
            reader.TryReadSingle(out z.FromRadius);
            reader.TryReadSingle(out z.ToX);
            reader.TryReadSingle(out z.ToZ);
            reader.TryReadSingle(out z.ToRadius);
            reader.TryReadUInt32(out z.ShrinkStartTick);
            reader.TryReadUInt32(out z.ShrinkEndTick);
            reader.TryReadUInt16(out z.DamagePerSecond);
            return Finite.Check(z.FromX) && Finite.Check(z.FromZ) && Finite.Check(z.FromRadius) && z.FromRadius >= 0f &&
                   Finite.Check(z.ToX) && Finite.Check(z.ToZ) && Finite.Check(z.ToRadius) && z.ToRadius >= 0f &&
                   z.ShrinkEndTick >= z.ShrinkStartTick;
        }

        public bool SameAs(in ZoneState other) =>
            Phase == other.Phase && FromX == other.FromX && FromZ == other.FromZ && FromRadius == other.FromRadius &&
            ToX == other.ToX && ToZ == other.ToZ && ToRadius == other.ToRadius &&
            ShrinkStartTick == other.ShrinkStartTick && ShrinkEndTick == other.ShrinkEndTick &&
            DamagePerSecond == other.DamagePerSecond;
    }

    // S->C, ReliableOrdered, to each participant still connected when the match finishes (D9, D11).
    // WinnerId 0 = no winner among the connected players (the last one left). Placement 1 = the winner.
    public struct MatchResult
    {
        public const int Size = 6;   // with the packet id

        public ushort WinnerId;
        public byte Placement;
        public byte Kills;
        public byte Participants;

        public static void Write(ref PacketWriter writer, in MatchResult r)
        {
            writer.WriteByte((byte)PacketId.MatchResult);
            writer.WriteUInt16(r.WinnerId);
            writer.WriteByte(r.Placement);
            writer.WriteByte(r.Kills);
            writer.WriteByte(r.Participants);
        }

        public static bool TryRead(ref PacketReader reader, out MatchResult r)
        {
            r = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadUInt16(out r.WinnerId);
            reader.TryReadByte(out r.Placement);
            reader.TryReadByte(out r.Kills);
            reader.TryReadByte(out r.Participants);
            return r.Placement >= 1 && r.Placement <= r.Participants;
        }
    }
}
