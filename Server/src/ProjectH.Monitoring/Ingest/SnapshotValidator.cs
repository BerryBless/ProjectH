using ProjectH.Monitoring.Contracts;

namespace ProjectH.Monitoring.Ingest;

// Monitoring D8: the one place a posted snapshot is checked. JSON shape errors (NaN tokens, a missing ServerId) are
// System.Text.Json's; this checks the values. Field names in the messages are camelCase, as in the JSON.
public static class SnapshotValidator
{
    // 기능: Snapshot 값을 검사한다(ServerId 규칙, 문자열 길이·문자, 시각, 음수, 비유한값).
    // 입력: s - 받은 Snapshot, now - Monitoring Server의 지금, maxClockSkewSeconds - ObservedAt이 now에서 벗어날 수 있는 초.
    // 출력: 맞으면 null, 틀리면 첫 번째로 찾은 이유(camelCase 필드 이름 포함, 받은 값은 넣지 않는다).
    public static string? Validate(ServerMonitoringSnapshot s, DateTimeOffset now, int maxClockSkewSeconds)
    {
        if (!MonitoringContract.IsValidServerId(s.ServerId))
            return $"serverId must be 1-{MonitoringContract.MaxServerIdLength} characters of [A-Za-z0-9._-]";
        // Version goes into the "first seen" log line and both are shown by the UI: no control characters (CR/LF, ESC)
        // or non-ASCII. The game server sends "1.0.0+<commit hash>" and an enum name, both printable ASCII.
        if (s.Version == null || s.Version.Length > MonitoringContract.MaxTextLength) return $"version is missing or longer than {MonitoringContract.MaxTextLength} characters";
        if (!IsPrintableAscii(s.Version)) return "version must be printable ASCII (0x20-0x7E)";
        if (s.MatchState == null || s.MatchState.Length > MonitoringContract.MaxTextLength) return $"matchState is missing or longer than {MonitoringContract.MaxTextLength} characters";
        if (!IsPrintableAscii(s.MatchState)) return "matchState must be printable ASCII (0x20-0x7E)";
        if (s.ObservedAt == default) return "observedAt is missing";
        if (Math.Abs((now - s.ObservedAt).TotalSeconds) > maxClockSkewSeconds) return $"observedAt is more than {maxClockSkewSeconds} s away from the monitoring clock";
        if (s.StartedAt == default || s.StartedAt > s.ObservedAt) return "startedAt is missing or after observedAt";
        if (s.ProtocolVersion < 0 || s.ConnectedPeers < 0 || s.Players < 0 || s.Graced < 0 || s.Round < 0 || s.TickSamples < 0
            || s.GcGen0 < 0 || s.GcGen1 < 0 || s.GcGen2 < 0 || s.DbQueueCount < 0 || s.DbQueueCapacity < 0
            || s.ManagedMemoryBytes < 0 || s.WorkingSetBytes < 0 || s.Exceptions < 0 || s.InvalidPackets < 0 || s.Disconnects < 0)
            return "a count is negative";
        // CpuPercent has no upper bound: it is normalized to all cores and rounding can put it a little over 100.
        return Check("uptimeSeconds", s.UptimeSeconds) ?? Check("windowSeconds", s.WindowSeconds)
            ?? Check("tickP50Ms", s.TickP50Ms) ?? Check("tickP95Ms", s.TickP95Ms) ?? Check("tickP99Ms", s.TickP99Ms) ?? Check("tickMaxMs", s.TickMaxMs)
            ?? Check("cpuPercent", s.CpuPercent)
            ?? Check("packetsInPerSecond", s.PacketsInPerSecond) ?? Check("packetsOutPerSecond", s.PacketsOutPerSecond)
            ?? Check("bytesInPerSecond", s.BytesInPerSecond) ?? Check("bytesOutPerSecond", s.BytesOutPerSecond);
    }

    // 기능: 문자열이 출력 가능한 ASCII(0x20-0x7E)로만 되어 있는지 본다.
    // 입력: text - 검사할 문자열(null 아님).
    // 출력: 모두 출력 가능한 ASCII면 true, 제어 문자·비ASCII가 하나라도 있으면 false.
    private static bool IsPrintableAscii(string text)
    {
        foreach (char c in text)
            if (c < 0x20 || c > 0x7E) return false;
        return true;
    }

    // 기능: double 하나가 유한한 0 이상인지 본다.
    // 입력: name - JSON 이름, value - 값.
    // 출력: 맞으면 null, 아니면 이름을 담은 이유.
    private static string? Check(string name, double value) =>
        double.IsFinite(value) && value >= 0 ? null : $"{name} is not a finite non-negative number";
}
