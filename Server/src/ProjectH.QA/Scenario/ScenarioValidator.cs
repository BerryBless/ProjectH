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

    // --set / a suite entry's variables: only names the scenario already has (a variable or a parameter), so a typo is
    // an error instead of a silently ignored value.
    public static IEnumerable<ValidationIssue> CheckOverrides(ScenarioDefinition s, IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            if (name is "runId" or "seed") yield return new ValidationIssue(true, "--set", $"'{name}' is built in (use --seed for the seed).");
            else if (!s.Variables.ContainsKey(name) && !(s.Parameters.Count > 0 && s.Parameters.All(p => p.TryGetProperty(name, out _))))
                yield return new ValidationIssue(true, "--set", $"'{name}' is not a variable of '{s.Name}' (known: {string.Join(", ", s.Variables.Keys.Take(30))}).");
        }
    }

    // 기능: 실행 전에 알 수 있는 시나리오 오류·경고를 모두 찾는다(Actor 종류별 허용 Action, Unity 입력 Action 규칙 포함).
    // 입력: s - 검사할 시나리오, registry - Action 목록, markers - 위치 이름 검사용 Marker.
    // 출력: 찾은 문제 목록. 오류가 하나라도 있으면 실행하지 않는다.
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
            if (!string.Equals(a.Type, ActorSpec.HeadlessClient, StringComparison.OrdinalIgnoreCase) && !a.IsUnity)
                Error(where, $"Actor type '{a.Type}' is not supported ({ActorSpec.HeadlessClient} or {ActorSpec.UnityClient}).");
            if (a.Unity != null && !a.IsUnity) Error(where, "'unity' is only for UnityClient actors.");
            if (a.IsUnity && a.Proxy) Error(where, "A UnityClient actor cannot use the network fault proxy.");
            if (a.Unity?.AttachPort is int ap && (ap < 1 || ap > 65535)) Error(where, "unity.attachPort must be 1-65535.");
            if (a.Unity?.AttachPort != null && a.Unity.Exe != null) Error(where, "Give unity.exe or unity.attachPort, not both.");
            if (a.Unity?.Width is int w && (w < 160 || w > 7680)) Error(where, "unity.width must be 160-7680.");
            if (a.Unity?.Height is int h && (h < 120 || h > 4320)) Error(where, "unity.height must be 120-4320.");
        }

        var proxied = new HashSet<string>(s.Actors.Where(a => a.Proxy).Select(a => a.Id), StringComparer.Ordinal);
        var unity = new HashSet<string>(s.Actors.Where(a => a.IsUnity).Select(a => a.Id), StringComparer.Ordinal);
        var variables = new HashSet<string>(s.Variables.Keys, StringComparer.Ordinal) { "runId", "seed" };
        foreach (string name in s.Variables.Keys)
        {
            if (!VariableName().IsMatch(name)) Error("variables", $"Bad variable name '{name}'.");
        }

        // D31: every parameter set is merged over `variables` for its run, so a name is usable when every set has it
        // (or `variables` has it as the default).
        var parameterNames = new List<HashSet<string>>();
        for (int i = 0; i < s.Parameters.Count; i++)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty p in s.Parameters[i].EnumerateObject())
            {
                if (!VariableName().IsMatch(p.Name)) Error($"parameters[{i}]", $"Bad parameter name '{p.Name}'.");
                else if (p.Name is "runId" or "seed") Error($"parameters[{i}]", $"'{p.Name}' is a built-in variable; it cannot be a parameter.");
                if (!names.Add(p.Name)) Error($"parameters[{i}]", $"Duplicate parameter '{p.Name}'.");
            }
            parameterNames.Add(names);
        }
        if (parameterNames.Count > 0)
        {
            foreach (string name in parameterNames[0].Where(n => parameterNames.All(set => set.Contains(n)))) variables.Add(name);
        }
        string UnknownVariable(string root)
        {
            var missing = parameterNames.Select((set, i) => (set, i)).Where(x => !x.set.Contains(root)).Select(x => $"parameters[{x.i}]").ToList();
            if (missing.Count > 0 && missing.Count < parameterNames.Count)
                return $"Variable '${{{root}}}' is not in every parameter set (missing in {string.Join(", ", missing.Take(10))}{(missing.Count > 10 ? ", ..." : "")}); add it there or give a default in 'variables'.";
            return $"Unknown variable '${{{root}}}'";
        }

        if (!(s.BaselineWarnPercent > 0 && s.BaselineWarnPercent <= 100_000))
            Error("scenario", "baselineWarnPercent must be a positive percentage (default 50).");
        if (s.BaselineValues.Count > ScenarioDefinition.MaxBaselineValues)
            Error("baseline", $"At most {ScenarioDefinition.MaxBaselineValues} baseline values.");
        // Stress D39: headless clients only; QA events are off unless the scenario turns them on.
        bool eventsOff = s.Stress && !(s.Server.Options.TryGetValue("Qa:Events", out string? ev) && string.Equals(ev, "true", StringComparison.OrdinalIgnoreCase));
        if (s.Stress)
        {
            foreach (ActorSpec a in s.Actors.Where(a => a.IsUnity)) Error($"actor {a.Id}", "A stress scenario uses headless clients only (no UnityClient actor, D39).");
        }
        var groups = new HashSet<string>(StringComparer.Ordinal);
        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (StepDefinition step in s.Steps)
        {
            string where = $"step {step.Index + 1:00} ({step.Id})";
            if (s.Stress && (step.Action == "manualCheck" || Array.IndexOf(UnityActions.UnityOnly, step.Action) >= 0))
                Error(where, $"'{step.Action}' is not allowed in a stress scenario (D39: no screenshots, Unity players or manual steps).");
            if (eventsOff && (step.Action == "waitForEvent" || step.Params.Any(p => p.Key is "path" or "condition" && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.StartsWith("event.", StringComparison.Ordinal))))
                Warn(where, "QA events are off in a stress scenario (Qa:Events=false): this step sees no events. Set server.options Qa:Events to \"true\" if it needs them.");
            // Soak: a matchLoop longer than the scenario timeout would be cut short (values from literals or variable defaults).
            if (step.Action == "matchLoop" && StressActions.LoopSeconds(step, s.Variables) is double loopSeconds && loopSeconds > s.TimeoutSeconds)
                Warn(where, $"matchLoop needs about {loopSeconds:0} s (matches x (matchSeconds + {StressActions.MatchOverheadSeconds} s per match)) but timeoutSeconds is {s.TimeoutSeconds:0}: raise timeoutSeconds or lower matches.");
            // Stress D38: group actions name a group an earlier actorGroup made ("all" for stopGroup).
            if (step.Action == "actorGroup" && step.Params.TryGetValue("groups", out JsonElement defined) && defined.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement g in defined.EnumerateArray())
                    if (g.ValueKind == JsonValueKind.Object && g.TryGetProperty("name", out JsonElement gn) && gn.ValueKind == JsonValueKind.String) groups.Add(gn.GetString()!);
            }
            if (step.Params.TryGetValue("group", out JsonElement used) && used.ValueKind == JsonValueKind.String && !Variables.HasReference(used)
                && (step.Action.StartsWith("group", StringComparison.Ordinal) || step.Action == "stopGroup")
                && !groups.Contains(used.GetString()!) && !(step.Action == "stopGroup" && used.GetString() == "all"))
                Error(where, $"Unknown group '{used.GetString()}' (no earlier actorGroup defines it).");
            if (!stepIds.Add(step.Id)) Error(where, "Duplicate step id.");
            if (step.Phase != null && Array.IndexOf(s_phases, step.Phase.ToLowerInvariant()) < 0)
                Error(where, $"phase must be one of {string.Join(", ", s_phases)}.");
            if (step.TimeoutMilliseconds is int t && (t <= 0 || t > MaxStepTimeoutMs))
                Error(where, $"timeoutMilliseconds must be 1-{MaxStepTimeoutMs}.");

            foreach (var p in step.Params)
            {
                foreach (string root in Variables.ReferencedRoots(p.Value))
                {
                    if (!variables.Contains(root)) Error(where, $"{UnknownVariable(root)} in '{p.Key}'.");
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
            // QA-4 §87: a Unity player takes gameplay input only through the real input path (unityKey, unityClick,
            // unityLook go through the Input System and InputReader); the headless gameplay actions never reach it.
            if (step.Actor != null && unity.Contains(step.Actor) && Array.IndexOf(UnityActions.HeadlessOnly, spec.Name) >= 0)
                Error(where, $"'{spec.Name}' is headless gameplay input; UnityClient actor '{step.Actor}' takes none (§87). Use unityKey/unityClick/unityLook (real input path) or a HeadlessClient actor.");
            if (step.Actor != null && actors.Contains(step.Actor) && !unity.Contains(step.Actor) && Array.IndexOf(UnityActions.UnityOnly, spec.Name) >= 0)
                Error(where, $"'{spec.Name}' needs a UnityClient actor ('{step.Actor}' is not one).");
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

        // D33: a baseline value is a variable the run has at its end (a saveAs, a scenario variable or a parameter).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in s.BaselineValues)
        {
            if (!seen.Add(name)) Error("baseline", $"Duplicate baseline value '{name}'.");
            else if (!VariableName().IsMatch(name)) Error("baseline", $"Bad baseline value name '{name}'.");
            else if (!variables.Contains(name)) Error("baseline", $"Baseline value '{name}' is never saved (no step has saveAs '{name}' and it is not a variable).");
        }
        return issues;
    }
}
