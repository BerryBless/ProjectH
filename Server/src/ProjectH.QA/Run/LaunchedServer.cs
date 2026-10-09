using System.Diagnostics;
using ProjectH.QA.Faults;

namespace ProjectH.QA;

// The launched game server of one run (D12), restartable by scenario steps (QA-3, request §78, §129): stopServer,
// killServer, startServer, restartServer. Each start is a new ServerProcessManager with the same arguments and seed
// (new free ports); the run is pointed at it (RunContext.SwitchServer). All processes of the run share one LogRing,
// so the report's server log covers every process.
//
// Lifetime: created and owned by the orchestrator, used by the run flow only (no locks). Exactly one process is
// current; a replaced one has already exited (StartAsync refuses while one runs) and is disposed at once. Cleanup
// (StopForCleanupAsync + Dispose) ends the current one: graceful stop, then a kill of our own child only.
public sealed class LaunchedServer : IServerControl, IDisposable
{
    private readonly string _dll;
    private readonly int _seed;
    private readonly IReadOnlyDictionary<string, string> _overrides;
    private readonly Func<Uri, IQaServerClient> _clientFactory;
    private readonly Action<string>? _liveLog;
    private readonly Action<string> _log;

    // 기능: 실행할 서버 DLL·시드·설정 재정의·QA Client 생성기·로그 출력을 보관한다(프로세스는 아직 시작하지 않음).
    // 입력: dll - 실행할 ProjectH.Server.dll 경로, seed - 서버 시드, overrides - 서버 설정 재정의(--Key=Value), clientFactory - QA URL로 QA 서버 Client를 만드는 함수, liveLog - 서버 로그 줄을 실시간으로 받을 출력(null이면 LogRing에만 기록), log - 실행 로그 출력.
    // 출력: 프로세스·Client가 없는(Starts 0) 서버 제어 객체.
    public LaunchedServer(string dll, int seed, IReadOnlyDictionary<string, string> overrides, Func<Uri, IQaServerClient> clientFactory,
        Action<string>? liveLog, Action<string> log)
    {
        _dll = dll;
        _seed = seed;
        _overrides = overrides;
        _clientFactory = clientFactory;
        _liveLog = liveLog;
        _log = log;
    }

    public LogRing Log { get; } = new();
    public ServerProcessManager? Process { get; private set; }
    public IQaServerClient? Client { get; private set; }
    public Uri? QaUrl { get; private set; }
    public int GamePort { get; private set; }
    public int Starts { get; private set; }
    // Set once the run exists; restarts point it at the new server.
    public RunContext? Run { get; set; }

    public bool Running => Process != null && !Process.HasExited;
    // D41: a scenario step stopped or killed the server on purpose (stopServer, killServer, restartServer): an exited
    // process is then not a crash. Cleared by the next start.
    public bool StoppedByScenario { get; private set; }
    public int? ExitCode => Process?.ExitCode;

    public IReadOnlyList<string> Arguments => ServerProcessManager.BuildArguments(_dll, _seed, _overrides);

    // 기능: 같은 인자·시드로 새 서버 프로세스를 시작하고(새 빈 포트) QA Client를 만들어, Run이 있으면 새 서버를 가리키게 한다. 이전 Client는 Run이 새 Client를 가리킨 뒤에 폐기한다.
    // 입력: token - 취소 토큰.
    // 출력: 게임 포트·QA 포트·PID·시작 소요 ms. 이미 실행 중이면 QaStepException, 시작에 실패하면 예외 전파(Run은 이전 Client를 유지).
    // The first start (before the run exists) and every later one.
    // The previous client is disposed only after the run points at the new one. If the start fails, the run keeps the
    // previous (stopped) server's client, so later steps see "not answering" (server.running = false), never a
    // disposed client; the new child is already current, so cleanup still stops it.
    public async Task<ServerStartInfo> LaunchAsync(CancellationToken token)
    {
        if (Running) throw new QaStepException("The server is already running (stop it first, or use restartServer).");
        ServerProcessManager? previous = Process;
        var clock = Stopwatch.StartNew();
        var process = new ServerProcessManager(_liveLog, Log);
        Process = process;
        previous?.Dispose();   // already exited (Running was false)
        (int game, int qa) = await process.StartAsync(_dll, _seed, _overrides, _clientFactory, token).ConfigureAwait(false);
        StoppedByScenario = false;
        IQaServerClient? previousClient = Client;
        QaUrl = new Uri($"http://127.0.0.1:{qa}/");
        Client = _clientFactory(QaUrl);
        GamePort = game;
        Starts++;
        Run?.SwitchServer(Client, new EventCursor(Client), GamePort);
        (previousClient as IDisposable)?.Dispose();
        return new ServerStartInfo(game, qa, process.Pid, clock.ElapsedMilliseconds);
    }

