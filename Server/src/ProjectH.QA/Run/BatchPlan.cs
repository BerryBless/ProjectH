using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ProjectH.QA;

// QA-5 D31-D32: what one `run` of one scenario file means when it has parameter sets, --repeat or --seed-sweep. Runs
// are sequential (never parallel: a stress run must not share the machine with another run). The CLI and the UI
// share this planner and BatchSummary, so both report the same way.
public sealed class BatchOptions
{
    public const int MaxRepeat = 1000;
    public const int MaxSweepSeeds = 1000;

    public int Repeat { get; init; } = 1;
    // Inclusive seed range (--seed-sweep A..B); null = no sweep.
    public (int From, int To)? SeedSweep { get; init; }
    // 1-based parameter set to run alone (--parameter-set N, the reproduce command); null = all sets.
    public int? ParameterSet { get; init; }
    public bool StopOnFail { get; init; }

    // 기능: --seed-sweep 값 "A..B"를 시드 범위로 파싱한다.
    // 입력: text - "A..B" 문자열, range - 파싱된 범위를 받을 변수, error - 실패 이유를 받을 문자열.
    // 출력: 형식이 맞고 A <= B이며 MaxSweepSeeds 이하면 true, 아니면 false와 error.
    // "A..B" (inclusive, A <= B, at most MaxSweepSeeds seeds). Negative seeds are allowed ("-3..3").
    public static bool TryParseSweep(string text, out (int From, int To) range, out string? error)
    {
        range = default;
        error = null;
        int dots = text.IndexOf("..", StringComparison.Ordinal);
        if (dots <= 0
            || !int.TryParse(text[..dots], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int from)
            || !int.TryParse(text[(dots + 2)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int to))
        {
            error = "--seed-sweep must be A..B (integers, e.g. 1..100).";
            return false;
        }
        if (to < from)
        {
            error = "--seed-sweep A..B needs A <= B.";
            return false;
        }
        if ((long)to - from + 1 > MaxSweepSeeds)
        {
            error = $"--seed-sweep covers at most {MaxSweepSeeds} seeds.";
            return false;
        }
        range = (from, to);
        return true;
    }

    // 기능: 배치 옵션의 값 범위와 조합(--repeat와 --seed-sweep 동시 사용 금지)을 검사한다.
    // 입력: 없음.
    // 출력: 문제없으면 null, 아니면 오류 메시지.
    // Null when the options can go together, else why not.
    public string? Check()
    {
        if (Repeat < 1 || Repeat > MaxRepeat) return $"--repeat must be 1-{MaxRepeat}.";
        if (SeedSweep != null && Repeat > 1) return "Use --repeat or --seed-sweep, not both.";
        if (ParameterSet is int p && (p < 1 || p > ScenarioDefinition.MaxParameterSets)) return $"--parameter-set must be 1-{ScenarioDefinition.MaxParameterSets}.";
        return null;
    }
}

// Iteration: 1-based repeat number with --repeat N > 1, else 0. Seed: the sweep's seed, else the fixed seed (--seed, then the
// scenario seed), else null = a new random seed for this run. ParameterIndex: 0-based set, null = no parameters.
public sealed record PlannedRun(int Number, int Total, int? ParameterIndex, JsonElement? Parameters, int Iteration, int? Seed)
{
    public string ParameterLabel => ParameterIndex is int i ? $"parameters[{i + 1}] {BatchSummary.Compact(Parameters)}" : string.Empty;
}

public static class BatchPlanner
{
    // 기능: 배치가 만들 실행 수(파라미터 세트 수 × 세트당 실행 수)를 센다.
    // 입력: s - 시나리오 정의, o - 배치 옵션.
    // 출력: 총 실행 수.
    public static int Count(ScenarioDefinition s, BatchOptions o) => Sets(s, o).Count * PerSet(o);

