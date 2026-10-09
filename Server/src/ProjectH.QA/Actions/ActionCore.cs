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

    // 기능: 단계의 Timeout을 정한다(단계 지정값 → 액션 기본값 → 표준값 순).
    // 입력: step - Timeout을 구할 단계 정의.
    // 출력: 적용할 Timeout(ms).
    public int TimeoutFor(StepDefinition step) => step.TimeoutMilliseconds ?? DefaultTimeout?.Invoke(step) ?? StandardTimeoutMs;

    // 기능: 이 액션이 받는 Parameter 이름인지 검사한다("a|b" 대안 포함).
    // 입력: param - 검사할 Parameter 이름.
    // 출력: Required 또는 Optional에 있으면 true, 아니면 false.
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

    // 기능: 통과 결과를 만든다.
    // 입력: message - 결과 메시지(선택), value - saveAs에 저장할 값(선택).
    // 출력: Passed가 true인 StepOutcome.
    public static StepOutcome Pass(string? message = null, JsonElement? value = null) => new() { Passed = true, Message = message, Value = value };

    // 기능: 환경 사유로 건너뛴 결과를 만든다(통과로 집계).
    // 입력: reason - 건너뛴 이유, skipRest - 나머지 단계도 건너뛸지 여부.
    // 출력: Skipped가 true인 StepOutcome.
    public static StepOutcome Skip(string reason, bool skipRest) => new() { Passed = true, Skipped = true, SkipRest = skipRest, Message = reason };

    // 기능: 실패 결과를 만든다.
    // 입력: message - 실패 메시지, expected - 기대값 설명(선택), actual - 실제값 설명(선택), value - saveAs에 저장할 값(선택).
    // 출력: Passed가 false인 StepOutcome.
    public static StepOutcome Fail(string message, string? expected = null, string? actual = null, JsonElement? value = null) =>
        new() { Passed = false, Message = message, Expected = expected, Actual = actual, Value = value };
}

public sealed class ActionRegistry
{
    private readonly Dictionary<string, IScenarioActionHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    // 기능: 액션 Handler를 이름으로 등록한다(대소문자 무시, 중복 이름이면 예외).
    // 입력: handler - 등록할 Handler.
    // 출력: 반환값 없음. Registry에 Handler가 추가된다.
    public void Add(IScenarioActionHandler handler) => _handlers.Add(handler.Spec.Name, handler);

    // 기능: 액션 이름으로 Handler를 찾는다.
    // 입력: action - 액션 이름, handler - 찾은 Handler(out).
    // 출력: 등록되어 있으면 true와 Handler, 없으면 false.
    public bool TryGet(string action, out IScenarioActionHandler handler) => _handlers.TryGetValue(action, out handler!);

    public IEnumerable<ActionSpec> Specs => _handlers.Values.Select(h => h.Spec).OrderBy(s => s.Name, StringComparer.Ordinal);

    // 기능: 모든 액션 모음(Flow·Actor·ServerCommand·Fault·Unity·Stress)을 등록한 기본 Registry를 만든다.
    // 입력: 없음.
    // 출력: 모든 액션 Handler가 등록된 ActionRegistry.
    // Every MVP action (QA-1).
    public static ActionRegistry CreateDefault()
    {
        var registry = new ActionRegistry();
        FlowActions.Register(registry);
        ActorActions.Register(registry);
        ServerCommandActions.Register(registry);
        FaultActions.Register(registry);
        UnityActions.Register(registry);
        StressActions.Register(registry);
        return registry;
    }
}

