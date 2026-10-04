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

    // 기능: 게임 데이터 파일을 읽고 GameLoop와 Meter를 만들어 Host 수명에 연결할 준비를 한다.
    // 입력: options - 서버 설정, logger - 로그 출력 대상, matchHistory - 끝난 Match 기록을 넘길 Queue, writer - Match 기록 Writer(Counter 조회용), statsQueries - 전적 조회 Queue, lifetime - 서버 정지를 요청할 Host 수명.
    // 출력: Loop가 아직 시작되지 않은 GameServerService 객체. 데이터 파일이 없거나 잘못되면 예외를 던져 Host 시작을 막는다.
    public GameServerService(IOptions<ServerOptions> options, ILogger<GameLoop> logger, MatchHistoryQueue matchHistory,
        MatchHistoryWriter writer, StatsQueryQueue statsQueries, IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        // The data files are copied next to appsettings.json. A missing or invalid file throws here, so the
        // host refuses to start, the same as an invalid ServerOptions value (Phase 3 D4, Phase 4 D2).
        var data = GameData.LoadDirectory(AppContext.BaseDirectory, options.Value.SimHz);
        // Phase 9: finished matches go to the bounded queue; MatchHistoryWriter saves them off the game loop.
        // Phase 10 D6: when match resets keep failing, the server stops with exit code 1 so a supervisor or a person
        // notices. StopApplication runs on the thread pool: the game loop thread must not wait on the host.
        _lifetime = lifetime;
        _fatalStall = TimeSpan.FromSeconds(options.Value.FatalStallSeconds);
        _loop = new GameLoop(options.Value, data, logger, matchSink: record => matchHistory.TryEnqueue(record),
            onFatal: StopWithError,
            // Phase 11 D8: statistics requests go to StatsQueryService through this queue; the loop sends the answers.
            statsQueries: statsQueries);
        // Phase 10 D9: the writer's totals go into the Health line and the Meter.
        _loop.Health.Persistence = () => writer.Counts;
        _meter = new ServerMeter(_loop.Health);
    }

    // 기능: Game Loop Thread를 시작하고 멈춤 감시기를 켠다. Host가 시작할 때 호출한다.
    // 입력: cancellationToken - Host 시작 취소 토큰(사용 안 함).
    // 출력: 바로 완료되는 Task. Game Loop와 멈춤 감시 Timer가 실행 중이 된다.
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

    // 기능: 종료 코드를 1로 정하고 Thread Pool에서 Host 정지를 요청한다.
    // 입력: 없음.
    // 출력: 반환값 없음. Process 종료 코드가 1이 되고 Host 정지가 요청된다.
    // Phase 10 D6, server review M8: the fatal path of failing resets and of a hung loop. Exit code 1 tells a supervisor or
    // a person; StopApplication runs on the thread pool, so neither the game loop thread nor the watchdog's timer waits on
    // the host. Any thread; calling it twice only asks the host to stop twice.
    private void StopWithError()
    {
        Environment.ExitCode = 1;
        _ = Task.Run(_lifetime.StopApplication);
    }

    // 기능: 멈춤 감시기를 끄고 Thread Pool에서 Game Loop를 정지시킨 뒤 Host 종료 제한 시간까지 기다린다.
    // 입력: cancellationToken - Host 종료 제한 시간이 끝나면 취소되는 토큰.
    // 출력: Loop 정지가 끝나거나 제한 시간이 지나면 완료되는 Task. 제한 시간이 지나면 경고만 남긴다.
    // Phase 10 D7: GameLoop.Stop blocks (thread join up to 5 s, shutdown notices up to 1 s), so it runs on the thread
    // pool and the host's thread only awaits it, for as long as the host's shutdown token allows.
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

    // 기능: 이 Service가 소유한 멈춤 감시기, Game Loop, Meter를 해제한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 소유한 Resource가 모두 해제된다.
    public void Dispose()
    {
        _watchdog?.Dispose();
        _loop.Dispose();
        _meter.Dispose();
    }
}
