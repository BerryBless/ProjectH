using System.Text.Json;

namespace ProjectH.QA;

// Where Unity players come from in this run (QA-4). Built by the orchestrator; read-only.
public sealed class UnitySettings
{
    public string RepoRoot { get; init; } = Directory.GetCurrentDirectory();
    // --unity-exe: the default Development player for actors without their own unity.exe.
    public string? DefaultExe { get; init; }
    // QA/Reports/<runId>/screenshots and the folder for the players' -logFile.
    public string ShotDir { get; init; } = string.Empty;
    public string LogDir { get; init; } = string.Empty;
    public int Width { get; init; } = 800;
    public int Height { get; init; } = 450;
    // How long a launched player may take until its QA receiver answers (a cold Unity start takes seconds).
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(60);
    // Test seam: a fake receiver per port (null = real HTTP).
    public Func<int, HttpMessageHandler?>? HandlerFactory { get; init; }
}

// QA-4 (D26-D28, request §16, §83-87): an actor that is a real Unity Development player. It joins the game by itself
// (-autoConnect with -devId qa-<alias>) and is observed and driven only through its QA receiver: status, screenshots,
// UI commands and gameplay input through the real input path (POST /qa/input feeds the player's Input System, so the
// game's InputReader bindings see it, §87). The headless gameplay commands (moveTo, fire, build ...) never reach it.
// State: a status poll every PollMs while connected (an async loop, no thread of its own) publishes an immutable
// ActorState (Volatile) with the status body under `Unity` (actor.unity.*). Game assertions still read the server
// (player.*).
// Lifetime: the player process and the HTTP client are created by connect and ended by disconnect, a new connect, or
// the run's cleanup (ActorManager.StopUnityAsync). Attach mode (unity.attachPort) never launches or closes anything.
// Closing first asks the player to release all held input (releaseAll, best effort, at most ReleaseTimeout), so a down
// without an up or a running async hold never leaks into the next scenario on an attached Editor.
public sealed class UnityActor : IQaActor, IAsyncDisposable
{
    public const int PollMs = 250;
    // Cleanup's releaseAll may delay closing by at most this much.
    public static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(1);

    private readonly UnitySpec? _spec;
    private readonly UnitySettings _settings;
    private readonly Action<string> _log;
    private ActorState _state;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private UnityQaClient? _client;
    private ClientProcessManager? _process;
    private CancellationTokenSource? _pollCts;
    private Task? _poll;
    private int _connections;
    private long _lastCommandId;
    private string _closeReason = string.Empty;

    public UnityActor(string alias, UnitySpec? spec, UnitySettings settings, Action<string> log)
    {
        Alias = alias;
        DevPlayerId = ActorManager.DevPlayerIdFor(alias);
        _spec = spec;
        _settings = settings;
        _log = log;
        _state = new ActorState { Alias = alias, DevPlayerId = DevPlayerId, Status = ActorStatus.Idle };
    }

    public string Alias { get; }
    public string DevPlayerId { get; }
    public ActorState State => Volatile.Read(ref _state);
    public bool Attached => _spec?.AttachPort != null;
    public int? Pid => _process?.Pid;
    // Results of closing launched players (cleanup lines).
    public (bool Stopped, string Message)? LastStop { get; private set; }

    public string? Exe
    {
        get
        {
            string? exe = _spec?.Exe ?? _settings.DefaultExe;
            if (exe == null) return null;
            return Path.IsPathRooted(exe) ? exe : Path.GetFullPath(Path.Combine(_settings.RepoRoot, exe));
        }
    }

    // Why this actor cannot start here (no player configured or not built): the connect step skips the rest of the
    // scenario instead of failing, like a missing Docker (a machine without a Unity build is not a game bug).
    public string? CannotStart
    {
        get
        {
            if (Attached) return null;
            string? exe = Exe;
            if (exe == null) return "No Unity player for this actor: pass --unity-exe <ProjectH.exe> or set the actor's unity.exe.";
            return File.Exists(exe) ? null : $"Unity player not found: {exe} (make a Development Build first).";
        }
    }