    // 기능: 파라미터 세트마다 반복 또는 시드 범위만큼의 실행을 순서대로 지연 생성한다.
    // 입력: s - 시나리오 정의, o - 배치 옵션, seedOverride - --seed 값(없으면 시나리오 시드, 그것도 없으면 null = 실행마다 새 난수 시드).
    // 출력: 번호·총수·파라미터 세트·반복 회차·시드가 정해진 PlannedRun 열거.
    // The runs in order: parameter set by parameter set, each with its iterations or seeds. Lazy (a full sweep with
    // 100 parameter sets is 100,000 runs; nothing per run is kept here).
    public static IEnumerable<PlannedRun> Plan(ScenarioDefinition s, BatchOptions o, int? seedOverride)
    {
        List<int?> sets = Sets(s, o);
        int perSet = PerSet(o);
        int total = sets.Count * perSet;
        int number = 0;
        foreach (int? set in sets)
        {
            JsonElement? parameters = set is int i ? s.Parameters[i] : null;
            for (int k = 0; k < perSet; k++)
            {
                int? seed = o.SeedSweep is { } sweep ? sweep.From + k : seedOverride ?? s.Seed;
                yield return new PlannedRun(++number, total, set, parameters, o.SeedSweep == null && perSet > 1 ? k + 1 : 0, seed);
            }
        }
    }

    // 기능: --parameter-set 번호가 이 시나리오의 파라미터 세트 수 안에 드는지 검사한다.
    // 입력: s - 시나리오 정의, o - 배치 옵션.
    // 출력: 문제없으면(또는 지정이 없으면) null, 아니면 오류 메시지.
    // Null when the parameter-set choice fits this scenario, else why not.
    public static string? CheckFor(ScenarioDefinition s, BatchOptions o)
    {
        if (o.ParameterSet is not int p) return null;
        if (s.Parameters.Count == 0) return $"--parameter-set {p}: '{s.Name}' has no parameters.";
        if (p > s.Parameters.Count) return $"--parameter-set {p}: '{s.Name}' has {s.Parameters.Count} parameter sets.";
        return null;
    }

    // 기능: 실행할 파라미터 세트의 0-based 인덱스 목록을 정한다.
    // 입력: s - 시나리오 정의, o - 배치 옵션.
    // 출력: 파라미터가 없으면 [null], --parameter-set이 유효하면 그 하나, 아니면 모든 세트.
    private static List<int?> Sets(ScenarioDefinition s, BatchOptions o)
    {
        if (s.Parameters.Count == 0) return new List<int?> { null };
        if (o.ParameterSet is int p && p >= 1 && p <= s.Parameters.Count) return new List<int?> { p - 1 };
        return Enumerable.Range(0, s.Parameters.Count).Select(i => (int?)i).ToList();
    }

    // 기능: 파라미터 세트 하나당 실행 수를 정한다.
    // 입력: o - 배치 옵션.
    // 출력: 시드 범위가 있으면 시드 개수, 아니면 반복 횟수(최소 1).
    private static int PerSet(BatchOptions o) => o.SeedSweep is { } sweep ? sweep.To - sweep.From + 1 : Math.Max(1, o.Repeat);
}

// The end-of-batch summary (D31 per parameter set, D32 failing seeds / iterations and how to reproduce one). Bounded:
// one counter row per parameter set (at most 100) and at most MaxFailureRows failure rows (the rest are counted).
public sealed class BatchSummary
{
    public const int MaxFailureRows = 200;
    public const int MaxPrintedFailures = 50;

    private readonly string _scenario;
    private readonly string _reproduceFile;
    private readonly SortedDictionary<int, Counts> _perSet = new();
    private readonly List<Row> _failures = new();
    private int _failuresDropped;

    // 기능: 한 시나리오 파일의 배치 요약을 만든다.
    // 입력: scenarioName - 시나리오 이름, reproduceFile - 재현 명령에 넣을 파일 경로('/' 구분 상대 경로).
    // 출력: 실행이 하나도 기록되지 않은 요약.
    public BatchSummary(string scenarioName, string reproduceFile)
    {
        _scenario = scenarioName;
        _reproduceFile = reproduceFile;
    }

    public int Runs { get; private set; }
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public int Skipped { get; private set; }
    public int Errors { get; private set; }
    public int ExitCode { get; private set; }
    public bool Stopped { get; set; }

