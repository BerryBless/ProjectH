using System.Text.Json;

namespace ProjectH.QA;

// Stress (D38): one share of an actorGroup split. Percent of the actors, an exact Count, or Rest (whatever is left).
// Proxy: the members connect through their own fault proxy (groupNetworkFault needs it; set before they connect).
public sealed record GroupShare(string Name, double? Percent, int? Count, bool Rest = false, bool Proxy = false);

public sealed class ActorGroup
{
    // 기능: 액터 그룹을 만든다.
    // 입력: name - 그룹 이름, members - 구성원 액터(액터 순서), indices - 각 구성원이 split 전체 액터 중 차지하는 0 기반 자리.
    // 출력: Role이 없고 Stats가 비어 있는 ActorGroup.
    public ActorGroup(string name, IReadOnlyList<IQaActor> members, IReadOnlyList<int> indices)
    {
        Name = name;
        Members = members;
        Indices = indices;
    }

    public string Name { get; }
    // In actor order (prefix-001 first); Indices[i] is Members[i]'s 0-based place among all the split's actors.
    public IReadOnlyList<IQaActor> Members { get; }
    public IReadOnlyList<int> Indices { get; }
    // What the group does now (set by group actions, for the report and `group.<name>.role`).
    public string? Role { get; set; }
    public GroupStats Stats { get; } = new();
}

// Counters of a group's workloads. Written by the background workload (re-arm, churn) and read by steps (measure,
// stopGroup, assertions), so plain longs through Interlocked; the error list has its own lock (only lock here, never
// held while calling anything).
public sealed class GroupStats
{
    public const int MaxErrors = 20;
    private readonly object _errorsLock = new();
    private readonly List<string> _errors = new();
    private long _errorCount;

    public long ArrangeCommands;      // QA commands sent by the group (initial arrange and re-arm after a respawn)
    public long CommandFailures;
    public long Rearms;               // respawned unarmed members given their weapon again
    public long Deaths;
    public long Refills;              // reserve ammo topped up (long runs)
    public long ChurnCycles;
    public long Disconnects;
    public long Reconnects;
    public long ReconnectFailures;
    public long ReconnectMsTotal;
    public long ReconnectMsMax;
    public long Duplicates;           // a DevPlayerId the server listed twice after a reconnect
    public long FaultsSet;
    public long WorkloadFailures;     // a background workload that ended on an error (not stopped): stopGroup fails
    // Phase B. Loot brains (pump thread): items that went away after the member went for them, Drop presses, Medkit /
    // Shield Cell presses.
    public long LootPickups;
    public long LootDrops;
    public long LootUses;
    // groupConnect (join ramp / spike workload): joined, failed (closed or not joined in time), time from the connect
    // command to the first snapshot.
    public long Connects;
    public long ConnectFailures;
    public long ConnectMsTotal;
    public long ConnectMsMax;
    // Abuse brains (pump thread) and their rejoin workload: invalid packets or spam requests sent, kicks seen
    // (DisconnectCode Kicked), connections opened again after a kick or another server close.
    public long AbuseSent;
    public long Kicks;
    public long Rejoins;
    private string? _lastFailure;

    public string? LastFailure
    {
        get { lock (_errorsLock) return _lastFailure; }
    }

    // 기능: 배경 Workload가 오류로 끝났음을 기록한다.
    // 입력: message - 실패 설명.
    // 출력: 반환값 없음. WorkloadFailures가 늘고 LastFailure와 오류 목록이 갱신된다.
    public void WorkloadFailed(string message)
    {
        Interlocked.Increment(ref WorkloadFailures);
        lock (_errorsLock) _lastFailure = message;
        Error(message);
    }

    // 기능: 오류 한 건을 센다(메시지는 MaxErrors개까지만 보관).
    // 입력: message - 오류 설명.
    // 출력: 반환값 없음. errorCount가 늘고 목록에 자리가 있으면 메시지가 추가된다.
    public void Error(string message)
    {
        Interlocked.Increment(ref _errorCount);
        lock (_errorsLock)
        {
            if (_errors.Count < MaxErrors) _errors.Add(message);
        }
    }

    // 기능: 카운터를 value가 더 클 때만 갱신한다(Interlocked CAS 루프).
    // 입력: field - 갱신할 카운터, value - 후보값.
    // 출력: 반환값 없음. field가 value 이상이 된다.
    public void Max(ref long field, long value)
    {
        long seen;
        while (value > (seen = Interlocked.Read(ref field)) && Interlocked.CompareExchange(ref field, value, seen) != seen)
        {
        }
    }

