namespace ProjectH.QA.Faults;

// The run's network faults (D13): one UdpFaultProxy per actor that asked for one (`"network": { "proxy": true }`).
// Owned by RunContext and used only by the run's single async flow (steps run one after another), so plain
// collections and no locks; the proxies themselves are thread-safe for their own loops.
//
// A proxy lives for one connection attempt: every connect/reconnect (and the actor's first connect after a server
// restart, whose game port is new) disposes the old proxy and opens a new one towards the current game port. The
// server then sees each attempt from a new upstream endpoint, and the actor's new local port is latched afresh. The
// actor's fault settings are kept per alias and applied to each new proxy, so a reconnect stays under the same
// network conditions until clearNetworkFault.
//
// Bounds: at most ActorManager.MaxActors entries (one per proxied alias). Removal: DisposeAsync at the end of the run
// (cleanup), always, even when the run failed (request §113).
public sealed class NetworkFaultHub : IAsyncDisposable
{
    private readonly int _seed;
    private readonly Action<string> _log;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    // 기능: 실행의 네트워크 장애 허브를 만든다.
    // 입력: seed - 시나리오 시드(프록시 난수 시드의 바탕), log - 실행 로그 출력.
    // 출력: 프록시 항목이 없는 허브.
    public NetworkFaultHub(int seed, Action<string> log)
    {
        _seed = seed;
        _log = log;
    }

    // 기능: 액터가 프록시 사용을 등록했는지 확인한다.
    // 입력: alias - 액터 별칭.
    // 출력: 등록됐으면 true.
    public bool IsProxied(string alias) => _entries.ContainsKey(alias);

    public IEnumerable<string> ProxiedAliases => _entries.Keys;

    // 기능: 액터를 프록시 대상으로 등록한다(이미 등록됐으면 그대로 둔다).
    // 입력: alias - 액터 별칭.
    // 출력: 반환값 없음. 항목이 추가된다. MaxActors를 넘으면 QaStepException.
    public void EnableProxy(string alias)
    {
        if (_entries.ContainsKey(alias)) return;
        if (_entries.Count >= ActorManager.MaxActors) throw new QaStepException($"Too many proxied actors (max {ActorManager.MaxActors}).");
        _entries.Add(alias, new Entry());
    }

    // 기능: 액터의 다음 연결 목적지를 정한다. 프록시 대상이면 이전 프록시를 폐기하고 현재 게임 포트 앞에 새 프록시를 열어 저장된 장애 설정을 적용한다.
    // 입력: alias - 액터 별칭, gameHost - 게임 서버 호스트, gamePort - 게임 서버 포트.
    // 출력: 연결할 호스트와 포트(프록시가 없으면 게임 서버 그대로, 있으면 새 프록시의 수신 endpoint).
    // Where the actor's next connection goes: the game server itself, or a fresh proxy in front of it.
    public async Task<(string Host, int Port)> PrepareConnectAsync(string alias, string gameHost, int gamePort)
    {
        if (!_entries.TryGetValue(alias, out Entry? entry)) return (gameHost, gamePort);
        if (entry.Proxy != null)
        {
            await entry.Proxy.DisposeAsync().ConfigureAwait(false);
            entry.Proxy = null;
        }
        entry.Attempts++;
        var proxy = new UdpFaultProxy(UdpFaultProxy.ParseTarget(gameHost, gamePort), ProxySeed(alias, entry.Attempts));
        proxy.SetFaults(entry.ToServer, FaultDirection.ToServer);
        proxy.SetFaults(entry.ToClient, FaultDirection.ToClient);
        entry.Proxy = proxy;
        _log($"network proxy for {alias}: {proxy.ListenEndPoint} -> {gameHost}:{gamePort} (attempt {entry.Attempts})");
        return (proxy.ListenEndPoint.Address.ToString(), proxy.ListenEndPoint.Port);
    }

    // 기능: 액터의 해당 방향 장애 설정을 바꿔 저장하고 살아 있는 프록시에도 적용한다.
    // 입력: alias - 액터 별칭, settings - 새 장애 설정, direction - 적용 방향(Both면 양쪽).
    // 출력: 반환값 없음. 저장된 설정과 현재 프록시가 바뀐다. 프록시 미등록이면 QaStepException, 값이 범위 밖이면 ArgumentOutOfRangeException.
    // Replaces the settings of the given direction(s) and applies them to the live proxy.
    public void SetFaults(string alias, NetworkFaultSettings settings, FaultDirection direction)
    {
        Entry entry = Require(alias);
        settings.Validate();
        if (direction != FaultDirection.ToClient) entry.ToServer = settings;
        if (direction != FaultDirection.ToServer) entry.ToClient = settings;
        entry.Proxy?.SetFaults(settings, direction);
    }

    // 기능: 액터에 저장된 한 방향의 장애 설정을 읽는다.
    // 입력: alias - 액터 별칭, direction - ToClient면 서버→액터 설정, 그 외는 액터→서버 설정.
    // 출력: 해당 방향의 장애 설정. 프록시 미등록이면 QaStepException.
    public NetworkFaultSettings GetFaults(string alias, FaultDirection direction)
    {
        Entry entry = Require(alias);
        return direction == FaultDirection.ToClient ? entry.ToClient : entry.ToServer;
    }

