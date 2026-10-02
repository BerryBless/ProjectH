using System.Text.Json;

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
        foreach (var pair in scenarioVariables) Variables[pair.Key] = pair.Value;
        Variables["runId"] = JsonPath.From(runId);
        Variables["seed"] = JsonPath.From(seed);
    }

    public string RunId { get; }
    public int Seed { get; }
    public IQaServerClient Server { get; }
    public ActorManager Actors { get; }
    public MarkerStore Markers { get; }
    public Action<string> Log { get; }
    public Dictionary<string, JsonElement> Variables { get; } = new(StringComparer.Ordinal);
    public EventCursor? Events { get; init; }
    // Where actors connect (the launched server's free port, or the attach target).
    public string GameHost { get; init; } = "127.0.0.1";
    public int GamePort { get; init; }
    // Server state polling interval for waitFor / waitForEvent (request §29: configurable, not every frame).
    public int PollIntervalMs { get; init; } = 100;

    public void SetVariable(string name, JsonElement value)
    {
        if (!Variables.ContainsKey(name) && Variables.Count >= MaxVariables) throw new QaStepException($"Too many variables (max {MaxVariables}).");
        Variables[name] = value.Clone();
    }
}
