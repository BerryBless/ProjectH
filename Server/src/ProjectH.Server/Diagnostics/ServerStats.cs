using System.Threading;

namespace ProjectH.Server.Diagnostics;

public readonly struct StatsCounters
{
    // 기능: 한 통계 구간의 Counter 증가분을 묶는다.
    // 입력: packetsIn·bytesIn - 수신 Packet·Byte 수, packetsOut·bytesOut - 송신 Packet·Byte 수, badPackets - 잘못된 Packet 수, inputDrops - 버린 입력 수.
    // 출력: 받은 값을 그대로 담은 StatsCounters.
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

    // 기능: 수신 Packet 하나와 그 크기를 센다.
    // 입력: bytes - 수신한 Packet의 Byte 수.
    // 출력: 반환값 없음. 수신 Packet 수와 수신 Byte 수가 증가한다.
    public void AddIn(int bytes)
    {
        Interlocked.Increment(ref _packetsIn);
        Interlocked.Add(ref _bytesIn, bytes);
    }

    // 기능: 송신 Packet 하나와 그 크기를 센다.
    // 입력: bytes - 송신한 Packet의 Byte 수.
    // 출력: 반환값 없음. 송신 Packet 수와 송신 Byte 수가 증가한다.
    public void AddOut(int bytes)
    {
        Interlocked.Increment(ref _packetsOut);
        Interlocked.Add(ref _bytesOut, bytes);
    }

    // 기능: 주기 통계용 잘못된 Packet 수를 1 센다. LiteNetLib 수신 Thread에서 호출한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 다음 TakeDelta까지의 잘못된 Packet 수가 1 증가한다.
    public void AddBadPacket() => Interlocked.Increment(ref _badPackets);

    // 기능: 가득 찬 입력 Channel이 버린 입력 하나를 센다. LiteNetLib 수신 Thread에서 호출한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 다음 TakeDelta까지의 입력 Drop 수가 1 증가한다.
    public void AddInputDrop() => Interlocked.Increment(ref _inputDrops);

    // 기능: 지난 호출 이후 쌓인 Counter 값을 꺼내고 각 Counter를 0으로 되돌린다.
    // 입력: 없음.
    // 출력: 지난 호출 이후의 수신·송신·잘못된 Packet·입력 Drop 증가분.
    public StatsCounters TakeDelta() => new StatsCounters(
        Interlocked.Exchange(ref _packetsIn, 0),
        Interlocked.Exchange(ref _bytesIn, 0),
        Interlocked.Exchange(ref _packetsOut, 0),
        Interlocked.Exchange(ref _bytesOut, 0),
        Interlocked.Exchange(ref _badPackets, 0),
        Interlocked.Exchange(ref _inputDrops, 0));
}
