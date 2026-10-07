using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProjectH.QA;

// QA-4 actions (request §83-90): Unity player screenshots and UI commands, gameplay input through the player's real
// input path (unityKey, unityClick, unityLook: POST /qa/input into the Input System, §87), waits on the player's own
// status, and manual checks (D30).
public static partial class UnityActions
{
    public static readonly string[] UiCommands = { "openMenu", "closeMenu", "openStats", "closeStats", "toggleDebug" };
    public const int ManualTimeoutMs = 24 * 3600 * 1000;   // a person answers; the scenario timeout is paused meanwhile

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ShotName();

    [GeneratedRegex(@"[^A-Za-z0-9_-]")]
    private static partial Regex NotShotChar();

    // POST /qa/input contract (Docs/QA.md "Unity Client"): Input System key names the player accepts, and the limits.
    public static readonly string[] InputKeys =
    {
        "w", "a", "s", "d", "space", "leftShift", "leftCtrl", "c", "q", "f", "z", "x", "v", "b", "t", "r", "e", "g",
        "1", "2", "3", "4", "5", "escape", "f1", "h",
    };
    public static readonly string[] InputButtons = { "left", "right" };
    public static readonly string[] InputPhases = { "down", "up" };
    public const int MaxHoldMs = 10_000;
    public const int MaxLookMs = 5_000;
    public const double MaxLookPixels = 20_000;
    // A waiting input step ends this long after the input is over (about two frames), so the next step sees its result.
    public const int InputSettleMs = 50;

    private enum InputKind
    {
        Key,
        Click,
        Look,
    }

