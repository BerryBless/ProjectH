using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Game;
using ProjectH.Server.Persistence;

namespace ProjectH.Server;

// Bridges the Generic Host lifetime (Ctrl+C, SIGTERM) to the game loop's own thread.
// The host owns this service; this service owns the GameLoop, the Meter and the stall watchdog and disposes them.
public sealed class GameServerService : IHostedService, System.IDisposable
{
    private readonly GameLoop _loop;
    private readonly ILogger _logger;
    private readonly ServerMeter _meter;
    private StallWatchdog? _watchdog;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeSpan _fatalStall;   // server review M8 (Zero = off)

    // 기능: Game Loop·Meter를 만들고(데이터 파일을 읽는다) QA 모드면 QA 실행기를, Monitoring이 켜져 있으면 수집기를 루프에 붙인다.
    //   리뷰 수정 B1: 서버 키를 읽는다 — Production에서 개발용 키면 예외로 시작을 막는다. 리뷰 수정 C1: Production에서 DeterministicSeeds면 Warning.
    // 입력: options - 서버 설정, logger - 루프 로그, matchHistory·writer·persistence - DB 큐·Writer·설정, statsQueries - 통계 요청 큐,
    //       lifetime - 호스트 수명(치명 정지), environment - 호스트 환경(Production 판단), monitoring - Monitoring 설정,
    //       qa - QA 모드일 때만, monitoringSlot - Monitoring이 켜져 있을 때만(null = 수집기 없음).
    // 출력: Start 전의 GameServerService. 데이터 파일이나 서버 키가 없거나 틀리면 예외(호스트가 시작하지 않는다).
    public GameServerService(IOptions<ServerOptions> options, ILogger<GameLoop> logger, MatchHistoryQueue matchHistory,
        MatchHistoryWriter writer, StatsQueryQueue statsQueries, IHostApplicationLifetime lifetime, IHostEnvironment environment,
        Monitoring.MonitoringOptions monitoring, IOptions<PersistenceOptions> persistence, Qa.QaControl? qa = null,
        Monitoring.MonitoringSlot? monitoringSlot = null)
    {
        _logger = logger;
        // Review fix B1: a missing or invalid key, or the development key in Production, throws here: the host refuses to start.
        var identity = Net.ServerIdentity.Load(options.Value, environment.IsProduction(), AppContext.BaseDirectory);
        if (identity.IsDevKey)
            logger.LogWarning("Server identity: DEV KEY (fingerprint {Fingerprint}, {Source}); never use it on a public server", identity.Fingerprint, identity.Source);
        else
            logger.LogInformation("Server identity: fingerprint {Fingerprint} ({Source})", identity.Fingerprint, identity.Source);
        // Review fix C1: seeds anyone can read from the options make the loot, zone and spread predictable.
        if (options.Value.DeterministicSeeds && environment.IsProduction())
            logger.LogWarning("DeterministicSeeds is on in Production: loot, zone, drop order and spread can be predicted from the seed options");
        // The data files are copied next to appsettings.json. A missing or invalid file throws here, so the
        // host refuses to start, the same as an invalid ServerOptions value (Phase 3 D4, Phase 4 D2).
        var data = GameData.LoadDirectory(AppContext.BaseDirectory, options.Value.SimHz);
        // Phase 9: finished matches go to the bounded queue; MatchHistoryWriter saves them off the game loop.
        // Phase 10 D6: when match resets keep failing, the server stops with exit code 1 so a supervisor or a person
        // notices. StopApplication runs on the thread pool: the game loop thread must not wait on the host.
        _lifetime = lifetime;
        _fatalStall = TimeSpan.FromSeconds(options.Value.FatalStallSeconds);
        // Monitoring D3: the collector runs on the game loop thread and reads the DB queue's count (any thread) and capacity.
        int queueCapacity = persistence.Value.QueueCapacity;
        Monitoring.MonitoringCollector? collector = monitoringSlot == null ? null
            : new Monitoring.MonitoringCollector(monitoring, options.Value.SimHz, monitoringSlot, TimeProvider.System, () => (matchHistory.Count, queueCapacity));
        _loop = new GameLoop(options.Value, data, logger, matchSink: record => matchHistory.TryEnqueue(record),
            onFatal: StopWithError,
            // Phase 11 D8: statistics requests go to StatsQueryService through this queue; the loop sends the answers.
            statsQueries: statsQueries, identity: identity, monitoring: collector);
        // Phase 10 D9: the writer's totals go into the Health line and the Meter.
        _loop.Health.Persistence = () => writer.Counts;
        // QA-1 D2: registered only in QA mode (Program.cs); otherwise null and the loop pays one null check per tick.
        if (qa != null) _loop.AttachQa(qa);
        _meter = new ServerMeter(_loop.Health);
    }

    // 기능: Game Loop를 시작하고, 정지 감시기(FatalStallSeconds를 넘기면 새 연결 거부 + 오류 종료)를 만들어 시작한다.
    // 입력: cancellationToken - 호스트 시작 취소 토큰(쓰지 않는다).
    // 출력: 완료된 Task. UDP 수신과 Tick이 시작된다.
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop.Start();
        // Server review M8: a stall past FatalStallSeconds refuses new connections and stops the process with exit code 1
        // (the loop is hung; GameLoop.Stop leaves its thread behind after the join limit).
        _watchdog = new StallWatchdog(() => _loop.LastTickTimestamp, _loop.Time, _loop.Health, _logger, fatalAfter: _fatalStall,
            onFatalStall: () =>
            {
                _loop.Listener.BeginStopping();
                StopWithError();
            });
        _watchdog.Start();
        return Task.CompletedTask;
    }

    // Phase 10 D6, server review M8: the fatal path of failing resets and of a hung loop. Exit code 1 tells a supervisor or
    // a person; StopApplication runs on the thread pool, so neither the game loop thread nor the watchdog's timer waits on
    // the host. Any thread; calling it twice only asks the host to stop twice.
    // 기능: 종료 코드를 1로 두고 Thread Pool에서 호스트 종료를 요청한다(치명 리셋·멈춘 Loop의 경로, 어느 스레드든, 막지 않는다).
    // 입력: 없음.
    // 출력: 반환값 없음. 호스트가 종료 절차에 들어간다.
    private void StopWithError()
    {
        Environment.ExitCode = 1;
        _ = Task.Run(_lifetime.StopApplication);
    }

    // Phase 10 D7: GameLoop.Stop blocks (thread join up to 5 s, shutdown notices up to 1 s), so it runs on the thread
    // pool and the host's thread only awaits it, for as long as the host's shutdown token allows.
    // 기능: 감시기를 먼저 해제하고 GameLoop.Stop을 Thread Pool에서 돌려 기다린다. 호스트 종료 시한이 먼저 끝나면 Warning만 남긴다.
    // 입력: cancellationToken - 호스트 종료 시한 토큰.
    // 출력: Stop이 끝나거나 시한이 지나면 완료. 연결과 소켓이 닫힌다.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // A stopping loop does not tick: that is no stall.
        _watchdog?.Dispose();
        try
        {
            await Task.Run(_loop.Stop, CancellationToken.None).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("The host shutdown timeout ended before the game loop finished stopping");
        }
    }

    // 기능: 감시기, GameLoop(멈추지 않았으면 멈춘다), Meter를 해제한다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void Dispose()
    {
        _watchdog?.Dispose();
        _loop.Dispose();
        _meter.Dispose();
    }
}
