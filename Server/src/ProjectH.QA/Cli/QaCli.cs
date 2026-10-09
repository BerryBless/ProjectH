using System.Globalization;
using System.Text.Json;

namespace ProjectH.QA;

// D18 CLI: run / validate / list. Kept out of Program so tests can call it (exit codes, malformed files) without a
// child process. Exit: 0 all PASS or SKIPPED (SKIPPED is 1 with --fail-on-skip), 1 a scenario failed (or was stopped), 2 a tool error (bad file, server would not
// start, internal error); with several scenarios the highest wins.
public static class QaCli
{
    public const string Usage =
        "Usage:\n" +
        "  dotnet run --project Server/src/ProjectH.QA -- run <file.json | directory | category:<name> | suite:<name> | name> [options]\n" +
        "  dotnet run --project Server/src/ProjectH.QA -- validate <same targets>\n" +
        "    (a bare name is a category or a suite; if both exist, choose with category: or suite:)\n" +
        "  dotnet run --project Server/src/ProjectH.QA -- list\n" +
        "  dotnet run --project Server/src/ProjectH.QA -- convert-recording <recording.jsonl> [--out QA/Scenarios/Recorded/<name>.json] [--actor playerA] [--force]\n" +
        "  dotnet run --project Server/src/ProjectH.QA -- ui [--port 5180]   (web UI on http://127.0.0.1:<port>/)\n" +
        "Options:\n" +
        "  --seed N            override the scenario seed\n" +
        "  --attach URL        use a running QA-mode server (e.g. http://127.0.0.1:7780) instead of launching one\n" +
        "  --server-dll PATH   ProjectH.Server.dll to launch (default: the newest build under Server/src/ProjectH.Server/bin)\n" +
        "  --report-dir DIR    where QA/Reports/<runId>/ goes (default QA/Reports)\n" +
        "  --poll-ms N         server polling interval for waitFor/waitForEvent, 20-5000 (default 100)\n" +
        "  --verbose           step details and the server's log on the console\n" +
        "  --fail-on-skip      a SKIPPED scenario (e.g. no Docker for a DB fault) exits 1 instead of 0\n" +
        "  --unity-exe PATH    QA-4: the Unity Development player for UnityClient actors (ProjectH.exe)\n" +
        "  --manual MODE       QA-4 manual checks: ask (default: ask on a terminal, SKIPPED otherwise), skip, fail (FAIL when nobody can answer)\n" +
        "  --repeat N          QA-5: run each scenario N times in a row, 1-1000 (same seed when it has one)\n" +
        "  --seed-sweep A..B   QA-5: run each scenario once per seed A..B (at most 1000 seeds), in order\n" +
        "  --stop-on-fail      QA-5: stop a repeat / sweep / parameter batch at its first failing run\n" +
        "  --parameter-set N   QA-5: run only the scenario's parameter set N (1-based; the reproduce command)\n" +
        "  --set name=value    stress: set a scenario variable over its default and the parameter set (repeatable), e.g. --set soakSeconds=1800\n" +
        "  --repo DIR          repository root (default: found from the current directory)\n" +
        "  --port N            ui only: the UI's port on 127.0.0.1, 0-65535 (default 5180; 0 = any free port)";

    public sealed class Parsed
    {
        public string Command { get; set; } = string.Empty;
        public string? Target { get; set; }
        public int? Seed { get; set; }
        public string? Attach { get; set; }
        public string? ServerDll { get; set; }
        public string? ReportDir { get; set; }
        public int PollMs { get; set; } = 100;
        public bool Verbose { get; set; }
        public bool FailOnSkip { get; set; }
        public string? Repo { get; set; }
        public string? UnityExe { get; set; }
        public string Manual { get; set; } = "ask";
        public int Port { get; set; } = UiHostOptions.DefaultPort;
        // QA-5 (D31-D32).
        public int Repeat { get; set; } = 1;
        public (int From, int To)? SeedSweep { get; set; }
        public bool StopOnFail { get; set; }
        public int? ParameterSet { get; set; }
        // Stress: --set name=value (a number, true/false, or text), applied over the scenario variables and parameters.
        public Dictionary<string, JsonElement> Set { get; } = new(StringComparer.Ordinal);
        // QA-5 convert-recording (D34).
        public string? Out { get; set; }
        public string Actor { get; set; } = "playerA";
        public bool Force { get; set; }

        public BatchOptions Batch => new() { Repeat = Repeat, SeedSweep = SeedSweep, StopOnFail = StopOnFail, ParameterSet = ParameterSet };
    }

