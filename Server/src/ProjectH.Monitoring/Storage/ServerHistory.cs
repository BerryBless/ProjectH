using ProjectH.Monitoring.Contracts;

namespace ProjectH.Monitoring.Storage;

// Monitoring D9: one server's ring of the newest samples. Owned by MetricStore and touched only under its lock.
// The array is allocated once (HistoryCapacity) and never grows; a full ring overwrites its oldest sample.
internal sealed class ServerHistory
{
    private readonly MetricSample[] _ring;
    private int _next;
    private int _count;

    // 기능: 첫 샘플을 담은 링을 만든다.
    // 입력: capacity - 샘플 수(1 이상), first - 첫 샘플.
    // 출력: 샘플 하나가 든 ServerHistory(LastReceivedAt·Latest = first).
    public ServerHistory(int capacity, MetricSample first)
    {
        _ring = new MetricSample[capacity];
        LastReceivedAt = first.ReceivedAt;
        Latest = first.Snapshot;
        Add(first);
    }

    public int Count => _count;
    public DateTimeOffset LastReceivedAt { get; private set; }
    public ServerMonitoringSnapshot Latest { get; private set; }
    // Set by SweepOffline when "offline" was logged; cleared by the next snapshot.
    public bool OfflineLogged { get; set; }

    // 기능: 샘플을 링에 넣는다(가득 차면 가장 오래된 것을 덮는다).
    // 입력: sample - 받은 샘플.
    // 출력: 반환값 없음. 링, LastReceivedAt, Latest가 갱신된다.
    public void Add(MetricSample sample)
    {
        _ring[_next] = sample;
        _next = (_next + 1) % _ring.Length;
        if (_count < _ring.Length) _count++;
        LastReceivedAt = sample.ReceivedAt;
        Latest = sample.Snapshot;
    }

    // 기능: from 이후에 받은 샘플을 오래된 순서로 복사한다.
    // 입력: from - 이 시각 이상만, into - 담을 목록.
    // 출력: 반환값 없음. into에 최대 용량만큼의 샘플이 더해진다.
    public void CopySince(DateTimeOffset from, List<MetricSample> into)
    {
        int oldest = (_next - _count + _ring.Length) % _ring.Length;
        for (int i = 0; i < _count; i++)
        {
            MetricSample s = _ring[(oldest + i) % _ring.Length];
            if (s.ReceivedAt >= from) into.Add(s);
        }
    }
}
