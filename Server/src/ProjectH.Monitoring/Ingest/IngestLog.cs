namespace ProjectH.Monitoring.Ingest;

// Monitoring D7: invalid posts are logged at most once a minute, with the count of the ones not logged. A game server
// posting garbage every 5 s must not fill the log. Holds two numbers, no collection.
public sealed class IngestLog
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ILogger<IngestLog> _logger;
    private readonly TimeProvider _time;
    // Lock ordering: never held together with MetricStore._gate (IngestEndpoint calls Invalid only after store.Add has
    // returned) and the logger is called after this lock is released, so no ordering is needed.
    private readonly object _gate = new();
    private bool _logged;        // false until the first line; a timestamp of 0 is a valid time for a test clock
    private long _windowStart;   // TimeProvider timestamp of the last logged line
    private int _suppressed;

    // 기능: 로그와 시계를 받는다.
    // 입력: logger - 로그, time - 시계.
    // 출력: 아직 한 줄도 남기지 않은 IngestLog.
    public IngestLog(ILogger<IngestLog> logger, TimeProvider time)
    {
        _logger = logger;
        _time = time;
    }

    // 기능: 잘못된 Ingest 하나를 기록한다(1분에 한 줄, 나머지는 세어서 다음 줄에 붙인다). 어느 요청 스레드나 부른다.
    // 입력: reason - 거부 이유, serverId - 보낸 서버(모르면 null).
    // 출력: 반환값 없음. 창이 지났으면 Warning 한 줄이 남고, 아니면 억눌린 수가 1 늘어난다.
    public void Invalid(string reason, string? serverId)
    {
        int suppressed;
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            if (_logged && _time.GetElapsedTime(_windowStart, now) < Window)
            {
                _suppressed++;
                return;
            }
            suppressed = _suppressed;
            _suppressed = 0;
            _windowStart = now;
            _logged = true;
        }
        _logger.LogWarning("Invalid ingest ({Reason}) from {ServerId}; {Suppressed} more were not logged in the last minute", reason, serverId ?? "?", suppressed);
    }
}