    // 기능: Actor 공통 명령(connect, disconnect, 정리용 의도)을 Unity Player에 적용한다.
    // 입력: command - 실행할 Actor 명령, token - 실행 취소.
    // 출력: 반환값 없음. 연결 상태가 바뀌고 ActorState가 다시 게시된다. Headless Gameplay 명령은 QaStepException.
    public async ValueTask SendAsync(ActorCommand command, CancellationToken token)
    {
        switch (command)
        {
            case ConnectCommand c:
                await ConnectAsync(c.Host, c.Port, token).ConfigureAwait(false);
                break;
            case DisconnectCommand:
                await CloseAsync("Closed by QA").ConfigureAwait(false);
                break;
            // Clean-up intents other steps send in their finally: nothing to undo on a Unity player.
            case ResetIntentCommand or ClearInputQueueCommand or StopFireCommand or ClearAimCommand or MoveToCommand { Target: null }:
                break;
            default:
                throw new QaStepException($"UnityClient actor '{Alias}' takes no headless gameplay commands (§87); use unityKey/unityClick/unityLook (real input path) or a HeadlessClient actor for {command.GetType().Name.Replace("Command", string.Empty)}.");
        }
        Volatile.Write(ref _lastCommandId, Math.Max(Volatile.Read(ref _lastCommandId), command.Id));
        // Under the refresh gate like every other publish, so this cannot overwrite a newer poll status.
        await _refresh.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Publish(State.Unity);
        }
        finally
        {
            _refresh.Release();
        }
    }

    public async Task<UnityAnswer> ScreenshotAsync(string name, CancellationToken token) =>
        await Client().ScreenshotAsync(name, token).ConfigureAwait(false);

    public async Task<UnityAnswer> UiAsync(string command, CancellationToken token)
    {
        UnityAnswer answer = await Client().UiAsync(command, token).ConfigureAwait(false);
        await RefreshAsync(token).ConfigureAwait(false);
        return answer;
    }

    // 기능: Gameplay 입력 하나를 Player의 실제 입력 경로(POST /qa/input)로 보낸다.
    // 입력: body - /qa/input 본문, token - 실행 취소.
    // 출력: Player의 답(상태 코드 포함). 연결 전이면 QaStepException.
    public async Task<UnityAnswer> InputAsync(object body, CancellationToken token) =>
        await Client().InputAsync(body, token).ConfigureAwait(false);

    // 기능: Player에 눌린 입력 전부를 떼게 한다(unityReleaseAll Step).
    // 입력: token - 실행 취소.
    // 출력: Player의 답(상태 코드 포함). 연결 전이면 QaStepException.
    public async Task<UnityAnswer> ReleaseAllAsync(CancellationToken token) =>
        await Client().ReleaseAllAsync(token).ConfigureAwait(false);

    // One status read now (actions call it so they judge a fresh state, not the last poll).
    // The poll loop and the run flow both refresh: one at a time, so an older answer is never published after a newer
    // one. The semaphore is the only lock here, held across one status request; nothing else is taken inside it.
    public async Task RefreshAsync(CancellationToken token)
    {
        UnityQaClient? client = _client;
        if (client == null) return;
        await _refresh.WaitAsync(token).ConfigureAwait(false);
        try
        {
            UnityAnswer answer = await client.StatusAsync(token).ConfigureAwait(false);
            if (answer.Ok) Publish(answer.Json);
        }
        catch (QaApiException)
        {
            // Not answering: if the player is gone the state says so; otherwise the next poll tries again.
            Publish(State.Unity);
        }
        finally
        {
            _refresh.Release();
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync("QA run ended").ConfigureAwait(false);

    private UnityQaClient Client() => _client ?? throw new QaStepException($"UnityClient actor '{Alias}' is not connected (connect it first).");

    private async Task ConnectAsync(string host, int gamePort, CancellationToken token)
    {
        await CloseAsync("replaced by a new connection").ConfigureAwait(false);
        string? why = CannotStart;
        if (why != null) throw new QaStepException(why);
        _connections++;
        _closeReason = string.Empty;
        int port;
        if (Attached)
        {
            port = _spec!.AttachPort!.Value;
            _log($"{Alias}: attaching to the Unity QA receiver on 127.0.0.1:{port}");
        }
        else
        {
            port = ClientProcessManager.FreePort();
            Directory.CreateDirectory(_settings.ShotDir);
            Directory.CreateDirectory(_settings.LogDir);
            string logFile = Path.Combine(_settings.LogDir, $"unity-{Alias}.log");
            IReadOnlyList<string> args = ClientProcessManager.BuildArguments(host, gamePort, DevPlayerId, port, _settings.ShotDir, logFile,
                _spec?.Width ?? _settings.Width, _spec?.Height ?? _settings.Height);
            _process = new ClientProcessManager();
            _process.Start(Exe!, args);
            _log($"{Alias}: launched {Path.GetFileName(Exe)} pid {_process.Pid} (QA port {port}, log {logFile})");
            if (_process.JobWarning != null) _log($"{Alias}: {_process.JobWarning}");
        }
        _client = new UnityQaClient(port, _settings.HandlerFactory?.Invoke(port));
        Publish(null);

        // Readiness (request §81, §82): the receiver answers. Never a fixed sleep.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (_process != null && _process.HasExited)
                throw new QaStepException($"The Unity player exited before its QA receiver answered ({_process.ExitDescription}); see unity-{Alias}.log in the report folder.");
            try
            {
                UnityAnswer answer = await _client.StatusAsync(token).ConfigureAwait(false);
                if (answer.Ok)
                {
                    Publish(answer.Json);
                    break;
                }
            }
            catch (QaApiException)
            {
                // Not listening yet.
            }
            if (clock.Elapsed > _settings.ReadyTimeout)
                throw new QaStepException($"The Unity player's QA receiver did not answer on port {port} within {_settings.ReadyTimeout.TotalSeconds:0} s (a Development Build is needed; Release has no QA code).");
            await Task.Delay(PollMs, token).ConfigureAwait(false);
        }
        _pollCts = new CancellationTokenSource();
        CancellationToken poll = _pollCts.Token;
        _poll = Task.Run(async () =>
        {
            while (!poll.IsCancellationRequested)
            {
                await RefreshAsync(poll).ConfigureAwait(false);
                await Task.Delay(PollMs, poll).ConfigureAwait(false);
            }
        }, poll);
    }

    // 기능: Player 연결을 끝낸다. 상태 Poll을 멈추고, 눌린 입력을 모두 떼게 한 뒤(releaseAll, 최선 노력), 띄운 Player를 닫고
    //       HTTP Client를 버린다. Attach한 Player는 닫지 않는다.
    // 입력: reason - 연결이 끝난 이유(Report와 상태에 남는다).
    // 출력: 반환값 없음. Actor가 Disconnected(또는 Idle) 상태로 게시된다.
    private async Task CloseAsync(string reason)
    {
        CancellationTokenSource? cts = _pollCts;
        Task? poll = _poll;
        _pollCts = null;
        _poll = null;
        if (cts != null)
        {
            cts.Cancel();
            try
            {
                if (poll != null) await poll.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
            cts.Dispose();
        }
        if (_client != null && !(_process != null && _process.HasExited)) await ReleaseOnCloseAsync(_client).ConfigureAwait(false);
        if (_process != null)
        {
            LastStop = await _process.StopAsync().ConfigureAwait(false);
            _log($"{Alias}: Unity player {LastStop.Value.Message}");
            _process.Dispose();
            _process = null;
        }
        if (_client != null)
        {
            _client.Dispose();
            _client = null;
            _closeReason = reason;
        }
        Publish(State.Unity);
    }

    // 기능: Player에 눌린 키·버튼과 진행 중인 시점 이동을 모두 떼게 한다(POST /qa/input {"releaseAll":true}). down만 보내고
    //       up이 없거나 async hold가 남은 채 시나리오가 끝나도, 같은 Editor에 붙는 다음 시나리오로 입력이 새지 않게 한다.
    // 입력: client - 아직 열린 Player QA Client.
    // 출력: 반환값 없음. 실패·무응답(ReleaseTimeout)은 로그만 남기고 무시한다(정리는 계속된다).
    private async Task ReleaseOnCloseAsync(UnityQaClient client)
    {
        using var cts = new CancellationTokenSource(ReleaseTimeout);
        try
        {
            UnityAnswer answer = await client.ReleaseAllAsync(cts.Token).ConfigureAwait(false);
            if (!answer.Ok) _log($"{Alias}: releaseAll refused ({answer.Error}, HTTP {answer.StatusCode})");
        }
        catch (Exception e) when (e is QaApiException or OperationCanceledException)
        {
            _log($"{Alias}: releaseAll not answered ({e.Message})");
        }
    }

    private void Publish(JsonElement? status)
    {
        bool B(string name) => status != null && JsonPath.Child(status.Value, name) is { ValueKind: JsonValueKind.True };
        int I(string name) => status != null && JsonPath.Child(status.Value, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out int i) ? i : 0;
        bool open = _client != null;
        bool exited = _process != null && _process.HasExited;
        bool gone = (!open && _connections > 0) || exited;
        bool joined = !gone && B("joined");
        Volatile.Write(ref _state, new ActorState
        {
            Alias = Alias,
            DevPlayerId = DevPlayerId,
            Status = gone ? ActorStatus.Disconnected : joined ? ActorStatus.Joined : open ? ActorStatus.Connecting : ActorStatus.Idle,
            Connected = !gone && B("connected"),
            Joined = joined,
            HasSnapshot = joined,
            Disconnected = gone,
            DisconnectReason = exited ? $"Unity player {_process!.ExitDescription}" : gone ? _closeReason : string.Empty,
            Connections = _connections,
            Alive = !gone && B("alive"),
            Health = I("health"),
            Unity = status,
            LastCommandId = Volatile.Read(ref _lastCommandId),
        });
    }
}
