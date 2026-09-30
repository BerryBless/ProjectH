using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ProjectH.Shared.Protocol
{
    // Reads little-endian values from received bytes. Every read is TryXxx: short or malformed data
    // returns false instead of throwing, because client packets are untrusted and exceptions must not
    // be used as control flow on the receive path.
    public ref struct PacketReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public PacketReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public int Remaining => _data.Length - _position;

        public bool TryReadByte(out byte value)
        {
            if (Remaining < 1)
            {
                value = 0;
                return false;
            }
            value = _data[_position];
            _position += 1;
            return true;
        }

        public bool TryReadUInt16(out ushort value)
        {
            if (Remaining < 2)
            {
                value = 0;
                return false;
            }
            value = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_position));
            _position += 2;
            return true;
        }

        public bool TryReadUInt32(out uint value)
        {
            if (Remaining < 4)
            {
                value = 0;
                return false;
            }
            value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position));
            _position += 4;
            return true;
        }

        public bool TryReadSingle(out float value)
        {
            if (!TryReadUInt32(out uint bits))
            {
                value = 0f;
                return false;
            }
            value = BitConverter.Int32BitsToSingle(unchecked((int)bits));
            return true;
        }

        public bool TryReadVector3(out Vector3 value)
        {
            if (Remaining < 12)
            {
                value = default;
                return false;
            }
            TryReadSingle(out float x);
            TryReadSingle(out float y);
            TryReadSingle(out float z);
            value = new Vector3(x, y, z);
            return true;
        }

        // Allocates the string: only used at connect time, never on the per-tick path.
        public bool TryReadString(int maxBytes, out string value)
        {
            value = null;
            if (!TryReadByte(out byte length)) return false;
            if (length > maxBytes || Remaining < length) return false;
            value = Encoding.UTF8.GetString(_data.Slice(_position, length));
            _position += length;
            return true;
        }

        public bool TryReadPacketId(out PacketId id)
        {
            id = PacketId.None;
            if (!TryReadByte(out byte raw)) return false;
            if (raw < (byte)PacketId.JoinMatchRequest || raw > (byte)PacketId.WorldSnapshot) return false;
            id = (PacketId)raw;
            return true;
        }
    }
}