    public sealed record Row(PlannedRun Run, int Seed, RunStatus Status, string RunId, string? Where);

    // Stress D40: one row per run that measured something (bounded like the failures), for the comparison table.
    public sealed record StressRow(int Number, int? ParameterSet, string? Parameters, int Seed, string Status, string RunId, StressSummary Summary);

    public const int MaxStressRows = 200;
    private readonly List<StressRow> _stress = new();
    public IReadOnlyList<StressRow> StressRows => _stress;

    private sealed class Counts
    {
        public string Label = string.Empty;
        public int Runs, Passed, NotPassed;
    }

    // 기능: 실행 결과 하나를 집계한다(상태 카운터, 종료 코드, 파라미터 세트별 집계, 스트레스 행, 실패 행 - 상한을 넘으면 개수만 센다).
    // 입력: planned - 계획된 실행, report - 그 실행의 보고서.
    // 출력: 반환값 없음. 카운터와 행 목록이 갱신된다.
    public void Add(PlannedRun planned, RunReport report)
    {
        Runs++;
        ExitCode = Math.Max(ExitCode, report.ExitCode);
        switch (report.Status)
        {
            case RunStatus.Passed: Passed++; break;
            case RunStatus.Skipped: Skipped++; break;
            case RunStatus.Error: Errors++; break;
            default: Failed++; break;
        }
        bool bad = report.Status is RunStatus.Failed or RunStatus.Error or RunStatus.Cancelled;
        if (report.Stress?.Summary is StressSummary ss && _stress.Count < MaxStressRows)
            _stress.Add(new StressRow(planned.Number, planned.ParameterIndex + 1, planned.ParameterIndex != null ? Compact(report.Parameters ?? planned.Parameters) : (report.Overrides != null ? Compact(report.Overrides) : null),
                report.Seed, report.Status.ToString().ToUpperInvariant(), report.RunId, ss));
        if (planned.ParameterIndex is int i)
        {
            if (!_perSet.TryGetValue(i, out Counts? c)) _perSet[i] = c = new Counts { Label = planned.ParameterLabel };
            c.Runs++;
            if (report.Status == RunStatus.Passed) c.Passed++;
            else c.NotPassed++;
        }
        if (!bad) return;
        if (_failures.Count >= MaxFailureRows)
        {
            _failuresDropped++;
            return;
        }
        string? where = report.Failure != null ? $"step {report.Failure.StepIndex + 1:00} ({report.Failure.StepId})" : report.ToolError != null ? JsonPath.Truncate(report.ToolError, 120) : null;
        _failures.Add(new Row(planned, report.Seed, report.Status, report.RunId, where));
    }

    public IReadOnlyList<Row> Failures => _failures;

    // 기능: 실패한 실행 하나를 그대로 다시 돌리는 CLI 명령을 만든다.
    // 입력: row - 실패 행.
    // 출력: `run <file> --seed S [--parameter-set N]` 명령 문자열.
    // `run <file> --seed S [--parameter-set N]`: the exact run again.
    public string ReproduceCommand(Row row) =>
        $"dotnet run --project Server/src/ProjectH.QA -- run {_reproduceFile} --seed {row.Seed.ToString(CultureInfo.InvariantCulture)}"
        + (row.Run.ParameterIndex is int i ? $" --parameter-set {i + 1}" : string.Empty);

