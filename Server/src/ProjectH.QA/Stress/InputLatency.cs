namespace ProjectH.QA;

// D43 (request §90-91, R1): input -> server-applied latency. A headless actor notes when it sent each input (its own
// ring of SendRing entries, keyed by Seq) and, when a snapshot acknowledges inputs (AckInputSeq), records how long
// each acknowledged input took. One histogram for every headless actor of the run.
//
// What the number contains: the server buffering the input until its next tick, the 15 Hz snapshot interval that
// carries the ack, and the actor pump's tick granularity (the send and the ack are both read on pump ticks, about
// 33 ms apart). "minus RTT" subtracts the connection's round trip time (clamped at 0), leaving roughly the server-side
// part. It is a client-side measure of the whole loop, not a server tick cost.
//
// Threads: the pump thread records (Interlocked), a `measure` step reads (Volatile) at its start and end and subtracts.
// Memory is fixed: two arrays of Buckets + 1 counters. Lives as long as the run's ActorManager.
public sealed class InputLatencyHistogram
{
    public const int Buckets = 2000;   // 1 ms each; the last counter holds everything at or above Buckets ms

    private readonly long[] _raw = new long[Buckets + 1];
    private readonly long[] _minusRtt = new long[Buckets + 1];

    // 기능: 입력 한 건의 지연을 raw histogram과 RTT 차감 histogram에 기록한다.
    // 입력: rawMs - 전송부터 ack까지 ms, rttMs - 그 시점 연결 RTT ms.
    // 출력: 반환값 없음. 두 bucket 카운터가 1씩 는다.
    public void Record(double rawMs, double rttMs)
    {
        Interlocked.Increment(ref _raw[Bucket(rawMs)]);
        Interlocked.Increment(ref _minusRtt[Bucket(rawMs - rttMs)]);
    }

    // 기능: 현재 누적 카운트를 복사한다.
    // 입력: 없음.
    // 출력: Raw·MinusRtt 배열 복사본을 담은 LatencyCounts.
    public LatencyCounts Snapshot()
    {
        var raw = new long[Buckets + 1];
        var net = new long[Buckets + 1];
        for (int i = 0; i <= Buckets; i++)
        {
            raw[i] = Volatile.Read(ref _raw[i]);
            net[i] = Volatile.Read(ref _minusRtt[i]);
        }
        return new LatencyCounts(raw, net);
    }

    // 기능: ms를 1 ms bucket index로 바꾼다.
    // 입력: ms - 지연 ms.
    // 출력: bucket index(NaN·0 이하는 0, Buckets 이상은 overflow bucket).
    private static int Bucket(double ms) => double.IsNaN(ms) || ms <= 0 ? 0 : ms >= Buckets ? Buckets : (int)ms;
}

// Cumulative counts at one moment; Since(earlier) gives the counts of a measure phase.
public sealed record LatencyCounts(long[] Raw, long[] MinusRtt)
{
    public static readonly LatencyCounts Empty = new(new long[InputLatencyHistogram.Buckets + 1], new long[InputLatencyHistogram.Buckets + 1]);

    // 기능: 이전 snapshot과의 차이(한 측정 구간의 카운트)를 만든다.
    // 입력: earlier - 구간 시작의 snapshot.
    // 출력: bucket별 차이(0 미만은 0)를 담은 LatencyCounts.
    public LatencyCounts Since(LatencyCounts earlier)
    {
        var raw = new long[Raw.Length];
        var net = new long[MinusRtt.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = Math.Max(0, Raw[i] - earlier.Raw[i]);
            net[i] = Math.Max(0, MinusRtt[i] - earlier.MinusRtt[i]);
        }
        return new LatencyCounts(raw, net);
    }

    public long Count => Raw.Sum();

    // 기능: 카운트에서 p50·p95·p99·max를 raw와 RTT 차감 각각 계산한다.
    // 입력: 없음.
    // 출력: LatencyStats. 기록이 없으면 null.
    // Null when nothing was recorded ("Not Available" in the report).
    public LatencyStats? Stats()
    {
        long count = Count;
        if (count == 0) return null;
        return new LatencyStats(count,
            Percentile(Raw, count, 0.50), Percentile(Raw, count, 0.95), Percentile(Raw, count, 0.99), Max(Raw),
            Percentile(MinusRtt, count, 0.50), Percentile(MinusRtt, count, 0.95), Percentile(MinusRtt, count, 0.99), Max(MinusRtt));
    }

    // 기능: 1 ms bucket 카운트에서 nearest rank 백분위를 구한다.
    // 입력: counts - bucket 카운트, total - 전체 표본 수, fraction - 0..1 백분위.
    // 출력: 해당 bucket의 하한 ms(overflow bucket은 Buckets ms).
    // Nearest rank over 1 ms buckets: the bucket's lower edge (the overflow bucket reads as Buckets ms).
    internal static double Percentile(long[] counts, long total, double fraction)
    {
        long rank = Math.Max(1, (long)Math.Ceiling(fraction * total));
        long seen = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            seen += counts[i];
            if (seen >= rank) return i;
        }
        return counts.Length - 1;
    }

    // 기능: 표본이 있는 가장 큰 bucket을 찾는다.
    // 입력: counts - bucket 카운트.
    // 출력: 그 bucket의 ms, 표본이 없으면 0.
    private static double Max(long[] counts)
    {
        for (int i = counts.Length - 1; i >= 0; i--) if (counts[i] > 0) return i;
        return 0;
    }
}

// Milliseconds (1 ms buckets). MinusRtt* = the latency minus the round trip time at that moment, clamped at 0.
public sealed record LatencyStats(long Samples, double P50Ms, double P95Ms, double P99Ms, double MaxMs,
    double MinusRttP50Ms, double MinusRttP95Ms, double MinusRttP99Ms, double MinusRttMaxMs);
