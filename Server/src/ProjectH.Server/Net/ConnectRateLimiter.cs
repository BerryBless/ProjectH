using System;
using System.Buffers.Binary;
using System.Net;

namespace ProjectH.Server.Net;

// Server review M2: connection requests per remote IP, as a token bucket (burst at once, then perSecond). A request
// over the rate is refused before Accept, so connect/disconnect churn from one address cannot fill the shared Control
// channel and get other players' connects and joins closed with ServerError.
//
// Size: a fixed table of Slots entries, made once, indexed by a hash of the address; it never grows. Two addresses in
// one slot overwrite each other (the newer one starts with a full bucket), so a collision can only let a request
// through, never refuse an address it should not. Nothing is removed: a slot is reused by the next address that hashes
// to it.
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
        public uint Key;
        public long Tokens;   // thousandths of a request
        public long LastMs;
    }

    private readonly Slot[] _slots;
    private readonly long _capacity;
    private readonly long _perMs;

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

    // True when the address may connect now (one token taken). nowMs: a monotonic millisecond clock.
    public bool TryAcquire(IPAddress address, long nowMs)
    {
        if (!Enabled) return true;
        uint key = KeyOf(address);
        ref Slot slot = ref _slots[IndexOf(key)];
        if (!slot.Used || slot.Key != key)
        {
            slot.Used = true;
            slot.Key = key;
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

    internal static int SlotOf(IPAddress address) => IndexOf(KeyOf(address));

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
