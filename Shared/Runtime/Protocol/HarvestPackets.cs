using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Shared.Protocol
{
    // Phase 13 D5: what a character has in hand. Values are the wire format (snapshot flag bits 6-7, the self block's
    // weapon slot byte bits 6-7): never renumber.
    public enum ToolKind : byte
    {
        Weapon = 0,
        Harvest = 1,
        Build = 2,
    }

    // Phase 13 D15: S->C, ReliableOrdered, to its owner only: the three building resources, at the end of a tick in which
    // they changed and at a join or resume. Not in the snapshot (they change only on a hit, a build or a pickup).
    public struct ResourcesState
    {
        public const int Size = 7;   // with the packet id

        public ushort Wood;
        public ushort Stone;
        public ushort Metal;

        // 기능: 건설 재료 종류에 해당하는 보유량을 돌려준다.
        // 입력: material - Wood, Stone, Metal(그 밖의 값은 Metal로 본다).
        // 출력: 그 재료의 보유량.
        public ushort Get(BuildMaterialType material)
        {
            switch (material)
            {
                case BuildMaterialType.Wood: return Wood;
                case BuildMaterialType.Stone: return Stone;
                default: return Metal;
            }
        }

        // 기능: ResourcesState 패킷(id + 재료 세 가지 uint16)을 쓴다.
        // 입력: writer - 대상, s - 세 재료의 보유량.
        // 출력: 반환값 없음. writer에 Size(7)바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in ResourcesState s)
        {
            writer.WriteByte((byte)PacketId.ResourcesState);
            writer.WriteUInt16(s.Wood);
            writer.WriteUInt16(s.Stone);
            writer.WriteUInt16(s.Metal);
        }

        // 기능: ResourcesState 본문(PacketId 뒤)을 읽는다.
        // 입력: reader - 본문, s - 읽은 보유량을 받을 변수.
        // 출력: 본문이 6바이트 이상이면 true와 보유량, 짧으면 false.
        public static bool TryRead(ref PacketReader reader, out ResourcesState s)
        {
            s = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadUInt16(out s.Wood);
            reader.TryReadUInt16(out s.Stone);
            reader.TryReadUInt16(out s.Metal);
            return true;
        }
    }

    // Phase 13 D7: S->C, ReliableOrdered, to the player whose swing hit a harvestable: what the hit did. The client never
    // names a target or a weak point (request §12, §20); this only tells it what the server decided.
    public struct HarvestHit
    {
        public const int Size = 18;   // with the packet id
        public const byte WeakPointHitFlag = 1;
        public const byte DestroyedFlag = 2;
        public const byte HasWeakPointFlag = 4;

        public byte TargetId;          // GameMap.Harvestables index
        public ushort Health;          // left after the hit
        public Vector3 WeakPoint;      // where the weak point is now (HasWeakPointFlag)
        public byte Gained;            // resources added (the target's material), after the cap
        public byte Flags;

        public bool WeakPointHit => (Flags & WeakPointHitFlag) != 0;
        public bool Destroyed => (Flags & DestroyedFlag) != 0;
        public bool HasWeakPoint => (Flags & HasWeakPointFlag) != 0;

        // 기능: HarvestHit 패킷(id, 대상, 남은 체력, 약점 위치, 얻은 자원, 플래그)을 쓴다.
        // 입력: writer - 대상, h - 채집 타격 결과.
        // 출력: 반환값 없음. writer에 Size(18)바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in HarvestHit h)
        {
            writer.WriteByte((byte)PacketId.HarvestHit);
            writer.WriteByte(h.TargetId);
            writer.WriteUInt16(h.Health);
            writer.WriteVector3(h.WeakPoint);
            writer.WriteByte(h.Gained);
            writer.WriteByte(h.Flags);
        }

        // 기능: HarvestHit 본문(PacketId 뒤)을 읽는다.
        // 입력: reader - 본문, h - 읽은 결과를 받을 변수.
        // 출력: 성공하면 true와 결과. 짧거나, 맵에 없는 대상이거나, 모르는 플래그 비트가 있거나, 약점 위치가 유한하지 않으면 false.
        // A target the map does not have, an unknown flag bit or a non-finite weak point is refused.
        public static bool TryRead(ref PacketReader reader, out HarvestHit h)
        {
            h = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadByte(out h.TargetId);
            reader.TryReadUInt16(out h.Health);
            reader.TryReadVector3(out h.WeakPoint);
            reader.TryReadByte(out h.Gained);
            reader.TryReadByte(out h.Flags);
            return h.TargetId < GameMap.Harvestables.Length && (h.Flags & ~(WeakPointHitFlag | DestroyedFlag | HasWeakPointFlag)) == 0 &&
                   Finite.Check(h.WeakPoint);
        }
    }

    // Phase 18 D7: what a WorldSound is. Values are the wire format.
    public enum WorldSoundKind : byte
    {
        HarvestHit = 0,         // a harvest swing hit a harvestable
        HarvestDestroyed = 1,   // a harvest swing destroyed a harvestable (sent instead of HarvestHit for that swing)
    }

    // Phase 18 D7: S->C, Unreliable, channel 0: a sound another player made that the client cannot derive from other packets.
    // The server sends it only to the other players within its hearing range (30 m of Position); the player who made it gets
    // its own packet instead (HarvestHit for a swing). SourceId is that player's entity id, Position where the sound is
    // (the point the swing hit). Layout: [PacketId 1][Kind 1][SourceId 2][Position 12] = 16 bytes.
    public struct WorldSound
    {
        public const int Size = 16;   // with the packet id

        public WorldSoundKind Kind;
        public ushort SourceId;
        public Vector3 Position;

        // 기능: WorldSound 패킷을 쓴다.
        // 입력: writer - 대상, s - 소리 사건.
        // 출력: 반환값 없음. writer에 16바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in WorldSound s)
        {
            writer.WriteByte((byte)PacketId.WorldSound);
            writer.WriteByte((byte)s.Kind);
            writer.WriteUInt16(s.SourceId);
            writer.WriteVector3(s.Position);
        }

        // 기능: WorldSound 본문(PacketId 뒤)을 읽는다.
        // 입력: reader - 본문.
        // 출력: 성공하면 true와 사건. 짧거나 모르는 종류이거나 위치가 유한하지 않으면 false.
        public static bool TryRead(ref PacketReader reader, out WorldSound s)
        {
            s = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadByte(out byte kind);
            reader.TryReadUInt16(out s.SourceId);
            reader.TryReadVector3(out s.Position);
            if (kind > (byte)WorldSoundKind.HarvestDestroyed) return false;
            s.Kind = (WorldSoundKind)kind;
            return Finite.Check(s.Position);
        }
    }

    // Phase 13 D6: S->C, ReliableOrdered: which harvestables are destroyed (bit i = GameMap.Harvestables[i]). To everyone
    // at the end of a tick in which one was destroyed and at a round reset (all standing again), and to a newcomer or a
    // resumed player.
    public static class HarvestStatesPacket
    {
        public const int Size = 9;   // with the packet id

        // 기능: 파괴된 채집물 비트 마스크를 HarvestStates 패킷(id + 하위·상위 uint32)으로 쓴다.
        // 입력: writer - 대상, destroyedMask - 비트 i가 GameMap.Harvestables[i]의 파괴 여부.
        // 출력: 반환값 없음. writer에 Size(9)바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, ulong destroyedMask)
        {
            writer.WriteByte((byte)PacketId.HarvestStates);
            writer.WriteUInt32((uint)destroyedMask);
            writer.WriteUInt32((uint)(destroyedMask >> 32));
        }

        // 기능: HarvestStates 본문(PacketId 뒤)의 파괴 마스크를 읽는다.
        // 입력: reader - 본문, destroyedMask - 읽은 마스크를 받을 변수.
        // 출력: 8바이트가 있고 맵에 없는 채집물의 비트가 꺼져 있으면 true와 마스크, 아니면 false.
        // A bit for a harvestable the map does not have is refused.
        public static bool TryRead(ref PacketReader reader, out ulong destroyedMask)
        {
            destroyedMask = 0;
            if (!reader.TryReadUInt32(out uint low) || !reader.TryReadUInt32(out uint high)) return false;
            destroyedMask = low | ((ulong)high << 32);
            int count = GameMap.Harvestables.Length;
            return count >= 64 || destroyedMask >> count == 0;
        }
    }
}
