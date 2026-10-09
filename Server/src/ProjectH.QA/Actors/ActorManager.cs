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

    // 기능: Run의 Actor 관리자를 만든다.
    // 입력: seed - Headless Actor Seed의 기준값, log - 로그 출력, factory - (별칭, 종류)로 Actor를 만드는 테스트용 함수(null이면 한 Pump 스레드의 HeadlessActor).
    // 출력: Actor가 없는 ActorManager.
    // factory (alias, type) → actor: tests pass MockActors; null = HeadlessActors on one pump thread.
    public ActorManager(int seed, Action<string> log, Func<string, string, IQaActor>? factory = null)
    {
        _seed = seed;
        _log = log;
        _factory = factory;
    }

    // 기능: 시나리오 별칭을 서버가 아는 DevPlayerId로 바꾼다.
    // 입력: alias - Actor 별칭.
    // 출력: "qa-" 접두사가 붙은 DevPlayerId.
    public static string DevPlayerIdFor(string alias) => DevPlayerIdPrefix + alias;

    // 기능: 별칭이 유효한 DevPlayerId가 되는지 검사한다(비어 있음, 바이트 한도, 플레이어 이름 규칙, '.'·'$' 금지)(D7).
    // 입력: alias - 검사할 Actor 별칭.
    // 출력: 유효하면 null, 아니면 이유 문장.
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

    // 기능: 별칭으로 Actor를 찾는다.
    // 입력: alias - Actor 별칭, actor - 찾은 Actor(out).
    // 출력: 있으면 true와 Actor, 없으면 false.
    public bool TryGet(string alias, out IQaActor actor) => _actors.TryGetValue(alias, out actor!);

    // 기능: 별칭으로 Actor를 가져온다.
    // 입력: alias - Actor 별칭.
    // 출력: 해당 IQaActor. 없으면 QaStepException.
    public IQaActor Get(string alias) =>
        _actors.TryGetValue(alias, out IQaActor? actor) ? actor : throw new QaStepException($"Unknown actor '{alias}'.");

    // 기능: 별칭·종류로 Actor를 만들어 등록한다(UnityClient는 UnityActor, 그 외는 factory 또는 Pump에 더한 HeadlessActor).
    // 입력: alias - Actor 별칭, type - Actor 종류, token - 취소 토큰, unity - Unity Actor 설정(선택).
    // 출력: 만든 IQaActor. 중복 별칭·한도 초과·잘못된 별칭·지원하지 않는 종류·Unity 불가면 QaStepException.
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

    // 기능: Pump를 멈춰 모든 Headless 연결을 Pump 스레드에서 닫는다(Actor 목록은 DisposeAsync가 비운다).
    // 입력: 없음.
    // 출력: Pump가 없거나 시간 안에 멈췄으면 true, 아니면 false(정리 실패로 보고; 스레드는 Background라 프로세스와 함께 끝난다).
    // Closes every connection (on the pump thread). Returns false when the pump did not stop in
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

    // 기능: 이 Run의 Unity Actor를 모두 정리한다(띄운 Player는 종료, 붙은 Player는 그대로 둔다).
    // 입력: 없음.
    // 출력: Unity Actor마다의 정리 결과 목록(예외는 실패 결과로 기록).
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

    // 기능: Unity Player를 정리하고 Pump를 멈춘 뒤 Actor 목록을 비운다(Run 종료).
    // 입력: 없음.
    // 출력: 반환값 없음. 모든 연결이 닫히고 Actor 목록과 Snapshot이 비워진다.
    public async ValueTask DisposeAsync()
    {
        await StopUnityAsync().ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
        _actors.Clear();
        Volatile.Write(ref _snapshot, Array.Empty<IQaActor>());
    }
}
