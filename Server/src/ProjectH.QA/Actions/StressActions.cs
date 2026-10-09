using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProjectH.Shared.Simulation;

namespace ProjectH.QA;

// Stress test actions (Docs/requests/2026-10-02-server-stress-test-request.md, design D37-D43): `measure` (a measured
// phase) and the actor group actions. Group actions only hand each member a behaviour (ActorBrain) that the actor pump
// runs every tick; arrange commands (positions, weapons, resources) go through the QA API with bounded concurrency, and
// the only background work (re-arm after a respawn, churn cycles) is a GroupWorkload the run's cleanup always ends.
public static partial class StressActions
{
    public const int MaxMeasureSeconds = 24 * 3600;
    public const int ArrangeParallel = 8;
    private const int MaxRetries = 2;

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,32}$")]
    private static partial Regex GroupName();

    // Fight areas (D38): the map is 160 m wide, so a circle beyond 1.5 x its half size only adds empty candidates;
    // the spacing keeps the candidate grid small ((2 r / spacing)^2 at most about 14,400 points).
    public const float MaxAreaRadius = ProjectH.Shared.Simulation.GameMap.HalfSize * 1.5f;
    public const float MinSpacing = 2f;
    public const float MaxSpacing = 50f;

    // stopGroup's own budget: the workloads' stop (GroupRegistry.StopTimeout) plus room to send the members' resets.
    public const int StopGroupTimeoutMs = 20_000;

    public static readonly string[] MovePatterns = { "mixed", "clockwise", "counterClockwise", "radial", "random" };
    public static readonly string[] Roles = { "move", "sprint", "jump", "idle", "loot", "build", "turbo" };

    // 기능: Stress 액션(measure, actorGroup, groupMove·Combat·Build·Loot·Roles·Churn·NetworkFault, stopGroup, Phase B)을 Registry에 등록한다.
    // 입력: r - 등록 대상 Registry.
    // 출력: 반환값 없음. Registry에 Stress 액션 Handler가 추가된다.
    public static void Register(ActionRegistry r)
    {
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "measure",
            Required = new[] { "name", "seconds" },
            Optional = new[] { "sampleSeconds" },
            // A ${variable} length is known only at run time: then the longest phase bounds the step (the scenario timeout
            // still bounds the run).
            DefaultTimeout = s => (int)Math.Min(int.MaxValue - 10_000, Seconds(s, "seconds", MaxMeasureSeconds) * 1000 + 30_000),
            Check = CheckMeasure,
        }, MeasureAsync));
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "actorGroup",
            Required = new[] { "groups" },
            Optional = new[] { "prefix", "seed" },
            Check = CheckActorGroup,
        }, ActorGroupAsync));
        r.Add(Group("groupMove", GroupMoveAsync, new[] { "pattern", "center", "radius", "sprint", "sprintPercent", "jumpEverySeconds" }, positions: new[] { "center" },
            check: s => CheckChoice(s, "pattern", MovePatterns)));
        r.Add(Group("groupCombat", GroupCombatAsync, new[]
        {
            "pairing", "groupSize", "weapon", "ammo", "ammoType", "burst", "pauseMs", "reloadEvery", "hitPercent", "engageRange", "strafeSeconds",
            "arrange", "rearm", "center", "radius", "spacing", "pairDistance", "shield",
        }, positions: new[] { "center" }, check: s => CheckChoice(s, "pairing", new[] { "pairs", "groups" }).Concat(CheckRange(s, "radius", 0, MaxAreaRadius, exclusiveMin: true))
                .Concat(CheckRange(s, "spacing", MinSpacing, MaxSpacing)).Concat(CheckRange(s, "pairDistance", MinSpacing, MaxSpacing)), timeoutMs: 60_000));
        r.Add(Group("groupBuild", GroupBuildAsync, new[] { "pieces", "material", "ratePerSecond", "arrange", "resources", "center", "radius", "role", "recycle", "weapon", "ammo" },
            positions: new[] { "center" }, check: CheckBuildPieces, timeoutMs: 60_000));
        r.Add(Group("groupLoot", GroupLootAsync, new[] { "searchRange", "dropEvery" }));
        r.Add(Group("groupRoles", GroupRolesAsync, new[] { "roles", "switchSeconds", "center", "radius", "ratePerSecond", "turboRatePerSecond", "resources", "material" },
            positions: new[] { "center" }, check: CheckRoles, required: new[] { "roles" }, timeoutMs: 60_000));
        r.Add(Group("groupChurn", GroupChurnAsync, new[] { "percent", "cycleSeconds", "cycles", "mode", "offlineMs", "reconnectTimeoutMs" },
            check: s => CheckChoice(s, "mode", new[] { "graceful", "drop", "mixed" })));
        r.Add(Group("groupNetworkFault", GroupNetworkFaultAsync, new[] { "latencyMs", "jitterMs", "lossPercent", "duplicatePercent", "direction" }));
        r.Add(Group("stopGroup", StopGroupAsync, Array.Empty<string>(), timeoutMs: StopGroupTimeoutMs));
        RegisterPhaseB(r);
    }

    // 기능: group Parameter가 필수인 Stress 그룹 액션 정의를 만든다.
    // 입력: name - 액션 이름, run - 실행 함수, optional - 선택 Parameter, positions - 위치 Parameter, check - Literal 검사, required - group 외의 필수 Parameter, timeoutMs - 고정 기본 Timeout(ms).
    // 출력: Required에 "group"이 포함된 DelegateAction.
    private static DelegateAction Group(string name, Func<StepContext, CancellationToken, Task<StepOutcome>> run, string[] optional,
        string[]? positions = null, Func<StepDefinition, IEnumerable<string>>? check = null, string[]? required = null, int? timeoutMs = null) =>
        new(new ActionSpec
        {
            Name = name,
            Required = new[] { "group" }.Concat(required ?? Array.Empty<string>()).ToArray(),
            Optional = optional,
            PositionParams = positions ?? Array.Empty<string>(),
            Check = check,
            DefaultTimeout = timeoutMs != null ? _ => timeoutMs.Value : null,
        }, run);

    // 기능: 단계 Parameter의 Literal 숫자를 읽는다(변수이거나 없으면 fallback).
    // 입력: s - 단계 정의, name - Parameter 이름, fallback - 숫자를 읽을 수 없을 때 쓸 값.
    // 출력: Literal 숫자 값, 아니면 fallback.
    private static double Seconds(StepDefinition s, string name, double fallback) =>
        s.Params.TryGetValue(name, out JsonElement v) && Comparison.TryNumber(v, out double d) ? d : fallback;

    // ---- validation of literal values ----

    // 기능: measure 단계의 Literal 인자(name 형식, seconds·sampleSeconds 범위)를 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
    private static IEnumerable<string> CheckMeasure(StepDefinition s)
    {
        if (s.Params.TryGetValue("name", out JsonElement n) && !Variables.HasReference(n) && (n.ValueKind != JsonValueKind.String || !GroupName().IsMatch(n.GetString()!)))
            yield return "'name' must be 1-32 letters, digits, '_' or '-' (warmup, steady, cooldown, match_01...).";
        if (s.Params.TryGetValue("seconds", out JsonElement v) && !Variables.HasReference(v) && (!Comparison.TryNumber(v, out double d) || d < 1 || d > MaxMeasureSeconds))
            yield return $"'seconds' must be 1-{MaxMeasureSeconds}.";
        if (s.Params.TryGetValue("sampleSeconds", out JsonElement ss) && !Variables.HasReference(ss) && (!Comparison.TryNumber(ss, out double x) || x < 1 || x > 120))
            yield return "'sampleSeconds' must be 1-120.";
    }

    // 기능: actorGroup 단계의 groups Literal(비어 있지 않은 배열, 그룹 이름 형식·중복, percent/count/rest 택일, proxy, rest 하나, 합계 100 이하)을 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
    private static IEnumerable<string> CheckActorGroup(StepDefinition s)
    {
        if (!s.Params.TryGetValue("groups", out JsonElement g)) yield break;
        if (g.ValueKind != JsonValueKind.Array || g.GetArrayLength() == 0)
        {
            yield return "'groups' must be a non-empty array like [ { \"name\": \"combat\", \"percent\": 30 }, { \"name\": \"move\", \"rest\": true } ].";
            yield break;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        int rest = 0;
        double percent = 0;
        foreach (JsonElement e in g.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                yield return "Each group must be an object.";
                continue;
            }
            string? name = e.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (name == null || !GroupName().IsMatch(name)) yield return "Each group needs a 'name' of 1-32 letters, digits, '_' or '-'.";
            else if (!names.Add(name)) yield return $"Group '{name}' is listed twice.";
            int sizes = 0;
            foreach (JsonProperty p in e.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "name":
                        break;
                    case "percent":
                        sizes++;
                        if (!Variables.HasReference(p.Value) && (!Comparison.TryNumber(p.Value, out double pc) || pc < 0 || pc > 100)) yield return $"Group '{name}': 'percent' must be 0-100.";
                        else if (Comparison.TryNumber(p.Value, out double pv)) percent += pv;
                        break;
                    case "count":
                        sizes++;
                        if (!Variables.HasReference(p.Value) && (!Comparison.TryNumber(p.Value, out double c) || c < 0 || c > ActorManager.MaxActors || c != Math.Floor(c)))
                            yield return $"Group '{name}': 'count' must be an integer 0-{ActorManager.MaxActors}.";
                        break;
                    case "rest":
                        sizes++;
                        rest++;
                        if (p.Value.ValueKind != JsonValueKind.True) yield return $"Group '{name}': 'rest' can only be true.";
                        break;
                    case "proxy":
                        if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) yield return $"Group '{name}': 'proxy' must be true or false.";
                        break;
                    default:
                        yield return $"Group '{name}': unknown field '{p.Name}' (name, percent, count, rest, proxy).";
                        break;
                }
            }
            if (sizes != 1) yield return $"Group '{name}': give exactly one of 'percent', 'count' or 'rest'.";
        }
        if (rest > 1) yield return "Only one group can take the rest.";
        if (percent > 100.0001) yield return $"The percentages add up to {percent} (more than 100).";
    }

    // 기능: 숫자 Parameter의 Literal 값이 min-max 범위(m 단위) 안인지 검사한다.
    // 입력: s - 단계 정의, name - Parameter 이름, min - 최소값, max - 최대값, exclusiveMin - true면 min 초과여야 함.
    // 출력: 오류 문장들(없으면 빈 목록).
    private static IEnumerable<string> CheckRange(StepDefinition s, string name, double min, double max, bool exclusiveMin = false)
    {
        if (s.Params.TryGetValue(name, out JsonElement v) && !Variables.HasReference(v)
            && (!Comparison.TryNumber(v, out double d) || d > max || (exclusiveMin ? d <= min : d < min)))
            yield return $"'{name}' must be {(exclusiveMin ? "above " + min : min.ToString(CultureInfo.InvariantCulture))} and at most {max} m.";
    }

    // 기능: 문자열 Parameter의 Literal 값이 choices 중 하나인지 검사한다(대소문자 무시).
    // 입력: s - 단계 정의, name - Parameter 이름, choices - 허용 값 목록.
    // 출력: 오류 문장들(없으면 빈 목록).
    private static IEnumerable<string> CheckChoice(StepDefinition s, string name, string[] choices)
    {
        if (s.Params.TryGetValue(name, out JsonElement v) && !Variables.HasReference(v)
            && (v.ValueKind != JsonValueKind.String || !choices.Contains(v.GetString()!, StringComparer.OrdinalIgnoreCase)))
            yield return $"'{name}' must be one of {string.Join(", ", choices)}.";
    }

    // 기능: pieces Literal이 비어 있지 않은 조각 이름(wall, floor, ramp, roof) 배열인지 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
    private static IEnumerable<string> CheckBuildPieces(StepDefinition s)
    {
        if (!s.Params.TryGetValue("pieces", out JsonElement p) || Variables.HasReference(p)) yield break;
        if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() == 0) yield return "'pieces' must be a list like [\"wall\", \"floor\", \"ramp\", \"roof\"].";
        else foreach (JsonElement e in p.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String || !Enum.TryParse(e.GetString(), true, out BuildPieceType _) || int.TryParse(e.GetString(), out _))
                yield return $"Unknown piece {e.GetRawText()} (wall, floor, ramp, roof).";
        }
    }

    // 기능: roles Literal이 role(Roles 중 하나)과 percent(0-100)를 가진 객체의 비어 있지 않은 배열인지 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
    private static IEnumerable<string> CheckRoles(StepDefinition s)
    {
        if (!s.Params.TryGetValue("roles", out JsonElement r) || Variables.HasReference(r) && r.ValueKind != JsonValueKind.Array) yield break;
        if (r.ValueKind != JsonValueKind.Array || r.GetArrayLength() == 0)
        {
            yield return "'roles' must be a list like [ { \"role\": \"move\", \"percent\": 60 }, { \"role\": \"loot\", \"percent\": 40 } ].";
            yield break;
        }
        foreach (JsonElement e in r.EnumerateArray())
        {
            string? role = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("role", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (role == null || !Roles.Contains(role, StringComparer.OrdinalIgnoreCase)) yield return $"Each role needs 'role' (one of {string.Join(", ", Roles)}) and 'percent'.";
            if (e.ValueKind == JsonValueKind.Object && (!e.TryGetProperty("percent", out JsonElement pc) || (!Variables.HasReference(pc) && (!Comparison.TryNumber(pc, out double d) || d < 0 || d > 100))))
                yield return $"Role '{role}': 'percent' must be 0-100.";
        }
    }

    // ---- measure (D37, D41, D43) ----

    // 기능: 측정 Phase 하나를 돌려(MeasurePhaseAsync) 결과를 Run의 Stress 보고에 더한다.
    // 입력: ctx - 단계 문맥(name, seconds, sampleSeconds), token - 취소 토큰.
    // 출력: 서버가 끝까지 답하면 Pass(saveAs: 표본을 뺀 Phase 수치), 서버 미응답·종료면 Fail. seconds가 범위 밖이면 QaStepException.
    private static async Task<StepOutcome> MeasureAsync(StepContext ctx, CancellationToken token)
    {
        string name = ctx.RequireString("name");
        double seconds = ctx.Double("seconds") ?? 0;
        if (seconds < 1 || seconds > MaxMeasureSeconds) throw new QaStepException($"'seconds' must be 1-{MaxMeasureSeconds}.");
        PhaseRun phase = await MeasurePhaseAsync(ctx.Run, name, ctx.Step.Id, seconds, ctx.Double("sampleSeconds"), token).ConfigureAwait(false);
        MeasureResult result = phase.Result;
        if (phase.Failure != null) return StepOutcome.Fail($"Measure {name}: {phase.Failure}", "server answering", phase.Failure);
        return StepOutcome.Pass(PhaseText(result), JsonPath.From(Flat(result)));
    }

    // 기능: 측정 결과를 한 줄 요약(길이, 인원, Tick 분위수, CPU, 관리 메모리, 송신량, 입력 지연 p95)으로 만든다.
    // 입력: result - 측정 결과.
    // 출력: 요약 문자열.
    internal static string PhaseText(MeasureResult result)
    {
        string latencyText = result.InputLatency is LatencyStats l ? $", R1 p95 {MeasureMath.F(l.P95Ms)} ms" : string.Empty;
        return $"{result.Name} {MeasureMath.F(result.Seconds)} s, {result.PlayersMax} players: tick p50 {MeasureMath.F(result.TickP50Ms)} p95 {MeasureMath.F(result.TickP95Ms)} p99 {MeasureMath.F(result.TickP99Ms)} max {MeasureMath.F(result.TickMaxMs)} ms, CPU {MeasureMath.F(result.CpuAvgPercent)}%, managed {MeasureMath.F(result.ManagedEndMB)} MB, send {MeasureMath.F(result.SendKBps)} KB/s{latencyText}";
    }

    // One measured phase (D37): the start reading, a sample every interval, the exact whole-phase window when it fits the
    // server's ring, then the summary into the run's stress report. Failure = the server did not answer (or exited).
    // Shared by `measure` and `matchLoop` (one phase per match). Start = the cumulative reading the phase began with.
    internal sealed record PhaseRun(MeasureResult Result, MeasureSample Start, string? Failure);

    // 기능: 측정 Phase 하나를 돈다. 시작 Metrics를 읽고 간격마다 표본을 모으며(Stall이 늘면 기록), 서버 링 안이면 전체 창을 한 번 더 읽어 요약을 Stress 보고에 더한다(D37).
    // 입력: run - Run, name - Phase 이름, stepId - 단계 id, seconds - 측정 길이(초), sampleSeconds - 표본 간격(초, null이면 자동), token - 취소 토큰, keep - 보고에 Phase를 넣을지, warnTickBudget - Tick 예산 초과를 이 Phase에서 경고할지.
    // 출력: 결과·시작 표본·실패 사유(서버 미응답이면 설명, 아니면 null)를 담은 PhaseRun. 취소돼도 측정한 부분까지 요약된다.
    // keep: add the phase to the report (matchLoop stops keeping its phases after MatchLoopKeptPhases, so later measure steps
    // still get a slot); warnTickBudget: the D42 tick-budget warning per phase (matchLoop sums them into one warning).
    internal static async Task<PhaseRun> MeasurePhaseAsync(RunContext run, string name, string? stepId, double seconds, double? sampleSeconds, CancellationToken token,
        bool keep = true, bool warnTickBudget = true)
    {
        int interval = MeasureMath.SampleInterval(seconds, sampleSeconds);
        var result = new MeasureResult
        {
            Name = name, StepId = stepId, PlannedSeconds = seconds, SampleSeconds = interval,
            StartedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        IQaServerClient server = run.Server;
        var tool = new ToolCpu();
        var clock = Stopwatch.StartNew();
        MeasureSample start;
        try
        {
            start = MeasureSample.From(await MetricsAsync(server, null, token).ConfigureAwait(false), 0, 0);
        }
        catch (QaApiException e)
        {
            return new PhaseRun(result, new MeasureSample(), ServerGone(run, e));
        }
        run.LastPhase = name;
        run.LastSample = start;
        LatencyCounts latencyStart = run.Actors.Latency.Snapshot();
        long arrangeStart = run.Groups.ArrangeCommandsTotal();
        var samples = new List<MeasureSample>();
        MeasureSample? whole = null;
        long stalls = start.Stalls;
        string? failure = null;
        bool done = false;
        try
        {
            for (int k = 1; ; k++)
            {
                double due = Math.Min(k * (double)interval, seconds);
                double wait = due - clock.Elapsed.TotalSeconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), token).ConfigureAwait(false);
                bool last = due >= seconds;
                // The last interval may be shorter than the others; the server window is whole seconds.
                int window = last ? Math.Clamp((int)Math.Round(seconds - (k - 1) * (double)interval), 1, interval) : interval;
                MeasureSample sample = MeasureSample.From(await MetricsAsync(server, window, token).ConfigureAwait(false), clock.Elapsed.TotalSeconds, tool.Percent());
                if (samples.Count < MeasureMath.MaxSamples) samples.Add(sample);
                else result.SamplesDropped++;
                run.LastSample = sample;
                if (sample.Stalls > stalls)
                {
                    await RecordStallAsync(run, name, stalls, sample, token).ConfigureAwait(false);
                    stalls = sample.Stalls;
                }
                if (last) break;
            }
            // D37: within the server's ring the whole phase is one exact window (ticks, CPU, rates).
            if (seconds <= QaMetricsRingSeconds)
            {
                whole = MeasureSample.From(await MetricsAsync(server, (int)Math.Round(seconds), token).ConfigureAwait(false), clock.Elapsed.TotalSeconds, tool.WholePercent());
                run.LastSample = whole;
            }
            done = true;
        }
        catch (QaApiException e)
        {
            failure = ServerGone(run, e);
        }
        finally
        {
            // Also on cancel (Ctrl+C, scenario timeout): the part measured so far is reported.
            result.Cancelled = !done && failure == null;
            MeasureMath.Summarize(result, start, samples, whole, clock.Elapsed.TotalSeconds);
            result.Samples = samples;
            LatencyStats? latency = run.Actors.Latency.Snapshot().Since(latencyStart).Stats();
            result.InputLatency = latency;
            result.InputLatencyNote = latency != null
                ? $"{latency.Samples} acknowledged inputs; includes the snapshot interval and the actor pump's ~33 ms tick"
                : "Not Available (no headless actor input was acknowledged in this phase)";
            result.ArrangeCommands = run.Groups.ArrangeCommandsTotal() - arrangeStart;
            if (keep && run.Stress.Phases.Count < StressReport.MaxPhases) run.Stress.Phases.Add(result);
            else if (keep) run.Stress.PhasesDropped++;
            if (warnTickBudget && !result.Cancelled && !name.StartsWith("warmup", StringComparison.OrdinalIgnoreCase) && result.TickMaxMs > MeasureResult.TickBudgetMs)
                run.Warnings.Add($"Stress: phase '{name}' tickMaxMs {MeasureMath.F(result.TickMaxMs)} ms is over the {MeasureResult.TickBudgetMs} ms tick budget (30 Hz; D42 warning, not a failure).");
        }
        return new PhaseRun(result, start, failure);
    }

    // 503 (QA queue full) and 504 (the loop did not run the query in time) are retried twice, half a second apart; a
    // server that still does not answer is a hang (D41: the measure fails).
    // 기능: /qa/metrics를 읽는다(503·504는 0.5초 간격으로 최대 MaxRetries 재시도).
    // 입력: server - QA 서버 Client, window - 창 길이(초, null이면 누적값), token - 취소 토큰.
    // 출력: Metrics JSON. 재시도 뒤에도 실패면 QaApiException.
    internal static async Task<JsonElement> MetricsAsync(IQaServerClient server, int? window, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await server.GetMetricsAsync(window, token).ConfigureAwait(false);
            }
            catch (QaApiException e) when (e.StatusCode is 503 or 504 && attempt < MaxRetries)
            {
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
    }

    // The server keeps 120 s of ticks (Qa/QaOptions.MetricsWindowSeconds); windowSeconds above it is refused.
    public const int QaMetricsRingSeconds = 120;

    // 기능: saveAs용으로 측정 결과에서 표본 목록을 뺀 수치만 모은다(변수를 작게 유지).
    // 입력: r - 측정 결과.
    // 출력: 수치만 담은 익명 객체.
    // saveAs: the phase's numbers without the samples (variables stay small).
    private static object Flat(MeasureResult r) => new
    {
        r.Name, r.Seconds, r.TicksExact, r.TickP50Ms, r.TickP95Ms, r.TickP99Ms, r.TickMaxMs, r.CpuAvgPercent, r.CpuMaxPercent, r.ToolCpuAvgPercent,
        r.WorkingSetStartMB, r.WorkingSetEndMB, r.WorkingSetMaxMB, r.ManagedStartMB, r.ManagedEndMB, r.ManagedMaxMB, r.Gen0, r.Gen1, r.Gen2,
        r.AllocatedMB, r.AllocatedMBPerSec, r.GcPauseMs, r.SendKBps, r.SendKBpsMax, r.RecvKBps, r.PktInPerSec, r.PktOutPerSec, r.QaCommandMsAvg,
        r.PlayersMin, r.PlayersMax, r.SessionsMin, r.SessionsMax, r.AliveMin, r.AliveMax, r.BuildPiecesStart, r.BuildPiecesEnd, r.DbQueueMax,
        r.DbSaved, r.DbFailed, r.Stalls, r.TickFailures, r.BadPackets, r.ArrangeCommands,
        inputLatencyP50Ms = r.InputLatency?.P50Ms, inputLatencyP95Ms = r.InputLatency?.P95Ms, inputLatencyP99Ms = r.InputLatency?.P99Ms, inputLatencyMaxMs = r.InputLatency?.MaxMs,
    };

    // 기능: QA API 실패를 서버 프로세스 종료(띄운 서버가 끝났으면 exit code 포함) 또는 미응답 설명으로 바꾼다.
    // 입력: run - Run, e - QA API 예외.
    // 출력: 실패 사유 문자열.
    internal static string ServerGone(RunContext run, QaApiException e)
    {
        if (run.ServerControl is LaunchedServer { Running: false } launched)
            return $"the server process exited (exit code {launched.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}) — {e.Message}";
        return $"the QA API did not answer: {e.Message}";
    }

    // 기능: 서버가 센 Stall을 Phase·단계·인원·표본·최근 이벤트(켜져 있으면)와 함께 Stress 보고에 기록하고 경고를 남긴다(D41).
    // 입력: run - Run, phase - Phase 이름, before - 이전 Stall 수, sample - Stall이 늘어난 표본, token - 취소 토큰.
    // 출력: 반환값 없음. run.Stress.Stalls(한도를 넘으면 StallsDropped)와 run.Warnings가 바뀐다.
    // D41: the moment the server counted a stall: phase, step, players, the sample, and the recent events (when on).
    private static async Task RecordStallAsync(RunContext run, string phase, long before, MeasureSample sample, CancellationToken token)
    {
        if (run.Stress.Stalls.Count >= StressReport.MaxStalls)
        {
            run.Stress.StallsDropped++;
            return;
        }
        var record = new StallRecord
        {
            Utc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), Phase = phase, StepId = run.CurrentStepId,
            StallsBefore = before, StallsAfter = sample.Stalls, Players = sample.Players, Actors = run.Actors.Count, Sample = sample,
        };
        if (run.EventsEnabled && run.Events != null)
        {
            try
            {
                await run.Events.FetchAsync(token).ConfigureAwait(false);
                record.RecentEvents.AddRange(run.Events.Recent.TakeLast(20).Select(e => e.Raw));
            }
            catch (QaApiException e)
            {
                record.EventsNote = $"Events not read: {e.Message}";
            }
        }
        else
        {
            record.EventsNote = "QA events are off in this run (Qa:Events=false, stress mode).";
        }
        run.Stress.Stalls.Add(record);
        run.Warnings.Add($"Stress: the server counted {sample.Stalls - before} stall(s) during '{phase}' (step {run.CurrentStepId}); see Stalls.");
    }

    // The QA tool's own CPU, on the server's scale (all logical processors = 100 %): since the last call, and over the
    // whole phase.
    private sealed class ToolCpu
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly TimeSpan _start = Process.GetCurrentProcess().TotalProcessorTime;
        private TimeSpan _cpu;
        private TimeSpan _wall;

        // 기능: 도구 CPU 측정기를 만들고 직전 기준점을 생성 시점의 CPU 시간으로 둔다.
        // 입력: 없음.
        // 출력: 시계가 시작된 ToolCpu.
        public ToolCpu() => _cpu = _start;

        // 기능: 직전 호출(처음은 생성) 이후 이 도구의 CPU 사용률을 구하고 기준점을 지금으로 옮긴다.
        // 입력: 없음.
        // 출력: 구간 CPU 사용률(%, 모든 논리 프로세서 = 100).
        public double Percent()
        {
            TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime;
            TimeSpan wall = _clock.Elapsed;
            double percent = Scale(cpu - _cpu, wall - _wall);
            _cpu = cpu;
            _wall = wall;
            return percent;
        }

        // 기능: 생성 이후 전체 구간의 이 도구 CPU 사용률을 구한다.
        // 입력: 없음.
        // 출력: 전체 CPU 사용률(%, 모든 논리 프로세서 = 100).
        public double WholePercent() => Scale(Process.GetCurrentProcess().TotalProcessorTime - _start, _clock.Elapsed);

        // 기능: CPU 시간을 경과 시간 x 논리 프로세서 수로 나눠 백분율로 바꾼다.
        // 입력: cpu - 쓴 CPU 시간, wall - 경과 시간.
        // 출력: CPU 사용률(%). wall이 1 ms 이하면 0.
        private static double Scale(TimeSpan cpu, TimeSpan wall) =>
            wall.TotalSeconds > 0.001 ? cpu.TotalSeconds / (wall.TotalSeconds * Environment.ProcessorCount) * 100.0 : 0;
    }

    // ---- groups ----

    // 기능: Headless Actor(prefix로 고를 수 있음)를 groups의 percent·count·rest로 Seed에 따라 나눠 ActorGroup을 정의한다(proxy 그룹은 구성원에 Proxy를 켠다).
    // 입력: ctx - 단계 문맥(groups, prefix, seed), token - 취소 토큰(사용 안 함).
    // 출력: 그룹별 인원을 담은 Pass(saveAs: actors, seed, groups, ungrouped), Headless Actor가 없으면 Fail. groups 형식 오류나 proxy 그룹에 이미 연결된 Actor가 있으면 QaStepException.
    private static Task<StepOutcome> ActorGroupAsync(StepContext ctx, CancellationToken token)
    {
        RunContext run = ctx.Run;
        string? prefix = ctx.String("prefix");
        IQaActor[] actors = run.Actors.All.Where(a => a is not UnityActor && (prefix == null || a.Alias.StartsWith(prefix, StringComparison.Ordinal)))
            .OrderBy(a => a.Alias, StringComparer.Ordinal).ToArray();
        if (actors.Length == 0) return Task.FromResult(StepOutcome.Fail($"No headless actors{(prefix != null ? $" with prefix '{prefix}'" : string.Empty)} to group."));
        JsonElement groups = ctx.Param("groups") ?? throw new QaStepException("'groups' is required.");
        if (groups.ValueKind != JsonValueKind.Array) throw new QaStepException("'groups' must be an array.");
        var shares = new List<GroupShare>();
        foreach (JsonElement e in groups.EnumerateArray())
        {
            string name = JsonPath.Child(e, "name") is { ValueKind: JsonValueKind.String } n ? n.GetString()! : throw new QaStepException("Each group needs a 'name'.");
            double? percent = JsonPath.Child(e, "percent") is JsonElement p ? (Comparison.TryNumber(p, out double pd) ? pd : throw new QaStepException($"Group '{name}': 'percent' must be a number.")) : null;
            int? count = JsonPath.Child(e, "count") is JsonElement c ? (Comparison.TryNumber(c, out double cd) ? (int)cd : throw new QaStepException($"Group '{name}': 'count' must be a number.")) : null;
            bool rest = JsonPath.Child(e, "rest") is { ValueKind: JsonValueKind.True };
            bool proxy = JsonPath.Child(e, "proxy") is { ValueKind: JsonValueKind.True };
            if (percent is < 0 or > 100) throw new QaStepException($"Group '{name}': 'percent' must be 0-100.");
            shares.Add(new GroupShare(name, percent, count, rest, proxy));
        }
        int seed = ctx.Int("seed", int.MinValue, int.MaxValue) ?? run.Seed;
        int[] assignment = GroupRegistry.Distribute(actors.Length, shares, seed);
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int s = 0; s < shares.Count; s++)
        {
            var members = new List<IQaActor>();
            var indices = new List<int>();
            for (int i = 0; i < actors.Length; i++)
            {
                if (assignment[i] != s) continue;
                members.Add(actors[i]);
                indices.Add(i);
            }
            if (shares[s].Proxy)
            {
                IQaActor? joined = members.FirstOrDefault(m => m.State.Status is ActorStatus.Joined or ActorStatus.Connecting);
                if (joined != null) throw new QaStepException($"Group '{shares[s].Name}' asks for a network proxy, but {joined.Alias} is already connected: make proxy groups before connectAll.");
                foreach (IQaActor m in members) run.Network.EnableProxy(m.Alias);
            }
            run.Groups.Define(new ActorGroup(shares[s].Name, members, indices));
            sizes[shares[s].Name] = members.Count;
        }
        int ungrouped = assignment.Count(a => a < 0);
        string text = string.Join(", ", sizes.Select(p => $"{p.Key} {p.Value}")) + (ungrouped > 0 ? $", {ungrouped} in no group" : string.Empty);
        return Task.FromResult(StepOutcome.Pass($"{actors.Length} actors (seed {seed}): {text}", JsonPath.From(new { actors = actors.Length, seed, groups = sizes, ungrouped })));
    }

    // 기능: 단계의 group 인자에 해당하는 ActorGroup을 가져온다.
    // 입력: ctx - 단계 문맥(group).
    // 출력: 해당 ActorGroup. group이 없거나 정의되지 않은 그룹이면 예외.
    private static ActorGroup GroupOf(StepContext ctx) => ctx.Run.Groups.Get(ctx.RequireString("group"));

    // 기능: 단계의 center 위치를 XZ 평면 좌표로 읽는다.
    // 입력: ctx - 단계 문맥(center).
    // 출력: (x, z) 좌표. center가 없으면 (0, 0).
    private static Vector2 Center(StepContext ctx)
    {
        if (ctx.Position("center") is QaPosition p) return new Vector2(p.X, p.Z);
        return Vector2.Zero;
    }

    // Hands every member its brain and waits (bounded) until each has applied it. The group's previous behaviour goes with
    // its workload (a re-arm loop for an old combat or recycle behaviour); a churn keeps running beside any behaviour.
    // 기능: 그룹의 재무장 작업을 멈춘 뒤 모든 구성원에게 make가 만든 Brain을 보내고 전부 적용될 때까지 기다린다.
    // 입력: ctx - 단계 문맥, group - 대상 그룹, make - (구성원 순번, Actor)로 Brain을 만드는 함수(null이면 Brain 없음), token - 취소 토큰.
    // 출력: 모든 구성원이 적용하면 true, 단계 Timeout까지 안 되면 false.
    private static async Task<bool> SetBrainsAsync(StepContext ctx, ActorGroup group, Func<int, IQaActor, ActorBrain?> make, CancellationToken token)
    {
        await ctx.Run.Groups.StopAsync(group.Name, GroupRegistry.StopTimeout, kind: "rearm").ConfigureAwait(false);
        var sent = new List<(IQaActor Actor, long Id)>(group.Members.Count);
        for (int i = 0; i < group.Members.Count; i++)
        {
            IQaActor a = group.Members[i];
            var command = new SetBrainCommand(make(i, a));
            await a.SendAsync(command, token).ConfigureAwait(false);
            sent.Add((a, command.Id));
        }
        return await ctx.WaitUntilAsync(() => sent.All(s => s.Actor.State.LastCommandId >= s.Id), token).ConfigureAwait(false);
    }

    // 기능: 그룹 구성원에게 이동 Brain(패턴, 중심·반경, 달리기 여부·비율, 점프 주기)을 준다.
    // 입력: ctx - 단계 문맥(group, pattern, center, radius, sprint, sprintPercent, jumpEverySeconds), token - 취소 토큰.
    // 출력: 모두 적용되면 Pass, 아니면 Fail. 패턴이나 반경이 잘못되면 QaStepException.
    private static async Task<StepOutcome> GroupMoveAsync(StepContext ctx, CancellationToken token)
    {
        ActorGroup group = GroupOf(ctx);
        string pattern = ctx.String("pattern") ?? "mixed";
        if (!MovePatterns.Contains(pattern, StringComparer.OrdinalIgnoreCase)) throw new QaStepException($"'pattern' must be one of {string.Join(", ", MovePatterns)}.");
        Vector2 center = Center(ctx);
        float radius = (float)(ctx.Double("radius") ?? 60);
        if (!(radius > 0)) throw new QaStepException("'radius' must be positive.");
        bool sprint = ctx.Bool("sprint") ?? false;
        double sprintPercent = ctx.Double("sprintPercent") ?? 0;
        float jump = (float)(ctx.Double("jumpEverySeconds") ?? 0);
        int seed = ctx.Run.Seed;
        int sprinting = 0;
        bool applied = await SetBrainsAsync(ctx, group, (i, _) =>
        {
            MovePattern p = PatternFor(pattern, i);
            bool sprints = sprint || (sprintPercent > 0 && new Random(ActorBrain.Seed(seed, group.Indices[i], 5)).NextDouble() * 100 < sprintPercent);
            if (sprints) sprinting++;
            return new MoveBrain(new MovePlan(p, center, radius, sprints, jump, i, group.Members.Count, seed), sprints ? "sprint" : "move");
        }, token).ConfigureAwait(false);
        group.Role = "move";
        if (!applied) return StepOutcome.Fail($"Not every member of '{group.Name}' took the move behaviour within {ctx.TimeoutMs} ms.");
        return StepOutcome.Pass($"{group.Members.Count} moving ({pattern}, radius {radius} m around ({center.X}, {center.Y}), {sprinting} sprinting{(jump > 0 ? $", jump every {jump} s" : "")})");
    }

    // 기능: 패턴 이름을 MovePattern으로 바꾼다(mixed 등 그 외 이름은 순번에 따라 4개 패턴을 돌아가며).
    // 입력: pattern - 패턴 이름, index - 구성원 순번.
    // 출력: 해당 MovePattern.
    public static MovePattern PatternFor(string pattern, int index) => pattern.ToLowerInvariant() switch
    {
        "clockwise" => MovePattern.Clockwise,
        "counterclockwise" => MovePattern.CounterClockwise,
        "radial" => MovePattern.Radial,
        "random" => MovePattern.Random,
        // §12: the four groups side by side, by index.
        _ => (MovePattern)(index % 4),
    };

    // §14-17. Members in fights of groupSize (2 = pairs); member k of a fight shoots member k+1 (a ring), so nobody is
    // everyone's target. A last single member joins the previous fight's ring.
    // 기능: 그룹을 groupSize 단위의 싸움(고리형 표적)으로 나누고, arrange면 자리·무기·탄·실드를 Arrange한 뒤 Combat Brain을 주며, rearm이면 재무장 작업을 시작한다(§14-17).
    // 입력: ctx - 단계 문맥(group, pairing, groupSize, weapon, ammo, ammoType, burst, pauseMs, reloadEvery, hitPercent, engageRange, strafeSeconds, arrange, rearm, center, radius, spacing, pairDistance, shield), token - 취소 토큰.
    // 출력: 적용되면 Pass(saveAs: fights, size, weapon, arrangeCommands), 자리 부족·Arrange 거절·무기 미도착·Brain 미적용이면 Fail. 구성원이 2명 미만이면 싸움 없이 Pass. 범위 밖 인자는 QaStepException.
    private static async Task<StepOutcome> GroupCombatAsync(StepContext ctx, CancellationToken token)
    {
        RunContext run = ctx.Run;
        ActorGroup group = GroupOf(ctx);
        string pairing = ctx.String("pairing") ?? "pairs";
        int size = string.Equals(pairing, "groups", StringComparison.OrdinalIgnoreCase) ? ctx.Int("groupSize", 2, 8) ?? 3 : 2;
        string weapon = ctx.String("weapon") ?? "Vesper AR";
        string ammoType = ctx.String("ammoType") ?? AmmoTypeOf(weapon);
        int ammo = ctx.Int("ammo", 0, 999) ?? 300;
        int burst = ctx.Int("burst", 1, 30) ?? 3;
        double pauseMs = ctx.Double("pauseMs") ?? 2000;
        int reloadEvery = ctx.Int("reloadEvery", 0, 1000) ?? 5;
        int hitPercent = ctx.Int("hitPercent", 0, 100) ?? 25;
        float engage = (float)(ctx.Double("engageRange") ?? 40);
        float strafe = (float)(ctx.Double("strafeSeconds") ?? 1.5);
        bool arrange = ctx.Bool("arrange") ?? true;
        bool rearm = ctx.Bool("rearm") ?? true;
        Vector2 center = Center(ctx);
        float radius = (float)(ctx.Double("radius") ?? 70);
        float spacing = (float)(ctx.Double("spacing") ?? 12);
        float distance = (float)(ctx.Double("pairDistance") ?? 10);
        int shield = ctx.Int("shield", 0, 100) ?? 100;
        if (!(radius > 0 && radius <= MaxAreaRadius)) throw new QaStepException($"'radius' must be above 0 and at most {MaxAreaRadius} m.");
        if (!(spacing >= MinSpacing && spacing <= MaxSpacing)) throw new QaStepException($"'spacing' must be {MinSpacing}-{MaxSpacing} m.");
        if (!(distance >= MinSpacing && distance <= MaxSpacing)) throw new QaStepException($"'pairDistance' must be {MinSpacing}-{MaxSpacing} m.");
        if (group.Members.Count < 2)
        {
            // A small player count can leave a percentage group with 0 or 1 member: nothing to fight, not a failure.
            if (group.Members.Count == 1) run.Warnings.Add($"Stress: groupCombat '{group.Name}' has 1 member and no partner; it stays idle.");
            return StepOutcome.Pass($"'{group.Name}' has {group.Members.Count} member(s): no fight");
        }

        // Fights in member order; a leftover of 2+ is its own fight, a single one joins the last fight.
        var fights = new List<List<int>>();
        for (int i = 0; i < group.Members.Count; i += size) fights.Add(Enumerable.Range(i, Math.Min(size, group.Members.Count - i)).ToList());
        if (fights.Count > 1 && fights[^1].Count == 1)
        {
            fights[^2].AddRange(fights[^1]);
            fights.RemoveAt(fights.Count - 1);
        }
        var target = new IQaActor[group.Members.Count];
        var spot = new Vector3[group.Members.Count];
        var facing = new float[group.Members.Count];
        var spots = new Dictionary<int, List<Vector3[]>>();
        foreach (int fightSize in fights.Select(f => f.Count).Distinct())
            spots[fightSize] = StressMap.FightSpots(center, radius, spacing, distance, fightSize, fights.Count);
        var used = new HashSet<Vector3>();
        var next = fights.Select(f => f.Count).Distinct().ToDictionary(n => n, _ => 0);
        foreach (List<int> fight in fights)
        {
            List<Vector3[]> options = spots[fight.Count];
            Vector3[]? place = null;
            while (next[fight.Count] < options.Count)
            {
                Vector3[] candidate = options[next[fight.Count]++];
                if (candidate.Any(used.Contains)) continue;
                place = candidate;
                break;
            }
            if (place == null && arrange)
                return StepOutcome.Fail($"Only room for {next[fight.Count]} fights of {fight.Count} within {radius} m of ({center.X}, {center.Y}) (spacing {spacing} m): widen 'radius' or lower 'spacing'.",
                    $"{fights.Count} fight spots", $"{options.Count}");
            for (int k = 0; k < fight.Count; k++)
            {
                int me = fight[k];
                int other = fight[(k + 1) % fight.Count];
                target[me] = group.Members[other];
                if (place != null)
                {
                    spot[me] = place[k];
                    facing[me] = StressMap.YawTo(place[k], place[(k + 1) % fight.Count]);
                    used.Add(place[k]);
                }
            }
        }

        if (arrange)
        {
            var commands = new List<(string, string?, object)>();
            for (int i = 0; i < group.Members.Count; i++)
            {
                IQaActor a = group.Members[i];
                commands.AddRange(ArmCommands(a, spot[i], facing[i], weapon, ammoType, ammo, shield, position: true));
            }
            int failed = await CommandsAsync(run, group.Stats, commands, token).ConfigureAwait(false);
            if (failed > 0) return StepOutcome.Fail($"{failed} of {commands.Count} arrange commands for '{group.Name}' were refused (see group stats errors).", "all accepted", $"{failed} refused");
            bool armed = await ctx.WaitUntilAsync(() => group.Members.All(m => m.State.WeaponId != 0 || !m.State.Joined), token).ConfigureAwait(false);
            if (!armed)
            {
                IQaActor? bare = group.Members.FirstOrDefault(m => m.State.WeaponId == 0);
                return StepOutcome.Fail($"{bare?.Alias} has no weapon after giveWeapon (its inventory did not arrive within {ctx.TimeoutMs} ms).");
            }
        }
        var plan = new Func<int, CombatPlan>(i => new CombatPlan(0, burst, (float)(pauseMs / 1000.0), reloadEvery, hitPercent, engage, strafe, group.Indices[i], run.Seed));
        bool applied = await SetBrainsAsync(ctx, group, (i, _) => new CombatBrain(plan(i), target[i]), token).ConfigureAwait(false);
        group.Role = "combat";
        if (!applied) return StepOutcome.Fail($"Not every member of '{group.Name}' took the combat behaviour.");

        if (rearm)
        {
            var cts = new CancellationTokenSource();
            Task task = Task.Run(() => RearmLoopAsync(run.Server, run.RunId, run.PollIntervalMs, group, spot, facing, arrange, weapon, ammoType, ammo, cts.Token));
            run.Groups.Start(new GroupWorkload(group.Name, "rearm", cts, task));
        }
        string spotsText = arrange ? $", {fights.Count} spots within {radius} m" : string.Empty;
        return StepOutcome.Pass($"{group.Members.Count} fighting in {fights.Count} fight(s) of {size} with {weapon} (burst {burst}, pause {pauseMs} ms, hit {hitPercent}%){spotsText}",
            JsonPath.From(new { fights = fights.Count, size, weapon, arrangeCommands = Interlocked.Read(ref group.Stats.ArrangeCommands) }));
    }

    // 기능: 무기 이름이나 id의 탄 종류 이름(giveAmmo type)을 고른다(Phase 17: 산탄총·권총·로켓 포함).
    // 입력: weapon - 무기 이름 또는 id 문자열.
    // 출력: QA giveAmmo의 type 이름(모르는 무기는 medium).
    private static string AmmoTypeOf(string weapon) => weapon.ToLowerInvariant() switch
    {
        "kestrel lr" or "2" => "heavy",
        "wisp smg" or "3" => "light",
        "brute sg" or "4" => "shells",
        "sparrow p" or "5" => "light",
        "thunder rl" or "6" => "rockets",
        _ => "medium",
    };

    // 기능: 한 구성원을 무장시키는 Arrange 명령(자리, 무기 0번 칸 선택, 탄, 실드)을 나열한다.
    // 입력: a - 대상 Actor, at - 자리, yaw - 바라볼 방향, weapon - 무기 이름, ammoType - 탄 종류, ammo - 탄 수(0이면 생략), shield - 실드(0이면 생략), position - setPosition을 포함할지.
    // 출력: (명령 이름, DevPlayerId, 인자 객체) 목록.
    private static IEnumerable<(string Command, string? Player, object Args)> ArmCommands(IQaActor a, Vector3 at, float yaw, string weapon, string ammoType, int ammo, int shield, bool position)
    {
        if (position) yield return ("setPosition", a.DevPlayerId, new { x = at.X, z = at.Z, yaw });
        yield return ("giveWeapon", a.DevPlayerId, new { weapon, slot = 0, select = true });
        if (ammo > 0) yield return ("giveAmmo", a.DevPlayerId, new { type = ammoType, amount = ammo });
        if (shield > 0) yield return ("setShield", a.DevPlayerId, new { value = shield });
    }

    // Re-arm (Arrange after a respawn): a member alive with an empty slot 0 gets its weapon and ammo back and, when the
    // group was placed, its spot. Reads published actor state every 250 ms (no server query); sends at most one re-arm
    // per member per RearmCooldown. Every command is counted (group stats, measure phases' arrangeCommands).
    private static readonly TimeSpan RearmCooldown = TimeSpan.FromSeconds(2);
    private const int RefillBelow = 60;

    // 기능: 재무장 작업 본체. 250 ms마다 구성원 상태를 보고 사망을 세며, 살아 있는데 0번 칸이 비면 다시 무장시키고(position이면 자리로 되돌림) 예비 탄이 RefillBelow 아래면 채운다(구성원마다 RearmCooldown에 한 번).
    // 입력: server - QA 서버 Client, runId - Run id, pollMs - 재시도 간격(ms), group - 대상 그룹, spot - 구성원별 자리, facing - 구성원별 방향, position - 자리로 되돌릴지, weapon - 무기 이름, ammoType - 탄 종류, ammo - 탄 수, token - 중단 토큰.
    // 출력: 반환값 없음. 그룹 통계(Deaths, Rearms, Refills, 명령 수)가 갱신되고, 중단 외의 예외로 끝나면 WorkloadFailed가 기록된다.
    private static async Task RearmLoopAsync(IQaServerClient server, string runId, int pollMs, ActorGroup group, Vector3[] spot, float[] facing, bool position,
        string weapon, string ammoType, int ammo, CancellationToken token)
    {
        var last = new long[group.Members.Count];
        var wasAlive = new bool[group.Members.Count];
        var clock = Stopwatch.StartNew();
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                for (int i = 0; i < group.Members.Count; i++)
                {
                    ActorState s = group.Members[i].State;
                    bool alive = s.Joined && s.Alive;
                    if (wasAlive[i] && !alive && s.Joined) Interlocked.Increment(ref group.Stats.Deaths);
                    wasAlive[i] = alive;
                    if (!alive || !s.HasInventory) continue;
                    if (clock.ElapsedMilliseconds - last[i] < RearmCooldown.TotalMilliseconds && last[i] != 0) continue;
                    if (s.WeaponId == 0)
                    {
                        last[i] = clock.ElapsedMilliseconds;
                        Interlocked.Increment(ref group.Stats.Rearms);
                        foreach (var (command, player, args) in ArmCommands(group.Members[i], spot[i], facing[i], weapon, ammoType, ammo, 0, position))
                        {
                            await SendCommandAsync(server, runId, pollMs, group.Stats, command, player, args, token).ConfigureAwait(false);
                        }
                    }
                    else if (ammo > 0 && s.ReserveAmmo < RefillBelow && s.Tool == "Weapon")
                    {
                        // Long runs: the reserve runs low; the same Arrange tops it up (a real game would loot it).
                        last[i] = clock.ElapsedMilliseconds;
                        Interlocked.Increment(ref group.Stats.Refills);
                        await SendCommandAsync(server, runId, pollMs, group.Stats, "giveAmmo", group.Members[i].DevPlayerId, new { type = ammoType, amount = ammo }, token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            group.Stats.WorkloadFailed($"re-arm stopped: {e.GetType().Name}: {e.Message}");
        }
    }

    // 기능: Arrange 명령들을 최대 ArrangeParallel개씩 동시에 보낸다.
    // 입력: run - Run, stats - 명령 수·실패를 집계할 그룹 통계, commands - (명령, DevPlayerId, 인자) 목록, token - 취소 토큰.
    // 출력: 거절되거나 실패한 명령 수.
    // Arrange commands with at most ArrangeParallel in flight (the server runs 16 per tick). Returns how many failed.
    private static async Task<int> CommandsAsync(RunContext run, GroupStats stats, IReadOnlyList<(string Command, string? Player, object Args)> commands, CancellationToken token)
    {
        using var gate = new SemaphoreSlim(ArrangeParallel);
        int failed = 0;
        IQaServerClient server = run.Server;
        Task[] tasks = commands.Select(async c =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!await SendCommandAsync(server, run.RunId, run.PollIntervalMs, stats, c.Command, c.Player, c.Args, token).ConfigureAwait(false)) Interlocked.Increment(ref failed);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return failed;
    }

    // 기능: QA 명령 하나를 보낸다(503·504는 pollMs 간격으로 최대 MaxRetries 재시도). 보낸 명령과 실패는 그룹 통계에 센다.
    // 입력: server - QA 서버 Client, runId - Run id, pollMs - 재시도 간격(ms), stats - 그룹 통계, command - 명령 이름, player - 대상 DevPlayerId, args - 인자 객체, token - 취소 토큰.
    // 출력: 받아들여지면 true, 거절이나 QA API 예외면 false(오류가 통계에 기록된다).
    // One QA command with the same retry as the Arrange steps (503 queue full, 504 not run in time: twice).
    private static async Task<bool> SendCommandAsync(IQaServerClient server, string runId, int pollMs, GroupStats stats, string command, string? player, object args, CancellationToken token)
    {
        JsonElement json = JsonPath.From(args);
        Interlocked.Increment(ref stats.ArrangeCommands);
        for (int attempt = 0; ; attempt++)
        {
            CommandResponse response;
            try
            {
                response = await server.CommandAsync(command, player, json, runId, token).ConfigureAwait(false);
            }
            catch (QaApiException e)
            {
                Interlocked.Increment(ref stats.CommandFailures);
                stats.Error($"{command} {player}: {e.Message}");
                return false;
            }
            if (response.Ok) return true;
            if (response.StatusCode is not (503 or 504) || attempt >= MaxRetries)
            {
                Interlocked.Increment(ref stats.CommandFailures);
                stats.Error($"{command} {player}: {response.Error} (HTTP {response.StatusCode})");
                return false;
            }
            await Task.Delay(pollMs, token).ConfigureAwait(false);
        }
    }

    // §20-23: builders on their own sites. Each member claims the free site nearest the group's centre (in member order),
    // then the next one near where it is whenever it finishes (BuildSitePool, shared by every build group of the run).
    // 기능: 그룹 구성원마다 중심 가까운 건설 터를 잡고(arrange면 자리·자원·recycle 무기 Arrange) Build Brain을 주며, recycle이면 재무장 작업도 시작한다(§20-23).
    // 입력: ctx - 단계 문맥(group, pieces, material, ratePerSecond, arrange, resources, center, role, recycle, weapon, ammo), token - 취소 토큰.
    // 출력: 적용되면 Pass(saveAs: builders, sites, free, shared, rate), 터가 없거나 Arrange 거절·Brain 미적용이면 Fail. ratePerSecond가 범위 밖이면 QaStepException.
    private static async Task<StepOutcome> GroupBuildAsync(StepContext ctx, CancellationToken token)
    {
        RunContext run = ctx.Run;
        ActorGroup group = GroupOf(ctx);
        BuildPieceType[] pieces = Pieces(ctx);
        BuildMaterialType material = Material(ctx);
        float rate = (float)(ctx.Double("ratePerSecond") ?? 1);
        if (!(rate > 0) || rate > 10) throw new QaStepException("'ratePerSecond' must be above 0 and at most 10 (the server places one piece per 0.1 s per player).");
        bool arrange = ctx.Bool("arrange") ?? true;
        int resources = ctx.Int("resources", 0, 500) ?? 500;
        string role = ctx.String("role") ?? (rate >= 4 ? "turbo" : "build");
        bool recycle = ctx.Bool("recycle") ?? false;
        string weapon = ctx.String("weapon") ?? "Vesper AR";
        string ammoType = AmmoTypeOf(weapon);
        int ammo = ctx.Int("ammo", 0, 999) ?? 300;
        BuildSitePool pool = run.Groups.SitePool;
        if (pool.Count == 0) return StepOutcome.Fail("No build site on the map (flat ground clear of map objects).");
        int sharedBefore = pool.Shared;
        Vector2 center = Center(ctx);
        var first = new StressMap.BuildSite[group.Members.Count];
        for (int i = 0; i < first.Length; i++) first[i] = pool.Claim(center)!;
        int shared = pool.Shared - sharedBefore;
        if (arrange)
        {
            var commands = new List<(string, string?, object)>();
            for (int i = 0; i < group.Members.Count; i++)
            {
                IQaActor a = group.Members[i];
                Vector3 stand = first[i].Stand;
                commands.Add(("setPosition", a.DevPlayerId, new { x = stand.X, z = stand.Z, yaw = 0f }));
                if (resources > 0) commands.Add(("giveResource", a.DevPlayerId, new { material = material.ToString().ToLowerInvariant(), amount = resources }));
                if (recycle) commands.AddRange(ArmCommands(a, stand, 0f, weapon, ammoType, ammo, 0, position: false));
            }
            int failed = await CommandsAsync(run, group.Stats, commands, token).ConfigureAwait(false);
            if (failed > 0) return StepOutcome.Fail($"{failed} of {commands.Count} arrange commands for '{group.Name}' were refused (see group stats errors).");
        }
        bool applied = await SetBrainsAsync(ctx, group, (i, _) => new BuildBrain(new BuildBrainPlan(pool, first[i], pieces, material, rate, group.Indices[i], recycle), role), token).ConfigureAwait(false);
        group.Role = role;
        if (!applied) return StepOutcome.Fail($"Not every member of '{group.Name}' took the build behaviour.");
        if (recycle)
        {
            // The weapon that takes the sites down comes back after a death, and its ammo is topped up (Arrange, counted).
            var cts = new CancellationTokenSource();
            var noSpot = new Vector3[group.Members.Count];
            var noYaw = new float[group.Members.Count];
            Task task = Task.Run(() => RearmLoopAsync(run.Server, run.RunId, run.PollIntervalMs, group, noSpot, noYaw, false, weapon, ammoType, ammo, cts.Token));
            run.Groups.Start(new GroupWorkload(group.Name, "rearm", cts, task));
        }
        string sharedText = shared > 0 ? $"; {shared} builder(s) share a site" : string.Empty;
        if (shared > 0) run.Warnings.Add($"Stress: groupBuild '{group.Name}': no free site left for {shared} builder(s); they share one (Occupied results expected).");
        return StepOutcome.Pass($"{group.Members.Count} {role}ing at {rate}/s ({string.Join(", ", pieces).ToLowerInvariant()}), {pool.Free} of {pool.Count} sites still free{sharedText}",
            JsonPath.From(new { builders = group.Members.Count, sites = pool.Count, free = pool.Free, shared, rate }));
    }

    // 기능: pieces 인자를 조각 종류 배열로 읽는다(없으면 벽·바닥·경사로·지붕 전부).
    // 입력: ctx - 단계 문맥(pieces).
    // 출력: 중복을 뺀 BuildPieceType 배열. 배열이 아니거나 모르는 이름이면 QaStepException.
    private static BuildPieceType[] Pieces(StepContext ctx)
    {
        JsonElement? p = ctx.Param("pieces");
        if (p == null) return new[] { BuildPieceType.Wall, BuildPieceType.Floor, BuildPieceType.Ramp, BuildPieceType.Roof };
        if (p.Value.ValueKind != JsonValueKind.Array) throw new QaStepException("'pieces' must be a list.");
        return p.Value.EnumerateArray().Select(e => Enum.TryParse(QaJson.Text(e), true, out BuildPieceType t) ? t : throw new QaStepException($"Unknown piece {e.GetRawText()}.")).Distinct().ToArray();
    }

    // 기능: material 인자를 재료 종류로 읽는다(기본 wood, 숫자 문자열은 거부).
    // 입력: ctx - 단계 문맥(material).
    // 출력: BuildMaterialType. 모르는 이름이면 QaStepException.
    private static BuildMaterialType Material(StepContext ctx)
    {
        string text = ctx.String("material") ?? "wood";
        return Enum.TryParse(text, true, out BuildMaterialType m) && Enum.IsDefined(m) && !int.TryParse(text, out _) ? m : throw new QaStepException($"Unknown material '{text}'.");
    }

    // 기능: 그룹 구성원에게 Loot Brain(탐색 범위, 몇 번 줍고 버릴지)을 준다.
    // 입력: ctx - 단계 문맥(group, searchRange, dropEvery), token - 취소 토큰.
    // 출력: 모두 적용되면 Pass, 아니면 Fail.
    private static async Task<StepOutcome> GroupLootAsync(StepContext ctx, CancellationToken token)
    {
        ActorGroup group = GroupOf(ctx);
        float range = (float)(ctx.Double("searchRange") ?? 40);
        int dropEvery = ctx.Int("dropEvery", 0, 100) ?? 3;
        int seed = ctx.Run.Seed;
        bool applied = await SetBrainsAsync(ctx, group, (i, _) => new LootBrain(new LootPlan(range, dropEvery, group.Indices[i], seed, group.Stats)), token).ConfigureAwait(false);
        group.Role = "loot";
        return applied ? StepOutcome.Pass($"{group.Members.Count} looting (search {range} m, drop every {dropEvery} pickups)") : StepOutcome.Fail($"Not every member of '{group.Name}' took the loot behaviour.");
    }

    // §51-52: each member changes role every switchSeconds (a seeded weighted pick per period). Builders get their own
    // sites and, when arranged, resources up front.
    // 기능: 그룹 구성원에게 switchSeconds마다 roles의 비율로 역할을 바꾸는 Role Brain을 준다(건설 역할이 있으면 자원을 먼저 Arrange)(§51-52).
    // 입력: ctx - 단계 문맥(group, roles, switchSeconds, center, radius, ratePerSecond, turboRatePerSecond, resources, material), token - 취소 토큰.
    // 출력: 모두 적용되면 Pass, 건설 터 없음·giveResource 거절·Brain 미적용이면 Fail. roles 형식이 틀리면 QaStepException.
    private static async Task<StepOutcome> GroupRolesAsync(StepContext ctx, CancellationToken token)
    {
        RunContext run = ctx.Run;
        ActorGroup group = GroupOf(ctx);
        JsonElement rolesJson = ctx.Param("roles") ?? throw new QaStepException("'roles' is required.");
        var roles = new List<RoleShare>();
        foreach (JsonElement e in rolesJson.EnumerateArray())
        {
            string role = JsonPath.Child(e, "role") is { ValueKind: JsonValueKind.String } r ? r.GetString()!.ToLowerInvariant() : throw new QaStepException("Each role needs 'role'.");
            if (!Roles.Contains(role)) throw new QaStepException($"Unknown role '{role}' ({string.Join(", ", Roles)}).");
            double weight = JsonPath.Child(e, "percent") is JsonElement pc && Comparison.TryNumber(pc, out double d) ? d : throw new QaStepException($"Role '{role}' needs 'percent'.");
            if (weight > 0) roles.Add(new RoleShare(role, weight));
        }
        if (roles.Count == 0) throw new QaStepException("'roles' has no role with a percentage above 0.");
        float switchSeconds = (float)(ctx.Double("switchSeconds") ?? 20);
        Vector2 center = Center(ctx);
        float radius = (float)(ctx.Double("radius") ?? 60);
        float rate = (float)(ctx.Double("ratePerSecond") ?? 1);
        float turbo = (float)(ctx.Double("turboRatePerSecond") ?? 6);
        BuildMaterialType material = Material(ctx);
        int resources = ctx.Int("resources", 0, 500) ?? 500;
        bool builds = roles.Any(r => r.Role is "build" or "turbo");
        BuildSitePool pool = run.Groups.SitePool;
        if (builds)
        {
            if (pool.Count == 0) return StepOutcome.Fail("No build site on the map for the build roles.");
            if (resources > 0)
            {
                var commands = group.Members.Select(a => ("giveResource", (string?)a.DevPlayerId, (object)new { material = material.ToString().ToLowerInvariant(), amount = resources })).ToList();
                int failed = await CommandsAsync(run, group.Stats, commands, token).ConfigureAwait(false);
                if (failed > 0) return StepOutcome.Fail($"{failed} giveResource commands for '{group.Name}' were refused.");
            }
        }
        int seed = run.Seed;
        int n = group.Members.Count;
        bool applied = await SetBrainsAsync(ctx, group, (i, _) =>
        {
            int index = group.Indices[i];
            return new RoleBrain(roles, switchSeconds, seed, index, role => role switch
            {
                "move" => new MoveBrain(new MovePlan(PatternFor("mixed", i), center, radius, false, 0, i, n, seed), "move"),
                "sprint" => new MoveBrain(new MovePlan(MovePattern.Random, center, radius, true, 0, i, n, seed), "sprint"),
                "jump" => new MoveBrain(new MovePlan(MovePattern.Random, center, radius, false, 3, i, n, seed), "jump"),
                "loot" => new LootBrain(new LootPlan(40, 3, index, seed, group.Stats)),
                "build" => new BuildBrain(new BuildBrainPlan(pool, null, StressMap.AllPieces, material, rate, index), "build"),
                "turbo" => new BuildBrain(new BuildBrainPlan(pool, null, StressMap.AllPieces, material, turbo, index), "turbo"),
                _ => new IdleBrain(),
            });
        }, token).ConfigureAwait(false);
        group.Role = "roles";
        string text = string.Join(", ", roles.Select(r => $"{r.Role} {r.Weight}%"));
        return applied ? StepOutcome.Pass($"{n} with switching roles ({text}; every {switchSeconds} s)") : StepOutcome.Fail($"Not every member of '{group.Name}' took the role behaviour.");
    }

    // §36-38: every cycle, percent of the group (a seeded pick) disconnects (graceful, drop or both halves), waits until
    // the server no longer counts the old connection (one /qa/players query per poll, not one per actor), and
    // reconnects with the same DevPlayerId. A background workload: measure runs alongside; stopGroup or cleanup ends it.
    // 기능: 그룹의 이전 Churn 작업을 멈추고 새 Churn 작업(주기마다 percent만큼 끊고 재접속)을 Background로 시작한다(§36-38).
    // 입력: ctx - 단계 문맥(group, percent, cycleSeconds, cycles, mode, offlineMs, reconnectTimeoutMs), token - 취소 토큰.
    // 출력: 작업이 시작되면 Pass. 인자가 범위 밖이거나 Proxy를 쓰는 구성원이 있으면 QaStepException.
    private static async Task<StepOutcome> GroupChurnAsync(StepContext ctx, CancellationToken token)
    {
        RunContext run = ctx.Run;
        ActorGroup group = GroupOf(ctx);
        double percent = ctx.Double("percent") ?? 15;
        if (percent <= 0 || percent > 100) throw new QaStepException("'percent' must be above 0 and at most 100.");
        double cycleSeconds = ctx.Double("cycleSeconds") ?? 8;
        if (cycleSeconds < 1 || cycleSeconds > 3600) throw new QaStepException("'cycleSeconds' must be 1-3600.");
        int cycles = ctx.Int("cycles", 1, 100_000) ?? 20;
        string mode = (ctx.String("mode") ?? "graceful").ToLowerInvariant();
        int offlineMs = ctx.Int("offlineMs", 0, 60_000) ?? 500;
        int reconnectTimeoutMs = ctx.Int("reconnectTimeoutMs", 1000, 120_000) ?? 15_000;
        IQaActor? proxied = group.Members.FirstOrDefault(m => run.Network.IsProxied(m.Alias));
        if (proxied != null) throw new QaStepException($"groupChurn does not reconnect proxied actors ({proxied.Alias}): churn a group without 'proxy'.");
        await run.Groups.StopAsync(group.Name, GroupRegistry.StopTimeout, kind: "churn").ConfigureAwait(false);
        var churn = new ChurnPlan(run.Server, run.GameHost, run.GamePort, run.Seed, percent, cycleSeconds, cycles, mode, offlineMs, reconnectTimeoutMs, run.PollIntervalMs);
        var cts = new CancellationTokenSource();
        Task task = Task.Run(() => ChurnLoopAsync(churn, group, cts.Token));
        run.Groups.Start(new GroupWorkload(group.Name, "churn", cts, task));
        int per = Math.Max(1, (int)Math.Round(group.Members.Count * percent / 100.0));
        return StepOutcome.Pass($"churn started: {per} of {group.Members.Count} per cycle, {cycles} cycles of {cycleSeconds} s, {mode}");
    }

    private sealed record ChurnPlan(IQaServerClient Server, string Host, int Port, int Seed, double Percent, double CycleSeconds, int Cycles, string Mode,
        int OfflineMs, int ReconnectTimeoutMs, int PollMs);

    // 기능: Churn 작업 본체. 주기마다 Seed로 고른 구성원을 끊고(graceful/drop/mixed), 서버가 옛 연결을 잊을 때까지 기다린 뒤 같은 DevPlayerId로 재접속시켜 재접속 시간·실패·중복 등록을 센다.
    // 입력: plan - Churn 설정, group - 대상 그룹, token - 중단 토큰.
    // 출력: 반환값 없음. 그룹 통계가 갱신되고, 어떻게 끝나든 보내둔 구성원은 BringBackAsync로 되돌린다. 중단 외의 예외면 WorkloadFailed가 기록된다.
    private static async Task ChurnLoopAsync(ChurnPlan plan, ActorGroup group, CancellationToken token)
    {
        GroupStats stats = group.Stats;
        IQaActor[] away = Array.Empty<IQaActor>();
        try
        {
            for (int cycle = 0; cycle < plan.Cycles && !token.IsCancellationRequested; cycle++)
            {
                var clock = Stopwatch.StartNew();
                IQaActor[] joined = group.Members.Where(m => m.State.Joined).ToArray();
                int count = Math.Min(joined.Length, Math.Max(1, (int)Math.Round(group.Members.Count * plan.Percent / 100.0)));
                var rng = new Random(ActorBrain.Seed(plan.Seed, cycle, 77));
                IQaActor[] picked = joined.OrderBy(_ => rng.Next()).Take(count).ToArray();
                away = picked;
                for (int i = 0; i < picked.Length; i++)
                {
                    bool graceful = plan.Mode == "graceful" || (plan.Mode == "mixed" && i % 2 == 0);
                    await picked[i].SendAsync(new DisconnectCommand(graceful), token).ConfigureAwait(false);
                    Interlocked.Increment(ref stats.Disconnects);
                }
                await WaitAsync(() => picked.All(a => a.State.Status is ActorStatus.Disconnected or ActorStatus.Idle), 3000, token).ConfigureAwait(false);
                if (plan.OfflineMs > 0) await Task.Delay(plan.OfflineMs, token).ConfigureAwait(false);
                // Like `reconnect`: a connect while the server still has the old peer would join as a new player.
                var ids = new HashSet<string>(picked.Select(a => a.DevPlayerId), StringComparer.Ordinal);
                var gone = Stopwatch.StartNew();
                while (gone.ElapsedMilliseconds < plan.ReconnectTimeoutMs)
                {
                    JsonElement players = await PlayersAsync(plan.Server, token).ConfigureAwait(false);
                    if (!ConnectedIds(players).Any(ids.Contains)) break;
                    await Task.Delay(Math.Max(100, plan.PollMs), token).ConfigureAwait(false);
                }
                var before = picked.Select(a => a.State.Connections).ToArray();
                var started = Stopwatch.StartNew();
                foreach (IQaActor a in picked) await a.SendAsync(new ConnectCommand(plan.Host, plan.Port, true), token).ConfigureAwait(false);
                var doneAt = new long[picked.Length];
                await WaitAsync(() =>
                {
                    bool all = true;
                    for (int i = 0; i < picked.Length; i++)
                    {
                        ActorState s = picked[i].State;
                        if (doneAt[i] == 0 && s.Connections > before[i] && ((s.Joined && s.HasSnapshot) || s.Disconnected)) doneAt[i] = Math.Max(1, started.ElapsedMilliseconds);
                        all &= doneAt[i] != 0;
                    }
                    return all;
                }, plan.ReconnectTimeoutMs, token).ConfigureAwait(false);
                for (int i = 0; i < picked.Length; i++)
                {
                    ActorState s = picked[i].State;
                    if (s.Joined && s.Connections > before[i])
                    {
                        Interlocked.Increment(ref stats.Reconnects);
                        Interlocked.Add(ref stats.ReconnectMsTotal, doneAt[i]);
                        stats.Max(ref stats.ReconnectMsMax, doneAt[i]);
                    }
                    else
                    {
                        Interlocked.Increment(ref stats.ReconnectFailures);
                        stats.Error($"cycle {cycle + 1}: {picked[i].Alias} did not rejoin within {plan.ReconnectTimeoutMs} ms ({FlowActions.Describe(s)})");
                    }
                }
                away = Array.Empty<IQaActor>();
                JsonElement after = await PlayersAsync(plan.Server, token).ConfigureAwait(false);
                foreach (var dup in AllIds(after).Where(ids.Contains).GroupBy(x => x).Where(g => g.Count() > 1))
                {
                    Interlocked.Increment(ref stats.Duplicates);
                    stats.Error($"cycle {cycle + 1}: the server lists {dup.Key} {dup.Count()} times");
                }
                Interlocked.Increment(ref stats.ChurnCycles);
                double left = plan.CycleSeconds * 1000 - clock.ElapsedMilliseconds;
                if (left > 0) await Task.Delay(TimeSpan.FromMilliseconds(left), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stopped (stopGroup, cleanup): not a failure.
        }
        catch (Exception e)
        {
            // The QA API failed beyond its retries (or anything else): the churn ends and says so (stopGroup fails).
            stats.WorkloadFailed($"churn stopped: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            // Every exit path: whoever a cycle sent away comes back, so the group is whole again while measuring goes on.
            await BringBackAsync(plan, away, stats).ConfigureAwait(false);
        }
    }

    // /qa/players with the same bounded retry as the metrics: 503 / 504 and a request that timed out on its own (not the
    // workload's cancellation) are tried again twice, half a second apart.
    // 기능: /qa/players를 읽는다(503·504·연결 실패·요청 자체 Timeout은 0.5초 간격으로 최대 MaxRetries 재시도, 작업 취소는 재시도하지 않음).
    // 입력: server - QA 서버 Client, token - 취소 토큰.
    // 출력: 플레이어 목록 JSON. 재시도 뒤에도 실패면 예외.
    internal static async Task<JsonElement> PlayersAsync(IQaServerClient server, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await server.GetPlayersAsync(token).ConfigureAwait(false);
            }
            catch (Exception e) when (attempt < MaxRetries && !token.IsCancellationRequested
                                      && (e is QaApiException { StatusCode: 503 or 504 or 0 } || e is OperationCanceledException))
            {
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
    }

    // Bounded (BringBackTimeout, inside GroupRegistry.StopTimeout): reconnects the actors a stopped cycle left away. Longer
    // than the server's default DisconnectTimeoutMs (5 s) plus a QA request (5 s at most): an actor dropped without a
    // disconnect packet just before the stop is still "connected" on the server until that timeout.
    public static readonly TimeSpan BringBackTimeout = TimeSpan.FromSeconds(8);

    // 기능: 멈춘 Churn 주기가 끊어 둔 구성원을 BringBackTimeout 안에 재접속시킨다(서버가 옛 연결을 잊을 때까지 먼저 기다림).
    // 입력: plan - Churn 설정, away - 주기가 끊어 둔 Actor들, stats - 그룹 통계.
    // 출력: 반환값 없음. 시간 안에 돌아오지 못한 Actor가 있으면 오류와 WorkloadFailed가 기록된다.
    private static async Task BringBackAsync(ChurnPlan plan, IQaActor[] away, GroupStats stats)
    {
        IQaActor[] gone = away.Where(a => !a.State.Joined).ToArray();
        if (gone.Length == 0) return;
        using var cts = new CancellationTokenSource(BringBackTimeout);
        try
        {
            var ids = new HashSet<string>(gone.Select(a => a.DevPlayerId), StringComparer.Ordinal);
            try
            {
                while (ConnectedIds(await plan.Server.GetPlayersAsync(cts.Token).ConfigureAwait(false)).Any(ids.Contains))
                    await Task.Delay(100, cts.Token).ConfigureAwait(false);
            }
            catch (QaApiException)
            {
                // The server does not answer the check: reconnect anyway (the cycle already waited for the old peers once).
            }
            foreach (IQaActor a in gone) await a.SendAsync(new ConnectCommand(plan.Host, plan.Port, true), cts.Token).ConfigureAwait(false);
            await WaitAsync(() => gone.All(a => a.State.Joined), (int)BringBackTimeout.TotalMilliseconds, cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or QaApiException)
        {
            stats.Error($"stop: {gone.Count(a => !a.State.Joined)} churned actor(s) not back in time ({e.GetType().Name})");
        }
        if (gone.Any(a => !a.State.Joined)) stats.WorkloadFailed($"churn: {gone.Count(a => !a.State.Joined)} actor(s) sent away were not back within {BringBackTimeout.TotalSeconds:0} s");
    }

    // 기능: /qa/players 응답의 모든 devPlayerId를 나열한다.
    // 입력: players - /qa/players 응답.
    // 출력: devPlayerId 목록(배열이 아니면 빈 목록).
    private static IEnumerable<string> AllIds(JsonElement players)
    {
        if (players.ValueKind != JsonValueKind.Array) yield break;
        foreach (JsonElement p in players.EnumerateArray())
            if (JsonPath.Child(p, "devPlayerId") is { ValueKind: JsonValueKind.String } id) yield return id.GetString()!;
    }

    // 기능: /qa/players 응답에서 connected가 true인 플레이어의 devPlayerId만 나열한다.
    // 입력: players - /qa/players 응답.
    // 출력: 연결 중인 devPlayerId 목록(배열이 아니면 빈 목록).
    private static IEnumerable<string> ConnectedIds(JsonElement players)
    {
        if (players.ValueKind != JsonValueKind.Array) yield break;
        foreach (JsonElement p in players.EnumerateArray())
        {
            if (JsonPath.Child(p, "connected") is { ValueKind: JsonValueKind.True } && JsonPath.Child(p, "devPlayerId") is { ValueKind: JsonValueKind.String } id)
                yield return id.GetString()!;
        }
    }

    // 기능: 조건이 성립할 때까지 ActorPollMs 간격으로 최대 timeoutMs 기다린다.
    // 입력: condition - 검사할 조건, timeoutMs - 한도(ms), token - 취소 토큰.
    // 출력: 조건이 성립하면 true, 한도까지 성립하지 않으면 false.
    private static async Task<bool> WaitAsync(Func<bool> condition, int timeoutMs, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (condition()) return true;
            if (clock.ElapsedMilliseconds >= timeoutMs) return false;
            await Task.Delay(StepContext.ActorPollMs, token).ConfigureAwait(false);
        }
    }

    // 기능: 그룹 모든 구성원의 Proxy에 같은 네트워크 장애(지연·Jitter·손실·중복)를 direction 방향으로 설정한다(§45, D38).
    // 입력: ctx - 단계 문맥(group, latencyMs, jitterMs, lossPercent, duplicatePercent, direction), token - 취소 토큰(사용 안 함).
    // 출력: 설정 내용을 담은 Pass. Proxy 없는 구성원·모르는 방향·범위 밖 값이면 QaStepException.
    // §45, D38: the same network fault on every member's proxy (the group was made with "proxy": true).
    private static Task<StepOutcome> GroupNetworkFaultAsync(StepContext ctx, CancellationToken token)
    {
        RunContext run = ctx.Run;
        ActorGroup group = GroupOf(ctx);
        string? directionText = ctx.String("direction");
        Faults.FaultDirection direction = directionText == null ? Faults.FaultDirection.Both
            : FaultActions.TryDirection(directionText, out Faults.FaultDirection d) ? d : throw new QaStepException("'direction' must be toServer, toClient or both.");
        var settings = new Faults.NetworkFaultSettings
        {
            LatencyMs = ctx.Int("latencyMs", 0, Faults.NetworkFaultSettings.MaxDelayMs) ?? 0,
            JitterMs = ctx.Int("jitterMs", 0, Faults.NetworkFaultSettings.MaxDelayMs) ?? 0,
            PacketLossPercent = ctx.Double("lossPercent") ?? 0,
            DuplicatePercent = ctx.Double("duplicatePercent") ?? 0,
        };
        IQaActor? plain = group.Members.FirstOrDefault(m => !run.Network.IsProxied(m.Alias));
        if (plain != null) throw new QaStepException($"{plain.Alias} has no network proxy: make '{group.Name}' with \"proxy\": true in actorGroup (before connectAll).");
        try
        {
            foreach (IQaActor m in group.Members)
            {
                if (direction != Faults.FaultDirection.ToClient) run.Network.SetFaults(m.Alias, settings, Faults.FaultDirection.ToServer);
                if (direction != Faults.FaultDirection.ToServer) run.Network.SetFaults(m.Alias, settings, Faults.FaultDirection.ToClient);
                Interlocked.Increment(ref group.Stats.FaultsSet);
            }
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new QaStepException(e.Message);
        }
        string text = $"{group.Members.Count} actors {direction}: latency {settings.LatencyMs} ms, jitter {settings.JitterMs} ms, loss {settings.PacketLossPercent}%, duplicate {settings.DuplicatePercent}%";
        run.Log($"network fault group {group.Name}: {text}");
        return Task.FromResult(StepOutcome.Pass(text));
    }

    // Ends a group's workloads (bounded wait) and behaviours; the members stand still, still sending input.
    // group "all": every group. saveAs: the group(s) stats.
    // 기능: 그룹("all"이면 모든 그룹)의 Background 작업을 멈추고 구성원의 행동·의도를 초기화한다.
    // 입력: ctx - 단계 문맥(group), token - 취소 토큰.
    // 출력: 작업이 시간 안에 멈추고 오류로 끝난 작업이 없으면 Pass, 아니면 Fail. saveAs: 그룹 통계(여러 그룹이면 이름별).
    private static async Task<StepOutcome> StopGroupAsync(StepContext ctx, CancellationToken token)
    {
        RunContext run = ctx.Run;
        string name = ctx.RequireString("group");
        ActorGroup[] groups = name == "all" ? run.Groups.All.ToArray() : new[] { run.Groups.Get(name) };
        // All the groups' workloads stop together, so the wait is one StopTimeout whatever the group count.
        bool stopped = await run.Groups.StopAsync(name == "all" ? null : name, GroupRegistry.StopTimeout).ConfigureAwait(false);
        foreach (ActorGroup g in groups)
        {
            foreach (IQaActor m in g.Members) await m.SendAsync(new ResetIntentCommand(), token).ConfigureAwait(false);
            g.Role = null;
        }
        object stats = groups.Length == 1 ? JsonPath.Get(run.Groups.Describe(groups[0].Name), "stats")!.Value
            : groups.ToDictionary(g => g.Name, g => (object)JsonPath.Get(run.Groups.Describe(g.Name), "stats")!.Value);
        if (!stopped) return StepOutcome.Fail($"A workload of {name} did not stop within {GroupRegistry.StopTimeout.TotalSeconds:0} s.", "stopped", "still running", JsonPath.From(stats));
        // A workload that ended on an error (not by this stop) means the group did not do its work for part of the run.
        ActorGroup[] failed = groups.Where(g => Interlocked.Read(ref g.Stats.WorkloadFailures) > 0).ToArray();
        if (failed.Length > 0)
            return StepOutcome.Fail($"A workload of {string.Join(", ", failed.Select(g => g.Name))} ended with an error: {string.Join("; ", failed.Select(g => g.Stats.LastFailure))}",
                "workloads ran until stopped", "ended early", JsonPath.From(stats));
        return StepOutcome.Pass($"{groups.Length} group(s) stopped", JsonPath.From(stats));
    }
}