    // 기능: 다른 장애 값은 유지한 채 해당 방향의 차단(Blocked)만 켜거나 끈다.
    // 입력: alias - 액터 별칭, blocked - 차단 여부, direction - 적용 방향.
    // 출력: 반환값 없음. 저장된 설정과 현재 프록시의 Blocked가 바뀐다. 프록시 미등록이면 QaStepException.
    public void SetBlocked(string alias, bool blocked, FaultDirection direction)
    {
        Entry entry = Require(alias);
        if (direction != FaultDirection.ToClient) SetFaults(alias, entry.ToServer with { Blocked = blocked }, FaultDirection.ToServer);
        if (direction != FaultDirection.ToServer) SetFaults(alias, entry.ToClient with { Blocked = blocked }, FaultDirection.ToClient);
    }

    // 기능: 액터의 양방향 장애 설정을 모두 없앤다.
    // 입력: alias - 액터 별칭.
    // 출력: 반환값 없음. 저장된 설정과 현재 프록시가 None이 된다. 프록시 미등록이면 QaStepException.
    public void Clear(string alias) => SetFaults(alias, NetworkFaultSettings.None, FaultDirection.Both);

    // 기능: 변수 network.proxy.*로 노출할 프록시 상태(활성 여부·시도 횟수·수신 주소·카운터·마지막 오류·양방향 설정)를 만든다.
    // 입력: alias - 액터 별칭.
    // 출력: 상태가 담긴 익명 객체. 프록시 미등록이면 null.
    // network.proxy.* : the current proxy's counters plus the settings (null when the actor has no proxy).
    public object? Describe(string alias)
    {
        if (!_entries.TryGetValue(alias, out Entry? e)) return null;
        UdpFaultProxyCounters c = e.Proxy?.Counters ?? default;
        return new
        {
            enabled = true,
            active = e.Proxy != null,
            attempts = e.Attempts,
            listen = e.Proxy?.ListenEndPoint.ToString(),
            forwarded = c.Forwarded,
            dropped = c.Dropped,
            duplicated = c.Duplicated,
            delayed = c.Delayed,
            queueFull = c.QueueFull,
            foreign = c.Foreign,
            lastError = e.Proxy?.LastError,
            toServer = e.ToServer,
            toClient = e.ToClient,
        };
    }

    // 기능: 정리 단계에서 모든 항목의 장애 설정을 지우고 살아 있는 프록시를 전부 폐기한다. 등록 항목 자체는 남긴다.
    // 입력: 없음.
    // 출력: 폐기 중 생긴 문제 목록("alias: 메시지"). 비어 있으면 깨끗이 닫힌 것.
    // Cleanup: faults cleared and every proxy disposed. Returns the problems (empty = clean).
    public async Task<List<string>> CloseAllAsync()
    {
        var problems = new List<string>();
        foreach (var pair in _entries)
        {
            Entry e = pair.Value;
            e.ToServer = e.ToClient = NetworkFaultSettings.None;
            if (e.Proxy == null) continue;
            try
            {
                e.Proxy.ClearFaults();
                await e.Proxy.DisposeAsync().ConfigureAwait(false);
                if (e.Proxy.LastError != null) problems.Add($"{pair.Key}: {e.Proxy.LastError}");
            }
            catch (Exception ex)
            {
                problems.Add($"{pair.Key}: {ex.Message}");
            }
            e.Proxy = null;
        }
        return problems;
    }

    // 기능: 모든 프록시를 닫고 등록 항목까지 비운다.
    // 입력: 없음.
    // 출력: 반환값 없음. 허브가 빈 상태가 된다(문제 목록은 버려진다).
    public async ValueTask DisposeAsync()
    {
        await CloseAllAsync().ConfigureAwait(false);
        _entries.Clear();
    }

    // 기능: 액터의 프록시 항목을 찾는다.
    // 입력: alias - 액터 별칭.
    // 출력: 등록된 항목. 없으면 "network": { "proxy": true } 안내가 담긴 QaStepException.
    private Entry Require(string alias) =>
        _entries.TryGetValue(alias, out Entry? e) ? e
            : throw new QaStepException($"Actor '{alias}' has no network proxy: add \"network\": {{ \"proxy\": true }} to it in 'actors'.");

    // 기능: 시나리오 시드·액터 별칭(FNV-1a 해시)·연결 시도 횟수로 프록시 난수 시드를 만든다.
    // 입력: alias - 액터 별칭, attempt - 이 액터의 연결 시도 번호.
    // 출력: 프로세스가 달라도 같은 입력이면 같은 시드 값.
    // Stable over processes (string.GetHashCode is randomized per process): same scenario seed, same decisions.
    private int ProxySeed(string alias, int attempt)
    {
        uint h = 2166136261;
        foreach (char ch in alias) h = (h ^ ch) * 16777619;
        return unchecked(_seed * 31 + (int)h + attempt * 7919);
    }

