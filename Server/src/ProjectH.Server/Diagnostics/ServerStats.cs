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

    // QA-1: packets (and, for the stress runs, bytes) since the start. TakeDelta folds each delta into these (game loop thread only), so the packet path pays
    // nothing extra; the totals are the folded part plus the counter not yet taken. Read on the game loop thread only.
    private long _takenPacketsIn;
    private long _takenPacketsOut;
    private long _takenBytesIn;
    private long _takenBytesOut;

    public long PacketsInTotal => _takenPacketsIn + Interlocked.Read(ref _packetsIn);
    public long PacketsOutTotal => _takenPacketsOut + Interlocked.Read(ref _packetsOut);
    public long BytesInTotal => _takenBytesIn + Interlocked.Read(ref _bytesIn);
    public long BytesOutTotal => _takenBytesOut + Interlocked.Read(ref _bytesOut);

    public StatsCounters TakeDelta()
    {
        var delta = new StatsCounters(
            Interlocked.Exchange(ref _packetsIn, 0),
            Interlocked.Exchange(ref _bytesIn, 0),
            Interlocked.Exchange(ref _packetsOut, 0),
            Interlocked.Exchange(ref _bytesOut, 0),
            Interlocked.Exchange(ref _badPackets, 0),
            Interlocked.Exchange(ref _inputDrops, 0));
        _takenPacketsIn += delta.PacketsIn;
        _takenPacketsOut += delta.PacketsOut;
        _takenBytesIn += delta.BytesIn;
        _takenBytesOut += delta.BytesOut;
        return delta;
    }
}