// What one step execution sees: its parameters with variables substituted, its actor, the run, and a soft deadline.
public sealed class StepContext
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // 기능: 단계 실행 Context를 만들고 경과 시계를 시작한다.
    // 입력: run - 실행 중인 Run, step - 실행할 단계 정의, timeoutMs - 단계의 Soft Timeout(ms).
    // 출력: 시계가 시작된 StepContext.
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

    // 기능: 단계 Parameter를 읽고 Run 변수를 치환한다.
    // 입력: name - Parameter 이름.
    // 출력: 치환된 값, Parameter가 없으면 null.
    public JsonElement? Param(string name) =>
        Step.Params.TryGetValue(name, out JsonElement raw) ? Variables.Substitute(raw, Run.Variables) : null;

    // 기능: 단계 Parameter를 문자열로 읽는다(문자열이 아니면 원문 JSON).
    // 입력: name - Parameter 이름.
    // 출력: 문자열 값, 없거나 null이면 null.
    public string? String(string name)
    {
        JsonElement? v = Param(name);
        if (v == null || v.Value.ValueKind == JsonValueKind.Null) return null;
        return v.Value.ValueKind == JsonValueKind.String ? v.Value.GetString() : v.Value.GetRawText();
    }

    // 기능: 필수 문자열 Parameter를 읽는다.
    // 입력: name - Parameter 이름.
    // 출력: 문자열 값. 없으면 QaStepException.
    public string RequireString(string name) => String(name) ?? throw new QaStepException($"'{name}' is required.");

    // 기능: 단계 Parameter를 숫자로 읽는다.
    // 입력: name - Parameter 이름.
    // 출력: 숫자 값, 없거나 null이면 null. 숫자가 아니면 QaStepException.
    public double? Double(string name)
    {
        JsonElement? v = Param(name);
        if (v == null || v.Value.ValueKind == JsonValueKind.Null) return null;
        if (Comparison.TryNumber(v.Value, out double d)) return d;
        throw new QaStepException($"'{name}' must be a number (got {JsonPath.Describe(v)}).");
    }

    // 기능: 단계 Parameter를 범위 안의 정수로 읽는다.
    // 입력: name - Parameter 이름, min - 허용 최소값, max - 허용 최대값.
    // 출력: 정수 값, 없으면 null. 정수가 아니거나 범위 밖이면 QaStepException.
    public int? Int(string name, int min, int max)
    {
        double? d = Double(name);
        if (d == null) return null;
        if (d.Value != Math.Floor(d.Value) || d.Value < min || d.Value > max)
            throw new QaStepException($"'{name}' must be an integer {min}-{max} (got {d.Value}).");
        return (int)d.Value;
    }

    // 기능: 단계 Parameter를 bool로 읽는다(JSON bool 또는 "true"/"false" 문자열).
    // 입력: name - Parameter 이름.
    // 출력: bool 값, 없거나 null이면 null. 해석할 수 없으면 QaStepException.
    public bool? Bool(string name)
    {
        JsonElement? v = Param(name);
        if (v == null || v.Value.ValueKind == JsonValueKind.Null) return null;
        if (v.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.Value.GetBoolean();
        if (v.Value.ValueKind == JsonValueKind.String && bool.TryParse(v.Value.GetString(), out bool b)) return b;
        throw new QaStepException($"'{name}' must be true or false.");
    }

    // 기능: 단계 Parameter를 위치(마커 이름 또는 {x,y,z})로 해석한다.
    // 입력: name - Parameter 이름.
    // 출력: 해석된 QaPosition, Parameter가 없으면 null. 해석 실패면 QaStepException.
    public QaPosition? Position(string name)
    {
        JsonElement? v = Param(name);
        if (v == null) return null;
        if (!Run.Markers.TryResolve(v.Value, out QaPosition p, out string? error)) throw new QaStepException($"'{name}': {error}");
        return p;
    }

    public string ActorAlias => Step.Actor ?? throw new QaStepException("'actor' is required.");

    // 기능: 단계의 actor 별칭에 해당하는 Actor를 가져온다.
    // 입력: 없음.
    // 출력: 해당 IQaActor. actor가 없거나 미등록 별칭이면 QaStepException.
    public IQaActor Actor() => Run.Actors.Get(ActorAlias);

    // 기능: 로컬 조건이 성립할 때까지 단계의 Soft Timeout 안에서 주기적으로 검사한다.
    // 입력: condition - 검사할 조건, token - 취소 토큰, intervalMs - 검사 간격(ms).
    // 출력: 조건이 성립하면 true, Timeout까지 성립하지 않으면 false.
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
