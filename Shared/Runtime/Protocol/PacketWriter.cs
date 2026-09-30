using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ProjectH.Shared.Protocol
{
    // Writes little-endian values into a caller-owned buffer. Never throws on overflow: it sets
    // Overflowed and ignores further writes, so a too-large packet is caught by one check before sending.
    public ref struct PacketWriter
    {
        private readonly Span<byte> _buffer;
        private int _length;
        private bool _overflowed;

        public PacketWriter(Span<byte> buffer)
        {
            _buffer = buffer;
            _length = 0;
            _overflowed = false;
        }

        public int Length => _length;
        public bool Overflowed => _overflowed;
        public ReadOnlySpan<byte> WrittenSpan => _buffer.Slice(0, _length);

        public void WriteByte(byte value)
        {
            if (!Reserve(1)) return;
            _buffer[_length] = value;
            _length += 1;
        }

        public void WriteUInt16(ushort value)
        {
            if (!Reserve(2)) return;
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_length), value);
            _length += 2;
        }

        public void WriteUInt32(uint value)
        {
            if (!Reserve(4)) return;
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.Slice(_length), value);
            _length += 4;
        }

        // BinaryPrimitives.WriteSingleLittleEndian is .NET 5+ and missing in Unity's netstandard2.1.
        public void WriteSingle(float value)
        {
            WriteUInt32(unchecked((uint)BitConverter.SingleToInt32Bits(value)));
        }

        public void WriteVector3(Vector3 value)
        {
            WriteSingle(value.X);
            WriteSingle(value.Y);
            WriteSingle(value.Z);
        }

        // 1-byte length prefix + UTF-8 bytes. Longer than maxBytes marks the writer overflowed.
        public void WriteString(string value, int maxBytes)
        {
            if (value == null) value = string.Empty;
            int byteCount = Encoding.UTF8.GetByteCount(value);
            if (byteCount > maxBytes || byteCount > byte.MaxValue)
            {
                _overflowed = true;
                return;
            }
            if (!Reserve(1 + byteCount)) return;
            _buffer[_length] = (byte)byteCount;
            Encoding.UTF8.GetBytes(value.AsSpan(), _buffer.Slice(_length + 1, byteCount));
            _length += 1 + byteCount;
        }

        private bool Reserve(int count)
        {
            if (_overflowed) return false;
            if (_length + count > _buffer.Length)
            {
                _overflowed = true;
                return false;
            }
            return true;
        }
    }
}