    // 기능: 명령줄 인자를 명령·대상·옵션으로 파싱하고 값 범위와 조합(--seed/--seed-sweep 동시 사용 등)을 검증한다.
    // 입력: args - 명령줄 인자, parsed - 파싱 결과를 받을 객체, error - 실패 이유를 받을 문자열.
    // 출력: 파싱과 검증에 성공하면 true, 실패하면 false와 error에 이유.
    public static bool TryParse(string[] args, out Parsed parsed, out string? error)
    {
        parsed = new Parsed();
        error = null;
        if (args.Length == 0)
        {
            error = "No command.";
            return false;
        }
        parsed.Command = args[0].ToLowerInvariant();
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                if (parsed.Target != null)
                {
                    error = $"Unexpected argument '{a}'.";
                    return false;
                }
                parsed.Target = a;
                continue;
            }
            if (a == "--verbose")
            {
                parsed.Verbose = true;
                continue;
            }
            if (a == "--fail-on-skip")
            {
                parsed.FailOnSkip = true;
                continue;
            }
            if (a == "--stop-on-fail")
            {
                parsed.StopOnFail = true;
                continue;
            }
            if (a == "--force")
            {
                parsed.Force = true;
                continue;
            }
            if (i + 1 >= args.Length)
            {
                error = $"{a} needs a value.";
                return false;
            }
            string value = args[++i];
            switch (a)
            {
                case "--seed":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed)) { error = "--seed must be an integer."; return false; }
                    parsed.Seed = seed;
                    break;
                case "--attach":
                    if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")) { error = "--attach must be an http URL."; return false; }
                    parsed.Attach = value;
                    break;
                case "--server-dll":
                    parsed.ServerDll = Path.GetFullPath(value);
                    break;
                case "--report-dir":
                    parsed.ReportDir = Path.GetFullPath(value);
                    break;
                case "--poll-ms":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int poll) || poll < 20 || poll > 5000) { error = "--poll-ms must be 20-5000."; return false; }
                    parsed.PollMs = poll;
                    break;
                case "--port":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port < 0 || port > 65535) { error = "--port must be 0-65535."; return false; }
                    parsed.Port = port;
                    break;
                case "--unity-exe":
                    parsed.UnityExe = Path.GetFullPath(value);
                    break;
                case "--manual":
                    if (value is not ("ask" or "skip" or "fail")) { error = "--manual must be ask, skip or fail."; return false; }
                    parsed.Manual = value;
                    break;
                case "--repo":
                    parsed.Repo = Path.GetFullPath(value);
                    break;
                case "--repeat":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int repeat) || repeat < 1 || repeat > BatchOptions.MaxRepeat) { error = $"--repeat must be 1-{BatchOptions.MaxRepeat}."; return false; }
                    parsed.Repeat = repeat;
                    break;
                case "--seed-sweep":
                    if (!BatchOptions.TryParseSweep(value, out var sweep, out error)) return false;
                    parsed.SeedSweep = sweep;
                    break;
                case "--parameter-set":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int set) || set < 1 || set > ScenarioDefinition.MaxParameterSets) { error = $"--parameter-set must be 1-{ScenarioDefinition.MaxParameterSets}."; return false; }
                    parsed.ParameterSet = set;
                    break;
                case "--out":
                    parsed.Out = value;
                    break;
                case "--set":
                {
                    int eq = value.IndexOf('=');
                    if (eq <= 0) { error = "--set must be name=value."; return false; }
                    string name = value[..eq];
                    string text = value[(eq + 1)..];
                    JsonElement parsedValue;
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(text);
                        parsedValue = doc.RootElement.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? doc.RootElement.Clone() : JsonPath.From(text);
                    }
                    catch (JsonException)
                    {
                        parsedValue = JsonPath.From(text);
                    }
                    if (parsed.Set.Count >= 50) { error = "At most 50 --set values."; return false; }
                    parsed.Set[name] = parsedValue;
                    break;
                }
                case "--actor":
                    parsed.Actor = value;
                    break;
                default:
                    error = $"Unknown option {a}.";
                    return false;
            }
        }
        if (parsed.Command is "run" or "validate" or "convert-recording" && parsed.Target == null)
        {
            error = parsed.Command == "convert-recording" ? "'convert-recording' needs the recording file (.jsonl)." : $"'{parsed.Command}' needs a scenario file, directory, category or suite.";
            return false;
        }
        if (parsed.SeedSweep != null && parsed.Seed != null)
        {
            error = "Use --seed or --seed-sweep, not both.";
            return false;
        }
        error = parsed.Batch.Check();
        return error == null;
    }

    // 기능: CLI 진입점. 인자를 파싱해 list / ui / convert-recording / validate / run 명령을 수행하고, run은 선택된 시나리오마다 배치(반복·시드·파라미터 세트)를 돌린다.
    // 입력: args - 명령줄 인자, output - 콘솔 출력, token - 취소 토큰, serverFactory - QA 서버 Client 생성(테스트 주입), actorFactory - 액터 생성(테스트 주입), input - 수동 확인 입력(기본 Console.In), interactive - 터미널 여부(기본 입력 리디렉션 여부로 판단), unityHandlerFactory - Unity 액터 HTTP Handler 생성(테스트 주입).
    // 출력: 프로세스 종료 코드(0 전부 PASS/SKIPPED, 1 실패·중단, 2 도구 오류). 보고서 파일과 콘솔 요약이 기록된다.
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken token,
        Func<Uri, IQaServerClient>? serverFactory = null, Func<string, string, IQaActor>? actorFactory = null,
        TextReader? input = null, bool? interactive = null, Func<int, HttpMessageHandler?>? unityHandlerFactory = null)
    {
        if (!TryParse(args, out Parsed p, out string? error))
        {
            output.WriteLine(error);
            output.WriteLine(Usage);
            return 2;
        }
        string root = p.Repo ?? ServerLocator.FindRepoRoot(Directory.GetCurrentDirectory())
            ?? ServerLocator.FindRepoRoot(AppContext.BaseDirectory) ?? Directory.GetCurrentDirectory();

        MarkerStore markers;
        try
        {
            markers = MarkerStore.LoadFile(Path.Combine(root, "QA", "Markers.json"));
        }
        catch (Exception e) when (e is FormatException or JsonException or IOException)
        {
            output.WriteLine($"QA/Markers.json: {e.Message}");
            return 2;
        }
        ActionRegistry registry = ActionRegistry.CreateDefault();

        switch (p.Command)
        {
            case "list":
                List(root, output);
                return 0;
            case "ui":
                return await RunUiAsync(p, root, output, token, serverFactory, actorFactory).ConfigureAwait(false);
            case "convert-recording":
                return RecordingConverter.Run(root, p.Target!, p.Out, p.Actor, p.Force, output);
            case "validate":
            case "run":
                break;
            default:
                output.WriteLine($"Unknown command '{p.Command}'.");
                output.WriteLine(Usage);
                return 2;
        }

        IReadOnlyList<SelectionItem> files;
        try
        {
            ScenarioSelection selection = ScenarioCatalog.Select(root, p.Target!);
            // First line: what was resolved, so a run never silently covers a different set than meant.
            output.WriteLine(selection.Description);
            files = selection.Items;
        }
        catch (QaToolException e)
        {
            output.WriteLine(e.Message);
            return 2;
        }

        int exit = 0;
        int passed = 0, failed = 0, skipped = 0, errors = 0, runs = 0;
        foreach (SelectionItem item in files)
        {
            string file = item.File;
            // A suite entry may pick a parameter set and set variables; the command line wins over it.
            BatchOptions batchOptions = p.ParameterSet == null && item.ParameterSet != null
                ? new BatchOptions { Repeat = p.Repeat, SeedSweep = p.SeedSweep, StopOnFail = p.StopOnFail, ParameterSet = item.ParameterSet }
                : p.Batch;
            var overrides = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (item.Variables is JsonElement entryVariables) foreach (JsonProperty v in entryVariables.EnumerateObject()) overrides[v.Name] = v.Value.Clone();
            foreach (var pair in p.Set) overrides[pair.Key] = pair.Value;
            if (token.IsCancellationRequested)
            {
                output.WriteLine("Stopped.");
                return Math.Max(exit, 1);
            }
            ScenarioLoadResult load = ScenarioLoader.LoadFile(file);
            var issues = new List<ValidationIssue>();
            foreach (string e in load.Errors) issues.Add(new ValidationIssue(true, "file", e));
            if (load.Scenario != null) issues.AddRange(ScenarioValidator.Validate(load.Scenario, registry, markers));
            if (load.Scenario != null) issues.AddRange(ScenarioValidator.CheckOverrides(load.Scenario, overrides.Keys));
            bool invalid = issues.Any(i => i.IsError);
            if (p.Command == "validate" || invalid)
            {
                output.WriteLine($"{(invalid ? "INVALID" : "OK")} {Relative(root, file)}");
                foreach (ValidationIssue i in issues) output.WriteLine("   " + i);
                if (invalid)
                {
                    exit = 2;
                    errors++;
                }
                continue;
            }
            foreach (ValidationIssue w in issues) output.WriteLine($"   {w}");

            ScenarioDefinition scenario = load.Scenario!;
            if (BatchPlanner.CheckFor(scenario, batchOptions) is string planError)
            {
                output.WriteLine($"INVALID {Relative(root, file)}: {planError}");
                exit = 2;
                errors++;
                continue;
            }
            // D31-D32: one run per parameter set and per iteration / seed, one after another.
            int total = BatchPlanner.Count(scenario, batchOptions);
            var summary = new BatchSummary(scenario.Name, Relative(root, file).Replace('\\', '/'));
            DateTimeOffset batchStarted = DateTimeOffset.Now;
            foreach (PlannedRun planned in BatchPlanner.Plan(scenario, batchOptions, p.Seed))
            {
                if (token.IsCancellationRequested)
                {
                    summary.Stopped = true;
                    break;
                }
                var options = new QaRunOptions
                {
                    RepoRoot = root,
                    SeedOverride = planned.Seed,
                    AttachUrl = p.Attach,
                    ServerDll = p.ServerDll,
                    ReportDir = p.ReportDir,
                    PollMs = p.PollMs,
                    Verbose = p.Verbose,
                    ServerClientFactory = serverFactory,
                    ActorFactory = actorFactory,
                    FailOnSkip = p.FailOnSkip,
                    UnityExe = p.UnityExe,
                    UnityHandlerFactory = unityHandlerFactory,
                    ManualPrompt = ManualPrompt(p.Manual, input ?? Console.In, interactive ?? !Console.IsInputRedirected, output),
                    Parameters = planned.Parameters,
                    ParameterIndex = planned.ParameterIndex,
                    BatchLabel = total > 1 ? BatchLabel(planned) : null,
                    VariableOverrides = overrides.Count > 0 ? overrides : null,
                };
                RunReport report = await new QaOrchestrator(options, registry, markers, output).RunAsync(scenario, issues, token).ConfigureAwait(false);
                summary.Add(planned, report);
                runs++;
                exit = Math.Max(exit, report.ExitCode);
                switch (report.Status)
                {
                    case RunStatus.Passed: passed++; break;
                    case RunStatus.Skipped: skipped++; break;
                    case RunStatus.Error: errors++; break;
                    default: failed++; break;
                }
                // A stopped run ends the batch at once (Ctrl+C), and --stop-on-fail ends it at the first failing run.
                if (report.Status == RunStatus.Cancelled || token.IsCancellationRequested)
                {
                    summary.Stopped = true;
                    break;
                }
                if (batchOptions.StopOnFail && report.Status is RunStatus.Failed or RunStatus.Error && planned.Number < total)
                {
                    summary.Stopped = true;
                    output.WriteLine($"   --stop-on-fail: {total - planned.Number} remaining runs not started.");
                    break;
                }
            }
            if (total > 1)
            {
                foreach (string line in summary.Lines()) output.WriteLine(line);
                // D40: the stress comparison of the batch (console + QA/Reports/batch-<id>/summary.html/json).
                foreach (string line in summary.StressLines()) output.WriteLine(line);
                try
                {
                    string? dir = summary.WriteStressSummary(p.ReportDir ?? Path.Combine(root, "QA", "Reports"), Relative(root, file).Replace('\\', '/'), batchStarted);
                    if (dir != null) output.WriteLine($"   batch summary: {Path.Combine(dir, "summary.html")}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    output.WriteLine($"   batch summary could not be written: {e.Message}");
                }
            }
            if (token.IsCancellationRequested)
            {
                output.WriteLine("Stopped.");
                return Math.Max(exit, 1);
            }
            if (summary.Stopped && batchOptions.StopOnFail && summary.Failures.Count > 0) break;
        }
        if (files.Count > 1 || runs > files.Count)
        {
            string what = runs == files.Count ? $"{files.Count} scenarios" : $"{files.Count} scenario{(files.Count == 1 ? "" : "s")}, {runs} runs";
            output.WriteLine(p.Command == "run"
                ? $"{what}: {passed} passed, {failed} failed, {skipped} skipped, {errors} errors; exit code {exit}."
                : $"{files.Count} scenarios, exit code {exit}.");
        }
        return exit;
    }

    // 기능: 배치 안의 한 실행을 콘솔에 표시할 레이블("run 2/5, iteration 2, parameters[1]")로 만든다.
    // 입력: r - 계획된 실행 한 건.
    // 출력: 실행 번호·반복 회차·파라미터 세트 번호가 들어간 레이블 문자열.
    private static string BatchLabel(PlannedRun r)
    {
        string label = $"run {r.Number}/{r.Total}";
        if (r.Iteration > 0) label += $", iteration {r.Iteration}";
        if (r.ParameterIndex is int i) label += $", parameters[{i + 1}]";
        return label;
    }

    // 기능: --manual 모드와 터미널 여부에 따라 수동 확인 단계에 답을 받는 콘솔 프롬프트 함수를 만든다.
    // 입력: mode - ask / skip / fail, input - 답을 읽을 입력, interactive - 터미널에서 실행 중인지, output - 프롬프트를 쓸 출력.
    // 출력: 터미널이면 p/f/s와 메모를 묻는 비동기 프롬프트, 비터미널 fail 모드면 항상 FAIL을 답하는 함수, skip 모드나 비터미널 ask 모드면 null(단계 SKIPPED).
    // D30 on the console: on a terminal ask p(ass) / f(ail) / s(kip) and a note; without one, SKIPPED (or FAIL with
    // --manual fail). Console reads ignore cancellation, so each read runs on its own task and the wait for it ends on
    // Ctrl+C / Stop (the abandoned read finishes with the next Enter or with the process).
    public static Func<StepDefinition, string, CancellationToken, Task<ManualCheckAnswer>>? ManualPrompt(string mode, TextReader input, bool interactive, TextWriter output)
    {
        if (mode == "skip") return null;
        if (!interactive)
        {
            if (mode == "fail") return (_, _, _) => Task.FromResult(new ManualCheckAnswer(false, "Nobody to answer (non-interactive, --manual fail).", "auto"));
            return null;
        }
        return async (step, description, token) =>
        {
            output.WriteLine($"   MANUAL CHECK {step.Index + 1:00} ({step.Id}): {description}");
            while (true)
            {
                output.Write("   p = PASS, f = FAIL, s = skip > ");
                string? line = await ReadLine(input, token).ConfigureAwait(false);
                if (line == null) return new ManualCheckAnswer(null, "Input closed.", "cli");
                string answer = line.Trim().ToLowerInvariant();
                if (answer is not ("p" or "f" or "s")) continue;
                if (answer == "s") return new ManualCheckAnswer(null, "Skipped at the terminal.", "cli");
                output.Write("   note (optional) > ");
                string? note = await ReadLine(input, token).ConfigureAwait(false);
                return new ManualCheckAnswer(answer == "p", string.IsNullOrWhiteSpace(note) ? null : note.Trim(), "cli");
            }
        };
    }

    // 기능: 취소할 수 없는 콘솔 ReadLine을 별도 Task에서 실행하고 토큰으로 대기만 끊는다.
    // 입력: input - 읽을 입력, token - 대기를 끊을 취소 토큰.
    // 출력: 읽은 한 줄. 입력이 닫혔으면 null. 취소되면 OperationCanceledException.
    private static async Task<string?> ReadLine(TextReader input, CancellationToken token)
    {
        string? line = await Task.Run(input.ReadLine, CancellationToken.None).WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return line;
    }

    // 기능: 웹 UI 호스트를 127.0.0.1에 띄우고 취소(Ctrl+C)될 때까지 유지한 뒤 활성 실행 정리 → 호스트 종료 순으로 닫는다.
    // 입력: p - 파싱된 CLI 옵션, root - 저장소 루트, output - 콘솔 출력, token - 종료 토큰, serverFactory - QA 서버 Client 생성(테스트 주입), actorFactory - 액터 생성(테스트 주입).
    // 출력: 정상 종료면 0, 포트를 열지 못하면 2.
    // D19: serve the UI until Ctrl+C. Shutdown order: stop the active run and wait for its cleanup (actors, launched
    // server, report), then stop the web host.
    private static async Task<int> RunUiAsync(Parsed p, string root, TextWriter output, CancellationToken token,
        Func<Uri, IQaServerClient>? serverFactory, Func<string, string, IQaActor>? actorFactory)
    {
        QaUiHost host;
        try
        {
            host = await QaUiHost.StartAsync(new UiHostOptions
            {
                RepoRoot = root,
                Port = p.Port,
                AttachUrl = p.Attach,
                ServerDll = p.ServerDll,
                UnityExe = p.UnityExe,
                ReportDir = p.ReportDir,
                PollMs = p.PollMs,
                ServerClientFactory = serverFactory,
                ActorFactory = actorFactory,
            }, token).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            output.WriteLine($"The UI could not start on 127.0.0.1:{p.Port}: {e.Message}");
            return 2;
        }
        await using (host.ConfigureAwait(false))
        {
            output.WriteLine($"QA UI: {host.Url}  (127.0.0.1 only; Ctrl+C stops it)");
            try
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                output.WriteLine("Stopping the UI (an active run is stopped and cleaned up first)...");
            }
        }
        return 0;
    }

    // 기능: QA/Scenarios의 카테고리별 시나리오(이름·태그), QA/Suites의 스위트, 등록된 액션 목록을 콘솔에 출력한다.
    // 입력: root - 저장소 루트, output - 콘솔 출력.
    // 출력: 반환값 없음. 목록이 output에 기록된다.
    private static void List(string root, TextWriter output)
    {
        string scenarios = Path.Combine(root, "QA", "Scenarios");
        output.WriteLine("Scenarios:");
        if (Directory.Exists(scenarios))
        {
            foreach (string dir in Directory.GetDirectories(scenarios).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                output.WriteLine($"  {Path.GetFileName(dir)}");
                foreach (string f in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    ScenarioLoadResult load = ScenarioLoader.LoadFile(f);
                    string name = load.Scenario?.Name ?? "(malformed)";
                    string tags = load.Scenario != null && load.Scenario.Tags.Count > 0 ? $" [{string.Join(", ", load.Scenario.Tags)}]" : string.Empty;
                    output.WriteLine($"    {Relative(scenarios, f)}  {name}{tags}");
                }
            }
        }
        string suites = Path.Combine(root, "QA", "Suites");
        output.WriteLine("Suites:");
        if (Directory.Exists(suites))
        {
            foreach (string f in Directory.GetFiles(suites, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                output.WriteLine($"  {Path.GetFileNameWithoutExtension(f)}");
        }
        output.WriteLine("Actions:");
        foreach (ActionSpec s in ActionRegistry.CreateDefault().Specs)
            output.WriteLine($"  {s.Name}{(s.ServerCommand ? " (server QA command)" : string.Empty)}");
    }

    // 기능: 경로를 기준 디렉터리에 대한 상대 경로로 바꾼다.
    // 입력: root - 기준 디렉터리, path - 바꿀 경로.
    // 출력: root 기준 상대 경로.
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path);
}

