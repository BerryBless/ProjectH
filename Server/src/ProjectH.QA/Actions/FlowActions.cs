using System.Globalization;
using System.Text.Json;

namespace ProjectH.QA;

// A handler from a spec and a function: most actions are a few lines, a class each would only add files.
public sealed class DelegateAction : IScenarioActionHandler
{
    private readonly Func<StepContext, CancellationToken, Task<StepOutcome>> _run;

    // 기능: 액션 사양과 실행 함수로 Handler를 만든다.
    // 입력: spec - 액션 사양, run - 단계 실행 함수.
    // 출력: 사양과 함수를 담은 DelegateAction.
    public DelegateAction(ActionSpec spec, Func<StepContext, CancellationToken, Task<StepOutcome>> run)
    {
        Spec = spec;
        _run = run;
    }

    public ActionSpec Spec { get; }

    // 기능: 단계를 실행 함수에 넘겨 실행한다.
    // 입력: context - 단계 문맥, token - 취소 토큰.
    // 출력: 단계 결과(StepOutcome).
    public Task<StepOutcome> ExecuteAsync(StepContext context, CancellationToken token) => _run(context, token);
}

// Waiting, assertions, events and actor groups.
public static class FlowActions
{
    public const int MaxWaitMs = 600_000;
    public const int MaxSpawn = ActorManager.MaxActors;