    // 기능: 카운터와 오류 목록을 보고용 객체로 읽는다(reconnect·connect 평균 ms 포함).
    // 입력: 없음.
    // 출력: 그룹 통계 익명 객체.
    public object Snapshot()
    {
        string[] errors;
        lock (_errorsLock) errors = _errors.ToArray();
        long reconnects = Interlocked.Read(ref Reconnects);
        return new
        {
            arrangeCommands = Interlocked.Read(ref ArrangeCommands),
            commandFailures = Interlocked.Read(ref CommandFailures),
            rearms = Interlocked.Read(ref Rearms),
            deaths = Interlocked.Read(ref Deaths),
            refills = Interlocked.Read(ref Refills),
            churnCycles = Interlocked.Read(ref ChurnCycles),
            disconnects = Interlocked.Read(ref Disconnects),
            reconnects,
            reconnectFailures = Interlocked.Read(ref ReconnectFailures),
            reconnectMsAvg = reconnects > 0 ? Math.Round((double)Interlocked.Read(ref ReconnectMsTotal) / reconnects, 1) : 0,
            reconnectMsMax = Interlocked.Read(ref ReconnectMsMax),
            duplicates = Interlocked.Read(ref Duplicates),
            faultsSet = Interlocked.Read(ref FaultsSet),
            lootPickups = Interlocked.Read(ref LootPickups),
            lootDrops = Interlocked.Read(ref LootDrops),
            lootUses = Interlocked.Read(ref LootUses),
            connects = Interlocked.Read(ref Connects),
            connectFailures = Interlocked.Read(ref ConnectFailures),
            connectMsAvg = Interlocked.Read(ref Connects) > 0 ? Math.Round((double)Interlocked.Read(ref ConnectMsTotal) / Interlocked.Read(ref Connects), 1) : 0,
            connectMsMax = Interlocked.Read(ref ConnectMsMax),
            abuseSent = Interlocked.Read(ref AbuseSent),
            kicks = Interlocked.Read(ref Kicks),
            rejoins = Interlocked.Read(ref Rejoins),
            workloadFailures = Interlocked.Read(ref WorkloadFailures),
            errorCount = Interlocked.Read(ref _errorCount),
            errors,
        };
    }
}

// A group's background work on the runner side (re-arm after respawns, churn cycles): a task with its own cancellation.
// It reads published actor state, sends actor commands (the pump's channel takes several writers) and QA commands
// (HttpClient is thread-safe); it never touches RunContext's collections.
public sealed class GroupWorkload
{
    // 기능: 그룹 배경 Workload 기록을 만든다.
    // 입력: group - 그룹 이름, kind - Workload 종류, cts - 취소 소스, task - 실행 중인 Task.
    // 출력: GroupWorkload.
    public GroupWorkload(string group, string kind, CancellationTokenSource cts, Task task)
    {
        Group = group;
        Kind = kind;
        Cts = cts;
        Task = task;
    }

    public string Group { get; }
    public string Kind { get; }
    public CancellationTokenSource Cts { get; }
    public Task Task { get; }
}

// The run's groups and their workloads. Used by the run flow only (steps run one at a time; the workloads never call
// it), so plain collections. Bounded: MaxGroups groups, MaxWorkloads workloads. Removed: stopGroup ends a group's
// workloads; StopAllAsync (orchestrator cleanup, before the actors stop) ends every one, bounded by a timeout.
public sealed class GroupRegistry
{
    public const int MaxGroups = 32;
    public const int MaxWorkloads = 64;
    // A churn stopped right after a drop needs its bring-back (8 s, StressActions.BringBackTimeout) to finish inside it.
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, ActorGroup> _groups = new(StringComparer.Ordinal);
    private readonly List<GroupWorkload> _workloads = new();
    // The run's build sites, made on first use (runner flow) and shared by every build group and role.
    private BuildSitePool? _sitePool;
    public BuildSitePool SitePool => _sitePool ??= new BuildSitePool(StressMap.BuildSites(System.Numerics.Vector2.Zero, 1000f, StressMap.AllPieces, ProjectH.Shared.Simulation.BuildMaterialType.Wood));

    public IEnumerable<ActorGroup> All => _groups.Values;
    public int WorkloadCount => _workloads.Count;

    // 기능: 그룹을 등록하거나 같은 이름의 그룹을 덮어쓴다.
    // 입력: group - 등록할 그룹.
    // 출력: 반환값 없음. 새 이름인데 MaxGroups를 넘으면 QaStepException.
    public void Define(ActorGroup group)
    {
        if (!_groups.ContainsKey(group.Name) && _groups.Count >= MaxGroups) throw new QaStepException($"Too many groups (max {MaxGroups}).");
        _groups[group.Name] = group;
    }

