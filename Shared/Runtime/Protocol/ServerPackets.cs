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

    // Layout: [PacketId 1][ServerTick 4][AckInputSeq 4][Count 2] then Count x SnapshotEntity.
    // The server writes one payload for everyone and patches AckInputSeq per recipient.
    public struct WorldSnapshotHeader
    {
        public const int Size = 11;
        public const int AckInputSeqOffset = 5;

        public uint ServerTick;
        public uint AckInputSeq;
        public ushort Count;

        public static void Write(ref PacketWriter writer, in WorldSnapshotHeader h)
        {
            writer.WriteByte((byte)PacketId.WorldSnapshot);
            writer.WriteUInt32(h.ServerTick);
            writer.WriteUInt32(h.AckInputSeq);
            writer.WriteUInt16(h.Count);
        }

        public static bool TryRead(ref PacketReader reader, out WorldSnapshotHeader h)
        {
            h = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadUInt32(out h.ServerTick);
            reader.TryReadUInt32(out h.AckInputSeq);
            reader.TryReadUInt16(out h.Count);
            if (h.Count > ProtocolConstants.MaxSnapshotEntities) return false;
            return reader.Remaining >= h.Count * SnapshotEntity.Size;
        }

        public static void PatchAckInputSeq(Span<byte> packet, uint ackInputSeq)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(AckInputSeqOffset, 4), ackInputSeq);
        }
    }

    public struct SnapshotEntity
    {
        public const int Size = 22; // id 2 + position 12 + velocityY 4 + yaw 4

        public ushort EntityId;
        public Vector3 Position;
        public float VelocityY;
        public float Yaw;

        public static void Write(ref PacketWriter writer, in SnapshotEntity e)
        {
            writer.WriteUInt16(e.EntityId);
            writer.WriteVector3(e.Position);
            writer.WriteSingle(e.VelocityY);
            writer.WriteSingle(e.Yaw);
        }

        public static bool TryRead(ref PacketReader reader, out SnapshotEntity e)
        {
            e = default;
            if (reader.Remaining < Size) return false;
            reader.TryReadUInt16(out e.EntityId);
            reader.TryReadVector3(out e.Position);
            reader.TryReadSingle(out e.VelocityY);
            reader.TryReadSingle(out e.Yaw);
            return true;
        }
    }
}
