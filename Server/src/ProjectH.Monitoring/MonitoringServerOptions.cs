namespace ProjectH.Monitoring;

// Monitoring D11: bound from the "MonitoringServer" section. Validate() runs when the app is created, so a bad value stops
// the process at start instead of producing an empty ring or a server that is never offline.
public sealed class MonitoringServerOptions
{
    public int HistoryMinutes { get; set; } = 10;
    // How often a game server is expected to post (its Monitoring:IntervalSeconds); sizes the ring with HistoryMinutes.
    public int ExpectedIntervalSeconds { get; set; } = 5;
    // No snapshot for this long = offline. 3 intervals by default: one failed post (2 s timeout) does not flip the state.
    public int OfflineThresholdSeconds { get; set; } = 15;
    public int SweepIntervalSeconds { get; set; } = 5;
    public int MaxServers { get; set; } = 32;
    // ObservedAt farther than this from the monitoring clock is rejected (a wrong clock would misplace the sample).
    public int MaxClockSkewSeconds { get; set; } = 300;
    // 16.7 ms = half of the 33.3 ms tick budget at 30 Hz (D11).
    public double TickP95WarningMs { get; set; } = 16.7;
    // 0 = off until the 50-player working set is measured (D11, request §90).
    public long MemoryWarningBytes { get; set; }
    // Empty = ingest is not authenticated. Set with the environment variable MonitoringServer__IngestToken, never in appsettings.
    public string IngestToken { get; set; } = "";

    // 기능: 서버별 링 용량을 계산한다(HistoryMinutes × 60 / ExpectedIntervalSeconds, 최소 1).
    // 입력: 없음.
    // 출력: 서버 하나가 보관하는 샘플 수.
    public int HistoryCapacity => Math.Max(1, HistoryMinutes * 60 / Math.Max(1, ExpectedIntervalSeconds));

    // 기능: 시작 때 설정 값을 검사한다.
    // 입력: 없음.
    // 출력: 맞으면 null, 틀리면 첫 번째로 틀린 키와 허용 범위를 적은 문장.
    public string? Validate()
    {
        if (HistoryMinutes < 1 || HistoryMinutes > 120) return "MonitoringServer:HistoryMinutes must be 1-120.";
        if (ExpectedIntervalSeconds < 1 || ExpectedIntervalSeconds > 60) return "MonitoringServer:ExpectedIntervalSeconds must be 1-60.";
        if (OfflineThresholdSeconds < ExpectedIntervalSeconds * 2 || OfflineThresholdSeconds > 3600)
            return "MonitoringServer:OfflineThresholdSeconds must be at least 2 × ExpectedIntervalSeconds and at most 3600.";
        if (SweepIntervalSeconds < 1 || SweepIntervalSeconds > 60) return "MonitoringServer:SweepIntervalSeconds must be 1-60.";
        if (MaxServers < 1 || MaxServers > 1000) return "MonitoringServer:MaxServers must be 1-1000.";
        if (MaxClockSkewSeconds < 1 || MaxClockSkewSeconds > 86400) return "MonitoringServer:MaxClockSkewSeconds must be 1-86400.";
        if (!double.IsFinite(TickP95WarningMs) || TickP95WarningMs < 0) return "MonitoringServer:TickP95WarningMs must be 0 or more.";
        if (MemoryWarningBytes < 0) return "MonitoringServer:MemoryWarningBytes must be 0 (off) or more.";
        return null;
    }
}