    // 기능: 배치 요약을 콘솔 줄로 만든다(합계, 파라미터 세트별 집계, 실패 시드·회차, 실패 행 최대 MaxPrintedFailures개, 재현 명령).
    // 입력: 없음.
    // 출력: 출력할 줄의 지연 열거.
    public IEnumerable<string> Lines()
    {
        yield return $"== Batch {_scenario}: {Runs} runs: {Passed} passed, {Failed} failed, {Skipped} skipped, {Errors} errors{(Stopped ? " (stopped early)" : string.Empty)}; exit code {ExitCode}.";
        foreach (Counts c in _perSet.Values)
            yield return $"   {c.Label}: {c.Runs} run{(c.Runs == 1 ? "" : "s")}, {c.Passed} passed, {c.NotPassed} not passed";
        if (_failures.Count == 0) yield break;
        var seeds = _failures.Select(f => f.Seed).Distinct().Take(100).Select(x => x.ToString(CultureInfo.InvariantCulture));
        yield return $"   failing seeds: {string.Join(", ", seeds)}";
        if (_failures.Any(f => f.Run.Iteration > 0))
            yield return $"   failing iterations: {string.Join(", ", _failures.Where(f => f.Run.Iteration > 0).Take(100).Select(f => f.Run.Iteration.ToString(CultureInfo.InvariantCulture)))}";
        foreach (Row f in _failures.Take(MaxPrintedFailures))
        {
            var sb = new StringBuilder($"   run {f.Run.Number}/{f.Run.Total}");
            if (f.Run.Iteration > 0) sb.Append($" iteration {f.Run.Iteration}");
            if (f.Run.ParameterIndex != null) sb.Append(' ').Append(f.Run.ParameterLabel);
            sb.Append($" seed {f.Seed} {f.Status.ToString().ToUpperInvariant()}");
            if (f.Where != null) sb.Append(" at ").Append(f.Where);
            sb.Append($"  runId {f.RunId}");
            yield return sb.ToString();
        }
        int more = _failures.Count - Math.Min(_failures.Count, MaxPrintedFailures) + _failuresDropped;
        if (more > 0) yield return $"   ... {more} more failing runs (see QA/Reports)";
        yield return $"   reproduce: {ReproduceCommand(_failures[0])}";
    }

    // 기능: 측정이 있던 실행들의 스트레스 비교표를 콘솔 줄(헤더 + 실행당 한 줄)로 만든다.
    // 입력: 없음.
    // 출력: 출력할 줄의 지연 열거. 스트레스 행이 없으면 비어 있다.
    // D40 (request §80, §104): the player-count comparison, one line per run (console).
    public IEnumerable<string> StressLines()
    {
        if (_stress.Count == 0) yield break;
        yield return "   Stress comparison (judged phase of each run; tick ms, CPU % of all cores, MB, KB/s):";
        yield return "   " + string.Join(" | ", StressColumns.Select(c => c.Header));
        foreach (StressRow r in _stress) yield return "   " + string.Join(" | ", StressColumns.Select(c => c.Value(r)));
    }

    // The table's columns, shared by the console, summary.html and summary.json readers.
    public static readonly (string Header, Func<StressRow, string> Value)[] StressColumns =
    {
        ("Run", r => r.Number.ToString(CultureInfo.InvariantCulture)),
        ("Parameters", r => r.Parameters ?? "-"),
        ("Players", r => r.Summary.Players.ToString(CultureInfo.InvariantCulture)),
        ("Phase", r => r.Summary.Phase),
        ("Tick P50", r => MeasureMath.F(r.Summary.TickP50Ms)),
        ("P95", r => MeasureMath.F(r.Summary.TickP95Ms)),
        ("P99", r => MeasureMath.F(r.Summary.TickP99Ms)),
        ("Max", r => MeasureMath.F(r.Summary.TickMaxMs) + (r.Summary.TicksExact ? "" : "*")),
        ("CPU %", r => MeasureMath.F(r.Summary.CpuPercent)),
        ("Managed MB", r => MeasureMath.F(r.Summary.ManagedMB)),
        ("WorkingSet MB", r => MeasureMath.F(r.Summary.WorkingSetMB)),
        ("GC 0/1/2", r => $"{r.Summary.Gen0}/{r.Summary.Gen1}/{r.Summary.Gen2}"),
        ("Send KB/s", r => MeasureMath.F(r.Summary.SendKBps)),
        ("Recv KB/s", r => MeasureMath.F(r.Summary.RecvKBps)),
        ("DB Queue", r => r.Summary.DbQueueMax.ToString(CultureInfo.InvariantCulture)),
        ("Build", r => r.Summary.BuildPieces.ToString(CultureInfo.InvariantCulture)),
        ("Stalls", r => r.Summary.Stalls.ToString(CultureInfo.InvariantCulture)),
        ("R1 P95 ms", r => r.Summary.InputLatencyP95Ms is double l ? MeasureMath.F(l) : "n/a"),
        ("Result", r => r.Status),
    };

