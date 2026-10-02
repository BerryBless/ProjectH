using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProjectH.QA;

public sealed record ValidationIssue(bool IsError, string Where, string Message)
{
    public override string ToString() => $"{(IsError ? "error" : "warning")} {Where}: {Message}";
}

// Request §52 + D7, D11: everything that can be known before the run. Errors stop the run (exit 2); warnings are
// printed and kept in the report.
public static partial class ScenarioValidator
{
    public const int MaxTimeoutSeconds = 24 * 3600;
    public const int MaxStepTimeoutMs = 3_600_000;
    private static readonly string[] s_phases = { "arrange", "act", "assert" };

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_\-]*$")]
    private static partial Regex VariableName();

    public static IReadOnlyList<ValidationIssue> Validate(ScenarioDefinition s, ActionRegistry registry, MarkerStore markers)
    {
        var issues = new List<ValidationIssue>();
        void Error(string where, string message) => issues.Add(new ValidationIssue(true, where, message));
        void Warn(string where, string message) => issues.Add(new ValidationIssue(false, where, message));

        if (s.SchemaVersion != ScenarioDefinition.SupportedSchemaVersion)
            Error("scenario", $"schemaVersion must be {ScenarioDefinition.SupportedSchemaVersion} (got {s.SchemaVersion}).");
        if (s.TimeoutSeconds <= 0 || s.TimeoutSeconds > MaxTimeoutSeconds) Error("scenario", $"timeoutSeconds must be 1-{MaxTimeoutSeconds}.");
        if (s.Steps.Count == 0) Error("scenario", "No steps.");

        if (!string.Equals(s.Server.Mode, ServerSpec.Launch, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(s.Server.Mode, ServerSpec.Attach, StringComparison.OrdinalIgnoreCase))
            Error("server", $"mode must be '{ServerSpec.Launch}' or '{ServerSpec.Attach}'.");
        if (s.Server.QaUrl != null && (!Uri.TryCreate(s.Server.QaUrl, UriKind.Absolute, out Uri? url) || url.Scheme is not ("http" or "https")))
            Error("server", $"qaUrl '{s.Server.QaUrl}' is not an http URL.");
        if (s.Server.GamePort is int port && (port < 1 || port > 65535)) Error("server", "gamePort must be 1-65535.");
        foreach (string key in s.Server.Options.Keys)
        {
            if (key.Length == 0 || key.StartsWith('-') || key.Contains('=')) Error("server", $"Bad option name '{key}' (write \"Section:Key\": \"value\").");
        }

        var actors = new HashSet<string>(StringComparer.Ordinal);
        foreach (ActorSpec a in s.Actors)
        {
            string where = $"actor {a.Id}";
            if (!actors.Add(a.Id)) Error(where, "Duplicate actor id.");
            if (ActorManager.CheckAlias(a.Id) is string aliasError) Error(where, aliasError);
            if (!string.Equals(a.Type, ActorSpec.HeadlessClient, StringComparison.OrdinalIgnoreCase))
                Error(where, $"Actor type '{a.Type}' is not supported yet (QA-1: {ActorSpec.HeadlessClient}).");
        }

        var proxied = new HashSet<string>(s.Actors.Where(a => a.Proxy).Select(a => a.Id), StringComparer.Ordinal);
        var variables = new HashSet<string>(s.Variables.Keys, StringComparer.Ordinal) { "runId", "seed" };
        foreach (string name in s.Variables.Keys)
        {
            if (!VariableName().IsMatch(name)) Error("variables", $"Bad variable name '{name}'.");
        }
        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (StepDefinition step in s.Steps)
        {
            string where = $"step {step.Index + 1:00} ({step.Id})";
            if (!stepIds.Add(step.Id)) Error(where, "Duplicate step id.");
            if (step.Phase != null && Array.IndexOf(s_phases, step.Phase.ToLowerInvariant()) < 0)
                Error(where, $"phase must be one of {string.Join(", ", s_phases)}.");
            if (step.TimeoutMilliseconds is int t && (t <= 0 || t > MaxStepTimeoutMs))
                Error(where, $"timeoutMilliseconds must be 1-{MaxStepTimeoutMs}.");

            foreach (var p in step.Params)
            {
                foreach (string root in Variables.ReferencedRoots(p.Value))
                {
                    if (!variables.Contains(root)) Error(where, $"Unknown variable '${{{root}}}' in '{p.Key}'.");
                }
            }

            if (!registry.TryGet(step.Action, out IScenarioActionHandler? handler))
            {
                Error(where, $"Unknown action '{step.Action}'.");
                if (step.SaveAs != null) variables.Add(step.SaveAs);
                continue;
            }
            ActionSpec spec = handler.Spec;

            if (spec.Actor == ActorUse.Required && step.Actor == null) Error(where, $"'{spec.Name}' needs 'actor'.");
            if (spec.Actor == ActorUse.None && step.Actor != null) Error(where, $"'{spec.Name}' takes no 'actor'.");
            if (step.Actor != null && !actors.Contains(step.Actor)) Error(where, $"Unknown actor '{step.Actor}'.");
            if (spec.NeedsProxy && step.Actor != null && actors.Contains(step.Actor) && !proxied.Contains(step.Actor))
                Error(where, $"'{spec.Name}' needs actor '{step.Actor}' to have \"network\": {{ \"proxy\": true }}.");
            if (spec.LaunchOnly && string.Equals(s.Server.Mode, ServerSpec.Attach, StringComparison.OrdinalIgnoreCase))
                Error(where, $"'{spec.Name}' controls the server process: it works only when the tool launches the server (not in attach mode).");
            if (step.Params.TryGetValue("target", out JsonElement target) && target.ValueKind == JsonValueKind.String
                && !Variables.HasReference(target) && !actors.Contains(target.GetString()!))
                Error(where, $"Unknown target actor '{target.GetString()}'.");

            foreach (string required in spec.Required)
            {
                string[] alternatives = required.Split('|');
                if (!alternatives.Any(step.Has)) Error(where, $"Missing parameter '{string.Join("' or '", alternatives)}'.");
            }
            foreach (string name in step.Params.Keys)
            {
                if (!spec.Accepts(name)) Warn(where, $"Parameter '{name}' is not used by '{spec.Name}'.");
            }
            foreach (string name in spec.PositionParams)
            {
                if (!step.Params.TryGetValue(name, out JsonElement value) || Variables.HasReference(value)) continue;
                if (!markers.TryResolve(value, out _, out string? error)) Error(where, $"'{name}': {error}");
            }
            if (spec.Check != null)
            {
                foreach (string error in spec.Check(step)) Error(where, error);
            }

            string? phase = step.EffectivePhase;
            if (spec.ServerCommand && spec.Name != "mark" && phase is "act" or "assert")
                Warn(where, $"Server QA command '{spec.Name}' in the {phase} phase: Act should use the real gameplay path (D11).");
            if (spec.ArrangeOnly && phase != "arrange")
                Warn(where, $"'{spec.Name}' is an Arrange-only command (phase: {phase ?? "none"}); never use it to stand in for gameplay (request §24).");

            if (step.SaveAs != null)
            {
                if (!VariableName().IsMatch(step.SaveAs)) Error(where, $"Bad saveAs name '{step.SaveAs}'.");
                variables.Add(step.SaveAs);
            }
            if (spec.CreatesActors != null)
            {
                foreach (string alias in spec.CreatesActors(step))
                {
                    if (!actors.Add(alias)) Error(where, $"Actor '{alias}' already exists.");
                }
            }
            if (actors.Count > ActorManager.MaxActors) Error(where, $"More than {ActorManager.MaxActors} actors.");
        }
        return issues;
    }
}