// What `run X` / `validate X` means (D18):
//  - `suite:<name>`     QA/Suites/<name>.json ({ "scenarios": [ "Smoke/connect.json", "Combat" ] }, entries relative to
//                       QA/Scenarios only: never the repo root, where "Server" would be the Server/ source folder);
//  - `category:<name>`  every scenario under QA/Scenarios/<name>;
//  - a path             a .json file or a directory (as given, under QA/Scenarios, or under the repo root);
//  - a bare name        the category or the suite of that name. When both exist it is a tool error that names both
//                       choices: a run never silently picks one set over the other.
// The resolution is always described ("suite smoke: 3 scenarios") so the console shows what will run.
public sealed record ScenarioSelection(string Description, IReadOnlyList<SelectionItem> Items)
{
    public IReadOnlyList<string> Files => Items.Select(i => i.File).ToList();
}

// One scenario to run. A suite entry may also be an object (stress suites): { "path": "Stress/baseline.json",
// "parameterSet": 3, "variables": { "steadySeconds": 30 } } runs only that parameter set, with those variables set over
// the scenario's (and the parameter set's) values. The same file may appear with different options.
public sealed record SelectionItem(string File, int? ParameterSet = null, JsonElement? Variables = null)
{
    public string Key => File.ToLowerInvariant() + "|" + ParameterSet + "|" + (Variables?.GetRawText() ?? string.Empty);
}

