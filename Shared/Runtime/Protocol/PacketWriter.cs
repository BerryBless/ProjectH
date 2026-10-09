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

        // 기능: 호출자가 소유한 버퍼 위에서 길이 0, Overflowed 아님으로 시작하는 Writer를 만든다.
        // 입력: buffer - 쓸 곳(복사하지 않고 참조한다).
        // 출력: 처음부터 쓸 준비가 된 PacketWriter.
        public PacketWriter(Span<byte> buffer)
        {
            _buffer = buffer;
            _length = 0;
            _overflowed = false;
        }

        public int Length => _length;
        public bool Overflowed => _overflowed;
        public ReadOnlySpan<byte> WrittenSpan => _buffer.Slice(0, _length);

        // 기능: 1바이트를 쓰고 길이를 1 늘린다.
        // 입력: value - 쓸 바이트.
        // 출력: 반환값 없음. 자리가 모자라면 쓰지 않고 Overflowed가 된다.
        public void WriteByte(byte value)
        {
            if (!Reserve(1)) return;
            _buffer[_length] = value;
            _length += 1;
        }

        // 기능: uint16을 Little-endian 2바이트로 쓰고 길이를 2 늘린다.
        // 입력: value - 쓸 값.
        // 출력: 반환값 없음. 자리가 모자라면 쓰지 않고 Overflowed가 된다.
        public void WriteUInt16(ushort value)
        {
            if (!Reserve(2)) return;
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_length), value);
            _length += 2;
        }

        // 기능: uint32를 Little-endian 4바이트로 쓰고 길이를 4 늘린다.
        // 입력: value - 쓸 값.
        // 출력: 반환값 없음. 자리가 모자라면 쓰지 않고 Overflowed가 된다.
        public void WriteUInt32(uint value)
        {
            if (!Reserve(4)) return;
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.Slice(_length), value);
            _length += 4;
        }

        // 기능: float를 비트 그대로 Little-endian 4바이트로 쓰고 길이를 4 늘린다.
        // 입력: value - 쓸 값.
        // 출력: 반환값 없음. 자리가 모자라면 쓰지 않고 Overflowed가 된다.
        // BinaryPrimitives.WriteSingleLittleEndian is .NET 5+ and missing in Unity's netstandard2.1.
        public void WriteSingle(float value)
        {
            WriteUInt32(unchecked((uint)BitConverter.SingleToInt32Bits(value)));
        }

        // 기능: 벡터의 X, Y, Z를 float 세 개(12바이트)로 차례로 쓴다.
        // 입력: value - 쓸 벡터.
        // 출력: 반환값 없음. 자리가 모자라면 그 성분부터 쓰지 않고 Overflowed가 된다.
        public void WriteVector3(Vector3 value)
        {
            WriteSingle(value.X);
            WriteSingle(value.Y);
            WriteSingle(value.Z);
        }

        // 기능: 바이트를 그대로 쓴다(리뷰 수정 A3: 연결 요청의 쿠키). 길이 접두사 없음.
        // 입력: value - 쓸 바이트.
        // 출력: 반환값 없음. 자리가 모자라면 writer가 Overflowed가 된다.
        public void WriteBytes(ReadOnlySpan<byte> value)
        {
            if (!Reserve(value.Length)) return;
            value.CopyTo(_buffer.Slice(_length));
            _length += value.Length;
        }

        // 기능: 문자열을 1바이트 길이 접두사 + UTF-8 바이트로 쓴다(null은 빈 문자열로).
        // 입력: value - 쓸 문자열, maxBytes - 허용하는 최대 UTF-8 바이트 길이.
        // 출력: 반환값 없음. 길이가 maxBytes나 255를 넘거나 자리가 모자라면 쓰지 않고 Overflowed가 된다.
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

        // 기능: count바이트를 더 쓸 자리가 있는지 확인하고, 없으면 Overflowed로 표시한다.
        // 입력: count - 쓰려는 바이트 수.
        // 출력: 이미 Overflowed가 아니고 자리가 있으면 true, 아니면 false(Overflowed가 된다).
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