    // 기능: 스트레스 비교표를 QA/Reports/batch-<id>/summary.json과 summary.html로 쓴다.
    // 입력: reportRoot - 보고서 루트 폴더, scenarioFile - 시나리오 파일 표기, started - 배치 시작 시각(폴더 이름에 쓰임).
    // 출력: 만든 폴더 경로. 스트레스 행이 없으면 아무것도 쓰지 않고 null. IO 오류는 호출자에게 전파된다.
    // D40: QA/Reports/batch-<id>/summary.json and summary.html (only when some run measured). Returns the folder, or
    // null when there was nothing to write. I/O problems are the caller's to report (never a run failure).
    public string? WriteStressSummary(string reportRoot, string scenarioFile, DateTimeOffset started)
    {
        if (_stress.Count == 0) return null;
        string id = $"batch-{started:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x10000):x4}";
        string dir = Path.Combine(reportRoot, id);
        Directory.CreateDirectory(dir);
        var json = new { batchId = id, scenario = _scenario, scenarioFile, started, runs = Runs, passed = Passed, failed = Failed, skipped = Skipped, errors = Errors, rows = _stress };
        File.WriteAllText(Path.Combine(dir, "summary.json"), JsonSerializer.Serialize(json, QaJson.Options), Encoding.UTF8);
        var sb = new StringBuilder(8 * 1024);
        string E(string text) => System.Net.WebUtility.HtmlEncode(text);
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>").Append(E($"Stress batch {_scenario} {id}")).Append("</title><style>")
          .Append("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#222}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:3px 8px;text-align:right;font-size:13px}")
          .Append("td:nth-child(2),th{text-align:left}.FAILED,.ERROR{background:#c62828;color:#fff}.PASSED{background:#2e7d32;color:#fff}.muted{color:#777}</style></head><body>")
          .Append("<h1>Stress batch: ").Append(E(_scenario)).Append("</h1><p>").Append(E(scenarioFile)).Append(" &middot; ").Append(E(id)).Append(" &middot; ")
          .Append(Runs).Append(" runs: ").Append(Passed).Append(" passed, ").Append(Failed).Append(" failed, ").Append(Skipped).Append(" skipped, ").Append(Errors).Append(" errors</p>")
          .Append("<p class=\"muted\">Each row is the run's judged phase (\"steady\", else the longest). Tick in ms (* = approximate, phase over 120 s); CPU % of all logical processors; KB = 1024 bytes of UDP payload; R1 = input to server acknowledgement as the headless clients saw it. Compare rows of the same machine and build only (§125).</p><table><tr>");
        foreach (var c in StressColumns) sb.Append("<th>").Append(E(c.Header)).Append("</th>");
        sb.Append("<th>Report</th></tr>");
        foreach (StressRow r in _stress)
        {
            sb.Append("<tr>");
            foreach (var c in StressColumns)
            {
                string v = c.Value(r);
                sb.Append(c.Header == "Result" ? $"<td class=\"{E(v)}\">" : "<td>").Append(E(v)).Append("</td>");
            }
            sb.Append("<td><a href=\"../").Append(E(r.RunId)).Append("/report.html\">").Append(E(r.RunId)).Append("</a></td></tr>");
        }
        sb.Append("</table></body></html>");
        File.WriteAllText(Path.Combine(dir, "summary.html"), sb.ToString(), Encoding.UTF8);
        return dir;
    }

    // 기능: JSON 값을 표시용 compact 문자열로 줄인다.
    // 입력: value - 파라미터 세트나 변수 재정의.
    // 출력: 200자로 자른 compact JSON. null이면 빈 문자열.
    public static string Compact(JsonElement? value)
    {
        if (value == null) return string.Empty;
        return JsonPath.Truncate(JsonSerializer.Serialize(value.Value, QaJson.Compact), 200);
    }
}
