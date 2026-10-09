using System;
using System.Buffers.Binary;
using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Shared.Protocol
{
    public struct JoinMatchResponse
    {
        public JoinResult Result;
        public ushort MyEntityId;
        public uint ServerTick;
        public byte SimHz;
        public byte SnapshotHz;

        // 기능: JoinMatchResponse 패킷(id, 결과, 내 Entity id, 서버 Tick, SimHz, SnapshotHz)을 쓴다.
        // 입력: writer - 쓸 곳, r - 입장 응답.
        // 출력: 반환값 없음. writer에 10바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in JoinMatchResponse r)
        {
            writer.WriteByte((byte)PacketId.JoinMatchResponse);
            writer.WriteByte((byte)r.Result);
            writer.WriteUInt16(r.MyEntityId);
            writer.WriteUInt32(r.ServerTick);
            writer.WriteByte(r.SimHz);
            writer.WriteByte(r.SnapshotHz);
        }

        // 기능: JoinMatchResponse 본문을 읽는다. 리뷰 수정 D3(SEC-25): 결과가 Resumed보다 크거나, Ok·Resumed인데 내 Entity id가 0이면 거절한다
        //   (서버는 MatchFull에만 id 0을 보낸다).
        // 입력: reader - 본문(PacketId 뒤).
        // 출력: 맞으면 true와 응답, 아니면 false.
        public static bool TryRead(ref PacketReader reader, out JoinMatchResponse r)
        {
            r = default;
            if (reader.Remaining < 9) return false;
            reader.TryReadByte(out byte result);
            r.Result = (JoinResult)result;
            reader.TryReadUInt16(out r.MyEntityId);
            reader.TryReadUInt32(out r.ServerTick);
            reader.TryReadByte(out r.SimHz);
            reader.TryReadByte(out r.SnapshotHz);
            if (r.Result > JoinResult.Resumed) return false;
            if ((r.Result == JoinResult.Ok || r.Result == JoinResult.Resumed) && r.MyEntityId == 0) return false;
            return r.SimHz > 0 && r.SnapshotHz > 0;
        }
    }

    // Phase 11 D9: Name is the player's DevPlayerId (1-32 bytes of UTF-8, the same limit as the connect request). It
    // comes once per spawn, never with snapshots, so the client can show names in the kill feed, the spectator line
    // and the result. The reader allocates the string: join time only.
    public struct PlayerSpawned
    {
        public ushort EntityId;
        public Vector3 Position;
        public float Yaw;
        public string Name;

        // 기능: PlayerSpawned 패킷(id, Entity id, 위치, Yaw, 이름 문자열)을 쓴다.
        // 입력: writer - 쓸 곳, s - 생성된 플레이어(Name은 MaxDevPlayerIdBytes 이하).
        // 출력: 반환값 없음. writer에 패킷이 쓰인다(이름이 길면 Overflowed).
        public static void Write(ref PacketWriter writer, in PlayerSpawned s)
        {
            writer.WriteByte((byte)PacketId.PlayerSpawned);
            writer.WriteUInt16(s.EntityId);
            writer.WriteVector3(s.Position);
            writer.WriteSingle(s.Yaw);
            writer.WriteString(s.Name, ProtocolConstants.MaxDevPlayerIdBytes);
        }

        // 기능: PlayerSpawned 본문을 읽는다. 리뷰 수정 D3(SEC-24): 위치·Yaw가 유한하고, 이름이 서버의 접속 이름 규칙(IsValidPlayerName)을 지켜야 한다.
        // 입력: reader - 본문(PacketId 뒤).
        // 출력: 맞으면 true와 Spawn, 아니면 false. 이름 문자열을 할당한다(입장 때만).
        public static bool TryRead(ref PacketReader reader, out PlayerSpawned s)
        {
            s = default;
            if (reader.Remaining < 18) return false;
            reader.TryReadUInt16(out s.EntityId);
            reader.TryReadVector3(out s.Position);
            reader.TryReadSingle(out s.Yaw);
            if (!reader.TryReadString(ProtocolConstants.MaxDevPlayerIdBytes, out s.Name) || s.Name.Length == 0) return false;
            return Finite.Check(s.Position) && Finite.Check(s.Yaw) && ProtocolConstants.IsValidPlayerName(s.Name);
        }
    }

    public struct PlayerDespawned
    {
        public ushort EntityId;

        // 기능: PlayerDespawned 패킷(id + Entity id)을 쓴다.
        // 입력: writer - 쓸 곳, d - 사라진 플레이어.
        // 출력: 반환값 없음. writer에 3바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in PlayerDespawned d)
        {
            writer.WriteByte((byte)PacketId.PlayerDespawned);
            writer.WriteUInt16(d.EntityId);
        }

        // 기능: PlayerDespawned 본문(PacketId 뒤)의 Entity id를 읽는다.
        // 입력: reader - 본문, d - 결과.
        // 출력: 2바이트가 있으면 true와 id(값 검사는 없다), 짧으면 false.
        public static bool TryRead(ref PacketReader reader, out PlayerDespawned d)
        {
            d = default;
            return reader.TryReadUInt16(out d.EntityId);
        }
    }

    // Layout: [PacketId 1][ServerTick 4][AckInputSeq 4][Count 2][Part 1][PartCount 1][SnapshotSelf 14] (27-byte header, Phase 12)
    // then Count x SnapshotEntity.
    // The server writes one payload for everyone and patches AckInputSeq and Self per recipient (D10).
    // Phase 8 D3: one packet of a snapshot. A tick's snapshot is PartCount packets (Part 0..PartCount-1), each a complete
    // header (same tick, ack and self block) with its own slice of the entities, so every packet can be applied on its
    // own: a lost part only means those entities get no sample for that tick.
    public struct WorldSnapshotHeader
    {
        public const int Size = 27;
        public const int AckInputSeqOffset = 5;
        public const int SelfOffset = 13;

        public uint ServerTick;
        public uint AckInputSeq;
        public ushort Count;       // entities in this packet
        public byte Part;
        public byte PartCount;
        public SnapshotSelf Self;

        // 기능: Snapshot 헤더(id, 서버 Tick, Ack 입력 순번, 엔티티 수, 부분 번호·수, 자기 블록)를 쓴다. 뒤에 Count개의 SnapshotEntity가 따른다.
        // 입력: writer - 쓸 곳, h - 헤더(Self는 받는 사람에 따라 PatchRecipient로 덮어쓴다).
        // 출력: 반환값 없음. writer에 Size(27)바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in WorldSnapshotHeader h)
        {
            writer.WriteByte((byte)PacketId.WorldSnapshot);
            writer.WriteUInt32(h.ServerTick);
            writer.WriteUInt32(h.AckInputSeq);
            writer.WriteUInt16(h.Count);
            writer.WriteByte(h.Part);
            writer.WriteByte(h.PartCount);
            SnapshotSelf.Write(ref writer, h.Self);
        }

        // 기능: Snapshot 헤더(PacketId 뒤)를 읽고 부분·엔티티 수가 말이 되는지 검사한다.
        // 입력: reader - 본문, h - 결과.
        // 출력: 성공하면 true와 헤더. 짧거나, 자기 블록이 잘못됐거나, 엔티티 수가 MaxEntitiesPerSnapshotPacket을 넘거나,
        //   부분 수가 1..MaxSnapshotParts 밖이거나 부분 번호가 부분 수 이상이거나, 남은 바이트가 Count x 13보다 적으면 false.
        public static bool TryRead(ref PacketReader reader, out WorldSnapshotHeader h)
        {
            h = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadUInt32(out h.ServerTick);
            reader.TryReadUInt32(out h.AckInputSeq);
            reader.TryReadUInt16(out h.Count);
            reader.TryReadByte(out h.Part);
            reader.TryReadByte(out h.PartCount);
            if (!SnapshotSelf.TryRead(ref reader, out h.Self)) return false;
            if (h.Count > ProtocolConstants.MaxEntitiesPerSnapshotPacket) return false;
            if (h.PartCount < 1 || h.PartCount > ProtocolConstants.MaxSnapshotParts || h.Part >= h.PartCount) return false;
            return reader.Remaining >= h.Count * SnapshotEntity.Size;
        }

        // 기능: 이미 쓴 Snapshot 패킷의 Ack 입력 순번과 자기 블록을 받는 사람 것으로 덮어쓴다(한 번 쓴 본문을 모두에게 재사용, D10).
        // 입력: packet - PacketId 바이트부터 시작하는 전체 Snapshot 패킷(Size 이상), ackInputSeq - 받는 사람의 마지막 처리 입력 순번, self - 받는 사람의 자기 블록.
        // 출력: 반환값 없음. packet의 5..8바이트와 13..26바이트가 바뀐다.
        // packet is the whole written snapshot, starting with its PacketId byte.
        public static void PatchRecipient(Span<byte> packet, uint ackInputSeq, in SnapshotSelf self)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(AckInputSeqOffset, 4), ackInputSeq);
            var writer = new PacketWriter(packet.Slice(SelfOffset, SnapshotSelf.Size));
            SnapshotSelf.Write(ref writer, self);
        }
    }

    // The recipient's own combat values (D10). Sent in every snapshot, so the HUD recovers even when a
    // Reliable combat event is late.
    // Phase 12 D11: plus the movement state only the owner needs for prediction (the mode, sprint and exhaustion are in
    // its entity's flags, the position and VelocityY in the entity). Energy is the exact value (MoveState.EnergySpent);
    // the horizontal velocity is quantized like the entity's VelocityY.
    // Phase 13 D5: the owner's tool rides in the weapon slot byte's top two bits (still 14 bytes).
    public struct SnapshotSelf
    {
        public const int Size = 14;
        private const int ToolShift = 6;
        private const byte SlotMask = 0x3F;

        public byte Health;
        public byte Shield;
        public byte WeaponSlot;             // loadout index: 0 = Slot1, 1 = Slot2 (bits 0-5 on the wire)
        public ToolKind Tool;               // Phase 13: bits 6-7 of the weapon slot byte
        public byte Ammo;                   // rounds in the current weapon's magazine
        public ushort ReloadRemainingTicks; // 0 = not reloading; at least 1 while a reload is running
        public ushort Energy;               // hundredths, 0..MoveState.MaxEnergyHundredths
        public Vector2 HorizontalVelocity;  // m/s, X and Z
        public byte ModeTicks;
        public byte EnergyDelayTicks;

        // 기능: 자기 블록(체력, 실드, 무기 칸+도구, 탄창, 재장전 잔여, 에너지, 수평 속도 고정소수점, 모드·에너지 지연 Tick)을 쓴다.
        // 입력: writer - 쓸 곳, s - 받는 사람 자신의 상태.
        // 출력: 반환값 없음. writer에 Size(14)바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in SnapshotSelf s)
        {
            writer.WriteByte(s.Health);
            writer.WriteByte(s.Shield);
            writer.WriteByte((byte)((s.WeaponSlot & SlotMask) | ((int)s.Tool << ToolShift)));
            writer.WriteByte(s.Ammo);
            writer.WriteUInt16(s.ReloadRemainingTicks);
            writer.WriteUInt16(s.Energy);
            writer.WriteUInt16(SnapshotEntity.ToFixed(s.HorizontalVelocity.X));
            writer.WriteUInt16(SnapshotEntity.ToFixed(s.HorizontalVelocity.Y));
            writer.WriteByte(s.ModeTicks);
            writer.WriteByte(s.EnergyDelayTicks);
        }

        // 기능: 자기 블록 14바이트를 읽는다.
        // 입력: reader - 블록이 시작되는 곳, s - 결과.
        // 출력: 성공하면 true와 상태. 짧거나, 에너지가 MaxEnergyHundredths를 넘거나, 도구 비트가 Build보다 크면 false.
        public static bool TryRead(ref PacketReader reader, out SnapshotSelf s)
        {
            s = default;
            if (reader.Remaining < Size) return false;
            reader.TryReadByte(out s.Health);
            reader.TryReadByte(out s.Shield);
            reader.TryReadByte(out byte slotAndTool);
            s.WeaponSlot = (byte)(slotAndTool & SlotMask);
            int tool = slotAndTool >> ToolShift;
            s.Tool = (ToolKind)tool;
            reader.TryReadByte(out s.Ammo);
            reader.TryReadUInt16(out s.ReloadRemainingTicks);
            reader.TryReadUInt16(out s.Energy);
            reader.TryReadUInt16(out ushort velocityX);
            reader.TryReadUInt16(out ushort velocityZ);
            reader.TryReadByte(out s.ModeTicks);
            reader.TryReadByte(out s.EnergyDelayTicks);
            s.HorizontalVelocity = new Vector2(SnapshotEntity.FromFixed(velocityX), SnapshotEntity.FromFixed(velocityZ));
            return s.Energy <= MoveState.MaxEnergyHundredths && tool <= (int)ToolKind.Build;
        }
    }

    // Phase 8 D4: quantized on the wire. Position and VelocityY are signed 16-bit fixed point with 1/256 resolution
    // (range +-128 m and +-128 m/s; the map is +-80 m), Yaw is 16 bits over 360 degrees. The error is at most half a
    // step (about 0.002 m per axis), well inside the client's reconcile tolerance (0.01 m). Values outside the range
    // are clamped; non-finite values are written as 0.
    // Phase 12 D11: Flags bit 0 alive, bits 1-3 the MovementMode, bit 4 sprinting, bit 5 exhausted (the owner's prediction
    // needs it; others may show it). Phase 13 D5: bits 6-7 the tool in hand (others see a pickaxe or a build plan).
    public struct SnapshotEntity
    {
        public const int Size = 13; // id 2 + position 3 x 2 + velocityY 2 + yaw 2 + flags 1
        public const byte AliveFlag = 1;
        public const int ModeShift = 1;
        public const byte ModeMask = 0x0E;
        public const byte SprintingFlag = 16;
        public const byte ExhaustedFlag = 32;
        public const int ToolShift = 6;
        public const byte ToolMask = 0xC0;
        public const float FixedScale = 256f;
        public const float YawScale = 65536f / 360f;

        public ushort EntityId;
        public Vector3 Position;
        public float VelocityY;
        public float Yaw;
        public byte Flags;

        public bool IsAlive => (Flags & AliveFlag) != 0;
        public bool IsSprinting => (Flags & SprintingFlag) != 0;
        public bool IsExhausted => (Flags & ExhaustedFlag) != 0;

        // Phase 13: the tool in the flags. 3 (a bad packet) reads as Weapon.
        public ToolKind Tool
        {
            get
            {
                int tool = (Flags & ToolMask) >> ToolShift;
                return tool <= (int)ToolKind.Build ? (ToolKind)tool : ToolKind.Weapon;
            }
        }

        // The mode in the flags. Phase 14: all 8 values of the 3 bits are modes (7 = Downed); the bound stays so a mode
        // added past Downed cannot be read by accident.
        public MovementMode Mode
        {
            get
            {
                int mode = (Flags & ModeMask) >> ModeShift;
                return mode <= (int)MovementMode.Downed ? (MovementMode)mode : MovementMode.Ground;
            }
        }

        // 기능: 엔티티 플래그 바이트를 만든다(bit 0 생존, 1-3 이동 모드, 4 질주, 5 탈진, 6-7 도구).
        // 입력: alive - 살아 있는지, mode - 이동 모드, sprinting - 질주 중인지, exhausted - 탈진했는지, tool - 손에 든 도구(기본 Weapon).
        // 출력: 조합된 플래그 바이트.
        public static byte MakeFlags(bool alive, MovementMode mode, bool sprinting, bool exhausted, ToolKind tool = ToolKind.Weapon)
        {
            int flags = ((int)mode << ModeShift) & ModeMask;
            flags |= ((int)tool << ToolShift) & ToolMask;
            if (alive) flags |= AliveFlag;
            if (sprinting) flags |= SprintingFlag;
            if (exhausted) flags |= ExhaustedFlag;
            return (byte)flags;
        }

        // 기능: 엔티티 하나(id, 위치·VelocityY 고정소수점, Yaw 16비트, 플래그)를 양자화해 쓴다.
        // 입력: writer - 쓸 곳, e - 엔티티 상태.
        // 출력: 반환값 없음. writer에 Size(13)바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in SnapshotEntity e)
        {
            writer.WriteUInt16(e.EntityId);
            writer.WriteUInt16(ToFixed(e.Position.X));
            writer.WriteUInt16(ToFixed(e.Position.Y));
            writer.WriteUInt16(ToFixed(e.Position.Z));
            writer.WriteUInt16(ToFixed(e.VelocityY));
            writer.WriteUInt16(ToYaw(e.Yaw));
            writer.WriteByte(e.Flags);
        }

        // 기능: 엔티티 하나 13바이트를 읽어 양자화를 되돌린다.
        // 입력: reader - 엔티티 기록이 시작되는 곳, e - 결과.
        // 출력: 13바이트가 있으면 true와 엔티티(값 검사는 없다), 짧으면 false.
        public static bool TryRead(ref PacketReader reader, out SnapshotEntity e)
        {
            e = default;
            if (reader.Remaining < Size) return false;
            reader.TryReadUInt16(out e.EntityId);
            reader.TryReadUInt16(out ushort x);
            reader.TryReadUInt16(out ushort y);
            reader.TryReadUInt16(out ushort z);
            reader.TryReadUInt16(out ushort velocityY);
            reader.TryReadUInt16(out ushort yaw);
            reader.TryReadByte(out e.Flags);
            e.Position = new Vector3(FromFixed(x), FromFixed(y), FromFixed(z));
            e.VelocityY = FromFixed(velocityY);
            e.Yaw = yaw / YawScale;
            return true;
        }

        // 기능: 값을 고정소수점으로 왕복시켜 받는 쪽이 읽게 될 값으로 맞춘다.
        // 입력: value - 미터 또는 m/s 값.
        // 출력: 1/256 단위로 양자화된 값.
        // What the receiver will read back for this value (used by tests).
        public static float Quantize(float value) => FromFixed(ToFixed(value));

        // 기능: 값을 부호 있는 16비트 고정소수점(1/256)으로 바꾼다(범위 밖은 끝값으로 자르고, NaN·Infinity는 0).
        // 입력: value - 미터 또는 m/s 값.
        // 출력: 보낼 16비트 값(short 비트를 ushort로 담은 것).
        // Signed 16-bit fixed point (1/256), clamped; non-finite is 0. Also the self block's horizontal velocity.
        internal static ushort ToFixed(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0;
            float scaled = (float)Math.Round(value * FixedScale);
            if (scaled > short.MaxValue) scaled = short.MaxValue;
            if (scaled < short.MinValue) scaled = short.MinValue;
            return unchecked((ushort)(short)scaled);
        }

        // 기능: 16비트 고정소수점(1/256)을 float로 되돌린다.
        // 입력: raw - 받은 16비트 값.
        // 출력: 미터 또는 m/s 값.
        internal static float FromFixed(ushort raw) => unchecked((short)raw) / FixedScale;

        // 기능: Yaw(도)를 0..360을 16비트에 나눈 값으로 바꾼다(음수는 360을 더해 정규화, NaN·Infinity는 0).
        // 입력: yaw - 도 단위 Yaw.
        // 출력: 보낼 16비트 Yaw.
        private static ushort ToYaw(float yaw)
        {
            if (float.IsNaN(yaw) || float.IsInfinity(yaw)) return 0;
            float degrees = yaw % 360f;
            if (degrees < 0f) degrees += 360f;
            return unchecked((ushort)(int)Math.Round(degrees * YawScale));
        }
    }
}
