using ProjectH.Monitoring.Contracts;
using ProjectH.Monitoring.Storage;

namespace ProjectH.Monitoring.Status;

// Monitoring D9, D10: the state is computed when read, from the last receipt time and the configured thresholds.
internal static class ServerStatus
{
    // 기능: 서버 하나의 상태(Online/Warning/Offline)를 정하고 경고 문장을 모은다. MetricStore의 Lock 안에서 불리므로 계산만 한다.
    // 입력: h - 서버 기록, now - 지금, o - 임계값 설정, warnings - 경고를 담을 목록.
    // 출력: 마지막 수신이 OfflineThresholdSeconds보다 오래면 Offline, 아니면 경고가 있으면 Warning, 없으면 Online. warnings에 이유가 더해진다.
    public static ServerState Of(ServerHistory h, DateTimeOffset now, MonitoringServerOptions o, List<string> warnings)
    {
        double silent = (now - h.LastReceivedAt).TotalSeconds;
        if (silent > o.OfflineThresholdSeconds)
        {
            warnings.Add($"offline: no snapshot for {silent:0} s (threshold {o.OfflineThresholdSeconds} s)");
            return ServerState.Offline;
        }
        ServerMonitoringSnapshot s = h.Latest;
        if (s.TickP95Ms >= o.TickP95WarningMs) warnings.Add($"tick p95 {s.TickP95Ms:0.00} ms is at or above {o.TickP95WarningMs} ms");
        if (o.MemoryWarningBytes > 0 && s.WorkingSetBytes >= o.MemoryWarningBytes)
            warnings.Add($"working set {s.WorkingSetBytes / 1048576.0:0} MB is at or above {o.MemoryWarningBytes / 1048576.0:0} MB");
        return warnings.Count > 0 ? ServerState.Warning : ServerState.Online;
    }
}
