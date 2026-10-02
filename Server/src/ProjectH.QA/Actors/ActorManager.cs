using System.Text;
using ProjectH.Shared.Protocol;

namespace ProjectH.QA;

// The run's actors by alias (request §42: scenarios name `playerA`, the server knows `qa-playerA`). Owned by the run's
// orchestrator flow (one async flow, D9): the dictionary is never touched by the pump. Bounded by MaxActors; emptied
// when the run ends (DisposeAsync stops the pump, which closes every connection).
public sealed class ActorManager : IAsyncDisposable
{
    public const int MaxActors = 100;    // the snapshot limit (ProtocolConstants.MaxSnapshotEntities)
    public const string DevPlayerIdPrefix = "qa-";
    private static readonly TimeSpan PumpStopTimeout = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, IQaActor> _actors = new(StringComparer.Ordinal);
    private readonly Func<string, string, IQaActor>? _factory;
    private readonly Action<string> _log;
    private readonly int _seed;
    private ActorPump? _pump;
    // D43: input latency of every headless actor of this run (fixed size; read by `measure`).
    public InputLatencyHistogram Latency { get; } = new();
    // For readers on other threads (the UI inspector): replaced on every create, never mutated (Volatile).
    private IQaActor[] _snapshot = Array.Empty<IQaActor>();

    // factory (alias, type) → actor: tests pass MockActors; null = HeadlessActors on one pump thread.
    public ActorManager(int seed, Action<string> log, Func<string, string, IQaActor>? factory = null)
    {
        _seed = seed;
        _log = log;
        _factory = factory;
    }

    public static string DevPlayerIdFor(string alias) => DevPlayerIdPrefix + alias;

    // Null when the alias makes a valid DevPlayerId, else why not (D7: the protocol's name rule and byte limit).
    public static string? CheckAlias(string alias)
    {
        if (string.IsNullOrEmpty(alias)) return "An actor id must not be empty.";
        string id = DevPlayerIdFor(alias);
        int bytes = Encoding.UTF8.GetByteCount(id);
        if (bytes > ProtocolConstants.MaxDevPlayerIdBytes)
            return $"Actor id '{alias}' is too long: DevPlayerId '{id}' is {bytes} bytes (max {ProtocolConstants.MaxDevPlayerIdBytes}).";
        if (!ProtocolConstants.IsValidPlayerName(id)) return $"Actor id '{alias}' is not a valid player name (control or invisible characters).";
        if (alias.Contains('.') || alias.Contains('$')) return $"Actor id '{alias}' must not contain '.' or '$'.";
        return null;
    }

    // QA-4: where UnityClient actors' players come from (null: UnityClient actors cannot be created).
    public UnitySettings? Unity { get; init; }

    public int Count => _actors.Count;
    public IReadOnlyList<IQaActor> Snapshot => Volatile.Read(ref _snapshot);
    public IEnumerable<IQaActor> All => _actors.Values;

    public bool TryGet(string alias, out IQaActor actor) => _actors.TryGetValue(alias, out actor!);

    public IQaActor Get(string alias) =>
        _actors.TryGetValue(alias, out IQaActor? actor) ? actor : throw new QaStepException($"Unknown actor '{alias}'.");

    public async Task<IQaActor> CreateAsync(string alias, string type, CancellationToken token, UnitySpec? unity = null)
    {
        if (_actors.ContainsKey(alias)) throw new QaStepException($"Actor '{alias}' already exists.");
        if (_actors.Count >= MaxActors) throw new QaStepException($"Too many actors (max {MaxActors}).");
        string? error = CheckAlias(alias);
        if (error != null) throw new QaStepException(error);

        IQaActor actor;
        if (string.Equals(type, ActorSpec.UnityClient, StringComparison.OrdinalIgnoreCase))
        {
            // Not from the test factory: Unity actors are tested with a fake receiver (UnitySettings.HandlerFactory).
            actor = new UnityActor(alias, unity, Unity ?? throw new QaStepException("UnityClient actors are not available in this run."), _log);
        }
        else if (_factory != null)
        {
            actor = _factory(alias, type);
        }
        else
        {
            if (!string.Equals(type, ActorSpec.HeadlessClient, StringComparison.OrdinalIgnoreCase))
                throw new QaStepException($"Actor type '{type}' is not supported yet (QA-1: HeadlessClient).");
            _pump ??= new ActorPump(_log);
            var headless = new HeadlessActor(alias, _pump, unchecked(_seed + 7919 * (_actors.Count + 1)), Latency);
            await _pump.AddAsync(headless, token).ConfigureAwait(false);
            actor = headless;
        }
        _actors.Add(alias, actor);
        Volatile.Write(ref _snapshot, _actors.Values.ToArray());
        return actor;
    }

    // Closes every connection (on the pump thread) and forgets the actors. Returns false when the pump did not stop in
    // time (reported as a cleanup failure; the thread is a background thread and ends with the process).
    public async Task<bool> StopAsync()
    {
        bool stopped = true;
        if (_pump != null)
        {
            stopped = await _pump.StopAsync(PumpStopTimeout).ConfigureAwait(false);
            _pump = null;
        }
        return stopped;
    }

    // Closes every Unity player this run launched (cleanup). One line per launched or attached Unity actor.
    public async Task<IReadOnlyList<CleanupResult>> StopUnityAsync()
    {
        var results = new List<CleanupResult>();
        foreach (UnityActor u in _actors.Values.OfType<UnityActor>())
        {
            try
            {
                await u.DisposeAsync().ConfigureAwait(false);
                if (u.Attached) results.Add(new CleanupResult($"unity {u.Alias}", true, "attached (left running)"));
                else if (u.LastStop is { } stop) results.Add(new CleanupResult($"unity {u.Alias}", stop.Stopped, stop.Message));
            }
            catch (Exception e)
            {
                results.Add(new CleanupResult($"unity {u.Alias}", false, e.Message));
            }
        }
        return results;
    }

    public async ValueTask DisposeAsync()
    {
        await StopUnityAsync().ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
        _actors.Clear();
        Volatile.Write(ref _snapshot, Array.Empty<IQaActor>());
    }
}