public static class ScenarioCatalog
{
    public const int MaxScenarios = 1000;
    public const string SuitePrefix = "suite:";
    public const string CategoryPrefix = "category:";

    // 기능: 대상 지정을 시나리오 파일 경로 목록으로만 푼다(Select의 파일 목록 버전).
    // 입력: root - 저장소 루트, target - suite:/category:/경로/이름 지정.
    // 출력: 중복을 제거한 시나리오 파일 절대 경로 목록. 해석에 실패하면 QaToolException.
    public static List<string> Resolve(string root, string target) => Select(root, target).Files.ToList();

    // 기능: run / validate 대상 지정(suite:, category:, 파일·디렉터리 경로, 카테고리 또는 스위트의 bare 이름)을 실행할 시나리오 항목 목록으로 푼다.
    // 입력: root - 저장소 루트, target - 대상 지정 문자열.
    // 출력: 해석 설명과 시나리오 항목 목록. 못 찾거나 bare 이름이 스위트·카테고리 둘 다이면 QaToolException.
    public static ScenarioSelection Select(string root, string target)
    {
        string scenarios = Path.Combine(root, "QA", "Scenarios");
        if (target.StartsWith(SuitePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string name = target[SuitePrefix.Length..];
            return DoneItems($"suite {name}", Suite(root, name) ?? throw new QaToolException($"No suite '{name}' (QA/Suites/{name}.json)."));
        }
        if (target.StartsWith(CategoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string name = target[CategoryPrefix.Length..];
            string? dir = Category(scenarios, name) ?? throw new QaToolException($"No category '{name}' (QA/Scenarios/{name}/).");
            return Done($"category {Path.GetFileName(dir)}", Directory(dir));
        }

        bool bare = target.Length > 0 && target.IndexOfAny(new[] { '/', '\\' }) < 0 && !target.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && target != "." && target != "..";
        if (bare)
        {
            string? category = Category(scenarios, target);
            bool suite = File.Exists(SuiteFile(root, target));
            if (category != null && suite)
                throw new QaToolException($"'{target}' is both a suite and a category. Choose one: '{SuitePrefix}{target}' (QA/Suites/{target}.json) or '{CategoryPrefix}{Path.GetFileName(category)}' (QA/Scenarios/{Path.GetFileName(category)}/).");
            if (category != null) return Done($"category {Path.GetFileName(category)}", Directory(category));
            if (suite) return DoneItems($"suite {target}", Suite(root, target)!);
        }

        var files = new List<string>();
        string kind = AddPath(root, target, files) ?? throw new QaToolException(
            $"'{target}' is not a scenario file, directory, category (QA/Scenarios/<name>) or suite (QA/Suites/<name>.json).");
        return Done($"{kind} {target}", files);
    }

    // 기능: 파일 경로 목록을 옵션 없는 시나리오 항목으로 감싸 선택 결과를 만든다.
    // 입력: what - 해석 설명("category Combat"), files - 시나리오 파일 경로 목록.
    // 출력: 중복 제거·상한 적용된 선택 결과. 항목이 없으면 QaToolException.
    private static ScenarioSelection Done(string what, List<string> files) => DoneItems(what, files.Select(f => new SelectionItem(f)).ToList());

    // 기능: 항목 목록에서 같은 Key(파일·파라미터 세트·변수)의 중복을 제거하고 MaxScenarios개까지 잘라 선택 결과를 만든다.
    // 입력: what - 해석 설명, items - 시나리오 항목 목록.
    // 출력: 설명("…: N scenarios")과 항목이 담긴 선택 결과. 항목이 없으면 QaToolException.
    private static ScenarioSelection DoneItems(string what, List<SelectionItem> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<SelectionItem> distinct = items.Where(i => seen.Add(i.Key)).Take(MaxScenarios).ToList();
        if (distinct.Count == 0) throw new QaToolException($"No scenarios found for {what}.");
        return new ScenarioSelection($"{what}: {distinct.Count} scenario{(distinct.Count == 1 ? string.Empty : "s")}", distinct);
    }

    // 기능: 스위트 이름을 QA/Suites/<name>.json 경로로 만든다.
    // 입력: root - 저장소 루트, name - 스위트 이름.
    // 출력: 스위트 파일 경로(존재 여부는 확인하지 않음).
    private static string SuiteFile(string root, string name) => Path.Combine(root, "QA", "Suites", name + ".json");

    // 기능: QA/Scenarios 아래에서 이름이 같은(대소문자 무시) 카테고리 폴더를 찾는다.
    // 입력: scenarios - QA/Scenarios 경로, name - 카테고리 이름.
    // 출력: 카테고리 폴더 경로. 이름이 비었거나 경로 구분자·"."·".."이거나 폴더가 없으면 null.
    // The category folder with this name (any case), or null.
    private static string? Category(string scenarios, string name)
    {
        if (name.Length == 0 || name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name is "." or ".." || !System.IO.Directory.Exists(scenarios)) return null;
        return System.IO.Directory.GetDirectories(scenarios).FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));
    }

