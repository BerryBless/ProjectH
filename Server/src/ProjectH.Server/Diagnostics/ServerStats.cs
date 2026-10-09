using System.Threading;

namespace ProjectH.Server.Diagnostics;

public readonly struct StatsCounters
{
    // 기능: 한 통계 구간의 패킷·바이트·잘못된 패킷·입력 드롭 수를 담는 값을 만든다.
    // 입력: packetsIn - 받은 패킷 수, bytesIn - 받은 바이트, packetsOut - 보낸 패킷 수, bytesOut - 보낸 바이트, badPackets - 잘못된 패킷 수, inputDrops - 버린 입력 수.
    // 출력: 여섯 값을 그대로 담은 StatsCounters.
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

    // 기능: 받은 패킷 하나를 패킷 수와 바이트에 더한다(수신 스레드).
    // 입력: bytes - 패킷 크기.
    // 출력: 반환값 없음.
    public void AddIn(int bytes)
    {
        Interlocked.Increment(ref _packetsIn);
        Interlocked.Add(ref _bytesIn, bytes);
    }

    // 기능: 보낸 패킷 하나를 패킷 수와 바이트에 더한다(Game Loop).
    // 입력: bytes - 패킷 크기.
    // 출력: 반환값 없음.
    public void AddOut(int bytes)
    {
        Interlocked.Increment(ref _packetsOut);
        Interlocked.Add(ref _bytesOut, bytes);
    }

    // 기능: 잘못된 패킷 하나를 센다(수신 스레드).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddBadPacket() => Interlocked.Increment(ref _badPackets);

    // 기능: 버린 입력 패킷 하나를 센다(수신 스레드).
    // 입력: 없음.
    // 출력: 반환값 없음.
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

    // 기능: 지난 구간의 수치를 꺼내 0으로 되돌리고, 패킷·바이트는 시작부터의 합계에 접는다. Game Loop만 부른다.
    // 입력: 없음.
    // 출력: 지난 TakeDelta 이후 쌓인 StatsCounters.
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
