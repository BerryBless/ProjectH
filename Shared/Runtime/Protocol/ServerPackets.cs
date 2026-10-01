using System;
using System.Buffers.Binary;
using System.Numerics;

namespace ProjectH.Shared.Protocol
{
    public struct JoinMatchResponse
    {
        public JoinResult Result;
        public ushort MyEntityId;
        public uint ServerTick;
        public byte SimHz;
        public byte SnapshotHz;

        public static void Write(ref PacketWriter writer, in JoinMatchResponse r)
        {
            writer.WriteByte((byte)PacketId.JoinMatchResponse);
            writer.WriteByte((byte)r.Result);
            writer.WriteUInt16(r.MyEntityId);
            writer.WriteUInt32(r.ServerTick);
            writer.WriteByte(r.SimHz);
            writer.WriteByte(r.SnapshotHz);
        }

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
            return r.SimHz > 0 && r.SnapshotHz > 0;
        }
    }

    public struct PlayerSpawned
    {
        public ushort EntityId;
        public Vector3 Position;
        public float Yaw;

        public static void Write(ref PacketWriter writer, in PlayerSpawned s)
        {
            writer.WriteByte((byte)PacketId.PlayerSpawned);
            writer.WriteUInt16(s.EntityId);
            writer.WriteVector3(s.Position);
            writer.WriteSingle(s.Yaw);
        }

        public static bool TryRead(ref PacketReader reader, out PlayerSpawned s)
        {
            s = default;
            if (reader.Remaining < 18) return false;
            reader.TryReadUInt16(out s.EntityId);
            reader.TryReadVector3(out s.Position);
            reader.TryReadSingle(out s.Yaw);
            return true;
        }
    }

    public struct PlayerDespawned
    {
        public ushort EntityId;

        public static void Write(ref PacketWriter writer, in PlayerDespawned d)
        {
            writer.WriteByte((byte)PacketId.PlayerDespawned);
            writer.WriteUInt16(d.EntityId);
        }

        public static bool TryRead(ref PacketReader reader, out PlayerDespawned d)
        {
            d = default;
            return reader.TryReadUInt16(out d.EntityId);
        }
    }

    // Layout: [PacketId 1][ServerTick 4][AckInputSeq 4][Count 2][Part 1][PartCount 1][SnapshotSelf 6] (19-byte header) then Count x SnapshotEntity.
    // The server writes one payload for everyone and patches AckInputSeq and Self per recipient (D10).
    // Phase 8 D3: one packet of a snapshot. A tick's snapshot is PartCount packets (Part 0..PartCount-1), each a complete
    // header (same tick, ack and self block) with its own slice of the entities, so every packet can be applied on its
    // own: a lost part only means those entities get no sample for that tick.
    public struct WorldSnapshotHeader
    {
        public const int Size = 19;
        public const int AckInputSeqOffset = 5;
        public const int SelfOffset = 13;

        public uint ServerTick;
        public uint AckInputSeq;
        public ushort Count;       // entities in this packet
        public byte Part;
        public byte PartCount;
        public SnapshotSelf Self;

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

        public static bool TryRead(ref PacketReader reader, out WorldSnapshotHeader h)
        {
            h = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadUInt32(out h.ServerTick);
            reader.TryReadUInt32(out h.AckInputSeq);
            reader.TryReadUInt16(out h.Count);
            reader.TryReadByte(out h.Part);
            reader.TryReadByte(out h.PartCount);
            SnapshotSelf.TryRead(ref reader, out h.Self);
            if (h.Count > ProtocolConstants.MaxEntitiesPerSnapshotPacket) return false;
            if (h.PartCount < 1 || h.PartCount > ProtocolConstants.MaxSnapshotParts || h.Part >= h.PartCount) return false;
            return reader.Remaining >= h.Count * SnapshotEntity.Size;
        }

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
    public struct SnapshotSelf
    {
        public const int Size = 6;

        public byte Health;
        public byte Shield;
        public byte WeaponSlot;             // loadout index: 0 = Slot1, 1 = Slot2
        public byte Ammo;                   // rounds in the current weapon's magazine
        public ushort ReloadRemainingTicks; // 0 = not reloading; at least 1 while a reload is running

        public static void Write(ref PacketWriter writer, in SnapshotSelf s)
        {
            writer.WriteByte(s.Health);
            writer.WriteByte(s.Shield);
            writer.WriteByte(s.WeaponSlot);
            writer.WriteByte(s.Ammo);
            writer.WriteUInt16(s.ReloadRemainingTicks);
        }

        public static bool TryRead(ref PacketReader reader, out SnapshotSelf s)
        {
            s = default;
            if (reader.Remaining < Size) return false;
            reader.TryReadByte(out s.Health);
            reader.TryReadByte(out s.Shield);
            reader.TryReadByte(out s.WeaponSlot);
            reader.TryReadByte(out s.Ammo);
            reader.TryReadUInt16(out s.ReloadRemainingTicks);
            return true;
        }
    }

    // Phase 8 D4: quantized on the wire. Position and VelocityY are signed 16-bit fixed point with 1/256 resolution
    // (range +-128 m and +-128 m/s; the map is +-80 m), Yaw is 16 bits over 360 degrees. The error is at most half a
    // step (about 0.002 m per axis), well inside the client's reconcile tolerance (0.01 m). Values outside the range
    // are clamped; non-finite values are written as 0.
    public struct SnapshotEntity
    {
        public const int Size = 13; // id 2 + position 3 x 2 + velocityY 2 + yaw 2 + flags 1
        public const byte AliveFlag = 1;
        public const float FixedScale = 256f;
        public const float YawScale = 65536f / 360f;

        public ushort EntityId;
        public Vector3 Position;
        public float VelocityY;
        public float Yaw;
        public byte Flags;

        public bool IsAlive => (Flags & AliveFlag) != 0;

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

        // What the receiver will read back for this value (used by tests).
        public static float Quantize(float value) => FromFixed(ToFixed(value));

        private static ushort ToFixed(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0;
            float scaled = (float)Math.Round(value * FixedScale);
            if (scaled > short.MaxValue) scaled = short.MaxValue;
            if (scaled < short.MinValue) scaled = short.MinValue;
            return unchecked((ushort)(short)scaled);
        }

        private static float FromFixed(ushort raw) => unchecked((short)raw) / FixedScale;

        private static ushort ToYaw(float yaw)
        {
            if (float.IsNaN(yaw) || float.IsInfinity(yaw)) return 0;
            float degrees = yaw % 360f;
            if (degrees < 0f) degrees += 360f;
            return unchecked((ushort)(int)Math.Round(degrees * YawScale));
        }
    }
}
