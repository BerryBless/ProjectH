using System.Text.Json;

namespace ProjectH.QA;

// Scenario DTOs (schemaVersion 1, design doc "시나리오 형식"). Built once by ScenarioLoader and never changed while a run
// uses them (request §141): runtime values (saveAs variables, actor state, results) live in RunContext.
public sealed class ScenarioDefinition
{
    public const int SupportedSchemaVersion = 1;
    public const double DefaultTimeoutSeconds = 120;

    public int SchemaVersion { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public int? Seed { get; init; }
    public double TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;
    public ServerSpec Server { get; init; } = new();
    public IReadOnlyDictionary<string, JsonElement> Variables { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyList<ActorSpec> Actors { get; init; } = Array.Empty<ActorSpec>();
    public IReadOnlyList<StepDefinition> Steps { get; init; } = Array.Empty<StepDefinition>();
    // Where it was loaded from (empty for text loaded in tests).
    public string SourcePath { get; init; } = string.Empty;
}

public sealed class ServerSpec
{
    public const string Launch = "launch";
    public const string Attach = "attach";

    public string Mode { get; init; } = Launch;
    public string? QaUrl { get; init; }
    public string? Host { get; init; }
    public int? GamePort { get; init; }
    // Extra server configuration (`--Key=Value`), applied after the tool's defaults (D12) so a scenario can override them.
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
}

public sealed record ActorSpec(string Id, string Type)
{
    // QA-3 (D13): the actor connects through its own UdpFaultProxy (`"network": { "proxy": true }`), so network fault
    // steps can target it. Explicit per actor: a proxy changes nothing until a fault is set, but it is one more hop.
    public bool Proxy { get; init; }

    public const string HeadlessClient = "HeadlessClient";
}

// One step. Common fields are typed; everything else is an action parameter (Params, as written, `${var}` not yet
// substituted: substitution happens per execution, so the DTO stays the file's content).
public sealed class StepDefinition
{
    public static readonly string[] CommonFields =
    {
        "id", "action", "actor", "phase", "timeoutMilliseconds", "continueOnFailure", "breakpoint", "saveAs", "description",
    };

    public int Index { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string? Actor { get; init; }
    // As written in the file (null when absent).
    public string? Phase { get; init; }
    // The phase in force: an explicit phase lasts until the next step that names one (Arrange/Act/Assert sections).
    public string? EffectivePhase { get; init; }
    public int? TimeoutMilliseconds { get; init; }
    public bool ContinueOnFailure { get; init; }
    public bool Breakpoint { get; init; }
    public string? SaveAs { get; init; }
    public string? Description { get; init; }
    public IReadOnlyDictionary<string, JsonElement> Params { get; init; } = new Dictionary<string, JsonElement>();

    public bool Has(string name) => Params.ContainsKey(name);
}
