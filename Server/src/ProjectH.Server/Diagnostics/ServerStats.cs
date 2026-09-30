using System.Threading;

namespace ProjectH.Server.Diagnostics;

public readonly struct StatsCounters
{
    public StatsCounters(long packetsIn, long bytesIn, long packetsOut, long bytesOut, long badPackets, long inputDrops)
    {
        PacketsIn = packetsIn;
        BytesIn = bytesIn;
        PacketsOut = packetsOut;
        BytesOut = bytesOut;
        BadPackets = badPackets;
        InputDrops = inputDrops;
    }

    public long PacketsIn { get; }
    public long BytesIn { get; }
    public long PacketsOut { get; }
    public long BytesOut { get; }
    public long BadPackets { get; }
    public long InputDrops { get; }
}

// Counters written from LiteNetLib's threads and the game loop, read by the game loop for the
// periodic stats log. Interlocked only, no lock: counters are independent, so a slightly
// inconsistent view across counters is acceptable for monitoring.
public sealed class ServerStats
{
    private long _packetsIn;
    private long _bytesIn;
    private long _packetsOut;
    private long _bytesOut;
    private long _badPackets;
    private long _inputDrops;

    public void AddIn(int bytes)
    {
        Interlocked.Increment(ref _packetsIn);
        Interlocked.Add(ref _bytesIn, bytes);
    }

    public void AddOut(int bytes)
    {
        Interlocked.Increment(ref _packetsOut);
        Interlocked.Add(ref _bytesOut, bytes);
    }

    public void AddBadPacket() => Interlocked.Increment(ref _badPackets);

    public void AddInputDrop() => Interlocked.Increment(ref _inputDrops);

    public StatsCounters TakeDelta() => new StatsCounters(
        Interlocked.Exchange(ref _packetsIn, 0),
        Interlocked.Exchange(ref _bytesIn, 0),
        Interlocked.Exchange(ref _packetsOut, 0),
        Interlocked.Exchange(ref _bytesOut, 0),
        Interlocked.Exchange(ref _badPackets, 0),
        Interlocked.Exchange(ref _inputDrops, 0));
}
