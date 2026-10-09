using ProjectH.Shared.Simulation;

namespace ProjectH.Shared.Protocol
{
    // Phase 14: the squad limits both sides check packets against.
    public static class SquadConstants
    {
        public const int MaxTeamSize = 4;      // D1: ServerOptions.TeamSize is 1-4
        public const int MaxCardsHeld = 3;     // D9: squad.json maxCardsHeld is 1-3 (InventoryState.RebootCards)
    }

    // Phase 14 D2: a team member as its team sees it. Values are the wire format.
    public enum TeamMemberState : byte
    {
        Up = 0,           // alive and standing
        Downed = 1,       // knocked down (DBNO), bleeding out or being revived
        Eliminated = 2,   // out; may come back through its reboot card
        Rebooting = 3,    // out, and a teammate is using its card at a station right now
    }

    // Phase 14 D2: where an eliminated member's reboot card is (none of these = no card, or the member is in).
    [System.Flags]
    public enum TeamMemberFlags : byte
    {
        None = 0,
        CardDropped = 1,   // lies in the world (a teammate can pick it up)
        CardHeld = 2,      // a teammate carries it (it can be used at a station)
    }

    public struct TeamMember
    {
        public ushort EntityId;
        public TeamMemberState State;
        public byte Health;             // health (the downed health while Downed), rounded up to a multiple of 10; 0 when out
        public TeamMemberFlags Flags;
    }

    // Phase 14 D2: S->C, ReliableOrdered, to each team member: its own team only (never another team's). Sent at the match
    // start, at a join or resume during the match, and at the end of a tick in which a member's state, rounded health or
    // card changed. Layout: [PacketId 1][TeamId 1][Count 1] then Count x [EntityId 2][State 1][Health 1][Flags 1].
    public struct TeamState
    {
        public const int HeaderSize = 3;
        public const int MemberSize = 5;
        public const int MaxSize = HeaderSize + SquadConstants.MaxTeamSize * MemberSize;   // 23 bytes

        public byte TeamId;   // 1.. (0 is never sent: no team)
        public byte Count;    // 1..MaxTeamSize
        public TeamMember Member0;
        public TeamMember Member1;
        public TeamMember Member2;
        public TeamMember Member3;

        // 기능: index번째 구성원을 돌려준다.
        // 입력: index - 0..Count-1.
        // 출력: 그 구성원(범위 밖이면 마지막 칸).
        public TeamMember Get(int index)
        {
            switch (index)
            {
                case 0: return Member0;
                case 1: return Member1;
                case 2: return Member2;
                default: return Member3;
            }
        }

        // 기능: index번째 구성원을 바꾼다.
        // 입력: index - 0..MaxTeamSize-1, member - 넣을 구성원.
        // 출력: 반환값 없음.
        public void Set(int index, in TeamMember member)
        {
            switch (index)
            {
                case 0: Member0 = member; break;
                case 1: Member1 = member; break;
                case 2: Member2 = member; break;
                default: Member3 = member; break;
            }
        }

        // 기능: TeamState 패킷을 쓴다(Count가 상한을 넘으면 상한까지만).
        // 입력: writer - 대상, s - 팀 상태.
        // 출력: 반환값 없음. writer에 패킷이 쓰인다.
        public static void Write(ref PacketWriter writer, in TeamState s)
        {
            int count = s.Count > SquadConstants.MaxTeamSize ? SquadConstants.MaxTeamSize : s.Count;
            writer.WriteByte((byte)PacketId.TeamState);
            writer.WriteByte(s.TeamId);
            writer.WriteByte((byte)count);
            for (int i = 0; i < count; i++)
            {
                TeamMember m = s.Get(i);
                writer.WriteUInt16(m.EntityId);
                writer.WriteByte((byte)m.State);
                writer.WriteByte(m.Health);
                writer.WriteByte((byte)m.Flags);
            }
        }

