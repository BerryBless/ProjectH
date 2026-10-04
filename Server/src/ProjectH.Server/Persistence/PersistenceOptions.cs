namespace ProjectH.Server.Persistence;

// Phase 9: bound from the "Persistence" section of appsettings.json. The default connection string is the local
// development container (docker-compose.yml); a deployment overrides it with the environment variable
// Persistence__ConnectionString.
public sealed class PersistenceOptions
{
    public bool Enabled { get; set; }
    public string ConnectionString { get; set; } = string.Empty;
    // D6: bounded queue between the game loop and the writer. Full = the record is dropped and counted.
    public int QueueCapacity { get; set; } = 16;
    // D7: tries per match record before it is given up (logged with its round and players).
    public int MaxAttempts { get; set; } = 3;
    // D8: the upper limit of ShutdownDrainSeconds. Program.cs sizes the host ShutdownTimeout from it.
    public const int MaxShutdownDrainSeconds = 60;

    // D8: on shutdown, how long the writer may keep saving what is still queued (1 or more: 0 would abort at once).
    public int ShutdownDrainSeconds { get; set; } = 5;

    // 기능: Persistence 설정 값의 범위와 활성화 시 연결 문자열 필수 여부를 검증한다.
    // 입력: 없음.
    // 출력: 문제가 없으면 null, 있으면 첫 번째 오류 메시지.
    public string? Validate()
    {
        if (QueueCapacity < 1 || QueueCapacity > 1024) return "Persistence:QueueCapacity must be 1-1024.";
        if (MaxAttempts < 1 || MaxAttempts > 10) return "Persistence:MaxAttempts must be 1-10.";
        if (ShutdownDrainSeconds < 1 || ShutdownDrainSeconds > MaxShutdownDrainSeconds)
            return $"Persistence:ShutdownDrainSeconds must be 1-{MaxShutdownDrainSeconds}.";
        if (Enabled && string.IsNullOrWhiteSpace(ConnectionString)) return "Persistence:ConnectionString is required when Persistence:Enabled is true.";
        return null;
    }
}