    // 기능: 시나리오 단계(startServer·restartServer)용 재시작. LaunchAsync를 호출하고 결과를 실행 로그에 남긴다.
    // 입력: token - 취소 토큰.
    // 출력: 새 서버의 게임 포트·QA 포트·PID·시작 소요 ms. 이미 실행 중이면 QaStepException.
    public async Task<ServerStartInfo> StartAsync(CancellationToken token)
    {
        ServerStartInfo info = await LaunchAsync(token).ConfigureAwait(false);
        _log($"server started again: pid {info.Pid} game={info.GamePort} qa={info.QaPort} ({info.StartMs} ms)");
        return info;
    }

    // 기능: 현재 서버에 QA 종료 명령(POST /qa/server/stop)을 보내 StopGrace 안에 끝나기를 기다리고, 끝나지 않으면 자식 프로세스만 죽인다. 의도된 정지로 기록한다(StoppedByScenario).
    // 입력: token - 취소 토큰.
    // 출력: 종료 여부·소요 ms·종료 코드·kill 여부·메시지가 담긴 결과. 도구가 시작한 서버가 없으면 QaStepException.
    public Task<ServerExitInfo> StopAsync(CancellationToken token)
    {
        if (Process == null || Client == null) throw new QaStepException("The server was not started by the tool.");
        StoppedByScenario = true;
        return Process.ShutdownAsync(Client, ServerProcessManager.StopGrace, token);
    }

    // 기능: 현재 서버 프로세스 트리를 강제 종료하고 의도된 정지로 기록한다(StoppedByScenario).
    // 입력: token - 취소 토큰.
    // 출력: 종료 여부·소요 ms·종료 코드·Killed·메시지가 담긴 결과. 도구가 시작한 서버가 없으면 QaStepException.
    public Task<ServerExitInfo> KillAsync(CancellationToken token)
    {
        if (Process == null) throw new QaStepException("The server was not started by the tool.");
        StoppedByScenario = true;
        return Process.KillAsync(token);
    }

    // 기능: 정리 단계에서 현재 프로세스를 정상 종료 → kill 순으로 끝낸다(이미 끝난 프로세스는 멈춘 것으로 본다).
    // 입력: 없음.
    // 출력: 멈췄는지와 설명 메시지. 시작한 적이 없으면 (true, "not started").
    // Cleanup: the existing graceful-then-kill path on the current process (an exited one counts as stopped).
    public Task<(bool Stopped, string Message)> StopForCleanupAsync() =>
        Process?.StopAsync(Client) ?? Task.FromResult((true, "not started"));

    // 기능: 현재 QA Client와 서버 프로세스 핸들을 해제한다(프로세스가 아직 살아 있으면 ServerProcessManager.Dispose가 죽인다).
    // 입력: 없음.
    // 출력: 반환값 없음. Client와 Process가 null이 된다.
    public void Dispose() => ReleaseCurrent();

    // 기능: 현재 Client를 폐기하고 ServerProcessManager를 Dispose한 뒤 둘 다 null로 둔다.
    // 입력: 없음.
    // 출력: 반환값 없음. Client와 Process가 null이 된다.
    private void ReleaseCurrent()
    {
        (Client as IDisposable)?.Dispose();
        Client = null;
        Process?.Dispose();
        Process = null;
    }
}