    // 기능: 이름으로 그룹을 찾는다.
    // 입력: name - 그룹 이름.
    // 출력: 그룹. 없으면 QaStepException.
    public ActorGroup Get(string name) =>
        _groups.TryGetValue(name, out ActorGroup? g) ? g : throw new QaStepException($"Unknown group '{name}' (define it with actorGroup first).");

    // 기능: 이름으로 그룹을 찾는다(예외 없음).
    // 입력: name - 그룹 이름.
    // 출력: 있으면 true와 그룹, 없으면 false.
    public bool TryGet(string name, out ActorGroup group) => _groups.TryGetValue(name, out group!);

    // 기능: 끝난 Workload를 목록에서 치우고 새 Workload를 등록한다.
    // 입력: workload - 시작된 Workload.
    // 출력: 반환값 없음. MaxWorkloads를 넘으면 취소하고 QaStepException.
    public void Start(GroupWorkload workload)
    {
        _workloads.RemoveAll(w => w.Task.IsCompleted);
        if (_workloads.Count >= MaxWorkloads)
        {
            workload.Cts.Cancel();
            throw new QaStepException($"Too many group workloads (max {MaxWorkloads}).");
        }
        _workloads.Add(workload);
    }

    // 기능: 한 그룹의 Workload 목록을 복사한다.
    // 입력: group - 그룹 이름.
    // 출력: 해당 그룹의 Workload 배열(끝난 것 포함).
    public IReadOnlyList<GroupWorkload> WorkloadsOf(string group) => _workloads.Where(w => w.Group == group).ToArray();

