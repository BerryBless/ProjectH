using System;
using System.Buffers.Binary;
using System.Net;
using System.Threading;

namespace ProjectH.Server.Net;

// Why a connection request was refused by the per-address table (review fix A2, A6).
public enum ConnectRefusal
{
    None,
    Rate,      // over the per-IP request rate (server review M2)
    PerIp,     // the address already holds MaxConnectionsPerIp connections (review fix A2, SEC-3)
    Penalty,   // the address is penalized for repeated player failures (review fix A6, SEC-14)
}

// Server review M2, review fixes A2 and A6: connection requests per remote IP, as a token bucket (burst at once, then
// perSecond), the connections each address holds now (at most maxActivePerAddress), and a penalty deadline. A request
// over any of them is refused before Accept, so connect/disconnect churn from one address cannot fill the shared Control
// channel and one address cannot hold every slot.
//
// Size: a fixed table of Slots entries, made once, indexed by a salted hash of the address; it never grows. Nothing is
// removed. Addresses that hash to one slot share its bucket, its connection count and its penalty: only an empty slot
// starts with a full burst, so alternating two colliding addresses gets no more than one bucket (review round 1; an
// earlier version gave a new address a full bucket, which alternating addresses could use to get past the limit). The
// trade-off: a normal address that collides with a busy one shares its limits and may be refused; with 1024 slots that is
// rare, and the salt (RandomNumberGenerator at startup, review fix A2) keeps an attacker from picking colliding addresses.
// Threads: TryAcquire runs on LiteNetLib's single receive thread only (OnConnectionRequest; the server binds IPv4 only,
// see PeerState), so Used, Tokens and LastMs need no synchronization. Two fields are shared, both without a lock:
//   - Active is raised at Accept by the receive thread (Acquired) and lowered at the disconnect by whatever thread ran
//     OnPeerDisconnected: with UnsyncedEvents that is the game loop's Close (peer.Disconnect raises it in place) or a
//     LiteNetLib thread (review A round 1). Interlocked.Increment and a CompareExchange loop (never below 0) change it;
//     TryAcquire reads it with Volatile.Read. Only the receive thread raises it, so the check-then-count in
//     TryAcquire/Acquired cannot admit past the limit; a concurrent Release only lowers it.
//   - PenaltyUntilMs: the game loop calls Penalize (Interlocked), the receive thread reads it with Volatile.Read.
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
        public int Active;    // connections accepted and not yet disconnected (Interlocked: see the class comment)
        public long PenaltyUntilMs;   // refused until this time (Environment.TickCount64); written by Penalize
    }

    private readonly Slot[] _slots = new Slot[Slots];
    private readonly long _capacity;
    private readonly long _perMs;
    private readonly int _maxActive;
    private readonly uint _hashSalt;

    // 기능: IP별 연결 요청 표를 만든다(서버 리뷰 M2의 빈도 제한 그대로, 동시 연결 상한·벌점은 끔, salt 0).
    // 입력: burst - 한 번에 받는 요청 수, perSecond - 초당 채워지는 수(둘 중 하나라도 0이면 빈도 제한 끔).
    // 출력: 빈 표를 가진 ConnectRateLimiter.
    public ConnectRateLimiter(int burst, int perSecond) : this(burst, perSecond, 0, 0)
    {
    }

    // 기능: IP별 연결 요청 표를 만든다(리뷰 수정 A2: 동시 연결 상한과 해시 salt, A6: 벌점). 표는 항상 만든다(빈도 제한이 꺼져도
    //   동시 연결 수와 벌점은 쓴다).
    // 입력: burst·perSecond - 빈도 Token Bucket(하나라도 0이면 빈도 제한 끔), maxActivePerAddress - 한 칸의 동시 연결 상한(0 = 끔),
    //   hashSalt - 주소 해시에 섞는 값(서버는 시작 때 난수).
    // 출력: 모든 칸이 빈 ConnectRateLimiter.
    public ConnectRateLimiter(int burst, int perSecond, int maxActivePerAddress, uint hashSalt)
    {
        if (burst > 0 && perSecond > 0)
        {
            _capacity = burst * Unit;
            _perMs = perSecond;
        }
        _maxActive = Math.Max(0, maxActivePerAddress);
        _hashSalt = hashSalt;
    }

    // Whether the per-IP request rate is on (the connection limit and the penalty do not depend on it).
    public bool Enabled => _capacity > 0;

    // 기능: 이 주소가 지금 연결해도 되는지 묻는다(토큰 하나를 쓴다). 연결 수는 세지 않는다(Accept 뒤 Acquired).
    // 입력: address - 요청한 주소, nowMs - 단조 증가 ms 시계(Environment.TickCount64).
    // 출력: 받아도 되면 true.
    public bool TryAcquire(IPAddress address, long nowMs) => TryAcquire(address, nowMs, out _);

    // 기능: 이 주소가 지금 연결해도 되는지 묻는다. 빈도(토큰 하나를 쓴다) → 벌점 → 동시 연결 수 순서로 본다. 수신 스레드 전용.
    // 입력: address - 요청한 주소, nowMs - 단조 증가 ms 시계(Environment.TickCount64), refusal - 거절 이유를 받을 곳.
    // 출력: 받아도 되면 true(refusal = None), 아니면 false와 거절 이유.
    public bool TryAcquire(IPAddress address, long nowMs, out ConnectRefusal refusal)
    {
        ref Slot slot = ref _slots[SlotOf(address)];
        if (Enabled)
        {
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
            if (slot.Tokens < Unit)
            {
                refusal = ConnectRefusal.Rate;
                return false;
            }
            slot.Tokens -= Unit;
        }
        if (Volatile.Read(ref slot.PenaltyUntilMs) > nowMs)
        {
            refusal = ConnectRefusal.Penalty;
            return false;
        }
        if (_maxActive > 0 && Volatile.Read(ref slot.Active) >= _maxActive)
        {
            refusal = ConnectRefusal.PerIp;
            return false;
        }
        refusal = ConnectRefusal.None;
        return true;
    }

    // 기능: Accept가 끝난 연결 하나를 그 칸에 센다(리뷰 수정 A2). Accept한 수신 스레드가 부른다. Release가 다른 스레드에서
    //   겹칠 수 있어 Interlocked로 더한다(리뷰 A 1차).
    // 입력: slot - SlotOf(주소).
    // 출력: 반환값 없음. 칸의 동시 연결 수가 1 는다.
    public void Acquired(int slot) => Interlocked.Increment(ref _slots[slot].Active);

    // 기능: 끊긴 연결 하나를 그 칸에서 뺀다(리뷰 수정 A2). OnPeerDisconnected를 부른 스레드(Game Loop의 Close, LiteNetLib 스레드)나
    //   Accept 뒤 예외 경로의 수신 스레드가 부른다. CompareExchange 반복으로 1을 빼고 0 아래로 내려가지 않는다(리뷰 A 1차).
    // 입력: slot - 연결이 Acquired로 세어진 칸.
    // 출력: 반환값 없음. 칸의 동시 연결 수가 1 준다(이미 0이면 그대로).
    public void Release(int slot)
    {
        ref int active = ref _slots[slot].Active;
        while (true)
        {
            int current = Volatile.Read(ref active);
            if (current <= 0) return;
            if (Interlocked.CompareExchange(ref active, current - 1, current) == current) return;
        }
    }

    // 기능: 칸의 지금 동시 연결 수를 읽는다(시험·진단용).
    // 입력: slot - 칸 번호.
    // 출력: 그 칸에 세어진 연결 수.
    internal int ActiveOf(int slot) => Volatile.Read(ref _slots[slot].Active);

    // 기능: 칸 하나를 untilMs까지 거절하게 한다(리뷰 수정 A6: 같은 출처의 반복 플레이어 실패). 어느 스레드에서 불러도 된다(Game Loop).
    // 입력: slot - SlotOf(주소), untilMs - 거절이 끝나는 시각(Environment.TickCount64 기준).
    // 출력: 반환값 없음. 그 칸의 다음 요청은 untilMs 전까지 Penalty로 거절된다.
    public void Penalize(int slot, long untilMs) => Interlocked.Exchange(ref _slots[slot].PenaltyUntilMs, untilMs);

    // 기능: 이 표에서 주소가 쓰는 칸 번호(salt 포함)를 구한다.
    // 입력: address - 원격 주소.
    // 출력: 0–Slots-1 칸 번호.
    public int SlotOf(IPAddress address) => IndexOf(KeyOf(address) ^ _hashSalt);

    // 기능: salt 0 표의 칸 번호를 구한다(테스트가 충돌하는 주소를 찾을 때).
    // 입력: address - 원격 주소.
    // 출력: 0–Slots-1 칸 번호.
    internal static int SlotOf(IPAddress address, uint salt = 0) => IndexOf(KeyOf(address) ^ salt);

    // Fibonacci hashing: the top bits of key * 2^32/phi, so neighbouring addresses spread over the table.
    private static int IndexOf(uint key) => (int)((key * 2654435769u) >> (32 - SlotBits));

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

