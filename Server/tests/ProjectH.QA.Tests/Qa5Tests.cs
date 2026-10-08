using System.Text;
using System.Text.Json;
using ProjectH.Client.Qa;
using ProjectH.QA;

namespace ProjectH.QA.Tests;

// QA-5: parameters (D31), repeat / seed sweep (D32), run history and baseline (D33), recordings and playInputs (D34).
public class Qa5Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa5-" + Guid.NewGuid().ToString("N"));
    private readonly FakeQaServer _server = new();
    private readonly List<MockActor> _actors = new();

    public Qa5Tests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "QA", "Scenarios", "T"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Reports => Path.Combine(_root, "out");

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private async Task<(int Exit, string Output)> Cli(CancellationToken token, Action<MockActor>? setup, params string[] args)
    {
        var output = new StringWriter();
        int exit = await QaCli.RunAsync(args.Concat(new[] { "--repo", _root }).ToArray(), output, token, _ => _server, (alias, _) =>
        {
            var a = new MockActor(alias);
            setup?.Invoke(a);
            lock (_actors) _actors.Add(a);
            return a;
        });
        return (exit, output.ToString());
    }

    private Task<(int Exit, string Output)> Cli(params string[] args) => Cli(default, null, args);

    private static IReadOnlyList<ValidationIssue> Validate(string json)
    {
        ScenarioLoadResult load = ScenarioLoader.Parse(json, "x.json");
        var issues = load.Errors.Select(e => new ValidationIssue(true, "file", e)).ToList();
        if (load.Scenario != null) issues.AddRange(ScenarioValidator.Validate(load.Scenario, ActionRegistry.CreateDefault(), MarkerStore.Empty()));
        return issues;
    }

    private static JsonDocument ReportJson(string dir, string runId) => JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, runId, "report.json")));

    // ---- D31 parameters ----

    [Fact]
    public void ParameterValidation()
    {
        string Scenario(string parameters, string steps = """{ "action": "wait", "milliseconds": "${ping}" }""") =>
            $$"""{ "schemaVersion": 1, "parameters": {{parameters}}, "steps": [ {{steps}} ] }""";

        Assert.DoesNotContain(Validate(Scenario("""[ { "ping": 1 }, { "ping": 2 } ]""")), i => i.IsError);
        Assert.Contains(Validate(Scenario("""[ { "ping": 1 }, 5 ]""")), i => i.IsError && i.Message.Contains("parameters[1] must be an object"));
        Assert.Contains(Validate(Scenario("""{ "ping": 1 }""")), i => i.IsError && i.Message.Contains("'parameters' must be an array"));
        Assert.Contains(Validate(Scenario("""[ { "ping": 1, "bad name": 2 } ]""")), i => i.IsError && i.Message.Contains("Bad parameter name 'bad name'"));
        Assert.Contains(Validate(Scenario("""[ { "ping": 1, "seed": 2 } ]""")), i => i.IsError && i.Message.Contains("built-in"));
        // Referenced but missing in one set: the error names that set.
        IReadOnlyList<ValidationIssue> missing = Validate(Scenario("""[ { "ping": 1 }, { "pong": 2 }, { "ping": 3 } ]"""));
        Assert.Contains(missing, i => i.IsError && i.Message.Contains("not in every parameter set") && i.Message.Contains("parameters[1]"));
        // A default in `variables` covers the sets without it.
        Assert.DoesNotContain(Validate("""{ "schemaVersion": 1, "variables": { "ping": 0 }, "parameters": [ { "ping": 1 }, { "pong": 2 } ], "steps": [ { "action": "wait", "milliseconds": "${ping}" } ] }"""), i => i.IsError);
        string many = "[" + string.Join(",", Enumerable.Range(0, 101).Select(i => $"{{ \"ping\": {i} }}")) + "]";
        Assert.Contains(Validate(Scenario(many)), i => i.IsError && i.Message.Contains("Too many parameter sets"));
        Assert.Contains(Validate(Scenario("[]")), i => i.IsError && i.Message.Contains("empty"));
    }

    [Fact]
    public void BaselineValidation()
    {
        Assert.DoesNotContain(Validate("""{ "schemaVersion": 1, "baseline": { "values": ["v"] }, "steps": [ { "action": "save", "path": "server.tickP95Ms", "saveAs": "v" } ] }"""), i => i.IsError);
        Assert.Contains(Validate("""{ "schemaVersion": 1, "baseline": { "values": ["nope"] }, "steps": [ { "action": "wait", "milliseconds": 1 } ] }"""), i => i.IsError && i.Message.Contains("never saved"));
        Assert.Contains(Validate("""{ "schemaVersion": 1, "baseline": { "value": ["v"] }, "steps": [ { "action": "wait", "milliseconds": 1 } ] }"""), i => i.IsError && i.Message.Contains("Unknown 'baseline' field"));
        Assert.Contains(Validate("""{ "schemaVersion": 1, "baselineWarnPercent": 0, "steps": [ { "action": "wait", "milliseconds": 1 } ] }"""), i => i.IsError && i.Message.Contains("baselineWarnPercent"));
    }

    [Fact]
    public async Task ParametersRunOncePerSetWithOwnRunIdAndSummary()
    {
        string file = Write("QA/Scenarios/T/p.json", """
        { "schemaVersion": 1, "name": "params", "seed": 5, "variables": { "limit": 150 },
          "parameters": [ { "ping": 0 }, { "ping": 100 }, { "ping": 200, "limit": 50 } ],
          "steps": [ { "id": "check", "assert": "var.ping", "lessThan": "${limit}" } ] }
        """);
        (int exit, string output) = await Cli("run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(1, exit);   // set 3: 200 < 50 fails (a parameter overrides the variable of its name)
        Assert.Contains("parameters[2] {\"ping\":100}", output);
        Assert.Contains("== Batch params: 3 runs: 2 passed, 1 failed", output);
        Assert.Contains("parameters[3] {\"ping\":200,\"limit\":50}: 1 run, 0 passed, 1 not passed", output);
        Assert.Contains("reproduce: dotnet run --project Server/src/ProjectH.QA -- run QA/Scenarios/T/p.json --seed 5 --parameter-set 3", output);
        string[] runs = Directory.GetDirectories(Reports).Select(Path.GetFileName).Where(n => n!.StartsWith("qa-")).ToArray()!;
        Assert.Equal(3, runs.Distinct().Count());
        foreach (string runId in runs)
        {
            using JsonDocument doc = ReportJson(Reports, runId);
            Assert.True(doc.RootElement.TryGetProperty("parameters", out JsonElement p) && p.TryGetProperty("ping", out _));
        }

        // The reproduce command runs that set alone.
        (exit, output) = await Cli("run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports, "--parameter-set", "2");
        Assert.Equal(0, exit);
        Assert.DoesNotContain("== Batch", output);
        Assert.Equal(2, (await Cli("run", file, "--attach", "http://127.0.0.1:1", "--parameter-set", "4")).Exit);
    }

    // ---- D32 repeat / seed sweep ----

    [Fact]
    public async Task RepeatRunsTheSameSeedNTimes()
    {
        string file = Write("QA/Scenarios/T/r.json", """{ "schemaVersion": 1, "name": "rep", "seed": 42, "actors": [ { "id": "a" } ], "steps": [ { "action": "connect", "actor": "a" } ] }""");
        (int exit, string output) = await Cli("run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports, "--repeat", "3");
        Assert.Equal(0, exit);
        Assert.Contains("[run 3/3, iteration 3]", output);
        Assert.Contains("== Batch rep: 3 runs: 3 passed, 0 failed", output);
        Assert.Equal(3, output.Split('\n').Count(l => l.StartsWith("== rep  runId") && l.Contains("seed 42")));
        Assert.Contains("1 scenario, 3 runs: 3 passed", output);
    }

    [Fact]
    public async Task SeedSweepListsFailingSeedsAndStopOnFailStops()
    {
        string file = Write("QA/Scenarios/T/s.json", """{ "schemaVersion": 1, "name": "sweep", "steps": [ { "id": "not_two", "assert": "var.seed", "notEquals": 2 } ] }""");
        (int exit, string output) = await Cli("run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports, "--seed-sweep", "1..4");
        Assert.Equal(1, exit);
        Assert.Contains("== Batch sweep: 4 runs: 3 passed, 1 failed", output);
        Assert.Contains("failing seeds: 2", output);
        Assert.Contains("seed 2 FAILED at step 01 (not_two)", output);
        Assert.Contains("reproduce: dotnet run --project Server/src/ProjectH.QA -- run QA/Scenarios/T/s.json --seed 2", output);

        (exit, output) = await Cli("run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports, "--seed-sweep", "1..4", "--stop-on-fail");
        Assert.Equal(1, exit);
        Assert.Contains("2 remaining runs not started", output);
        Assert.Contains("2 runs: 1 passed, 1 failed", output);
        Assert.Contains("(stopped early)", output);
    }

    [Fact]
    public async Task BadBatchOptionsAreExitTwo()
    {
        string file = Write("QA/Scenarios/T/o.json", """{ "schemaVersion": 1, "steps": [ { "action": "wait", "milliseconds": 1 } ] }""");
        Assert.Equal(2, (await Cli("run", file, "--repeat", "0")).Exit);
        Assert.Equal(2, (await Cli("run", file, "--repeat", "1001")).Exit);
        Assert.Equal(2, (await Cli("run", file, "--seed-sweep", "5..1")).Exit);
        Assert.Equal(2, (await Cli("run", file, "--seed-sweep", "1..1001")).Exit);
        Assert.Equal(2, (await Cli("run", file, "--seed-sweep", "1..3", "--seed", "4")).Exit);
        Assert.Equal(2, (await Cli("run", file, "--seed-sweep", "1..3", "--repeat", "2")).Exit);
        Assert.True(BatchOptions.TryParseSweep("-2..2", out var range, out _));
        Assert.Equal((-2, 2), range);
    }

    [Fact]
    public async Task CancellationStopsTheLoopPromptly()
    {
        string file = Write("QA/Scenarios/T/c.json", """{ "schemaVersion": 1, "name": "cancel", "seed": 1, "actors": [ { "id": "a" } ], "steps": [ { "action": "connect", "actor": "a" }, { "action": "wait", "seconds": 30 } ] }""");
        using var cts = new CancellationTokenSource();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        (int exit, string output) = await Cli(cts.Token, a => a.OnCommand = (_, c) => { if (c is ConnectCommand) cts.CancelAfter(100); },
            "run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports, "--repeat", "50");
        Assert.Equal(1, exit);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
        Assert.Single(_actors);   // only the first run started
        Assert.Contains("1 runs: 0 passed, 1 failed", output);
        Assert.Contains("Stopped.", output);
    }

    [Fact]
    public void PlannerOrderAndCounts()
    {
        ScenarioDefinition s = ScenarioLoader.Parse("""{ "schemaVersion": 1, "seed": 9, "parameters": [ { "a": 1 }, { "a": 2 } ], "steps": [ { "action": "wait", "milliseconds": 1 } ] }""").Scenario!;
        var repeat = new BatchOptions { Repeat = 3 };
        PlannedRun[] runs = BatchPlanner.Plan(s, repeat, null).ToArray();
        Assert.Equal(6, BatchPlanner.Count(s, repeat));
        Assert.Equal(new[] { 0, 0, 0, 1, 1, 1 }, runs.Select(r => r.ParameterIndex!.Value));
        Assert.Equal(new[] { 1, 2, 3, 1, 2, 3 }, runs.Select(r => r.Iteration));
        Assert.All(runs, r => Assert.Equal(9, r.Seed));
        PlannedRun[] sweep = BatchPlanner.Plan(s, new BatchOptions { SeedSweep = (10, 12), ParameterSet = 2 }, null).ToArray();
        Assert.Equal(new int?[] { 10, 11, 12 }, sweep.Select(r => r.Seed));
        Assert.All(sweep, r => Assert.Equal(1, r.ParameterIndex));
    }

    // ---- D33 history and baseline ----

    private static HistoryEntry Entry(string runId, string status = "Passed", double p95 = 1.0, string? parameters = null) => new()
    {
        RunId = runId, Status = status, Utc = "2026-10-02T00:00:00Z", ParametersKey = parameters,
        Metrics = new Dictionary<string, double> { ["tickP95Ms"] = p95 },
    };

    [Fact]
    public void HistoryKeepsTheLast50AndFindsThePreviousPassedRunWithTheSameParameters()
    {
        string dir = Path.Combine(_root, "history");
        for (int i = 1; i <= 55; i++) Assert.Null(BaselineHistory.Record(dir, "k", Entry($"r{i}", i == 55 ? "Failed" : "Passed")).Warning);
        IReadOnlyList<HistoryEntry> all = BaselineHistory.Read(dir, "k");
        Assert.Equal(50, all.Count);
        Assert.Equal("r6", all[0].RunId);
        Assert.Equal("r55", all[^1].RunId);
        BaselineHistory.Record(dir, "k", Entry("other", parameters: "{\"ping\":1}"));
        // Same parameters (none): the latest PASSED one (r55 failed), not the parameterized run.
        Assert.Equal("r54", BaselineHistory.Record(dir, "k", Entry("r56")).Previous!.RunId);
        Assert.Equal("other", BaselineHistory.Record(dir, "k", Entry("p2", parameters: "{\"ping\":1}")).Previous!.RunId);
    }

    [Fact]
    public async Task ConcurrentWritersKeepEveryLine()
    {
        string dir = Path.Combine(_root, "history");
        Task[] writers = Enumerable.Range(0, 2).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < 20; i++) Assert.Null(BaselineHistory.Record(dir, "k", Entry($"w{w}-{i}")).Warning);
        })).ToArray();
        await Task.WhenAll(writers);
        IReadOnlyList<HistoryEntry> all = BaselineHistory.Read(dir, "k");
        Assert.Equal(40, all.Select(e => e.RunId).Distinct().Count());
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void AHeldLockIsAWarningNotAnError()
    {
        string dir = Path.Combine(_root, "history");
        Directory.CreateDirectory(dir);
        using (new FileStream(Path.Combine(dir, "k.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            (HistoryEntry? previous, string? warning) = BaselineHistory.Record(dir, "k", Entry("x"));
            Assert.Null(previous);
            Assert.Contains("held by another QA process", warning);
        }
        Assert.Empty(BaselineHistory.Read(dir, "k"));
    }

    [Fact]
    public void KeysAreSanitizedScenarioPathsWithAHashOfThePath()
    {
        string Scenarios(params string[] parts) => Path.Combine(new[] { _root, "QA", "Scenarios" }.Concat(parts).ToArray());
        string nested = BaselineHistory.Key(_root, Scenarios("Stress", "load_bots_10.json"));
        string flat = BaselineHistory.Key(_root, Scenarios("Stress_load_bots_10.json"));
        Assert.Matches("^Stress_load_bots_10_[0-9a-f]{8}$", nested);
        Assert.Matches("^Stress_load_bots_10_[0-9a-f]{8}$", flat);
        Assert.NotEqual(nested, flat);   // the readable parts collapse to the same text; the hash keeps them apart
        Assert.Equal(nested[^8..], BaselineHistory.Key(_root, Scenarios("stress", "LOAD_BOTS_10.json"))[^8..]);   // the hash ignores case (Windows paths); the file system does the rest
        Assert.NotEqual(BaselineHistory.Key(_root, Scenarios("T", "a b.json")), BaselineHistory.Key(_root, Scenarios("T", "a+b.json")));
        Assert.StartsWith("external_x_", BaselineHistory.Key(_root, Path.Combine(_root, "x.json")));
        Assert.Equal("Stress/load_bots_10.json", BaselineHistory.ScenarioFile(_root, Scenarios("Stress", "load_bots_10.json")));
    }

    // Two files in one history file (an old shared key) never serve as each other's baseline.
    [Fact]
    public void ThePreviousRunMustBeOfTheSameScenarioFile()
    {
        string dir = Path.Combine(_root, "history");
        HistoryEntry a = Entry("a1");
        a.ScenarioFile = "Stress/a.json";
        HistoryEntry b = Entry("b1");
        b.ScenarioFile = "Stress_a.json";
        BaselineHistory.Record(dir, "k", a);
        Assert.Null(BaselineHistory.Record(dir, "k", b).Previous);
        HistoryEntry a2 = Entry("a2");
        a2.ScenarioFile = "Stress/a.json";
        Assert.Equal("a1", BaselineHistory.Record(dir, "k", a2).Previous!.RunId);
    }

    [Fact]
    public async Task WorseMetricWithoutThresholdIsAWarningNeverAFailure()
    {
        string file = Write("QA/Scenarios/T/b.json", """
        { "schemaVersion": 1, "name": "base", "seed": 3, "baseline": { "values": ["p99", "hp"] },
          "steps": [
            { "action": "save", "path": "server.tickP99Ms", "saveAs": "p99" },
            { "assert": "server.tickP50Ms", "lessThan": 100, "saveAs": "hp" }
          ] }
        """);
        (int exit, string output) = await Cli("run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(0, exit);
        // Twice as slow: p95 / p99 / memory have no threshold (warnings); p50 is asserted (no warning).
        _server.Metrics = new { tickP50Ms = 4.0, tickP95Ms = 1.8, tickP99Ms = 3.0, workingSetMB = 80.5 };
        (exit, output) = await Cli("run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(0, exit);
        Assert.Contains("PASSED", output);
        Assert.Contains("WARNING Baseline: server.tickP95Ms 0.9 -> 1.8 (+100%", output);
        Assert.Contains("WARNING Baseline: p99 1.5 -> 3", output);
        Assert.DoesNotContain("WARNING Baseline: server.tickP50Ms", output);
        Assert.DoesNotContain("WARNING Baseline: hp", output);
        Assert.DoesNotContain("workingSetMB", output.Split('\n').Where(l => l.Contains("WARNING")).Aggregate("", (a, b) => a + b));
        string runId = System.Text.RegularExpressions.Regex.Match(output, @"runId (qa-[0-9]{8}-[0-9]{6}-[0-9a-f]{4})").Groups[1].Value;
        using JsonDocument doc = ReportJson(Reports, runId);
        JsonElement baseline = doc.RootElement.GetProperty("baseline");
        Assert.NotNull(baseline.GetProperty("previousRunId").GetString());
        Assert.Contains(baseline.GetProperty("rows").EnumerateArray(), r => r.GetProperty("name").GetString() == "server.tickP95Ms" && r.GetProperty("warning").GetBoolean());
        string html = File.ReadAllText(Path.Combine(Reports, runId, "report.html"));
        Assert.Contains("<h2>Baseline</h2>", html);
        Assert.Contains("Previous", html);
        IReadOnlyList<HistoryEntry> history = BaselineHistory.Read(Path.Combine(Reports, "history"), BaselineHistory.Key(_root, file));
        Assert.Equal(2, history.Count);
        Assert.All(history, e => Assert.Equal("T/b.json", e.ScenarioFile));
    }

    [Fact]
    public async Task UnsavedOrFilelessRunsAreNotRecorded()
    {
        ScenarioDefinition s = ScenarioLoader.Parse("""{ "schemaVersion": 1, "steps": [ { "action": "wait", "milliseconds": 1 } ] }""").Scenario!;
        var options = new QaRunOptions { RepoRoot = _root, ReportDir = Reports, AttachUrl = "http://127.0.0.1:1", ServerClientFactory = _ => _server };
        RunReport report = await new QaOrchestrator(options, ActionRegistry.CreateDefault(), MarkerStore.Empty(), TextWriter.Null).RunAsync(s, Array.Empty<ValidationIssue>(), default);
        Assert.Equal(RunStatus.Passed, report.Status);
        Assert.Contains("no file", report.Baseline!.Note);
        Assert.False(Directory.Exists(Path.Combine(Reports, "history")));
    }

    // ---- D34 recordings ----

    private static string Recording(int count, int simHz = 30, Func<int, int>? buttons = null)
    {
        var sb = new StringBuilder();
        QaInputRecordFormat.AppendHeader(sb, simHz, "qa1");
        for (int i = 0; i < count; i++)
            QaInputRecordFormat.AppendInput(sb, QaInputRecordFormat.StepTime(i, simHz), 0.5f, 1f, 90.25f, buttons?.Invoke(i) ?? 0, 91.5f, -2.25f);
        return sb.ToString();
    }

    private static string Set(string line, string field, string value) =>
        System.Text.RegularExpressions.Regex.Replace(line, "\"" + field + "\":[^,}]+", "\"" + field + "\":" + value);

    private static InputRecording Read(string text) => InputRecording.Read(new StringReader(text));

    [Fact]
    public void RecordingsWrittenByTheClientLoad()
    {
        InputRecording r = Read(Recording(90, buttons: i => i == 5 ? 4 : 0));
        Assert.Equal(30, r.SimHz);
        Assert.Equal("qa1", r.DevPlayerId);
        Assert.Equal(90, r.Inputs.Length);
        Assert.Equal(3.0, r.Seconds, 3);
        Assert.Equal(new RecordedInput(0.5f, 1f, 90.25f, ProjectH.Shared.Simulation.InputButtons.Fire, 91.5f, -2.25f), r.Inputs[5]);
    }

    [Fact]
    public void BadRecordingsAreRefusedWithTheLine()
    {
        string ok = Recording(3);
        string[] lines = ok.Split('\n');
        void Bad(string text, string expected) => Assert.Contains(expected, Assert.Throws<RecordingException>(() => Read(text)).Message);
        Bad("", "empty");
        Bad(lines[0] + "\n", "no inputs");
        Bad(lines[0].Replace("\"version\":1", "\"version\":2") + "\n" + lines[1], "version 2");
        Bad(lines[1] + "\n", "header");
        Bad(lines[0] + "\n" + lines[2] + "\n" + lines[1] + "\n", "must increase");
        Bad(lines[0] + "\n" + Set(lines[1], "moveX", "2") + "\n", "'moveX' 2 is out of range");
        Bad(lines[0] + "\n" + Set(lines[1], "aimPitch", "\"x\"") + "\n", "'aimPitch' must be a number");
        Bad(lines[0] + "\n" + Set(lines[1], "buttons", "1.5") + "\n", "integer");
        Bad(lines[0] + "\n{\"t\":0," + new string(' ', 2000) + "}\n", "longer than");
        Bad(lines[0] + "\nnot json\n", "Line 2: malformed JSON");
        Bad(Recording(InputRecording.MaxInputs + 1), "More than 54000 inputs");
        Assert.Equal(InputRecording.MaxInputs, QaInputRecordFormat.MaxInputLines);
        // Phase 17: ThrowGrenade (32768) takes the last free bit and InteractHeld (16384) is known too, so no u16 bit is unknown.
        Assert.Equal(0, Read(lines[0] + "\n" + Set(lines[1], "buttons", "32772") + "\n").UnknownButtonLines);
        Assert.Equal(0, Read(lines[0] + "\n" + Set(lines[1], "buttons", "16384") + "\n").UnknownButtonLines);
    }

    [Fact]
    public async Task ConvertRecordingWritesAValidDraftNextToItsInputs()
    {
        string source = Write("rec/play1.jsonl", Recording(60));
        (int exit, string output) = await Cli("convert-recording", source);
        Assert.Equal(0, exit);
        string scenario = Path.Combine(_root, "QA", "Scenarios", "Recorded", "play1.json");
        Assert.True(File.Exists(scenario), output);
        Assert.Equal(File.ReadAllText(source), File.ReadAllText(Path.Combine(_root, "QA", "Scenarios", "Recorded", "play1.inputs.jsonl")));
        Assert.DoesNotContain(Validate(File.ReadAllText(scenario)), i => i.IsError);
        Assert.Contains("\"playInputs\"", File.ReadAllText(scenario));
        Assert.Contains("TODO", File.ReadAllText(scenario));

        Assert.Equal(2, (await Cli("convert-recording", source)).Exit);                 // exists
        Assert.Equal(0, (await Cli("convert-recording", source, "--force", "--actor", "rec")).Exit);
        Assert.Contains("\"rec\"", File.ReadAllText(scenario));
        Assert.Equal(2, (await Cli("convert-recording", source, "--out", Path.Combine(_root, "elsewhere.json"))).Exit);
        Assert.Equal(2, (await Cli("convert-recording", Write("rec/bad.jsonl", "{}\n"))).Exit);
        Assert.Equal(2, (await Cli("convert-recording", source, "--out", Path.Combine(_root, "QA", "Scenarios", "R", "x.json"), "--actor", "a.b")).Exit);
    }

    [Fact]
    public async Task PlayInputsSendsTheRecordingAndStaysUnderQa()
    {
        Write("QA/Scenarios/T/rec.inputs.jsonl", Recording(8));
        string file = Write("QA/Scenarios/T/play.json", """
        { "schemaVersion": 1, "name": "play", "seed": 1, "actors": [ { "id": "a" } ],
          "steps": [ { "action": "connect", "actor": "a" },
                     { "action": "playInputs", "actor": "a", "file": "rec.inputs.jsonl", "speed": 2, "saveAs": "played" },
                     { "assert": "var.played.sent", "equals": 5 } ] }
        """);
        (int exit, string output) = await Cli("run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(0, exit);
        PlayInputsCommand play = _actors.Single().Commands.OfType<PlayInputsCommand>().Single();
        Assert.Equal(8, play.Inputs.Count);
        Assert.Equal(2.0, play.Speed);

        Write("outside.inputs.jsonl", Recording(8));
        file = Write("QA/Scenarios/T/escape.json", """
        { "schemaVersion": 1, "actors": [ { "id": "a" } ],
          "steps": [ { "action": "connect", "actor": "a" }, { "action": "playInputs", "actor": "a", "file": "../../../outside.inputs.jsonl" } ] }
        """);
        (exit, output) = await Cli("run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(1, exit);
        Assert.Contains("outside QA/", output);

        // 100 s of inputs cannot finish in a 5 s step: refused before sending anything.
        Write("QA/Scenarios/T/long.inputs.jsonl", Recording(3000));
        file = Write("QA/Scenarios/T/long.json", """
        { "schemaVersion": 1, "actors": [ { "id": "a" } ],
          "steps": [ { "action": "connect", "actor": "a" }, { "action": "playInputs", "actor": "a", "file": "long.inputs.jsonl", "timeoutMilliseconds": 5000 } ] }
        """);
        (exit, output) = await Cli("run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(1, exit);
        Assert.Contains("longer than the step timeout", output);
    }

    [Fact]
    public async Task AnUnfinishedPlaybackFailsAndClearsTheQueue()
    {
        Write("QA/Scenarios/T/rec.inputs.jsonl", Recording(8));
        string file = Write("QA/Scenarios/T/stop.json", """
        { "schemaVersion": 1, "actors": [ { "id": "a" } ],
          "steps": [ { "action": "connect", "actor": "a" }, { "action": "playInputs", "actor": "a", "file": "rec.inputs.jsonl" } ] }
        """);
        (int exit, string output) = await Cli(default, a => a.NeverFinishPlayback = true, "run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(1, exit);
        Assert.Contains("Replay not finished", output);
        Assert.IsType<ClearInputQueueCommand>(_actors.Single().Commands.Last());
    }

    [Fact]
    public void PlayInputsValidation()
    {
        Assert.Contains(Validate("""{ "schemaVersion": 1, "actors": [ { "id": "a" } ], "steps": [ { "action": "playInputs", "actor": "a", "file": "x.json" } ] }"""), i => i.IsError && i.Message.Contains(".jsonl"));
        Assert.Contains(Validate("""{ "schemaVersion": 1, "actors": [ { "id": "a" } ], "steps": [ { "action": "playInputs", "actor": "a", "file": "x.jsonl", "speed": 5 } ] }"""), i => i.IsError && i.Message.Contains("'speed'"));
        Assert.Contains(Validate("""{ "schemaVersion": 1, "actors": [ { "id": "u", "type": "UnityClient" } ], "steps": [ { "action": "playInputs", "actor": "u", "file": "x.jsonl" } ] }"""), i => i.IsError && i.Message.Contains("gameplay input"));
    }

    // The replay step at every speed: each entry's button reaches the server at least once (skipped entries are merged
    // into the sent one), the last entry is always sent, and the tick count matches PlaybackTicks (the timeout check).
    [Theory]
    [InlineData(0.25)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public void PlaybackSendsEveryButtonAndTheLastEntryAtAnySpeed(double speed)
    {
        foreach (int count in new[] { 1, 2, 3, 8, 13 })
        {
            // A distinct bit per entry (13 known bits are enough).
            var bits = Enum.GetValues<ProjectH.Shared.Simulation.InputButtons>().Where(b => b != 0).ToArray();
            RecordedInput[] inputs = Enumerable.Range(0, count).Select(i => new RecordedInput(0, 0, i, bits[i], 0, 0)).ToArray();
            double cursor = 0;
            int last = -1, ticks = 0;
            var sent = ProjectH.Shared.Simulation.InputButtons.None;
            var indices = new List<int>();
            while (true)
            {
                (int index, var buttons, bool done) = HeadlessActor.PlaybackStep(inputs, ref cursor, ref last, speed);
                sent |= buttons;
                indices.Add(index);
                ticks++;
                Assert.True(ticks < 1000);
                if (done) break;
            }
            Assert.Equal(bits.Take(count).Aggregate(ProjectH.Shared.Simulation.InputButtons.None, (a, b) => a | b), sent);
            Assert.Equal(count - 1, indices[^1]);
            Assert.Equal(HeadlessActor.PlaybackTicks(count, speed), ticks);
            Assert.Equal(indices.OrderBy(i => i), indices);   // never goes back
            if (speed == 1.0) Assert.Equal(count, ticks);
        }
    }

    // --out is relative to the repository root (not the current directory), and "..": still only under QA/Scenarios.
    [Fact]
    public async Task ConvertOutIsRelativeToTheRepoRoot()
    {
        string source = Write("rec/r.jsonl", Recording(10));
        Assert.NotEqual(Path.GetFullPath(_root), Path.GetFullPath(Directory.GetCurrentDirectory()));
        (int exit, string output) = await Cli("convert-recording", source, "--out", "QA/Scenarios/Mine/r1.json");
        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(_root, "QA", "Scenarios", "Mine", "r1.json")), output);
        Assert.True(File.Exists(Path.Combine(_root, "QA", "Scenarios", "Mine", "r1.inputs.jsonl")));
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "QA", "Scenarios", "Mine", "r1.json")));
        Assert.Equal(2, (await Cli("convert-recording", source, "--out", "QA/Scenarios/../../r2.json")).Exit);
    }

    // The draft is written first; when the inputs copy then fails, nothing this call created is left behind.
    [Fact]
    public async Task AFailedConversionLeavesNoDraftBehind()
    {
        string source = Write("rec/r.jsonl", Recording(10));
        string dir = Path.Combine(_root, "QA", "Scenarios", "Fail");
        Directory.CreateDirectory(Path.Combine(dir, "r3.inputs.jsonl"));   // a folder where the inputs file must go
        (int exit, string output) = await Cli("convert-recording", source, "--out", "QA/Scenarios/Fail/r3.json");
        Assert.Equal(2, exit);
        Assert.Contains("Could not write the scenario", output);
        Assert.False(File.Exists(Path.Combine(dir, "r3.json")));
        Assert.True(Directory.Exists(Path.Combine(dir, "r3.inputs.jsonl")));   // not ours: untouched

        // With --force over an existing draft, a failed copy keeps the file that was there before.
        File.WriteAllText(Path.Combine(dir, "r3.json"), "{ \"old\": true }");
        Assert.Equal(2, (await Cli("convert-recording", source, "--out", "QA/Scenarios/Fail/r3.json", "--force")).Exit);
        Assert.True(File.Exists(Path.Combine(dir, "r3.json")));
    }
}
