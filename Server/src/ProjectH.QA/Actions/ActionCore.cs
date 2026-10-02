using System.Diagnostics;
using System.Text.Json;

namespace ProjectH.QA;

public enum ActorUse
{
    None,
    Required,
    Optional,
}

// What an action accepts, for validation before the run (request §52) and for the QA-2 editor's form later.
// Required entries may be "a|b": one of them must be present.
public sealed class ActionSpec
{
    public required string Name { get; init; }
    public ActorUse Actor { get; init; }
    public string[] Required { get; init; } = Array.Empty<string>();
    public string[] Optional { get; init; } = Array.Empty<string>();
    // Parameters holding a position (marker name or {x,y?,z}); literal names are checked against the markers.
    public string[] PositionParams { get; init; } = Array.Empty<string>();
    // D11: a server QA command (Arrange tool); ArrangeOnly ones warn outside the arrange phase.
    public bool ServerCommand { get; init; }
    public bool ArrangeOnly { get; init; }
    // QA-3: the step's actor must have `"network": { "proxy": true }` (network fault steps).
    public bool NeedsProxy { get; init; }
    // QA-3: controls the launched server process; a validation error in an attach scenario.
    public bool LaunchOnly { get; init; }
    // Default step timeout when the step gives none (ms). Polling actions read their own limit from it (soft) and the
    // runner cancels at it plus a grace (hard).
    public Func<StepDefinition, int>? DefaultTimeout { get; init; }
    // Extra checks of literal parameter values (errors).
    public Func<StepDefinition, IEnumerable<string>>? Check { get; init; }
    // Actor aliases this step creates (spawnActors), so later steps may name them.
    public Func<StepDefinition, IEnumerable<string>>? CreatesActors { get; init; }

    public const int StandardTimeoutMs = 10_000;

    public int TimeoutFor(StepDefinition step) => step.TimeoutMilliseconds ?? DefaultTimeout?.Invoke(step) ?? StandardTimeoutMs;

    public bool Accepts(string param)
    {
        foreach (string r in Required)
        {
            foreach (string alt in r.Split('|')) if (alt == param) return true;
        }
        return Array.IndexOf(Optional, param) >= 0;
    }
}

// Request §139-140: one handler per action name. Handlers are stateless; run state lives in RunContext.
public interface IScenarioActionHandler
{
    ActionSpec Spec { get; }
    Task<StepOutcome> ExecuteAsync(StepContext context, CancellationToken token);
}

public sealed class StepOutcome
{
    public bool Passed { get; init; }
    public string? Message { get; init; }
    public string? Expected { get; init; }
    public string? Actual { get; init; }
    // What saveAs stores (a read value or a command result).
    public JsonElement? Value { get; init; }

    // QA-3: the step could not run for an environment reason that is not a failure of the game (Docker or the DB
    // container missing, request §76). SkipRest: the remaining steps depend on it and are skipped too.
    public bool Skipped { get; init; }
    public bool SkipRest { get; init; }

    public static StepOutcome Pass(string? message = null, JsonElement? value = null) => new() { Passed = true, Message = message, Value = value };

    public static StepOutcome Skip(string reason, bool skipRest) => new() { Passed = true, Skipped = true, SkipRest = skipRest, Message = reason };

    public static StepOutcome Fail(string message, string? expected = null, string? actual = null) =>
        new() { Passed = false, Message = message, Expected = expected, Actual = actual };
}

public sealed class ActionRegistry
{
    private readonly Dictionary<string, IScenarioActionHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public void Add(IScenarioActionHandler handler) => _handlers.Add(handler.Spec.Name, handler);

    public bool TryGet(string action, out IScenarioActionHandler handler) => _handlers.TryGetValue(action, out handler!);

    public IEnumerable<ActionSpec> Specs => _handlers.Values.Select(h => h.Spec).OrderBy(s => s.Name, StringComparer.Ordinal);

    // Every MVP action (QA-1).
    public static ActionRegistry CreateDefault()
    {
        var registry = new ActionRegistry();
        FlowActions.Register(registry);
        ActorActions.Register(registry);
        ServerCommandActions.Register(registry);
        FaultActions.Register(registry);
        return registry;
    }
}

// What one step execution sees: its parameters with variables substituted, its actor, the run, and a soft deadline.
public sealed class StepContext
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public StepContext(RunContext run, StepDefinition step, int timeoutMs)
    {
        Run = run;
        Step = step;
        TimeoutMs = timeoutMs;
    }

    public RunContext Run { get; }
    public StepDefinition Step { get; }
    public int TimeoutMs { get; }
    public long ElapsedMs => _clock.ElapsedMilliseconds;
    public bool TimedOut => _clock.ElapsedMilliseconds >= TimeoutMs;

    public JsonElement? Param(string name) =>
        Step.Params.TryGetValue(name, out JsonElement raw) ? Variables.Substitute(raw, Run.Variables) : null;

    public string? String(string name)
    {
        JsonElement? v = Param(name);
        if (v == null || v.Value.ValueKind == JsonValueKind.Null) return null;
        return v.Value.ValueKind == JsonValueKind.String ? v.Value.GetString() : v.Value.GetRawText();
    }

    public string RequireString(string name) => String(name) ?? throw new QaStepException($"'{name}' is required.");

    public double? Double(string name)
    {
        JsonElement? v = Param(name);
        if (v == null || v.Value.ValueKind == JsonValueKind.Null) return null;
        if (Comparison.TryNumber(v.Value, out double d)) return d;
        throw new QaStepException($"'{name}' must be a number (got {JsonPath.Describe(v)}).");
    }

    public int? Int(string name, int min, int max)
    {
        double? d = Double(name);
        if (d == null) return null;
        if (d.Value != Math.Floor(d.Value) || d.Value < min || d.Value > max)
            throw new QaStepException($"'{name}' must be an integer {min}-{max} (got {d.Value}).");
        return (int)d.Value;
    }

    public bool? Bool(string name)
    {
        JsonElement? v = Param(name);
        if (v == null || v.Value.ValueKind == JsonValueKind.Null) return null;
        if (v.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.Value.GetBoolean();
        if (v.Value.ValueKind == JsonValueKind.String && bool.TryParse(v.Value.GetString(), out bool b)) return b;
        throw new QaStepException($"'{name}' must be true or false.");
    }

    public QaPosition? Position(string name)
    {
        JsonElement? v = Param(name);
        if (v == null) return null;
        if (!Run.Markers.TryResolve(v.Value, out QaPosition p, out string? error)) throw new QaStepException($"'{name}': {error}");
        return p;
    }

    public string ActorAlias => Step.Actor ?? throw new QaStepException("'actor' is required.");

    public IQaActor Actor() => Run.Actors.Get(ActorAlias);

    // Polls a local condition (actor state: no server cost) until it holds or the step's soft limit passes.
    public async Task<bool> WaitUntilAsync(Func<bool> condition, CancellationToken token, int intervalMs = ActorPollMs)
    {
        while (true)
        {
            if (condition()) return true;
            if (TimedOut) return false;
            await Task.Delay(intervalMs, token).ConfigureAwait(false);
        }
    }

    // Actor state changes at most once per actor tick (33 ms at 30 Hz); polling it costs nothing outside this process.
    public const int ActorPollMs = 15;
}
