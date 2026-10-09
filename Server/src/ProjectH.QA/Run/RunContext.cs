using System.Text.Json;
using ProjectH.QA.Faults;

namespace ProjectH.QA;

// One scenario run's runtime state (request §141-142): kept apart from the scenario DTO and used only by the run's
// single async flow (steps run one after another), so plain collections, no locks. Disposed with the run.
public sealed class RunContext
{
    public const int MaxVariables = 1000;   // saveAs per step: bounded by the step count anyway; a guard for loops later

    // 기능: 한 실행의 런타임 상태를 만든다. 시나리오 변수를 복사하고 내장 변수 runId·seed를 넣으며 네트워크 장애 허브를 준비한다.
    // 입력: runId - 실행 ID, seed - 실행 시드, server - QA 서버 Client, actors - 액터 관리자, markers - 위치 이름 저장소, scenarioVariables - 시나리오 변수(파라미터 세트·--set 병합 후), log - 실행 로그 출력.
    // 출력: 변수·허브가 준비되고 Events·ServerControl은 아직 null인 실행 상태.
    public RunContext(string runId, int seed, IQaServerClient server, ActorManager actors, MarkerStore markers,
        IReadOnlyDictionary<string, JsonElement> scenarioVariables, Action<string> log)
    {
        RunId = runId;
        Seed = seed;
        Server = server;
        Actors = actors;
        Markers = markers;
        Log = log;
        Network = new NetworkFaultHub(seed, log);
        foreach (var pair in scenarioVariables) Variables[pair.Key] = pair.Value;
        Variables["runId"] = JsonPath.From(runId);
        Variables["seed"] = JsonPath.From(seed);
    }

    // QA-5 playInputs: recordings are resolved next to the scenario file and must stay under <RepoRoot>/QA.
    public string RepoRoot { get; init; } = string.Empty;
    public string ScenarioPath { get; init; } = string.Empty;

    public string RunId { get; }
    public int Seed { get; }
    // Replaced by SwitchServer when a scenario restarts the launched server (new process, new ports).
    public IQaServerClient Server { get; private set; }
    public ActorManager Actors { get; }
    public MarkerStore Markers { get; }
    public Action<string> Log { get; }
    public Dictionary<string, JsonElement> Variables { get; } = new(StringComparer.Ordinal);
    public EventCursor? Events { get; internal set; }
    // Where actors connect (the launched server's free port, or the attach target).
    public string GameHost { get; init; } = "127.0.0.1";
    public int GamePort { get; internal set; }
    // Server state polling interval for waitFor / waitForEvent (request §29: configurable, not every frame).
    public int PollIntervalMs { get; init; } = 100;
    // QA-3 faults. Network: per-actor proxies. Db: docker stop/start. ServerControl: null in attach mode.
    public NetworkFaultHub Network { get; }
    public DbFaultHub Db { get; init; } = new();
    public IServerControl? ServerControl { get; internal set; }
    // QA-4. The run control (manual checks), the run's report folder (screenshots go to <it>/screenshots), and what the
    // run produced for the report (bounded: MaxScreenshots, one manual check per step).
    public IRunControl? Control { get; init; }
    public string ReportDirectory { get; init; } = string.Empty;
    public List<string> Screenshots { get; } = new();
    public List<ManualCheckRecord> ManualChecks { get; } = new();
    public const int MaxScreenshots = 200;
    // Stress (D37-D41): the run's actor groups and their workloads, the measure phases (bounded in StressReport), whether
    // the server's QA events are on (Qa:Events; stall diagnostics read recent events only then), the step running now
    // and the last metrics sample (for stall and crash records).
    public GroupRegistry Groups { get; } = new();
    public StressReport Stress { get; } = new();
    public bool EventsEnabled { get; init; } = true;
    public string? CurrentStepId { get; set; }
    public MeasureSample? LastSample { get; set; }
    public string? LastPhase { get; set; }
    // Warnings steps add for the report (stress D41-D42), copied into RunReport.Warnings at the end. Bounded.
    public BoundedList<string> Warnings { get; } = new(200);

    // 기능: 서버 재시작 뒤 실행이 바라보는 QA Client·이벤트 커서·게임 포트를 새 서버 것으로 바꾼다. 액터 객체는 그대로이며 다음 connect부터 새 포트로 간다.
    // 입력: server - 새 서버의 QA Client, events - 새 이벤트 커서(null 허용), gamePort - 새 게임 포트.
    // 출력: 반환값 없음. Server·Events·GamePort가 바뀐다.
    // After a server restart: the new QA client, a fresh event cursor (event sequence numbers start again) and the new
    // game port. Actors keep their objects; their next connect goes to the new port.
    public void SwitchServer(IQaServerClient server, EventCursor? events, int gamePort)
    {
        Server = server;
        Events = events;
        GamePort = gamePort;
    }

    // 기능: 액터의 다음 연결 목적지를 정한다(프록시 대상이면 새 프록시를 열어 그 주소를 준다).
    // 입력: alias - 액터 별칭.
    // 출력: 연결할 호스트와 포트.
    // Where an actor's next connection goes: the game server, or a fresh proxy in front of it.
    public Task<(string Host, int Port)> ConnectTargetAsync(string alias) => Network.PrepareConnectAsync(alias, GameHost, GamePort);

    // 기능: saveAs 변수를 저장한다(새 이름은 MaxVariables까지만, 기존 이름은 덮어씀).
    // 입력: name - 변수 이름, value - 저장할 JSON 값(복제해 보관).
    // 출력: 반환값 없음. Variables가 갱신된다. 상한을 넘으면 QaStepException.
    public void SetVariable(string name, JsonElement value)
    {
        if (!Variables.ContainsKey(name) && Variables.Count >= MaxVariables) throw new QaStepException($"Too many variables (max {MaxVariables}).");
        Variables[name] = value.Clone();
    }
}

// A list that keeps the first Capacity items and counts the rest (warnings of a long run).
public sealed class BoundedList<T>
{
    private readonly List<T> _items = new();

    // 기능: 보관 상한이 정해진 목록을 만든다.
    // 입력: capacity - 보관할 최대 항목 수.
    // 출력: 비어 있는 목록.
    public BoundedList(int capacity) => Capacity = capacity;

    public int Capacity { get; }
    public int Dropped { get; private set; }
    public IReadOnlyList<T> Items => _items;

    // 기능: 항목을 넣되 Capacity를 넘으면 버리고 Dropped만 센다.
    // 입력: item - 넣을 항목.
    // 출력: 반환값 없음. Items 또는 Dropped가 늘어난다.
    public void Add(T item)
    {
        if (_items.Count < Capacity) _items.Add(item);
        else Dropped++;
    }
}

// One manual check's outcome for the report (D30): PASS / FAIL / SKIPPED, who answered and the note.
public sealed record ManualCheckRecord(int StepIndex, string StepId, string Description, string Result, string? Note, string By);