    // 기능: 조건에 맞는 Workload를 취소하고 timeout 동안 끝나기를 기다린 뒤, 끝난 것을 목록에서 빼고 Cts를 해제한다.
    // 입력: group - 그룹 이름(null이면 전체), timeout - 대기 시간, kind - Workload 종류(null이면 전체).
    // 출력: 모두 시간 안에 끝났으면 true, 일부가 남았으면 false.
    // Cancels and waits (bounded). Returns false when some did not end in time (they end with the process; they hold
    // no server-side resource).
    public async Task<bool> StopAsync(string? group, TimeSpan timeout, string? kind = null)
    {
        GroupWorkload[] stopping = _workloads.Where(w => (group == null || w.Group == group) && (kind == null || w.Kind == kind)).ToArray();
        foreach (GroupWorkload w in stopping)
        {
            try
            {
                w.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        Task all = Task.WhenAll(stopping.Select(w => w.Task));
        bool done = await Task.WhenAny(all, Task.Delay(timeout)).ConfigureAwait(false) == all;
        foreach (GroupWorkload w in stopping.Where(w => w.Task.IsCompleted))
        {
            _workloads.Remove(w);
            w.Cts.Dispose();
        }
        return done;
    }

    // 기능: 모든 그룹의 ArrangeCommands를 합한다.
    // 입력: 없음.
    // 출력: 합계.
    // The sum over every group, for a measure phase (re-arm commands land in the server's qaCommandMs).
    public long ArrangeCommandsTotal() => _groups.Values.Sum(g => Interlocked.Read(ref g.Stats.ArrangeCommands));

    // 기능: 그룹의 현재 상태(크기·역할·Workload 수·joined/alive·구성원 Client 통계·RTT·체력·kick·건설 결과·사이트·Stats)를 JSON으로 만든다.
    // 입력: name - 그룹 이름.
    // 출력: 그룹 상태 JSON. 없는 그룹이면 QaStepException.
    public JsonElement Describe(string name)
    {
        ActorGroup g = Get(name);
        return JsonPath.From(new
        {
            name = g.Name,
            size = g.Members.Count,
            role = g.Role,
            workloads = _workloads.Count(w => w.Group == name && !w.Task.IsCompleted),
            joined = g.Members.Count(m => m.State.Joined),
            alive = g.Members.Count(m => m.State.Joined && m.State.Alive),
            // What the members' clients saw (actor side, summed): presses sent, HitConfirmed received, build answers.
            pressesSent = g.Members.Sum(m => m.State.PressesSent),
            hitsLanded = g.Members.Sum(m => (long)m.State.HitsLanded),
            damageTaken = g.Members.Sum(m => (long)m.State.DamageTaken),
            // Round trip of the members' connections now (LiteNetLib's value; 0 for members not connected).
            rttMsAvg = Math.Round(g.Members.Where(m => m.State.Connected).Select(m => (double)m.State.RttMs).DefaultIfEmpty(0).Average(), 1),
            rttMsMax = g.Members.Where(m => m.State.Connected).Select(m => m.State.RttMs).DefaultIfEmpty(0).Max(),
            // Health of the living members as their own snapshots show it (zone damage sends no DamageTaken; this sees it).
            healthMin = g.Members.Where(m => m.State.Joined && m.State.Alive).Select(m => (int)m.State.Health).DefaultIfEmpty(0).Min(),
            healthAvg = Math.Round(g.Members.Where(m => m.State.Joined && m.State.Alive).Select(m => (double)m.State.Health).DefaultIfEmpty(0).Average(), 1),
            // Members whose last connection the server closed with Kicked (abuse groups).
            kicked = g.Members.Count(m => m.State.Disconnected && m.State.DisconnectCode == "Kicked"),
            buildResults = BuildCodes(g.Members),
            // The run's build sites (shared by every build group): free = nobody holds it, shared = holders beyond one.
            buildSites = _sitePool == null ? null : new { total = _sitePool.Count, free = _sitePool.Free, shared = _sitePool.Shared },
            stats = g.Stats.Snapshot(),
        });
    }

    // 기능: 구성원들이 받은 건설 응답을 코드 이름별로 합한다.
    // 입력: members - 그룹 구성원.
    // 출력: 코드 이름 → 횟수(0인 코드는 뺀다).
    // Build answers by code name over the members (only codes seen).
    private static Dictionary<string, long> BuildCodes(IReadOnlyList<IQaActor> members)
    {
        var codes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (IQaActor m in members)
        {
            IReadOnlyList<long> counts = m.State.BuildCodeCounts;
            for (int c = 0; c < counts.Count; c++)
            {
                if (counts[c] == 0) continue;
                string name = ((ProjectH.Shared.Protocol.BuildResultCode)c).ToString();
                codes[name] = codes.GetValueOrDefault(name) + counts[c];
            }
        }
        return codes;
    }

    // 기능: count명의 액터를 seed로 섞은 뒤 shares 순서로 Count·Percent(최대 나머지 반올림)·Rest 크기만큼 배정한다.
    // 입력: count - 액터 수, shares - 배정 몫, seed - 셔플 시드.
    // 출력: 액터 index별 share index 배열(-1 = 배정 없음).
    // Pure (tests): which share each of `count` actors falls in (-1 = none). The actors are shuffled by `seed`
    // (Fisher-Yates), then the shares take their sizes in order: Count exactly, Percent of the actors rounded by largest
    // remainder (so 33/33/34 % of 10 gives 3/3/4, never 11), Rest whatever is left.
    public static int[] Distribute(int count, IReadOnlyList<GroupShare> shares, int seed)
    {
        var order = Enumerable.Range(0, count).ToArray();
        var rng = new Random(seed);
        for (int i = count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        var sizes = new int[shares.Count];
        var fractions = new List<(int Share, double Fraction)>();
        int used = 0;
        for (int s = 0; s < shares.Count; s++)
        {
            if (shares[s].Count is int c) sizes[s] = Math.Max(0, c);
            else if (shares[s].Percent is double p)
            {
                double exact = count * Math.Clamp(p, 0, 100) / 100.0;
                sizes[s] = (int)Math.Floor(exact);
                fractions.Add((s, exact - sizes[s]));
            }
            used += sizes[s];
        }
        double percentTotal = shares.Where(x => x.Percent != null).Sum(x => Math.Clamp(x.Percent!.Value, 0, 100));
        int percentTarget = (int)Math.Round(count * Math.Min(100, percentTotal) / 100.0);
        int percentNow = fractions.Sum(f => sizes[f.Share]);
        foreach (var f in fractions.OrderByDescending(f => f.Fraction).ThenBy(f => f.Share))
        {
            if (percentNow >= percentTarget || used >= count) break;
            sizes[f.Share]++;
            percentNow++;
            used++;
        }
        for (int s = 0; s < shares.Count; s++)
        {
            if (shares[s].Rest) sizes[s] = Math.Max(0, count - used);
        }
        var result = Enumerable.Repeat(-1, count).ToArray();
        int next = 0;
        for (int s = 0; s < shares.Count; s++)
        {
            for (int k = 0; k < sizes[s] && next < count; k++) result[order[next++]] = s;
        }
        return result;
    }
}
