namespace ProjectH.Monitoring.Contracts;

// Monitoring D1, D8: what both sides must agree on besides the snapshot shape.
public static class MonitoringContract
{
    public const string IngestPath = "/api/ingest/metrics";
    public const string TokenHeader = "X-Monitoring-Token";
    // Kestrel body limit on the monitoring server; a snapshot is about 1 KB.
    public const int MaxBodyBytes = 16 * 1024;
    public const int MaxServerIdLength = 64;
    // Version and MatchState.
    public const int MaxTextLength = 64;

    // 기능: ServerId가 규칙(1-64자, [A-Za-z0-9._-])에 맞는지 본다. Game Server는 시작 때, Monitoring Server는 Ingest 때 같은 규칙을 쓴다.
    // 입력: id - 검사할 값(null 허용).
    // 출력: 맞으면 true, 비었거나 64자를 넘거나 다른 문자가 있으면 false.
    public static bool IsValidServerId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxServerIdLength) return false;
        foreach (char c in id)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
            if (!ok) return false;
        }
        return true;
    }
}
