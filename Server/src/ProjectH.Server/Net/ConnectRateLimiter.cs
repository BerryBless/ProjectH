using System;
using System.Buffers.Binary;
using System.Net;

namespace ProjectH.Server.Net;

// Server review M2: connection requests per remote IP, as a token bucket (burst at once, then perSecond). A request
// over the rate is refused before Accept, so connect/disconnect churn from one address cannot fill the shared Control
// channel and get other players' connects and joins closed with ServerError.
//
// Size: a fixed table of Slots entries, made once, indexed by a hash of the address; it never grows. Nothing is
// removed. Addresses that hash to one slot share its bucket: only an empty slot starts with a full burst, so
// alternating two colliding addresses gets no more than one bucket (review round 1; an earlier version gave a new
// address a full bucket, which alternating addresses could use to get past the limit). The trade-off: a normal address
// that collides with a busy one shares its bucket and may be refused; with 1024 slots that is rare.
// Thread: NetworkListener.OnConnectionRequest only, which LiteNetLib runs on its single receive thread (the server binds
// IPv4 only; see PeerState). So the table needs no synchronization.
public sealed class ConnectRateLimiter
{
    public const int Slots = 1024;   // a power of two (SlotOf)
    private const int SlotBits = 10;
    // Tokens are counted in thousandths, so perSecond tokens per second is perSecond units per millisecond (integers).
    private const long Unit = 1000;

    private struct Slot
    {
        public bool Used;
        public long Tokens;   // thousandths of a request
        public long LastMs;
    }

    private readonly Slot[] _slots;
    private readonly long _capacity;
    private readonly long _perMs;

    // 기능: IP별 연결 요청 Token Bucket 표를 만든다.
    // 입력: burst - 한 번에 허용할 연결 요청 수, perSecond - 이후 초당 허용할 연결 요청 수.
    // 출력: 모든 Slot이 비어 있는 ConnectRateLimiter 객체. burst나 perSecond가 0 이하면 표 없이 꺼진 상태.
    // burst or perSecond 0 = off (every request passes, no table is made).
    public ConnectRateLimiter(int burst, int perSecond)
    {
        if (burst <= 0 || perSecond <= 0)
        {
            _slots = Array.Empty<Slot>();
            return;
        }
        _slots = new Slot[Slots];
        _capacity = burst * Unit;
        _perMs = perSecond;
    }

    public bool Enabled => _slots.Length != 0;

    // 기능: 주소의 Bucket을 경과 시간만큼 채운 뒤 연결 요청 하나에 쓸 Token을 꺼낸다. 빈 Slot은 가득 찬 Bucket으로 시작한다.
    // 입력: address - 연결을 요청한 원격 IP, nowMs - 단조 증가 Millisecond 시각.
    // 출력: 지금 연결해도 되면 true(Token 하나 소비), 비율을 넘었으면 false. 꺼져 있으면 항상 true.
    // True when the address may connect now (one token taken). nowMs: a monotonic millisecond clock.
    public bool TryAcquire(IPAddress address, long nowMs)
    {
        if (!Enabled) return true;
        ref Slot slot = ref _slots[IndexOf(KeyOf(address))];
        if (!slot.Used)
        {
            slot.Used = true;
            slot.Tokens = _capacity;
        }
        else
        {
            long elapsed = Math.Max(0, nowMs - slot.LastMs);
            // Capped before the multiply, so a long pause cannot overflow.
            slot.Tokens = Math.Min(_capacity, slot.Tokens + Math.Min(elapsed, _capacity) * _perMs);
        }
        slot.LastMs = nowMs;
        if (slot.Tokens < Unit) return false;
        slot.Tokens -= Unit;
        return true;
    }

    // 기능: IP 주소가 쓰는 Slot 번호를 돌려준다. 테스트에서 두 주소의 Slot 충돌 여부를 확인할 때 쓴다.
    // 입력: address - 확인할 원격 IP.
    // 출력: 0 이상 Slots 미만의 Slot 번호.
    internal static int SlotOf(IPAddress address) => IndexOf(KeyOf(address));

    // 기능: 주소 Key를 표의 Slot 번호로 바꾼다.
    // 입력: key - 32bit 주소 Key.
    // 출력: 0 이상 Slots 미만의 Slot 번호.
    // Fibonacci hashing: the top bits of key * 2^32/phi, so neighbouring addresses spread over the table.
    private static int IndexOf(uint key) => (int)((key * 2654435769u) >> (32 - SlotBits));

    // 기능: IP 주소를 32bit Key로 바꾼다.
    // 입력: address - 변환할 원격 IP.
    // 출력: IPv4는 주소 값 그대로, IPv6는 4Byte씩 XOR한 값. 주소를 쓸 수 없으면 0.
    // The IPv4 address as a number. The server binds IPv4 only; an IPv6 address (not expected) is folded into 32 bits,
    // so two such addresses may share a bucket. No allocation.
    private static uint KeyOf(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out int written)) return 0;
        uint key = 0;
        for (int i = 0; i + 4 <= written; i += 4) key ^= BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(i, 4));
        return key;
    }
}
