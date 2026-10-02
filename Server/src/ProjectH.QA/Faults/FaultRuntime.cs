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

    public NetworkFaultHub(int seed, Action<string> log)
    {
        _seed = seed;
        _log = log;
    }

    public bool IsProxied(string alias) => _entries.ContainsKey(alias);

    public IEnumerable<string> ProxiedAliases => _entries.Keys;

    public void EnableProxy(string alias)
    {
        if (_entries.ContainsKey(alias)) return;
        if (_entries.Count >= ActorManager.MaxActors) throw new QaStepException($"Too many proxied actors (max {ActorManager.MaxActors}).");
        _entries.Add(alias, new Entry());
    }

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

    // Replaces the settings of the given direction(s) and applies them to the live proxy.
    public void SetFaults(string alias, NetworkFaultSettings settings, FaultDirection direction)
    {
        Entry entry = Require(alias);
        settings.Validate();
        if (direction != FaultDirection.ToClient) entry.ToServer = settings;
        if (direction != FaultDirection.ToServer) entry.ToClient = settings;
        entry.Proxy?.SetFaults(settings, direction);
    }

    public NetworkFaultSettings GetFaults(string alias, FaultDirection direction)
    {
        Entry entry = Require(alias);
        return direction == FaultDirection.ToClient ? entry.ToClient : entry.ToServer;
    }

    public void SetBlocked(string alias, bool blocked, FaultDirection direction)
    {
        Entry entry = Require(alias);
        if (direction != FaultDirection.ToClient) SetFaults(alias, entry.ToServer with { Blocked = blocked }, FaultDirection.ToServer);
        if (direction != FaultDirection.ToServer) SetFaults(alias, entry.ToClient with { Blocked = blocked }, FaultDirection.ToClient);
    }

    public void Clear(string alias) => SetFaults(alias, NetworkFaultSettings.None, FaultDirection.Both);

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

    public async ValueTask DisposeAsync()
    {
        await CloseAllAsync().ConfigureAwait(false);
        _entries.Clear();
    }

    private Entry Require(string alias) =>
        _entries.TryGetValue(alias, out Entry? e) ? e
            : throw new QaStepException($"Actor '{alias}' has no network proxy: add \"network\": {{ \"proxy\": true }} to it in 'actors'.");

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

    public DbFaultHub(Func<string, DockerDbController>? factory = null)
    {
        _factory = factory ?? (name => new DockerDbController(name));
    }

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