    // 기능: 디렉터리 아래(하위 포함)의 모든 .json 파일을 모은다.
    // 입력: dir - 검색할 디렉터리.
    // 출력: 대소문자 무시 정렬된 절대 경로 목록.
    private static List<string> Directory(string dir) =>
        System.IO.Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).Select(Path.GetFullPath).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    // 기능: QA/Suites/<name>.json의 "scenarios" 항목(경로 문자열 또는 옵션 객체)을 시나리오 항목 목록으로 푼다.
    // 입력: root - 저장소 루트, name - 스위트 이름.
    // 출력: 시나리오 항목 목록. 스위트 파일이 없거나 이름이 잘못되면 null, 형식이 틀리거나 항목을 못 찾으면 QaToolException.
    private static List<SelectionItem>? Suite(string root, string name)
    {
        string file = SuiteFile(root, name);
        if (name.Length == 0 || name.IndexOfAny(new[] { '/', '\\' }) >= 0 || !File.Exists(file)) return null;
        var items = new List<SelectionItem>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("scenarios", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                throw new QaToolException($"Suite {name}: 'scenarios' must be an array.");
            foreach (JsonElement e in list.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.Object)
                {
                    items.Add(SuiteItem(root, name, e));
                    continue;
                }
                if (e.ValueKind != JsonValueKind.String) throw new QaToolException($"Suite {name}: entries must be paths or {{ \"path\", \"parameterSet\"?, \"variables\"? }} objects.");
                var files = new List<string>();
                if (AddPath(root, e.GetString()!, files, scenariosOnly: true) == null)
                    throw new QaToolException($"Suite {name}: '{e.GetString()}' is not a scenario file or directory under QA/Scenarios.");
                items.AddRange(files.Select(f => new SelectionItem(f)));
            }
        }
        catch (JsonException e)
        {
            throw new QaToolException($"Suite {name}: malformed JSON: {e.Message}");
        }
        return items;
    }

    // 기능: 스위트의 객체 항목({ path, parameterSet?, variables? })을 검증해 시나리오 항목 하나로 만든다.
    // 입력: root - 저장소 루트, suite - 오류 메시지용 스위트 이름, e - 항목 JSON 객체.
    // 출력: 파일 경로·파라미터 세트·변수가 담긴 시나리오 항목. 필드가 잘못되거나 path가 QA/Scenarios 아래 파일이 아니면 QaToolException.
    // { "path": "<file under QA/Scenarios>", "parameterSet"?: N (1-based), "variables"?: { name: value } }.
    private static SelectionItem SuiteItem(string root, string suite, JsonElement e)
    {
        string? path = null;
        int? set = null;
        JsonElement? variables = null;
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "path" when p.Value.ValueKind == JsonValueKind.String:
                    path = p.Value.GetString();
                    break;
                case "parameterSet" when p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int n) && n >= 1 && n <= ScenarioDefinition.MaxParameterSets:
                    set = n;
                    break;
                case "variables" when p.Value.ValueKind == JsonValueKind.Object:
                    variables = p.Value.Clone();
                    break;
                default:
                    throw new QaToolException($"Suite {suite}: bad entry field '{p.Name}' (path: string, parameterSet: 1-{ScenarioDefinition.MaxParameterSets}, variables: object).");
            }
        }
        if (path == null) throw new QaToolException($"Suite {suite}: an entry object needs 'path'.");
        var files = new List<string>();
        if (AddPath(root, path, files, scenariosOnly: true) != "file")
            throw new QaToolException($"Suite {suite}: '{path}' is not a scenario file under QA/Scenarios (an entry with options names one file).");
        return new SelectionItem(files[0], set, variables);
    }

    // 기능: 대상을 파일 또는 디렉터리로 해석해 시나리오 파일 경로를 files에 보탠다. 후보는 그대로 → QA/Scenarios 아래 → 저장소 루트 아래 순이며, scenariosOnly면 QA/Scenarios 아래만 허용한다.
    // 입력: root - 저장소 루트, target - 파일·디렉터리 지정, files - 찾은 파일 경로를 보탤 목록, scenariosOnly - 스위트 항목처럼 QA/Scenarios 밖("..")을 막을지.
    // 출력: 파일이면 "file", 디렉터리면 "directory", 못 찾으면 null.
    // A file or a directory: as given, under QA/Scenarios, under the repo root (CLI targets). Suite entries
    // (scenariosOnly): under QA/Scenarios and nowhere else, and they may not climb out of it with "..".
    private static string? AddPath(string root, string target, List<string> files, bool scenariosOnly = false)
    {
        string scenarios = Path.Combine(root, "QA", "Scenarios");
        string[] candidates = scenariosOnly ? new[] { Path.Combine(scenarios, target) } : new[] { target, Path.Combine(scenarios, target), Path.Combine(root, target) };
        foreach (string candidate in candidates)
        {
            if (scenariosOnly && !Path.GetFullPath(candidate).StartsWith(Path.GetFullPath(scenarios) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(candidate))
            {
                files.Add(Path.GetFullPath(candidate));
                return "file";
            }
            if (System.IO.Directory.Exists(candidate))
            {
                files.AddRange(Directory(candidate));
                return "directory";
            }
        }
        return null;
    }
}
