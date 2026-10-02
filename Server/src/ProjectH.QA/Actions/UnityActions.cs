using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProjectH.QA;

// QA-4 actions (request §83-90): Unity player screenshots and UI commands (never gameplay input, §87), waits on the
// player's own status, and manual checks (D30).
public static partial class UnityActions
{
    public static readonly string[] UiCommands = { "openMenu", "closeMenu", "openStats", "closeStats", "toggleDebug" };
    public const int ManualTimeoutMs = 24 * 3600 * 1000;   // a person answers; the scenario timeout is paused meanwhile

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ShotName();

    [GeneratedRegex(@"[^A-Za-z0-9_-]")]
    private static partial Regex NotShotChar();

    public static void Register(ActionRegistry r)
    {
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "captureScreenshot",
            Actor = ActorUse.Required,
            Required = new[] { "name" },
            DefaultTimeout = _ => 15_000,
            Check = CheckName,
        }, CaptureAsync));
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "uiCommand",
            Actor = ActorUse.Required,
            Required = new[] { "command" },
            Check = CheckCommand,
        }, UiAsync));
        string[] operators = Comparison.Operators.Append("tolerance").ToArray();
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "waitForUnity",
            Actor = ActorUse.Required,
            Required = new[] { "condition" },
            Optional = operators,
            Check = s => Comparison.Operators.Count(s.Has) == 1 ? Array.Empty<string>() : new[] { $"Give exactly one operator ({string.Join(", ", Comparison.Operators)})." },
        }, WaitForUnityAsync));
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "manualCheck",
            Actor = ActorUse.Optional,
            // The check's text is the step's own `description` field (a common field, so not a parameter).
            DefaultTimeout = _ => ManualTimeoutMs,
            Check = s => string.IsNullOrWhiteSpace(s.Description) ? new[] { "'manualCheck' needs a 'description' (what the person checks)." } : Array.Empty<string>(),
        }, ManualCheckAsync));
    }

    // Actions only a UnityClient actor can do, and the actor actions a UnityClient actor cannot (validator, §87).
    public static readonly string[] UnityOnly = { "captureScreenshot", "uiCommand", "waitForUnity" };
    public static readonly string[] HeadlessOnly =
    {
        "reconnect", "moveTo", "moveVector", "look", "aim", "fire", "stopFire", "press", "release", "switchWeapon", "jump", "sprint",
        "crouch", "build", "pauseInput", "resumeInput", "playInputs",
    };

    private static IEnumerable<string> CheckName(StepDefinition s)
    {
        if (s.Params.TryGetValue("name", out JsonElement n) && !Variables.HasReference(n)
            && (n.ValueKind != JsonValueKind.String || !ShotName().IsMatch(n.GetString()!)))
            yield return "'name' must be 1-64 characters of A-Z a-z 0-9 _ - (it becomes the PNG file name).";
    }

    private static IEnumerable<string> CheckCommand(StepDefinition s)
    {
        if (s.Params.TryGetValue("command", out JsonElement c) && !Variables.HasReference(c)
            && (c.ValueKind != JsonValueKind.String || Array.IndexOf(UiCommands, c.GetString()) < 0))
            yield return $"'command' must be one of {string.Join(", ", UiCommands)}.";
    }

    private static UnityActor Unity(StepContext ctx) =>
        ctx.Actor() as UnityActor ?? throw new QaStepException($"'{ctx.Step.Action}' needs a UnityClient actor ('{ctx.ActorAlias}' is not one).");

    // The file is <alias>_<name>.png in QA/Reports/<runId>/screenshots, listed in the report with a thumbnail.
    private static async Task<StepOutcome> CaptureAsync(StepContext ctx, CancellationToken token)
    {
        UnityActor actor = Unity(ctx);
        string name = ctx.RequireString("name");
        if (!ShotName().IsMatch(name)) throw new QaStepException("'name' must be 1-64 characters of A-Z a-z 0-9 _ -.");
        if (ctx.Run.Screenshots.Count >= RunContext.MaxScreenshots) throw new QaStepException($"Too many screenshots in one run (max {RunContext.MaxScreenshots}).");
        string file = ShotFileName(ctx.Step.Index, actor.Alias, name, ctx.Run.Screenshots);
        UnityAnswer answer = await actor.ScreenshotAsync(file, token).ConfigureAwait(false);
        if (!answer.Ok) return StepOutcome.Fail($"Screenshot refused: {answer.Error} (HTTP {answer.StatusCode})", "a PNG", answer.Error);
        string? path = answer.Json is JsonElement j && JsonPath.Child(j, "path") is { ValueKind: JsonValueKind.String } p ? p.GetString() : null;
        if (path == null || !File.Exists(path)) return StepOutcome.Fail($"The player reported '{path}' but the file is not there.", "a PNG file", path ?? "(no path)");
        string listed = ctx.Run.ReportDirectory.Length > 0 && Path.GetFullPath(path).StartsWith(Path.GetFullPath(ctx.Run.ReportDirectory), StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(ctx.Run.ReportDirectory, path).Replace('\\', '/')
            : path;
        ctx.Run.Screenshots.Add(listed);
        long bytes = new FileInfo(path).Length;
        return StepOutcome.Pass($"{listed} ({bytes / 1024} KB)", JsonPath.From(new { path, file = listed, bytes }));
    }

    private static async Task<StepOutcome> UiAsync(StepContext ctx, CancellationToken token)
    {
        UnityActor actor = Unity(ctx);
        string command = ctx.RequireString("command");
        if (Array.IndexOf(UiCommands, command) < 0) throw new QaStepException($"Unknown UI command '{command}'.");
        UnityAnswer answer = await actor.UiAsync(command, token).ConfigureAwait(false);
        string screen = Screen(answer.Json);
        if (screen == "?") screen = Screen(actor.State.Unity);
        if (answer.Ok) return StepOutcome.Pass($"{command}: screen {screen}", answer.Json);
        // 409: the command does not apply to the current screen (nothing changed).
        return StepOutcome.Fail($"{command} refused on screen {screen}: {answer.Error} (HTTP {answer.StatusCode})", $"{command} applied", $"screen {screen}");
    }

    // condition: a field of the player's /qa/status (screen, joined, connected, statsOpen, debugVisible, alive, health,
    // fps, frame), with or without "unity." in front. Polls the player itself (local, cheap) every UnityActor.PollMs.
    private static async Task<StepOutcome> WaitForUnityAsync(StepContext ctx, CancellationToken token)
    {
        UnityActor actor = Unity(ctx);
        string condition = ctx.RequireString("condition");
        string path = condition.StartsWith("unity.", StringComparison.Ordinal) ? condition["unity.".Length..] : condition;
        (string op, JsonElement expected) = Comparison.Operators.Select(o => (o, v: ctx.Param(o))).Where(x => x.v != null).Select(x => (x.o, x.v!.Value)).First();
        JsonElement? tolerance = ctx.Param("tolerance");
        string expectedText = string.Empty, actual = "(no status yet)";
        while (true)
        {
            await actor.RefreshAsync(token).ConfigureAwait(false);
            JsonElement? value = JsonPath.Get(actor.State.Unity, path);
            (bool ok, expectedText) = Comparison.Evaluate(op, expected, tolerance, value, null);
            actual = Comparison.Text(value);
            if (ok) return StepOutcome.Pass($"unity.{path} = {actual} after {ctx.ElapsedMs} ms", value);
            if (actor.State.Disconnected) return StepOutcome.Fail($"The Unity player is gone ({actor.State.DisconnectReason}).", expectedText, actual);
            if (ctx.TimedOut) return StepOutcome.Fail($"Timeout after {ctx.TimeoutMs} ms waiting for unity.{path} {expectedText}; last {actual}", expectedText, actual);
            await Task.Delay(UnityActor.PollMs, token).ConfigureAwait(false);
        }
    }

    // D30: the run waits for a person. UI: PASS/FAIL buttons and a note. CLI on a terminal: p/f/s and a note.
    // Nobody to ask (CI): SKIPPED for this step only, the run goes on (or FAIL with --manual fail).
    private static async Task<StepOutcome> ManualCheckAsync(StepContext ctx, CancellationToken token)
    {
        string description = Variables.SubstituteText(ctx.Step.Description ?? throw new QaStepException("'description' is required."), ctx.Run.Variables);
        IRunControl control = ctx.Run.Control ?? throw new QaStepException("No run control for manual checks.");
        ctx.Run.Log($"MANUAL CHECK {ctx.Step.Id}: {description}");
        ManualCheckAnswer answer = await control.ManualCheckAsync(ctx.Step, description, token).ConfigureAwait(false);
        string result = answer.Passed switch { true => "PASS", false => "FAIL", null => "SKIPPED" };
        ctx.Run.ManualChecks.Add(new ManualCheckRecord(ctx.Step.Index, ctx.Step.Id, description, result, answer.Note, answer.By));
        string note = string.IsNullOrWhiteSpace(answer.Note) ? string.Empty : $" ({answer.Note})";
        return answer.Passed switch
        {
            true => StepOutcome.Pass($"PASS by {answer.By}{note}"),
            false => StepOutcome.Fail($"Manual check failed{note}", "PASS", $"FAIL by {answer.By}{note}"),
            null => StepOutcome.Skip($"Manual check not answered: {answer.Note}", skipRest: false),
        };
    }

    // <step>_<alias>_<name>, within the player's 64-character rule: the step number keeps two actors whose aliases
    // sanitize alike (non-ASCII → '-') or are cut short apart, and a counter keeps a retried step's shot.
    public static string ShotFileName(int stepIndex, string alias, string name, IReadOnlyCollection<string> taken)
    {
        string step = (stepIndex + 1).ToString("000", System.Globalization.CultureInfo.InvariantCulture);
        string safeAlias = NotShotChar().Replace(alias, "-");
        string Build(string suffix)
        {
            // 64 = step + '_' + alias + '_' + name + suffix; the alias keeps at least one character, then the name is cut,
            // so the result never exceeds 64 and the dedupe suffix is never cut off.
            int room = 64 - step.Length - 2 - suffix.Length;
            string n = name[..Math.Min(name.Length, room - 1)];
            int aliasRoom = room - n.Length;
            string a = safeAlias.Length > aliasRoom ? safeAlias[..aliasRoom] : safeAlias;
            return $"{step}_{a}_{n}{suffix}";
        }
        string file = Build(string.Empty);
        for (int i = 2; taken.Any(t => string.Equals(Path.GetFileName(t), file + ".png", StringComparison.OrdinalIgnoreCase)); i++)
            file = Build("_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return file;
    }

    public static string Screen(JsonElement? status) =>
        status != null && JsonPath.Child(status.Value, "screen") is { ValueKind: JsonValueKind.String } s ? s.GetString()! : "?";
}