    private sealed class Entry
    {
        public UdpFaultProxy? Proxy;
        public int Attempts;
        public NetworkFaultSettings ToServer = NetworkFaultSettings.None;
        public NetworkFaultSettings ToClient = NetworkFaultSettings.None;
    }
}

// The run's DB faults (D13, request §76-77): docker stop/start of the configured MySQL container. Owned by RunContext,
// used by the run flow only. Bounded: at most MaxContainers controllers. Removal: RestoreAllAsync in cleanup starts
// every container this run stopped (and only those), with its own timeout.
public sealed class DbFaultHub
{
    public const int MaxContainers = 4;
    public static readonly TimeSpan RestoreHealthTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<string, DockerDbController> _factory;
    private readonly Dictionary<string, DockerDbController> _controllers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _unavailable = new(StringComparer.Ordinal);

    // 기능: 실행의 DB 장애 허브를 만든다.
    // 입력: factory - 컨테이너 이름으로 컨트롤러를 만드는 함수(null이면 실제 DockerDbController).
    // 출력: 컨트롤러가 없는 허브.
    public DbFaultHub(Func<string, DockerDbController>? factory = null)
    {
        _factory = factory ?? (name => new DockerDbController(name));
    }

    // 기능: 컨테이너의 컨트롤러를 얻는다. 처음 요청이면 만들어 Docker 가용성을 확인하고, 결과(가능/불가 이유)를 실행 동안 기억한다.
    // 입력: container - MySQL 컨테이너 이름, token - 취소 토큰.
    // 출력: 제어 가능하면 컨트롤러, 불가능하면 null과 그 이유. 컨테이너 수가 MaxContainers를 넘거나 이름이 잘못되면 QaStepException.
    // The controller, or the reason the container cannot be controlled (Docker missing, container absent). The answer
    // is remembered for the run, so later DB steps skip the same way without asking Docker again.
    public async Task<(DockerDbController? Controller, string? Unavailable)> GetAsync(string container, CancellationToken token)
    {
        if (_unavailable.TryGetValue(container, out string? reason)) return (null, reason);
        if (_controllers.TryGetValue(container, out DockerDbController? known)) return (known, null);
        if (_controllers.Count + _unavailable.Count >= MaxContainers) throw new QaStepException($"Too many DB containers in one run (max {MaxContainers}).");
        DockerDbController controller;
        try
        {
            controller = _factory(container);
        }
        catch (ArgumentException e)
        {
            throw new QaStepException(e.Message);
        }
        if (!await controller.IsAvailableAsync(token).ConfigureAwait(false))
        {
            reason = $"Docker or the container '{container}' is not available (docker inspect failed)";
            _unavailable[container] = reason;
            return (null, reason);
        }
        _controllers[container] = controller;
        return (controller, null);
    }

    public IEnumerable<string> StoppedContainers => _controllers.Where(p => p.Value.StoppedByThisController).Select(p => p.Key);

    // 기능: 정리 단계에서 이 실행이 멈춘 컨테이너만 컨테이너마다 독립 시간 한도로 다시 시작한다.
    // 입력: 없음.
    // 출력: 복구를 시도한 컨테이너마다 하나씩의 정리 결과("db <이름>", 성공 여부, 상세).
    // Cleanup: its own token per container (the run's may be cancelled). One line per container that was restored.
    public async Task<List<CleanupResult>> RestoreAllAsync()
    {
        var results = new List<CleanupResult>();
        foreach (var pair in _controllers)
        {
            if (!pair.Value.StoppedByThisController) continue;
            try
            {
                using var cts = new CancellationTokenSource(DockerDbController.StartTimeout + RestoreHealthTimeout + TimeSpan.FromSeconds(30));
                DockerRestoreResult r = await pair.Value.RestoreAsync(RestoreHealthTimeout, cts.Token).ConfigureAwait(false);
                results.Add(new CleanupResult($"db {pair.Key}", r.Ok, r.Detail));
            }
            catch (Exception e)
            {
                results.Add(new CleanupResult($"db {pair.Key}", false, e.Message));
            }
        }
        return results;
    }
}

// What a server stop/kill step measured (saveAs value). ExitMs: from the request (or the kill) to process exit.
public sealed record ServerExitInfo(bool Exited, long ExitMs, int? ExitCode, bool Killed, string Message);

public sealed record ServerStartInfo(int GamePort, int QaPort, int? Pid, long StartMs);

// Server process steps (request §78, §129). Only a launched server can be controlled; attach mode has none
// (RunContext.ServerControl is null and the steps fail with a clear message).
public interface IServerControl
{
    bool Running { get; }
    Task<ServerExitInfo> StopAsync(CancellationToken token);
    Task<ServerExitInfo> KillAsync(CancellationToken token);
    // Starts the server again with the same arguments and seed (new free ports) and points the run at it.
    Task<ServerStartInfo> StartAsync(CancellationToken token);
}