    // 기능: 흐름 Action(wait, assert, waitFor, save, waitForEvent 등)을 등록한다(Phase 19: 검증·save에 vehicleId 인자).
    // 입력: r - Action 목록.
    // 출력: 반환값 없음.
    public static void Register(ActionRegistry r)
    {
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "wait",
            Required = new[] { "milliseconds|seconds" },
            DefaultTimeout = s => WaitMs(s) + 1000,
            Check = CheckWait,
        }, WaitAsync));

        string[] operatorParams = Comparison.Operators.Append("tolerance").Append("pieceId").Append("vehicleId").Append("at").Append("radius").Append("windowSeconds").ToArray();
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "assert",
            Actor = ActorUse.Optional,
            Required = new[] { "path" },
            Optional = operatorParams,
            PositionParams = new[] { "at" },
            Check = s => CheckAssertion(s, "path", requireOperator: true),
        }, AssertAsync));
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "waitFor",
            Actor = ActorUse.Optional,
            Required = new[] { "condition|path" },
            Optional = operatorParams,
            PositionParams = new[] { "at" },
            Check = s => CheckAssertion(s, s.Has("condition") ? "condition" : "path", requireOperator: true),
        }, WaitForAsync));
        // Reads a value into a variable (saveAs), e.g. the health before a shot.
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "save",
            Actor = ActorUse.Optional,
            Required = new[] { "path" },
            Optional = new[] { "pieceId", "vehicleId", "at", "radius", "windowSeconds" },
            PositionParams = new[] { "at" },
            Check = s => CheckAssertion(s, "path", requireOperator: false).Concat(s.SaveAs == null ? new[] { "'save' needs 'saveAs'." } : Array.Empty<string>()),
        }, SaveAsync));
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "waitForEvent",
            Actor = ActorUse.Optional,
            Required = new[] { "event" },
            Optional = new[] { "where" },
        }, WaitForEventAsync));
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "spawnActors",
            Required = new[] { "count", "prefix" },
            Optional = new[] { "type" },
            Check = CheckSpawn,
            CreatesActors = SpawnedAliases,
        }, SpawnActorsAsync));
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "connectAll",
            Optional = new[] { "prefix" },
            DefaultTimeout = _ => 30_000,
        }, ConnectAllAsync));
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "disconnectAll",
            Optional = new[] { "prefix", "graceful" },
        }, DisconnectAllAsync));
    }

    // 기능: wait 단계의 대기 시간을 Literal에서 구한다(변수면 최대값, 둘 다 없으면 표준 Timeout).
    // 입력: s - 단계 정의.
    // 출력: 대기 시간(ms, 0-MaxWaitMs).
    private static int WaitMs(StepDefinition s)
    {
        if (s.Params.TryGetValue("milliseconds", out JsonElement ms) && Comparison.TryNumber(ms, out double m)) return (int)Math.Clamp(m, 0, MaxWaitMs);
        if (s.Params.TryGetValue("seconds", out JsonElement sec) && Comparison.TryNumber(sec, out double v)) return (int)Math.Clamp(v * 1000, 0, MaxWaitMs);
        // A ${variable} duration is known only at run time: the longest wait bounds it (the handler waits the real value).
        if (s.Params.ContainsKey("milliseconds") || s.Params.ContainsKey("seconds")) return MaxWaitMs;
        return ActionSpec.StandardTimeoutMs;
    }

    // 기능: wait 단계의 milliseconds·seconds가 0 이상이고 MaxWaitMs 이내인지 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
    private static IEnumerable<string> CheckWait(StepDefinition s)
    {
        foreach (string name in new[] { "milliseconds", "seconds" })
        {
            if (!s.Params.TryGetValue(name, out JsonElement v) || Variables.HasReference(v)) continue;
            if (!Comparison.TryNumber(v, out double d) || d < 0) yield return $"'{name}' must be a non-negative number.";
            else if ((name == "seconds" ? d * 1000 : d) > MaxWaitMs) yield return $"'{name}' is longer than {MaxWaitMs / 1000} s.";
        }
    }

    // 기능: assert·waitFor·save 단계의 경로(문법, actor 필요 여부)와 연산자(개수, tolerance, between 형식)를 검사한다.
    // 입력: s - 단계 정의, pathParam - 경로가 든 Parameter 이름(path 또는 condition), requireOperator - 연산자가 꼭 있어야 하는지.
    // 출력: 오류 문장들(없으면 빈 목록).
    private static IEnumerable<string> CheckAssertion(StepDefinition s, string pathParam, bool requireOperator)
    {
        if (s.Params.TryGetValue(pathParam, out JsonElement p))
        {
            if (p.ValueKind != JsonValueKind.String) yield return $"'{pathParam}' must be a string.";
            else
            {
                string path = p.GetString()!;
                string? error = AssertionEngine.CheckPath(path);
                if (error != null) yield return error;
                else if (AssertionEngine.NeedsActor(path) && s.Actor == null) yield return $"'{path}' needs 'actor'.";
            }
        }
        string[] ops = Comparison.Operators.Where(s.Has).ToArray();
        if (requireOperator && ops.Length == 0) yield return $"No operator: give one of {string.Join(", ", Comparison.Operators)}.";
        if (ops.Length > 1) yield return $"Only one operator per step (got {string.Join(", ", ops)}).";
        if (s.Has("tolerance") && !s.Has("approximately")) yield return "'tolerance' only applies to 'approximately'.";
        if (s.Params.TryGetValue("between", out JsonElement b) && !Variables.HasReference(b)
            && (b.ValueKind != JsonValueKind.Array || b.GetArrayLength() != 2 || !Comparison.TryNumber(b[0], out _) || !Comparison.TryNumber(b[1], out _)))
            yield return "'between' needs [low, high] numbers.";
    }

    // 기능: spawnActors 단계의 count 범위와 마지막 생성 별칭의 유효성을 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
    private static IEnumerable<string> CheckSpawn(StepDefinition s)
    {
        if (s.Params.TryGetValue("count", out JsonElement c) && !Variables.HasReference(c)
            && (!Comparison.TryNumber(c, out double n) || n < 1 || n > MaxSpawn || n != Math.Floor(n)))
            yield return $"'count' must be an integer 1-{MaxSpawn}.";
        foreach (string alias in SpawnedAliases(s).TakeLast(1))
        {
            string? error = ActorManager.CheckAlias(alias);
            if (error != null) yield return error;
        }
    }

    // 기능: spawnActors 단계가 만들 Actor 별칭(prefix-001..prefix-NNN)을 나열한다.
    // 입력: s - 단계 정의(prefix, count).
    // 출력: 별칭 목록. prefix나 count가 Literal이 아니면 빈 목록.
    // prefix-001 .. prefix-NNN, like the bots' names.
    public static IEnumerable<string> SpawnedAliases(StepDefinition s)
    {
        if (!s.Params.TryGetValue("prefix", out JsonElement p) || p.ValueKind != JsonValueKind.String) yield break;
        if (!s.Params.TryGetValue("count", out JsonElement c) || !Comparison.TryNumber(c, out double n)) yield break;
        int count = (int)Math.Clamp(n, 0, MaxSpawn);
        for (int i = 1; i <= count; i++) yield return SpawnAlias(p.GetString()!, i);
    }

    // 기능: 생성 Actor의 별칭을 만든다.
    // 입력: prefix - 별칭 접두사, i - 1부터 세는 번호.
    // 출력: "prefix-NNN" 형식의 별칭.
    public static string SpawnAlias(string prefix, int i) => $"{prefix}-{i.ToString("000", CultureInfo.InvariantCulture)}";

    // 기능: milliseconds 또는 seconds만큼 기다린다.
    // 입력: ctx - 단계 문맥(milliseconds, seconds), token - 취소 토큰.
    // 출력: Pass. 범위 밖이면 QaStepException.
    private static async Task<StepOutcome> WaitAsync(StepContext ctx, CancellationToken token)
    {
        double ms = ctx.Double("milliseconds") ?? (ctx.Double("seconds") ?? 0) * 1000;
        if (ms < 0 || ms > MaxWaitMs) throw new QaStepException($"Wait must be 0-{MaxWaitMs} ms.");
        await Task.Delay(TimeSpan.FromMilliseconds(ms), token).ConfigureAwait(false);
        return StepOutcome.Pass();
    }

    // 기능: 단계에 있는 비교 연산자 Parameter와 그 기대값을 찾는다.
    // 입력: ctx - 단계 문맥.
    // 출력: (연산자 이름, 기대값). 연산자가 없으면 QaStepException.
    private static (string Op, JsonElement Expected) Operator(StepContext ctx)
    {
        foreach (string op in Comparison.Operators)
        {
            JsonElement? v = ctx.Param(op);
            if (v != null) return (op, v.Value);
        }
        throw new QaStepException("No operator.");
    }

    // 기능: 단계의 path(없으면 condition) 경로 문자열을 읽는다.
    // 입력: ctx - 단계 문맥.
    // 출력: 경로 문자열. 둘 다 없으면 QaStepException.
    private static string PathOf(StepContext ctx) => ctx.String("path") ?? ctx.String("condition") ?? throw new QaStepException("'path' is required.");

    // 기능: 경로의 값을 한 번 읽어 연산자로 비교한다.
    // 입력: ctx - 단계 문맥(path, 연산자, tolerance), token - 취소 토큰.
    // 출력: 비교가 맞으면 Pass(saveAs: 실제값), 틀리면 기대·실제를 담은 Fail.
    private static async Task<StepOutcome> AssertAsync(StepContext ctx, CancellationToken token)
    {
        string path = PathOf(ctx);
        (string op, JsonElement expected) = Operator(ctx);
        JsonElement? actual = await AssertionEngine.ResolveAsync(path, ctx, token).ConfigureAwait(false);
        (bool ok, string expectedText) = Comparison.Evaluate(op, expected, ctx.Param("tolerance"), actual, AssertionEngine.EnumHint(path));
        return ok
            ? StepOutcome.Pass($"{path} = {Comparison.Text(actual)}", actual)
            : StepOutcome.Fail($"{path}: expected {expectedText}, actual {Comparison.Text(actual)}", expectedText, Comparison.Text(actual));
    }

    // 기능: 경로의 값을 읽어 saveAs 변수로 저장할 결과를 만든다.
    // 입력: ctx - 단계 문맥(path), token - 취소 토큰.
    // 출력: 값이 있으면 Pass(saveAs: 그 값), 없으면 Fail.
    private static async Task<StepOutcome> SaveAsync(StepContext ctx, CancellationToken token)
    {
        string path = PathOf(ctx);
        JsonElement? actual = await AssertionEngine.ResolveAsync(path, ctx, token).ConfigureAwait(false);
        if (actual == null) return StepOutcome.Fail($"{path} has no value to save.", "a value", "(missing)");
        return StepOutcome.Pass($"{path} = {Comparison.Text(actual)}", actual);
    }

    // Polls the same engine as assert at the run's poll interval until it holds or the step's limit passes; the
    // failure shows the last value seen. A QA API error while polling is remembered and polling goes on (the server
    // may be restarting); it is the actual value if nothing else came.
    // 기능: 경로의 값이 연산자 조건을 만족할 때까지 Run의 Poll 간격으로 읽는다(QA API 오류는 기억하고 계속).
    // 입력: ctx - 단계 문맥(path 또는 condition, 연산자, tolerance), token - 취소 토큰.
    // 출력: 조건이 성립하면 Pass(saveAs: 실제값), Timeout이면 마지막 값을 담은 Fail.
    private static async Task<StepOutcome> WaitForAsync(StepContext ctx, CancellationToken token)
    {
        string path = PathOf(ctx);
        (string op, JsonElement expected) = Operator(ctx);
        JsonElement? tolerance = ctx.Param("tolerance");
        Type? hint = AssertionEngine.EnumHint(path);
        string expectedText = string.Empty;
        string lastActual = "(not read)";
        while (true)
        {
            try
            {
                JsonElement? actual = await AssertionEngine.ResolveAsync(path, ctx, token).ConfigureAwait(false);
                (bool ok, expectedText) = Comparison.Evaluate(op, expected, tolerance, actual, hint);
                lastActual = Comparison.Text(actual);
                if (ok) return StepOutcome.Pass($"{path} = {lastActual} after {ctx.ElapsedMs} ms", actual);
            }
            catch (QaApiException e)
            {
                lastActual = $"(QA API error: {e.Message})";
            }
            if (ctx.TimedOut)
                return StepOutcome.Fail($"Timeout after {ctx.TimeoutMs} ms waiting for {path} {expectedText}; last {lastActual}", expectedText, lastActual);
            await Task.Delay(ctx.Run.PollIntervalMs, token).ConfigureAwait(false);
        }
    }

    // 기능: 서버 이벤트 중 type(과 actor의 플레이어, where 필드)이 맞는 첫 이벤트가 올 때까지 기다린다.
    // 입력: ctx - 단계 문맥(event, where, actor), token - 취소 토큰.
    // 출력: 맞는 이벤트가 오면 Pass(saveAs: 이벤트 원문), Timeout이면 Fail. 이벤트 소스가 없거나 where가 객체가 아니면 QaStepException.
    private static async Task<StepOutcome> WaitForEventAsync(StepContext ctx, CancellationToken token)
    {
        EventCursor events = ctx.Run.Events ?? throw new QaStepException("No event source in this run.");
        string type = ctx.RequireString("event");
        string? player = ctx.Step.Actor != null ? ctx.Actor().DevPlayerId : null;
        JsonElement? where = ctx.Param("where");
        if (where != null && where.Value.ValueKind != JsonValueKind.Object) throw new QaStepException("'where' must be an object of event data fields.");
        // 기능: 이벤트가 type·플레이어·where 필드와 모두 맞는지 검사한다.
        // 입력: e - 검사할 이벤트.
        // 출력: 모두 맞으면 true, 아니면 false.
        bool Match(QaEvent e)
        {
            if (!string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase)) return false;
            if (player != null && !string.Equals(e.Player, player, StringComparison.Ordinal)) return false;
            if (where == null) return true;
            JsonElement? data = JsonPath.Child(e.Raw, "data");
            foreach (JsonProperty p in where.Value.EnumerateObject())
            {
                if (!Comparison.AreEqual(JsonPath.Get(data, p.Name), p.Value)) return false;
            }
            return true;
        }
        while (true)
        {
            await events.FetchAsync(token).ConfigureAwait(false);
            QaEvent? found = events.TakeFirst(Match);
            if (found != null) return StepOutcome.Pass($"{found.Type} seq {found.Seq} tick {found.Tick}", found.Raw);
            if (ctx.TimedOut)
            {
                string expected = type + (player != null ? $" for {player}" : string.Empty);
                return StepOutcome.Fail($"Timeout after {ctx.TimeoutMs} ms waiting for event {expected}", expected, "no such event");
            }
            await Task.Delay(ctx.Run.PollIntervalMs, token).ConfigureAwait(false);
        }
    }

    // 기능: prefix-001..prefix-NNN 별칭의 Actor를 count개 만든다.
    // 입력: ctx - 단계 문맥(count, prefix, type), token - 취소 토큰.
    // 출력: Pass(만든 별칭 범위).
    private static async Task<StepOutcome> SpawnActorsAsync(StepContext ctx, CancellationToken token)
    {
        int count = ctx.Int("count", 1, MaxSpawn)!.Value;
        string prefix = ctx.RequireString("prefix");
        string type = ctx.String("type") ?? ActorSpec.HeadlessClient;
        for (int i = 1; i <= count; i++) await ctx.Run.Actors.CreateAsync(SpawnAlias(prefix, i), type, token).ConfigureAwait(false);
        return StepOutcome.Pass($"{count} actors {SpawnAlias(prefix, 1)}..{SpawnAlias(prefix, count)}");
    }

    // 기능: 별칭이 prefix로 시작하는 모든 Actor를 한꺼번에 접속시키고 전부 Join할 때까지 기다린다.
    // 입력: ctx - 단계 문맥(prefix), token - 취소 토큰.
    // 출력: 모두 Join하면 Pass, 아니면 Join 수와 실패 예시를 담은 Fail.
    // Cheap group connect: all at once, then one wait for all to join.
    private static async Task<StepOutcome> ConnectAllAsync(StepContext ctx, CancellationToken token)
    {
        string? prefix = ctx.String("prefix");
        IQaActor[] actors = ctx.Run.Actors.All.Where(a => prefix == null || a.Alias.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        foreach (IQaActor a in actors)
        {
            (string host, int port) = await ctx.Run.ConnectTargetAsync(a.Alias).ConfigureAwait(false);
            await a.SendAsync(new ConnectCommand(host, port, false), token).ConfigureAwait(false);
        }
        bool joined = await ctx.WaitUntilAsync(() => actors.All(a => a.State.Joined && a.State.HasSnapshot), token).ConfigureAwait(false);
        int count = actors.Count(a => a.State.Joined);
        if (joined) return StepOutcome.Pass($"{count} joined");
        string failed = string.Join(", ", actors.Where(a => !a.State.Joined).Take(5).Select(a => $"{a.Alias}: {Describe(a.State)}"));
        return StepOutcome.Fail($"{count}/{actors.Length} joined within {ctx.TimeoutMs} ms ({failed})", $"{actors.Length} joined", $"{count} joined");
    }

    // 기능: 별칭이 prefix로 시작하는 모든 Actor의 연결을 끊고 닫힐 때까지(단계 Timeout 이내) 기다린다.
    // 입력: ctx - 단계 문맥(prefix, graceful), token - 취소 토큰.
    // 출력: Pass(대상 수). 닫히지 않아도 Timeout 뒤 Pass를 돌려준다.
    private static async Task<StepOutcome> DisconnectAllAsync(StepContext ctx, CancellationToken token)
    {
        string? prefix = ctx.String("prefix");
        bool graceful = ctx.Bool("graceful") ?? true;
        IQaActor[] actors = ctx.Run.Actors.All.Where(a => prefix == null || a.Alias.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        foreach (IQaActor a in actors) await a.SendAsync(new DisconnectCommand(graceful), token).ConfigureAwait(false);
        await ctx.WaitUntilAsync(() => actors.All(a => a.State.Status is ActorStatus.Disconnected or ActorStatus.Idle), token).ConfigureAwait(false);
        return StepOutcome.Pass($"{actors.Length} disconnected");
    }

    // 기능: Actor 상태를 짧은 설명 문자열로 만든다(끊김 사유, Snapshot 없음 포함).
    // 입력: s - Actor 상태.
    // 출력: 상태 설명 문자열.
    public static string Describe(ActorState s) => s.Status switch
    {
        ActorStatus.Disconnected => $"disconnected ({s.DisconnectReason})",
        ActorStatus.Joined when !s.HasSnapshot => "joined, no snapshot yet",
        _ => s.Status.ToString().ToLowerInvariant(),
    };
}