    // 기능: QA-4 Unity Action(Screenshot, UI 명령, 실제 입력 경로 입력, 상태 대기, Manual Check)을 등록한다.
    // 입력: r - Action을 넣을 Registry.
    // 출력: 반환값 없음. r에 Unity Action들이 추가된다.
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
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "unityKey",
            Actor = ActorUse.Required,
            Required = new[] { "key" },
            Optional = new[] { "holdMs", "state", "async" },
            DefaultTimeout = s => InputTimeoutMs(s, InputKind.Key),
            Check = s => CheckInput(s, InputKind.Key),
        }, (ctx, token) => InputAsync(ctx, InputKind.Key, token)));
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "unityClick",
            Actor = ActorUse.Required,
            Optional = new[] { "button", "holdMs", "state", "async" },
            DefaultTimeout = s => InputTimeoutMs(s, InputKind.Click),
            Check = s => CheckInput(s, InputKind.Click),
        }, (ctx, token) => InputAsync(ctx, InputKind.Click, token)));
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "unityLook",
            Actor = ActorUse.Required,
            Required = new[] { "dx|dy" },
            Optional = new[] { "ms", "async" },
            DefaultTimeout = s => InputTimeoutMs(s, InputKind.Look),
            Check = s => CheckInput(s, InputKind.Look),
        }, (ctx, token) => InputAsync(ctx, InputKind.Look, token)));
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "unityReleaseAll",
            Actor = ActorUse.Required,
        }, ReleaseAllAsync));
    }

    // Actions only a UnityClient actor can do, and the actor actions a UnityClient actor cannot (validator, §87): a Unity
    // player gets gameplay input only through its real input path (unity* actions), never the headless commands.
    public static readonly string[] UnityOnly =
    {
        "captureScreenshot", "uiCommand", "waitForUnity", "unityKey", "unityClick", "unityLook", "unityReleaseAll",
    };
    public static readonly string[] HeadlessOnly =
    {
        "reconnect", "moveTo", "moveVector", "look", "aim", "fire", "stopFire", "press", "release", "switchWeapon", "jump", "sprint",
        "crouch", "holdInteract", "build", "buildEdit", "pauseInput", "resumeInput", "playInputs",
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
    // fps, frame, tool, preview, cursorLocked), with or without "unity." in front. Polls the player itself (local, cheap) every UnityActor.PollMs.
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

    // 기능: unityKey·unityClick·unityLook 한 Step을 실행한다. 입력을 Player의 POST /qa/input으로 보내고, async가 아니면
    //       입력이 끝날 때(holdMs 또는 ms)와 약 두 Frame(InputSettleMs)을 더 기다린 뒤 상태를 새로 읽는다.
    // 입력: ctx - Step 실행 문맥(인자·Actor), kind - 키·마우스 버튼·시점 중 무엇인지, token - 실행 취소.
    // 출력: Player가 받아들이면 Pass(Player의 applied 문장), 거부(400·409 미접속·503 동시 입력 초과)면 Fail.
    private static async Task<StepOutcome> InputAsync(StepContext ctx, InputKind kind, CancellationToken token)
    {
        UnityActor actor = Unity(ctx);
        (Dictionary<string, object> body, int inputMs, bool runAsync) = InputRequest(ctx, kind);
        string sent = JsonSerializer.Serialize(body);
        UnityAnswer answer = await actor.InputAsync(body, token).ConfigureAwait(false);
        if (!answer.Ok)
        {
            string why = answer.StatusCode switch
            {
                409 => "the player is not in a match (joined false)",
                503 => "too many inputs are active on the player (max 16 holds/looks)",
                _ => "the player refused the input",
            };
            return StepOutcome.Fail($"Input {sent} failed: {why}: {answer.Error} (HTTP {answer.StatusCode})", "input applied", $"HTTP {answer.StatusCode}");
        }
        string applied = answer.Json is JsonElement j && JsonPath.Child(j, "applied") is { ValueKind: JsonValueKind.String } a ? a.GetString()! : sent;
        if (runAsync) return StepOutcome.Pass($"{applied} (async: accepted, still running)", answer.Json);
        await Task.Delay(inputMs + InputSettleMs, token).ConfigureAwait(false);
        await actor.RefreshAsync(token).ConfigureAwait(false);
        return StepOutcome.Pass($"{applied}; done after {ctx.ElapsedMs} ms", answer.Json);
    }

    // 기능: Player의 눌린 키·버튼과 진행 중인 시점 이동을 모두 뗀다(POST /qa/input {"releaseAll":true}).
    // 입력: ctx - Step 실행 문맥(Actor), token - 실행 취소.
    // 출력: Player가 받아들이면 약 두 Frame 뒤 상태를 새로 읽고 Pass, 거부하면 Fail.
    private static async Task<StepOutcome> ReleaseAllAsync(StepContext ctx, CancellationToken token)
    {
        UnityActor actor = Unity(ctx);
        UnityAnswer answer = await actor.ReleaseAllAsync(token).ConfigureAwait(false);
        if (!answer.Ok) return StepOutcome.Fail($"releaseAll refused: {answer.Error} (HTTP {answer.StatusCode})", "input released", $"HTTP {answer.StatusCode}");
        await Task.Delay(InputSettleMs, token).ConfigureAwait(false);
        await actor.RefreshAsync(token).ConfigureAwait(false);
        string applied = answer.Json is JsonElement j && JsonPath.Child(j, "applied") is { ValueKind: JsonValueKind.String } a ? a.GetString()! : "released all";
        return StepOutcome.Pass(applied, answer.Json);
    }

    // 기능: Step 인자(변수 치환 뒤)를 검사하고 POST /qa/input Body를 만든다.
    // 입력: ctx - Step 실행 문맥, kind - 키·마우스 버튼·시점 중 무엇인지.
    // 출력: 있는 필드만 담은 평평한 Body(숫자는 JSON 숫자), 입력이 이어지는 시간(ms), async 여부. 인자가 틀리면 QaStepException.
    private static (Dictionary<string, object> Body, int InputMs, bool Async) InputRequest(StepContext ctx, InputKind kind)
    {
        foreach (string name in InputParams(kind))
        {
            JsonElement? value = ctx.Param(name);
            if (value != null && value.Value.ValueKind != JsonValueKind.Null && InputParamError(name, value.Value) is string error)
                throw new QaStepException(error);
        }
        bool runAsync = ctx.Bool("async") ?? false;
        var body = new Dictionary<string, object>(StringComparer.Ordinal);
        if (kind == InputKind.Look)
        {
            int ms = ctx.Int("ms", 0, MaxLookMs) ?? 0;
            body["lookX"] = ctx.Double("dx") ?? 0;
            body["lookY"] = ctx.Double("dy") ?? 0;
            body["ms"] = ms;
            return (body, ms, runAsync);
        }
        if (kind == InputKind.Key) body["key"] = ctx.RequireString("key");
        else body["button"] = ctx.String("button") ?? "left";
        int? holdMs = ctx.Int("holdMs", 1, MaxHoldMs);
        string? phase = ctx.String("state");
        if (holdMs != null && phase != null) throw new QaStepException("Give 'holdMs' or 'state', not both.");
        if (holdMs != null) body["holdMs"] = holdMs.Value;
        if (phase != null) body["action"] = phase;
        return (body, holdMs ?? 0, runAsync);
    }

    // 기능: 입력 Action이 받는 인자 이름을 돌려준다.
    // 입력: kind - 키·마우스 버튼·시점 중 무엇인지.
    // 출력: 그 Action의 인자 이름 목록.
    private static string[] InputParams(InputKind kind) => kind switch
    {
        InputKind.Key => new[] { "key", "holdMs", "state", "async" },
        InputKind.Click => new[] { "button", "holdMs", "state", "async" },
        _ => new[] { "dx", "dy", "ms", "async" },
    };

    // 기능: 입력 Action 인자 하나가 POST /qa/input 계약(허용 키, 버튼, down/up, 시간·픽셀 범위)에 맞는지 본다.
    // 입력: name - 인자 이름, value - 인자 값(Literal 또는 변수 치환 뒤).
    // 출력: 맞으면 null, 틀리면 오류 문장.
    private static string? InputParamError(string name, JsonElement value)
    {
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        bool IntIn(int min, int max) => Comparison.TryNumber(value, out double d) && d == Math.Floor(d) && d >= min && d <= max;
        return name switch
        {
            "key" when Array.IndexOf(InputKeys, text) < 0 => $"'key' must be one of {string.Join(", ", InputKeys)} (Input System key names, as strings: \"1\", not 1).",
            "button" when Array.IndexOf(InputButtons, text) < 0 => "'button' must be left or right.",
            "state" when Array.IndexOf(InputPhases, text) < 0 => "'state' must be down or up (or leave it out and use holdMs or a press).",
            "holdMs" when !IntIn(1, MaxHoldMs) => $"'holdMs' must be an integer 1-{MaxHoldMs}.",
            "ms" when !IntIn(0, MaxLookMs) => $"'ms' must be an integer 0-{MaxLookMs} (0 = one frame).",
            "dx" or "dy" when !(Comparison.TryNumber(value, out double v) && Math.Abs(v) <= MaxLookPixels) => $"'{name}' must be a number of pixels, -{MaxLookPixels}..{MaxLookPixels}.",
            "async" when !(value.ValueKind is JsonValueKind.True or JsonValueKind.False || bool.TryParse(text, out _)) => "'async' must be true or false.",
            _ => null,
        };
    }

    // 기능: 입력 Action의 Literal 인자를 실행 전에 검사한다(validate가 실행 없이 잘못된 시나리오를 알린다).
    // 입력: s - 검사할 Step, kind - 키·마우스 버튼·시점 중 무엇인지.
    // 출력: 오류 문장들(없으면 빈 목록). 변수 참조 값은 실행 때 검사한다. 기다리는 Step의 timeoutMilliseconds가 입력보다
    //       짧아도 오류다.
    private static IEnumerable<string> CheckInput(StepDefinition s, InputKind kind)
    {
        foreach (string name in InputParams(kind))
        {
            if (s.Params.TryGetValue(name, out JsonElement value) && !Variables.HasReference(value) && InputParamError(name, value) is string error)
                yield return error;
        }
        if (kind != InputKind.Look && s.Has("holdMs") && s.Has("state")) yield return "Give 'holdMs' or 'state', not both.";
        // An async step ends when the player accepts, so only a waiting step must outlast its input.
        bool runAsync = s.Params.TryGetValue("async", out JsonElement a) && (a.ValueKind == JsonValueKind.True
            || (a.ValueKind == JsonValueKind.String && bool.TryParse(a.GetString(), out bool b) && b));
        if (!runAsync && s.TimeoutMilliseconds is int timeout && LiteralInputMs(s, kind) is int inputMs && timeout <= inputMs + InputSettleMs)
            yield return $"timeoutMilliseconds ({timeout}) must be longer than the input ({inputMs} ms + {InputSettleMs} ms); leave it out for the default.";
    }

    // 기능: 입력 Step의 기본 Timeout을 정한다(보통 Step Timeout + 입력이 이어지는 시간).
    // 입력: s - Step, kind - 키·마우스 버튼·시점 중 무엇인지.
    // 출력: Timeout(ms). 시간이 변수 참조면 최대 시간(holdMs 10 s, ms 5 s)을 더한다.
    private static int InputTimeoutMs(StepDefinition s, InputKind kind)
    {
        string name = kind == InputKind.Look ? "ms" : "holdMs";
        int extra = s.Params.TryGetValue(name, out JsonElement v) && Variables.HasReference(v)
            ? (kind == InputKind.Look ? MaxLookMs : MaxHoldMs)
            : LiteralInputMs(s, kind) ?? 0;
        return ActionSpec.StandardTimeoutMs + extra;
    }

    // 기능: Step의 Literal holdMs(키·버튼) 또는 ms(시점)를 읽는다.
    // 입력: s - Step, kind - 키·마우스 버튼·시점 중 무엇인지.
    // 출력: 범위 안의 Literal 값(ms). 없거나 변수 참조거나 범위 밖이면 null.
    private static int? LiteralInputMs(StepDefinition s, InputKind kind)
    {
        string name = kind == InputKind.Look ? "ms" : "holdMs";
        if (!s.Params.TryGetValue(name, out JsonElement v) || Variables.HasReference(v) || InputParamError(name, v) != null) return null;
        return Comparison.TryNumber(v, out double d) ? (int)d : null;
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
