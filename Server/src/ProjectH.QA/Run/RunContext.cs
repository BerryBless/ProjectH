using System.Text.Json;
using ProjectH.QA.Faults;

namespace ProjectH.QA;

// One scenario run's runtime state (request §141-142): kept apart from the scenario DTO and used only by the run's
// single async flow (steps run one after another), so plain collections, no locks. Disposed with the run.
public sealed class RunContext
{
    public const int MaxVariables = 1000;   // saveAs per step: bounded by the step count anyway; a guard for loops later

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

    // After a server restart: the new QA client, a fresh event cursor (event sequence numbers start again) and the new
    // game port. Actors keep their objects; their next connect goes to the new port.
    public void SwitchServer(IQaServerClient server, EventCursor? events, int gamePort)
    {
        Server = server;
        Events = events;
        GamePort = gamePort;
    }

    // Where an actor's next connection goes: the game server, or a fresh proxy in front of it.
    public Task<(string Host, int Port)> ConnectTargetAsync(string alias) => Network.PrepareConnectAsync(alias, GameHost, GamePort);

    public void SetVariable(string name, JsonElement value)
    {
        if (!Variables.ContainsKey(name) && Variables.Count >= MaxVariables) throw new QaStepException($"Too many variables (max {MaxVariables}).");
        Variables[name] = value.Clone();
    }
}

// One manual check's outcome for the report (D30): PASS / FAIL / SKIPPED, who answered and the note.
public sealed record ManualCheckRecord(int StepIndex, string StepId, string Description, string Result, string? Note, string By);
