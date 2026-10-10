using ProjectH.Monitoring.Storage;

namespace ProjectH.Monitoring.Status;

// Monitoring D10: logs "offline" once per transition. The state itself is computed when read, so this service only
// owns the log line. Stops with the host (stoppingToken); the PeriodicTimer is disposed when ExecuteAsync returns.
public sealed class OfflineSweeper : BackgroundService
{
    private readonly MetricStore _store;
    private readonly MonitoringServerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<OfflineSweeper> _logger;

    // 기능: 저장소·설정·시계·로그를 받는다.
    // 입력: store - 서버 기록, options - SweepIntervalSeconds, time - 시계, logger - 로그.
    // 출력: 아직 시작하지 않은 OfflineSweeper.
    public OfflineSweeper(MetricStore store, MonitoringServerOptions options, TimeProvider time, ILogger<OfflineSweeper> logger)
    {
        _store = store;
        _options = options;
        _time = time;
        _logger = logger;
    }

    // 기능: SweepIntervalSeconds마다 새로 Offline이 된 서버를 로그한다.
    // 입력: stoppingToken - 호스트 종료.
    // 출력: 종료까지 끝나지 않는 Task. 서버마다 Offline 전환 때 Warning 한 줄이 남는다.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.SweepIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // SweepOffline copies the ids under the store's lock and returns; the log lines are written outside it.
                foreach ((string serverId, DateTimeOffset lastSeen) in _store.SweepOffline(_time.GetUtcNow()))
                    _logger.LogWarning("Server {ServerId} offline (last seen {LastSeen:O})", serverId, lastSeen);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }
}
