using System.Text.Json;

namespace ProjectH.QA;

public enum RunStatus
{
    Passed,
    Failed,      // a step failed or the scenario timed out (exit 1)
    Cancelled,   // stopped by the user (exit 1)
    Error,       // the tool could not run the scenario (exit 2)
}

public enum StepStatus
{
    Pending,     // not run (yet): the default, so a step never reports PASS by accident
    Passed,
    Failed,
    Error,
    Cancelled,
    Skipped,
    Running,     // UI only: the step is executing now
}

public sealed class StepResult
{
    // Retries of this step after a held failure (D23 Retry Failed Step); 0 = ran once.
    public int Attempts { get; set; }
    public int Index { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string? Actor { get; init; }
    public string? Phase { get; init; }
    public string Title { get; init; } = string.Empty;
    public StepStatus Status { get; set; }
    public long DurationMs { get; set; }
    public string? Message { get; set; }
    public string? Expected { get; set; }
    public string? Actual { get; set; }
    public bool ContinueOnFailure { get; init; }

    // Request §101: "01 Connect playerA PASS 121 ms".
    public string Line() => $"{Index + 1:00} {Title} {StatusWord(Status)} {DurationMs} ms";

    public static string StatusWord(StepStatus s) => s switch
    {
        StepStatus.Passed => "PASS",
        StepStatus.Failed => "FAIL",
        StepStatus.Error => "ERROR",
        StepStatus.Cancelled => "CANCELLED",
        StepStatus.Pending => "PENDING",
        StepStatus.Running => "RUNNING",
        _ => "SKIPPED",
    };
}

public sealed record CleanupResult(string Name, bool Ok, string Message);

public sealed class FailureInfo
{
    public int StepIndex { get; init; }
    public string StepId { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string? Message { get; init; }
    public string? Expected { get; init; }
    public string? Actual { get; init; }
}

public sealed class StateDump
{
    public List<ActorState> Actors { get; } = new();
    public JsonElement? Players { get; set; }
    public JsonElement? Match { get; set; }
    public string? Error { get; set; }
}

// D17, request §98: everything one run produced. Filled by the orchestrator flow, written once at the end.
public sealed class RunReport
{
    public string RunId { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    public string ScenarioFile { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
    public int Seed { get; set; }
    public RunStatus Status { get; set; }
    public int ExitCode { get; set; }
    public DateTimeOffset Started { get; set; }
    public long DurationMs { get; set; }
    public string? GitCommit { get; set; }
    public bool? GitDirty { get; set; }
    public string? GitError { get; set; }
    public string ServerMode { get; set; } = string.Empty;
    public string? ServerVersion { get; set; }
    public JsonElement? ServerHealth { get; set; }
    public string? QaUrl { get; set; }
    public int GamePort { get; set; }
    public int? ServerPid { get; set; }
    public IReadOnlyList<string> ServerArguments { get; set; } = Array.Empty<string>();
    public List<string> Warnings { get; } = new();
    public List<StepResult> Steps { get; } = new();
    public FailureInfo? Failure { get; set; }
    public StateDump? StateDump { get; set; }
    public List<JsonElement> Events { get; } = new();
    public long EventsDroppedByServer { get; set; }
    public JsonElement? Metrics { get; set; }
    public string[] ServerLogTail { get; set; } = Array.Empty<string>();
    public List<CleanupResult> Cleanup { get; } = new();
    // QA-4 fills these (manual checks D16, Unity screenshots D15).
    public List<JsonElement> ManualChecks { get; } = new();
    public List<string> Screenshots { get; } = new();
    // D24 Debug Run: server state around each step (bounded by RunnerOptions.MaxDebugSnapshots).
    public List<DebugSnapshot> DebugSnapshots { get; } = new();
    // The scenario ran from unsaved editor text (UI), so ScenarioFile may differ from what ran.
    public bool UnsavedText { get; set; }
    public Dictionary<string, JsonElement> Variables { get; } = new();
    public string? ToolError { get; set; }
    public string? ReportDirectory { get; set; }

    public static int ExitCodeFor(RunStatus status) => status switch
    {
        RunStatus.Passed => 0,
        RunStatus.Error => 2,
        _ => 1,
    };
}

public sealed class DebugSnapshot
{
    public int StepIndex { get; init; }
    public string StepId { get; init; } = string.Empty;
    public string When { get; init; } = string.Empty;   // before / after
    public System.Text.Json.JsonElement? Match { get; set; }
    public System.Text.Json.JsonElement? Players { get; set; }
    public string? Error { get; set; }
}
