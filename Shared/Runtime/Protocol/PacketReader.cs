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

        // 기능: 받은 바이트 위에서 읽기 위치 0으로 시작하는 Reader를 만든다.
        // 입력: data - 읽을 바이트(복사하지 않고 참조한다).
        // 출력: 처음부터 읽을 준비가 된 PacketReader.
        public PacketReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public int Remaining => _data.Length - _position;

        // 기능: 1바이트를 읽고 읽기 위치를 1 옮긴다.
        // 입력: value - 읽은 바이트를 받을 변수.
        // 출력: 남은 바이트가 있으면 true와 값, 없으면 false와 0(위치는 그대로).
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

        // 기능: Little-endian uint16 2바이트를 읽고 읽기 위치를 2 옮긴다.
        // 입력: value - 읽은 값을 받을 변수.
        // 출력: 남은 바이트가 2 이상이면 true와 값, 아니면 false와 0(위치는 그대로).
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

        // 기능: Little-endian uint32 4바이트를 읽고 읽기 위치를 4 옮긴다.
        // 입력: value - 읽은 값을 받을 변수.
        // 출력: 남은 바이트가 4 이상이면 true와 값, 아니면 false와 0(위치는 그대로).
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

        // 기능: Little-endian float 4바이트를 비트 그대로 읽고 읽기 위치를 4 옮긴다.
        // 입력: value - 읽은 값을 받을 변수.
        // 출력: 남은 바이트가 4 이상이면 true와 값, 아니면 false와 0(위치는 그대로).
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

        // 기능: float 세 개(X, Y, Z) 12바이트를 읽어 Vector3로 만들고 읽기 위치를 12 옮긴다.
        // 입력: value - 읽은 벡터를 받을 변수.
        // 출력: 남은 바이트가 12 이상이면 true와 벡터, 아니면 false와 default(위치는 그대로).
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

        // 기능: 정해진 길이의 바이트를 호출자 버퍼로 복사해 읽는다(리뷰 수정 A3: 연결 요청의 쿠키). 할당 없음.
        // 입력: destination - 받을 곳(길이만큼 읽는다).
        // 출력: 남은 바이트가 충분하면 true와 채워진 destination, 아니면 false(읽기 위치는 그대로).
        public bool TryReadBytes(Span<byte> destination)
        {
            if (Remaining < destination.Length) return false;
            _data.Slice(_position, destination.Length).CopyTo(destination);
            _position += destination.Length;
            return true;
        }

        // 기능: 1바이트 길이 접두사 뒤의 UTF-8 바이트를 문자열로 읽는다(잘못된 바이트는 U+FFFD로 대체).
        // 입력: maxBytes - 허용하는 최대 바이트 길이, value - 읽은 문자열을 받을 변수.
        // 출력: 길이가 maxBytes 이하이고 바이트가 충분하면 true와 문자열, 아니면 false와 null(길이 바이트는 이미 소비됨).
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

        // 기능: 첫 바이트를 PacketId로 읽는다(범위: JoinMatchRequest..VehicleStates, Phase 19 상한 48).
        // 입력: 없음(읽기 위치의 바이트).
        // 출력: 범위 안이면 true와 id, 아니면 false.
        public bool TryReadPacketId(out PacketId id)
        {
            id = PacketId.None;
            if (!TryReadByte(out byte raw)) return false;
            // Upper bound is the highest id in PacketId; raise it whenever a packet is added.
            if (raw < (byte)PacketId.JoinMatchRequest || raw > (byte)PacketId.VehicleStates) return false;
            id = (PacketId)raw;
            return true;
        }
    }
}
