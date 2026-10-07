using System;
using ProjectH.Shared.Simulation;

namespace ProjectH.Shared.Protocol
{
    // Phase 16 D3: S->C, ReliableOrdered on channel 0, to everyone: which loot containers spawned this match and which of
    // those are open (bit i = LootContainers.All[i]). A container is none (not spawned), closed (spawned, not opened) or
    // open. Sent at the end of a tick in which either mask changed (a match start, an open, a round reset) and to a newcomer
    // or a resumed player. Layout: [PacketId 1][Spawned 8][Opened 8] = 17 bytes. Nothing about the loot inside is sent.
    public static class ContainerStatesPacket
    {
        public const int Size = 17;   // with the packet id

        // 기능: ContainerStates 패킷을 쓴다.
        // 입력: writer - 대상, spawnedMask - 생성된 컨테이너 비트, openedMask - 열린 컨테이너 비트.
        // 출력: 반환값 없음. writer에 17바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, ulong spawnedMask, ulong openedMask)
        {
            writer.WriteByte((byte)PacketId.ContainerStates);
            writer.WriteUInt32((uint)spawnedMask);
            writer.WriteUInt32((uint)(spawnedMask >> 32));
            writer.WriteUInt32((uint)openedMask);
            writer.WriteUInt32((uint)(openedMask >> 32));
        }

        // 기능: ContainerStates 본문(PacketId 뒤)을 읽는다.
        // 입력: reader - 본문.
        // 출력: 성공하면 true와 두 마스크. 짧거나 맵에 없는 컨테이너의 비트가 켜져 있거나 생성되지 않은 컨테이너가 열려 있으면 false.
        public static bool TryRead(ref PacketReader reader, out ulong spawnedMask, out ulong openedMask)
        {
            spawnedMask = 0;
            openedMask = 0;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadUInt32(out uint spawnedLow);
            reader.TryReadUInt32(out uint spawnedHigh);
            reader.TryReadUInt32(out uint openedLow);
            reader.TryReadUInt32(out uint openedHigh);
            spawnedMask = spawnedLow | ((ulong)spawnedHigh << 32);
            openedMask = openedLow | ((ulong)openedHigh << 32);
            int count = LootContainers.Count;
            if (count < 64 && spawnedMask >> count != 0) return false;
            return (openedMask & ~spawnedMask) == 0;
        }
    }

    // Phase 16 D7: what a supply drop is doing. Values are the wire format.
    public enum SupplyDropState : byte
    {
        Falling = 0,   // between StartTick and LandTick (SupplyDropFall.HeightAt gives its height)
        Landed = 1,    // on the ground, closed: E opens it
        Opened = 2,    // its loot lies around it
    }

    // Phase 16 D7: one supply drop as SupplyDrops carries it. Id is its slot in the match (0..MaxSupplyDrops-1, the order it
    // spawned in). X/Z/LandY is where it lands; StartTick and LandTick are server ticks. 22 bytes on the wire.
    public struct SupplyDropInfo
    {
        public const int Size = 22;

        public byte Id;
        public SupplyDropState State;
        public float X;
        public float Z;
        public float LandY;
        public uint StartTick;
        public uint LandTick;
    }

    // Phase 16 D7: S->C, ReliableOrdered on channel 0, to everyone: every supply drop of the match (at most MaxSupplyDrops).
    // Sent at the end of a tick in which one spawned, landed or was opened, at a match start and a round reset (an empty
    // list), and to a newcomer or a resumed player. Not in the snapshot: the client replaces its list with this one and
    // computes the fall itself (SupplyDropFall). Layout: [PacketId 1][Count 1] Count x [Id 1][State 1][X 4][Z 4][LandY 4]
    // [StartTick 4][LandTick 4].
    public static class SupplyDropsPacket
    {
        public const int MaxSupplyDrops = 4;
        public const int MaxSize = 2 + MaxSupplyDrops * SupplyDropInfo.Size;   // 90 bytes
        // Landing positions lie inside the map; one unit of slack for rounding.
        private const float MaxHorizontal = GameMap.HalfSize + 0.01f;

        // 기능: SupplyDrops 패킷을 쓴다(목록이 상한보다 길면 상한까지만).
        // 입력: writer - 대상, drops - 경기의 Supply Drop(앞에서부터 Id 순서).
        // 출력: 반환값 없음. writer에 패킷이 쓰인다(최대 MaxSize = 90바이트).
        public static void Write(ref PacketWriter writer, ReadOnlySpan<SupplyDropInfo> drops)
        {
            int count = Math.Min(drops.Length, MaxSupplyDrops);
            writer.WriteByte((byte)PacketId.SupplyDrops);
            writer.WriteByte((byte)count);
            for (int i = 0; i < count; i++)
            {
                SupplyDropInfo d = drops[i];
                writer.WriteByte(d.Id);
                writer.WriteByte((byte)d.State);
                writer.WriteSingle(d.X);
                writer.WriteSingle(d.Z);
                writer.WriteSingle(d.LandY);
                writer.WriteUInt32(d.StartTick);
                writer.WriteUInt32(d.LandTick);
            }
        }

        // 기능: SupplyDrops 본문(PacketId 뒤)을 호출자가 가진 고정 배열에 읽는다(할당 없음). 서버가 보내지 않는 값은 거절한다.
        // 입력: reader - 본문, drops - MaxSupplyDrops 칸 이상.
        // 출력: 성공하면 true와 count(앞쪽 칸에 채워진다). 길이가 정확히 맞지 않거나, 수가 상한·배열을 넘거나, Id가 상한 이상이거나
        //   오름차순이 아니거나, 모르는 상태이거나, 좌표가 유한하지 않거나 맵 밖이거나, 착지 Tick이 시작 Tick보다 이르면 false
        //   (배열 내용은 쓰다 만 상태일 수 있다).
        public static bool TryRead(ref PacketReader reader, Span<SupplyDropInfo> drops, out int count)
        {
            count = 0;
            if (!reader.TryReadByte(out byte n) || n > MaxSupplyDrops || n > drops.Length) return false;
            if (reader.Remaining != n * SupplyDropInfo.Size) return false;
            int lastId = -1;
            for (int i = 0; i < n; i++)
            {
                var d = new SupplyDropInfo();
                reader.TryReadByte(out d.Id);
                reader.TryReadByte(out byte state);
                reader.TryReadSingle(out d.X);
                reader.TryReadSingle(out d.Z);
                reader.TryReadSingle(out d.LandY);
                reader.TryReadUInt32(out d.StartTick);
                reader.TryReadUInt32(out d.LandTick);
                if (d.Id >= MaxSupplyDrops || d.Id <= lastId || state > (byte)SupplyDropState.Opened) return false;
                if (!Finite.Check(d.X) || !Finite.Check(d.Z) || !Finite.Check(d.LandY)) return false;
                if (Math.Abs(d.X) > MaxHorizontal || Math.Abs(d.Z) > MaxHorizontal || d.LandTick < d.StartTick) return false;
                lastId = d.Id;
                d.State = (SupplyDropState)state;
                drops[i] = d;
            }
            count = n;
            return true;
        }
    }
}