        // 기능: TeamState 본문(PacketId 뒤)을 읽고 서버가 보내지 않는 값을 거절한다.
        // 입력: reader - 본문.
        // 출력: 성공하면 true와 팀 상태. 짧거나, TeamId 0·인원 0 또는 상한 초과·id 0·모르는 상태나 플래그면 false.
        public static bool TryRead(ref PacketReader reader, out TeamState s)
        {
            s = default;
            if (!reader.TryReadByte(out s.TeamId) || s.TeamId == 0) return false;
            if (!reader.TryReadByte(out s.Count) || s.Count == 0 || s.Count > SquadConstants.MaxTeamSize) return false;
            if (reader.Remaining < s.Count * MemberSize) return false;
            for (int i = 0; i < s.Count; i++)
            {
                var m = new TeamMember();
                reader.TryReadUInt16(out m.EntityId);
                reader.TryReadByte(out byte state);
                reader.TryReadByte(out m.Health);
                reader.TryReadByte(out byte flags);
                if (m.EntityId == 0 || state > (byte)TeamMemberState.Rebooting || flags > (byte)(TeamMemberFlags.CardDropped | TeamMemberFlags.CardHeld))
                    return false;
                m.State = (TeamMemberState)state;
                m.Flags = (TeamMemberFlags)flags;
                s.Set(i, m);
            }
            return true;
        }
    }

    // Phase 14 D5: S->C, ReliableOrdered, to everyone: a player was knocked down (the kill feed's "A > B downed").
    // AttackerId 0 = nobody (the zone, a fall, a QA command): Cause says which, like PlayerDied. Phase 17: Cause Explosion may
    // come with an attacker (the projectile's owner).
    public struct PlayerDowned
    {
        public const int Size = 6;   // with the packet id

        public ushort VictimId;
        public ushort AttackerId;
        public DeathCause Cause;

        // 기능: PlayerDowned 패킷을 쓴다.
        // 입력: writer - 대상, d - 기절 사건.
        // 출력: 반환값 없음.
        public static void Write(ref PacketWriter writer, in PlayerDowned d)
        {
            writer.WriteByte((byte)PacketId.PlayerDowned);
            writer.WriteUInt16(d.VictimId);
            writer.WriteUInt16(d.AttackerId);
            writer.WriteByte((byte)d.Cause);
        }

        // 기능: PlayerDowned 본문을 읽는다.
        // 입력: reader - 본문.
        // 출력: 성공하면 true와 사건, 짧거나 피해자 id 0이거나 모르는 원인이면 false.
        public static bool TryRead(ref PacketReader reader, out PlayerDowned d)
        {
            d = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadUInt16(out d.VictimId);
            reader.TryReadUInt16(out d.AttackerId);
            reader.TryReadByte(out byte cause);
            if (d.VictimId == 0 || cause > (byte)DeathCause.Explosion) return false;   // Phase 17: a knock-down by an explosion
            d.Cause = (DeathCause)cause;
            return true;
        }
    }

    // Phase 14 D8, D10: what a channel (a held E) does. Values are the wire format.
    public enum ChannelKind : byte
    {
        Revive = 0,   // Target = the downed teammate's entity id
        Reboot = 1,   // Target = the station index (RebootStations.All)
    }

    // Phase 14 D8: S->C, ReliableOrdered, to the actor's team (and a revived target, who is on it): a revive or reboot
    // started (Active, ending at EndTick, a server tick) or stopped (not Active: completed or cancelled; the snapshot and
    // TeamState tell which). Progress is EndTick against the server tick only: the client never times it on its own.
    public struct ChannelState
    {
        public const int Size = 11;   // with the packet id

        public ChannelKind Kind;
        public ushort ActorId;
        public ushort Target;
        public uint EndTick;
        public bool Active;

        // 기능: ChannelState 패킷을 쓴다.
        // 입력: writer - 대상, c - 진행 상태.
        // 출력: 반환값 없음.
        public static void Write(ref PacketWriter writer, in ChannelState c)
        {
            writer.WriteByte((byte)PacketId.ChannelState);
            writer.WriteByte((byte)c.Kind);
            writer.WriteUInt16(c.ActorId);
            writer.WriteUInt16(c.Target);
            writer.WriteUInt32(c.EndTick);
            writer.WriteByte(c.Active ? (byte)1 : (byte)0);
        }

