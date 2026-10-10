using ProjectH.Monitoring.Contracts;
using ProjectH.Monitoring.Status;

namespace ProjectH.Monitoring.Storage;

// Monitoring D9: every server's ring behind one lock. Writers: ingest requests (one per game server every few seconds).
// Readers: the UI's polling and the OfflineSweeper. Nothing inside the lock logs, waits or calls out.
// Lifetime: the process (restart = empty, D10). A registered ServerId is never removed; the dictionary is bounded by
// MaxServers (a new id beyond it is refused with 429) and each ring by HistoryCapacity.
public sealed class MetricStore
{
    // Lock ordering: the only other lock in this process is IngestLog._gate. The two are never held together —
    // IngestEndpoint calls IngestLog only after store.Add has returned, and nothing under this lock calls IngestLog
    // or a logger — so no ordering between them is needed.
    private readonly object _gate = new();
    private readonly Dictionary<string, ServerHistory> _servers = new(StringComparer.Ordinal);
    private readonly MonitoringServerOptions _options;

    // 기능: 설정에 맞는 빈 저장소를 만든다.
    // 입력: options - 검증이 끝난 설정.
    // 출력: 서버가 하나도 없는 MetricStore.
    public MetricStore(MonitoringServerOptions options) => _options = options;

    // 기능: 등록된 서버 수를 Lock 안에서 읽는다.
    // 입력: 없음.
    // 출력: 서버 수(0-MaxServers).
    public int Count
    {
        get { lock (_gate) return _servers.Count; }
    }

    // 기능: 검증이 끝난 Snapshot을 그 서버의 링에 넣는다(새 서버는 등록, MaxServers면 거절).
    // 입력: snapshot - 받은 값, receivedAt - 받은 시각(이 서버의 시계).
    // 출력: 호출자가 Lock 밖에서 로그를 남길 결과(FirstSeen / Returned / Stored / TooManyServers).
    public IngestOutcome Add(ServerMonitoringSnapshot snapshot, DateTimeOffset receivedAt)
    {
        var sample = new MetricSample(receivedAt, snapshot);
        lock (_gate)
        {
            if (!_servers.TryGetValue(snapshot.ServerId, out ServerHistory? history))
            {
                if (_servers.Count >= _options.MaxServers) return IngestOutcome.TooManyServers;
                _servers.Add(snapshot.ServerId, new ServerHistory(_options.HistoryCapacity, sample));
                return IngestOutcome.FirstSeen;
            }
            bool returned = (receivedAt - history.LastReceivedAt).TotalSeconds > _options.OfflineThresholdSeconds || history.OfflineLogged;
            history.OfflineLogged = false;
            history.Add(sample);
            return returned ? IngestOutcome.Returned : IngestOutcome.Stored;
        }
    }

    // 기능: 모든 서버의 요약을 이름순으로 돌려준다(최신 Snapshot만, History는 복사하지 않는다).
    // 입력: now - 지금.
    // 출력: 요약 목록(호출자 소유, 최대 MaxServers개).
    public IReadOnlyList<ServerSummary> List(DateTimeOffset now)
    {
        lock (_gate)
        {
            var result = new List<ServerSummary>(_servers.Count);
            foreach ((string id, ServerHistory h) in _servers) result.Add(Summary(id, h, now));
            result.Sort((a, b) => string.CompareOrdinal(a.ServerId, b.ServerId));
            return result;
        }
    }

    // 기능: 서버 하나의 요약을 돌려준다.
    // 입력: serverId - 서버, now - 지금.
    // 출력: 요약, 모르는 서버면 null.
    public ServerSummary? Get(string serverId, DateTimeOffset now)
    {
        lock (_gate) return _servers.TryGetValue(serverId, out ServerHistory? h) ? Summary(serverId, h, now) : null;
    }

    // 기능: 최근 minutes분의 샘플을 오래된 순서로 복사한다. minutes는 1-HistoryMinutes로 Clamp한다(무제한 조회 불가, D12).
    // 입력: serverId - 서버, minutes - 구간(분), now - 지금.
    // 출력: 샘플 목록(호출자 소유, 최대 HistoryCapacity개), 모르는 서버면 null.
    public IReadOnlyList<MetricSample>? History(string serverId, int minutes, DateTimeOffset now)
    {
        minutes = Math.Clamp(minutes, 1, _options.HistoryMinutes);
        DateTimeOffset from = now - TimeSpan.FromMinutes(minutes);
        lock (_gate)
        {
            if (!_servers.TryGetValue(serverId, out ServerHistory? h)) return null;
            var result = new List<MetricSample>(h.Count);
            h.CopySince(from, result);
            return result;
        }
    }

    // 기능: 새로 Offline이 된 서버를 찾아 표시한다(서버마다 한 번만 돌려준다; 다음 Snapshot이 표시를 지운다).
    // 입력: now - 지금.
    // 출력: (ServerId, 마지막 수신 시각) 목록. 없으면 빈 목록.
    public IReadOnlyList<(string ServerId, DateTimeOffset LastSeen)> SweepOffline(DateTimeOffset now)
    {
        List<(string, DateTimeOffset)>? result = null;
        lock (_gate)
        {
            foreach ((string id, ServerHistory h) in _servers)
            {
                if (h.OfflineLogged || (now - h.LastReceivedAt).TotalSeconds <= _options.OfflineThresholdSeconds) continue;
                h.OfflineLogged = true;
                (result ??= new List<(string, DateTimeOffset)>()).Add((id, h.LastReceivedAt));
            }
        }
        return result ?? (IReadOnlyList<(string, DateTimeOffset)>)Array.Empty<(string, DateTimeOffset)>();
    }

    // 기능: 서버 기록 하나를 요약으로 만든다(Lock 안에서 부른다).
    // 입력: id - 서버, h - 기록, now - 지금.
    // 출력: 상태·경고·최신 Snapshot을 담은 요약.
    private ServerSummary Summary(string id, ServerHistory h, DateTimeOffset now)
    {
        var warnings = new List<string>();
        ServerState state = ServerStatus.Of(h, now, _options, warnings);
        return new ServerSummary(id, state, h.LastReceivedAt, h.Latest.ObservedAt, warnings, h.Latest);
    }
}
