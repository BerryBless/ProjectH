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
    // The runs in order: parameter set by parameter set, each with its iterations or seeds. Lazy (a full sweep with
    // 100 parameter sets is 100,000 runs; nothing per run is kept here).
    public static int Count(ScenarioDefinition s, BatchOptions o) => Sets(s, o).Count * PerSet(o);

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

    // Null when the parameter-set choice fits this scenario, else why not.
    public static string? CheckFor(ScenarioDefinition s, BatchOptions o)
    {
        if (o.ParameterSet is not int p) return null;
        if (s.Parameters.Count == 0) return $"--parameter-set {p}: '{s.Name}' has no parameters.";
        if (p > s.Parameters.Count) return $"--parameter-set {p}: '{s.Name}' has {s.Parameters.Count} parameter sets.";
        return null;
    }

    private static List<int?> Sets(ScenarioDefinition s, BatchOptions o)
    {
        if (s.Parameters.Count == 0) return new List<int?> { null };
        if (o.ParameterSet is int p && p >= 1 && p <= s.Parameters.Count) return new List<int?> { p - 1 };
        return Enumerable.Range(0, s.Parameters.Count).Select(i => (int?)i).ToList();
    }

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

    private sealed class Counts
    {
        public string Label = string.Empty;
        public int Runs, Passed, NotPassed;
    }

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

    // `run <file> --seed S [--parameter-set N]`: the exact run again.
    public string ReproduceCommand(Row row) =>
        $"dotnet run --project Server/src/ProjectH.QA -- run {_reproduceFile} --seed {row.Seed.ToString(CultureInfo.InvariantCulture)}"
        + (row.Run.ParameterIndex is int i ? $" --parameter-set {i + 1}" : string.Empty);

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

    public static string Compact(JsonElement? value)
    {
        if (value == null) return string.Empty;
        return JsonPath.Truncate(JsonSerializer.Serialize(value.Value, QaJson.Compact), 200);
    }
}