        // 기능: ChannelState 본문을 읽는다.
        // 입력: reader - 본문.
        // 출력: 성공하면 true와 상태, 모르는 종류·행위자 id 0·없는 스테이션·대상 0인 소생·활성 값 2 이상이면 false.
        public static bool TryRead(ref PacketReader reader, out ChannelState c)
        {
            c = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadByte(out byte kind);
            reader.TryReadUInt16(out c.ActorId);
            reader.TryReadUInt16(out c.Target);
            reader.TryReadUInt32(out c.EndTick);
            reader.TryReadByte(out byte active);
            if (kind > (byte)ChannelKind.Reboot || active > 1 || c.ActorId == 0) return false;
            c.Kind = (ChannelKind)kind;
            c.Active = active == 1;
            return c.Kind == ChannelKind.Revive ? c.Target != 0 : c.Target < RebootStations.Count;
        }
    }

    // Phase 14 D10: S->C, ReliableOrdered: which reboot stations are cooling down (bit i = RebootStations.All[i]) and the
    // server tick each cooldown ends at (0 for a ready station). To everyone when a station changes (at most once per
    // tick) and at the round start, and to a newcomer or a resumed player. Layout: [PacketId 1][Mask 1][EndTick 4 x Count].
    public struct RebootStationsState
    {
        public const int Size = 2 + 4 * RebootStations.Count;   // 18 bytes with the packet id

        public byte CooldownMask;
        public uint EndTick0;
        public uint EndTick1;
        public uint EndTick2;
        public uint EndTick3;

        // 기능: i번째 스테이션의 대기 끝 Tick을 돌려준다.
        // 입력: index - 0..RebootStations.Count-1.
        // 출력: 끝 Tick(대기 중이 아니면 0).
        public uint GetEndTick(int index)
        {
            switch (index)
            {
                case 0: return EndTick0;
                case 1: return EndTick1;
                case 2: return EndTick2;
                default: return EndTick3;
            }
        }

        // 기능: i번째 스테이션의 대기 끝 Tick을 바꾼다.
        // 입력: index - 0..RebootStations.Count-1, tick - 끝 Tick.
        // 출력: 반환값 없음.
        public void SetEndTick(int index, uint tick)
        {
            switch (index)
            {
                case 0: EndTick0 = tick; break;
                case 1: EndTick1 = tick; break;
                case 2: EndTick2 = tick; break;
                default: EndTick3 = tick; break;
            }
        }

        // 기능: i번째 스테이션이 대기 중인지 마스크 비트로 본다.
        // 입력: index - 0..RebootStations.Count-1.
        // 출력: 그 비트가 켜져 있으면 true.
        public bool IsCoolingDown(int index) => (CooldownMask & (1 << index)) != 0;

        // 기능: RebootStations 패킷을 쓴다.
        // 입력: writer - 대상, s - 스테이션 상태.
        // 출력: 반환값 없음.
        public static void Write(ref PacketWriter writer, in RebootStationsState s)
        {
            writer.WriteByte((byte)PacketId.RebootStations);
            writer.WriteByte(s.CooldownMask);
            for (int i = 0; i < RebootStations.Count; i++) writer.WriteUInt32(s.GetEndTick(i));
        }

        // 기능: RebootStations 본문을 읽는다.
        // 입력: reader - 본문.
        // 출력: 성공하면 true와 상태, 짧거나 맵에 없는 스테이션의 비트가 켜져 있으면 false.
        public static bool TryRead(ref PacketReader reader, out RebootStationsState s)
        {
            s = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadByte(out s.CooldownMask);
            for (int i = 0; i < RebootStations.Count; i++)
            {
                reader.TryReadUInt32(out uint tick);
                s.SetEndTick(i, tick);
            }
            return s.CooldownMask >> RebootStations.Count == 0;
        }
    }
}
