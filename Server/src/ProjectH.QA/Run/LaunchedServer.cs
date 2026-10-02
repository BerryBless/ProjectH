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

    public async Task<ServerStartInfo> StartAsync(CancellationToken token)
    {
        ServerStartInfo info = await LaunchAsync(token).ConfigureAwait(false);
        _log($"server started again: pid {info.Pid} game={info.GamePort} qa={info.QaPort} ({info.StartMs} ms)");
        return info;
    }

    public Task<ServerExitInfo> StopAsync(CancellationToken token)
    {
        if (Process == null || Client == null) throw new QaStepException("The server was not started by the tool.");
        StoppedByScenario = true;
        return Process.ShutdownAsync(Client, ServerProcessManager.StopGrace, token);
    }

    public Task<ServerExitInfo> KillAsync(CancellationToken token)
    {
        if (Process == null) throw new QaStepException("The server was not started by the tool.");
        StoppedByScenario = true;
        return Process.KillAsync(token);
    }

    // Cleanup: the existing graceful-then-kill path on the current process (an exited one counts as stopped).
    public Task<(bool Stopped, string Message)> StopForCleanupAsync() =>
        Process?.StopAsync(Client) ?? Task.FromResult((true, "not started"));

    public void Dispose() => ReleaseCurrent();

    private void ReleaseCurrent()
    {
        (Client as IDisposable)?.Dispose();
        Client = null;
        Process?.Dispose();
        Process = null;
    }
}