// Review fix A2 (SEC-3): accepted connections per second over all addresses, a token bucket of burst (MaxPlayers) refilled
// at perSecond. ServerOptions.ControlChannelCapacity is sized from it: no more connections than this can be accepted
// between two game loop drains. LiteNetLib's receive thread only (OnConnectionRequest), so no synchronization. One
// bucket, made once; nothing grows.
public sealed class AcceptRateLimiter
{
    private const long Unit = 1000;
    private readonly long _capacity;
    private readonly long _perMs;
    private long _tokens;
    private long _lastMs;
    private bool _started;

    // 기능: 전역 수락 Token Bucket을 만든다(처음 요청 때 가득 찬다).
    // 입력: burst - 한 번에 받는 수(1 이상), perSecond - 초당 채워지는 수(1 이상).
    // 출력: 아직 시작하지 않은 AcceptRateLimiter.
    public AcceptRateLimiter(int burst, int perSecond)
    {
        if (burst < 1) throw new ArgumentOutOfRangeException(nameof(burst));
        if (perSecond < 1) throw new ArgumentOutOfRangeException(nameof(perSecond));
        _capacity = burst * Unit;
        _perMs = perSecond;
    }

    // 기능: 연결 하나를 지금 받아도 되는지 묻고 토큰 하나를 쓴다. 수신 스레드 전용.
    // 입력: nowMs - 단조 증가 ms 시계(Environment.TickCount64).
    // 출력: 토큰이 있으면 true(하나 썼다), 없으면 false.
    public bool TryAcquire(long nowMs)
    {
        if (!_started)
        {
            _started = true;
            _tokens = _capacity;
        }
        else
        {
            long elapsed = Math.Max(0, nowMs - _lastMs);
            _tokens = Math.Min(_capacity, _tokens + Math.Min(elapsed, _capacity) * _perMs);   // capped before the multiply
        }
        _lastMs = nowMs;
        if (_tokens < Unit) return false;
        _tokens -= Unit;
        return true;
    }
}
