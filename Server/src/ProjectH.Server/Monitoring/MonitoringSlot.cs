using System.Threading;
using ProjectH.Monitoring.Contracts;

namespace ProjectH.Server.Monitoring;

// Monitoring D4: capacity 1. The game loop publishes (Exchange), the sender takes (Exchange null). A slow or dead
// monitoring server costs the game server one snapshot of memory, never a queue. No lock.
// Lifetime: a DI singleton, created only when Monitoring:Enabled; it holds at most one snapshot, removed by Take or
// replaced by the next Publish.
public sealed class MonitoringSlot
{
    private ServerMonitoringSnapshot? _latest;
    private long _overwritten;

    // 기능: 최신 Snapshot을 넣는다(아직 보내지 않은 것은 버리고 센다). Game Loop 스레드.
    // 입력: snapshot - 방금 만든 Snapshot.
    // 출력: 반환값 없음. 슬롯에 snapshot 하나만 남고, 덮어썼으면 Overwritten이 1 늘어난다.
    public void Publish(ServerMonitoringSnapshot snapshot)
    {
        if (Interlocked.Exchange(ref _latest, snapshot) != null) Interlocked.Increment(ref _overwritten);
    }

    // 기능: Snapshot을 꺼내고 슬롯을 비운다. Sender 스레드.
    // 입력: 없음.
    // 출력: 마지막으로 넣은 Snapshot, 비어 있으면 null.
    public ServerMonitoringSnapshot? Take() => Interlocked.Exchange(ref _latest, null);

    // Snapshots that were replaced before being sent (tests; the monitoring server was slower than the interval).
    public long Overwritten => Interlocked.Read(ref _overwritten);
}
