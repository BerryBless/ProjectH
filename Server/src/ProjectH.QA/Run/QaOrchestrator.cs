using System.Diagnostics;
using System.Text.Json;

namespace ProjectH.QA;

public sealed class QaRunOptions
{
    public string RepoRoot { get; init; } = Directory.GetCurrentDirectory();
    public int? SeedOverride { get; init; }
    public string? AttachUrl { get; init; }
    public string? ServerDll { get; init; }
    public string? ReportDir { get; init; }
    public int PollMs { get; init; } = 100;
    public bool Verbose { get; init; }
    public bool WriteReport { get; init; } = true;
    // Test seams: a fake QA server and mock actors (null = the real ones).
    public Func<Uri, IQaServerClient>? ServerClientFactory { get; init; }
    public Func<string, string, IQaActor>? ActorFactory { get; init; }
    // QA-3 test seam: builds the DockerDbController for a container name (null = the real docker CLI).
    public Func<string, ProjectH.QA.Faults.DockerDbController>? DockerFactory { get; init; }
    public IRunControl? Control { get; init; }
    // UI (QA-2). Log lines by category (QA, Server, Actor, Network, Assertion); null = console only (CLI).
    public Action<string, string>? LogSink { get; init; }
    public Action<StepResult>? OnStepStarted { get; init; }
    public Action<StepResult>? OnStepFinished { get; init; }
    // The live run for the inspector (D22): set once the server answers and actors exist, null when cleanup starts.
    public Action<LiveRun?>? OnLive { get; init; }
    public int StartAtStep { get; init; }
    public bool DebugRun { get; init; }
    public bool UnsavedText { get; init; }
    // QA-3: a Skipped run exits 1 instead of 0 (CI that must not pass on a missing Docker).
    public bool FailOnSkip { get; init; }
    // QA-4: the default Unity Development player (--unity-exe) and a fake receiver per port (tests).
    public string? UnityExe { get; init; }
    public Func<int, HttpMessageHandler?>? UnityHandlerFactory { get; init; }
    // D30, when no Control is given (CLI): asks on the terminal; null = nobody to ask (manual checks are SKIPPED).
    public Func<StepDefinition, string, CancellationToken, Task<ManualCheckAnswer>>? ManualPrompt { get; init; }
    // QA-5 D31: the parameter set of this run (merged over the scenario variables) and its 0-based index; D32: the run's
    // place in a batch for the report ("run 3/10, iteration 3").
    public JsonElement? Parameters { get; init; }
    public int? ParameterIndex { get; init; }
    public string? BatchLabel { get; init; }
    // QA-5 D33: append the run to <report root>/history (only when reports are written too: tests that write no report
    // never touch a history folder).
    public bool History { get; init; } = true;
    // Stress: variables set over the scenario and the parameter set (--set, a suite entry's variables). Null = none.
    public IReadOnlyDictionary<string, JsonElement>? VariableOverrides { get; init; }
}

// D32: a batch starts several runs within one second. Run ids keep their format (qa-yyyyMMdd-HHmmss-xxxx: the UI and
// the report links match it) and are made unique: not issued before by this process and no such report folder yet.
// The set holds only ids of the current and the previous second (ids embed the time, older ones cannot recur).
internal static class RunIds
{
    private static readonly object s_lock = new();   // only lock here; held for the set update alone
    private static readonly HashSet<string> s_recent = new(StringComparer.Ordinal);
    private static string s_second = string.Empty;

    // 기능: 보고서 폴더가 아직 없고 이 프로세스가 최근(현재·직전 초)에 발급하지 않은 실행 ID를 만든다.
    // 입력: reportRoot - 보고서 루트 폴더(같은 ID의 폴더가 있는지 확인).
    // 출력: 고유한 실행 ID("qa-yyyyMMdd-HHmmss-xxxx"). 1000회 넘게 겹치면 마지막 후보를 그대로 준다.
    public static string Claim(string reportRoot)
    {
        // 65536 ids per second: a few attempts always find a free one; the bound only guards against a broken clock.
        for (int attempt = 0; ; attempt++)
        {
            string id = QaOrchestrator.NewRunId(DateTime.Now);
            // File I/O outside the lock (lock rules).
            if (attempt < 100 && Directory.Exists(Path.Combine(reportRoot, id))) continue;
            lock (s_lock)
            {
                string second = id[..18];   // "qa-yyyyMMdd-HHmmss"
                if (second != s_second)
                {
                    string previous = s_second;
                    s_recent.RemoveWhere(r => !r.StartsWith(previous, StringComparison.Ordinal));
                    s_second = second;
                }
                if (s_recent.Add(id) || attempt >= 1000) return id;
            }
        }
    }
}

// What the UI inspector may touch from its HTTP threads: the QA URL (it builds its own client) and the actors'
// published snapshot (ActorManager.Snapshot + immutable ActorState). Nothing else of the run.
public sealed record LiveRun(string RunId, Uri QaUrl, ActorManager Actors)
{
    // Stress D39: the inspector reads the server less often (RunSession).
    public bool Stress { get; init; }
}

// The scenario timeout counts running time only: while the gate holds the run (pause, breakpoint, held failure) the
// deadline is disarmed and re-armed with what was left. Used only from the run flow (IRunControl.PausedChanged).
internal sealed class PausableDeadline
{
    private readonly CancellationTokenSource _cts;
    private readonly Stopwatch _since = Stopwatch.StartNew();
    private TimeSpan _remaining;

    // 기능: 시나리오 시간 한도를 토큰 소스에 걸고 경과 시간 측정을 시작한다.
    // 입력: cts - 시간 초과 시 취소할 토큰 소스, timeout - 시나리오 시간 한도.
    // 출력: 한도가 작동 중인 마감 객체.
    public PausableDeadline(CancellationTokenSource cts, TimeSpan timeout)
    {
        _cts = cts;
        _remaining = timeout;
        cts.CancelAfter(timeout);
    }

    // 기능: 일시정지 시작이면 경과분을 남은 시간에서 빼고 마감을 해제하며, 재개면 남은 시간으로 마감을 다시 건다. 이미 취소된 토큰은 건드리지 않는다.
    // 입력: paused - true면 일시정지 시작, false면 재개.
    // 출력: 반환값 없음. 토큰 소스의 CancelAfter가 바뀐다.
    public void SetPaused(bool paused)
    {
        if (_cts.IsCancellationRequested) return;
        if (paused)
        {
            _remaining -= _since.Elapsed;
            _cts.CancelAfter(Timeout.InfiniteTimeSpan);
        }
        else
        {
            _since.Restart();
            _cts.CancelAfter(_remaining > TimeSpan.Zero ? _remaining : TimeSpan.Zero);
        }
    }
}

// One scenario run end to end: server (launch or attach) → actors → steps → state dump → cleanup → report.
// Everything here runs on the caller's async flow. Cleanup always runs and uses its own short timeouts (the run's
// tokens may already be cancelled); cleanup failures are reported apart from the result (request §113-114) and do not
// change the exit code, except a launched server that could not be stopped (D18: exit 2).
public sealed class QaOrchestrator
{
    public const int ReportEventCount = 50;
    public const int ReportLogLines = 200;
    private static readonly TimeSpan CleanupRequestTimeout = TimeSpan.FromSeconds(5);

    private readonly QaRunOptions _options;
    private readonly ActionRegistry _registry;
    private readonly MarkerStore _markers;
    private readonly TextWriter _out;

    // 기능: 한 실행을 수행할 오케스트레이터를 만든다.
    // 입력: options - 실행 옵션, registry - Action 목록, markers - 위치 이름 저장소, output - 콘솔 출력.
    // 출력: RunAsync를 기다리는 오케스트레이터.
    public QaOrchestrator(QaRunOptions options, ActionRegistry registry, MarkerStore markers, TextWriter output)
    {
        _options = options;
        _registry = registry;
        _markers = markers;
        _out = output;
    }

    // 기능: 시각과 16비트 난수로 실행 ID를 만든다.
    // 입력: now - ID에 넣을 현재 시각.
    // 출력: "qa-yyyyMMdd-HHmmss-xxxx" 형식의 ID(고유성은 RunIds.Claim이 보장).
    public static string NewRunId(DateTime now) => $"qa-{now:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x10000):x4}";

    // 기능: 시나리오 한 번을 끝까지 수행한다: 서버 시작 또는 attach → 액터 생성 → 단계 실행 → 실패 시 상태 덤프 → 정리(그룹·액터·프록시·DB·서버) → 기준선 기록 → 보고서 작성.
    // 입력: scenario - 실행할 시나리오, warnings - 검증 단계의 경고(보고서에 기록), userToken - 사용자 중단(Ctrl+C·Stop) 토큰.
    // 출력: 상태·종료 코드·단계 결과·정리 결과가 채워진 RunReport. 서버를 못 멈추거나 보고서를 못 쓰면 ExitCode 2.
    public async Task<RunReport> RunAsync(ScenarioDefinition scenario, IReadOnlyList<ValidationIssue> warnings, CancellationToken userToken)
    {
        var clock = Stopwatch.StartNew();
        int seed = _options.SeedOverride ?? scenario.Seed ?? Random.Shared.Next(1, int.MaxValue);
        string reportRoot = _options.ReportDir ?? Path.Combine(_options.RepoRoot, "QA", "Reports");
        var report = new RunReport
        {
            RunId = RunIds.Claim(reportRoot),
            Parameters = _options.Parameters,
            ParameterSet = _options.ParameterIndex + 1,
            Batch = _options.BatchLabel,
            Scenario = scenario.Name,
            ScenarioFile = scenario.SourcePath,
            Description = scenario.Description,
            Tags = scenario.Tags,
            Seed = seed,
            Started = DateTimeOffset.Now,
        };
        foreach (ValidationIssue w in warnings) report.Warnings.Add(w.ToString());
        // The report folder is known from the start: Unity screenshots and player logs are written into it during the run.
        string reportDir = Path.Combine(reportRoot, report.RunId);
        IRunControl control = _options.Control ?? new RunGate(honorBreakpoints: false) { Prompt = _options.ManualPrompt };
        string batch = _options.BatchLabel != null ? $"  [{_options.BatchLabel}]" : string.Empty;
        string parameterText = (_options.ParameterIndex is int pi ? $"  parameters[{pi + 1}] {BatchSummary.Compact(_options.Parameters)}" : string.Empty)
            + (_options.VariableOverrides is { Count: > 0 } vo ? $"  set {BatchSummary.Compact(JsonPath.From(vo))}" : string.Empty);
        _out.WriteLine($"== {scenario.Name}  runId {report.RunId}  seed {seed}{parameterText}{batch}");

        // D31: the parameter set is merged over the scenario variables (a parameter wins over a variable of its name).
        var variables = new Dictionary<string, JsonElement>(scenario.Variables, StringComparer.Ordinal);
        if (_options.Parameters is JsonElement { ValueKind: JsonValueKind.Object } set)
        {
            foreach (JsonProperty p in set.EnumerateObject()) variables[p.Name] = p.Value;
        }
        // Stress: --set / a suite entry's variables win over both (recorded with the parameters: a different value is a
        // different baseline).
        if (_options.VariableOverrides is { Count: > 0 } overrides)
        {
            var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (_options.Parameters is JsonElement { ValueKind: JsonValueKind.Object } ps) foreach (JsonProperty p in ps.EnumerateObject()) merged[p.Name] = p.Value;
            foreach (var pair in overrides)
            {
                variables[pair.Key] = pair.Value;
                merged[pair.Key] = pair.Value;
            }
            report.Parameters = JsonPath.From(merged);
            report.Overrides = JsonPath.From(overrides);
        }

        (report.GitCommit, report.GitDirty, report.GitError) = await GitInfo.ReadAsync(_options.RepoRoot).ConfigureAwait(false);

        // Stress D39: the live logs keep step lines and warnings only (the report's server log ring keeps everything).
        bool stress = scenario.Stress;
        bool Quiet(string line) => stress && !IsWarning(line);

        // 기능: 실행 로그 한 줄을 QA 분류로 내보낸다(스트레스 실행에서는 경고성 줄만).
        // 입력: line - 로그 줄.
        // 출력: 반환값 없음. --verbose면 콘솔에, LogSink가 있으면 "QA" 분류로 전달된다.
        void Log(string line)
        {
            if (Quiet(line)) return;
            if (_options.Verbose) _out.WriteLine("   " + line);
            _options.LogSink?.Invoke("QA", line);
        }

        // 기능: 액터 로그 한 줄을 Actor 분류로 내보낸다(스트레스 실행에서는 경고성 줄만).
        // 입력: line - 로그 줄.
        // 출력: 반환값 없음. --verbose면 콘솔에, LogSink가 있으면 "Actor" 분류로 전달된다.
        void ActorLog(string line)
        {
            if (Quiet(line)) return;
            if (_options.Verbose) _out.WriteLine("   " + line);
            _options.LogSink?.Invoke("Actor", line);
        }

        bool attach = _options.AttachUrl != null || string.Equals(scenario.Server.Mode, ServerSpec.Attach, StringComparison.OrdinalIgnoreCase);
        report.ServerMode = attach ? ServerSpec.Attach : ServerSpec.Launch;
        report.UnsavedText = _options.UnsavedText;
        Func<Uri, IQaServerClient> clientFactory = _options.ServerClientFactory ?? (uri => new QaServerClient(uri));
        LaunchedServer? launched = null;
        IQaServerClient? client = null;
        ActorManager? actors = null;
        RunContext? run = null;
        bool serverStopFailed = false;

        try
        {
            // Server.
            Uri qaUrl;
            int gamePort;
            string gameHost;
            if (attach)
            {
                string url = _options.AttachUrl ?? scenario.Server.QaUrl ?? throw new QaToolException("Attach mode needs server.qaUrl or --attach URL.");
                if (!Uri.TryCreate(url.EndsWith('/') ? url : url + "/", UriKind.Absolute, out Uri? parsed)) throw new QaToolException($"Bad QA URL '{url}'.");
                qaUrl = parsed;
                client = clientFactory(qaUrl);
                JsonElement health = await HealthAsync(client, userToken).ConfigureAwait(false);
                gameHost = scenario.Server.Host ?? (qaUrl.IsLoopback ? "127.0.0.1" : qaUrl.Host);
                gamePort = scenario.Server.GamePort ?? ReadInt(health, "gamePort") ?? throw new QaToolException("Attach: no gamePort (scenario server.gamePort or /qa/health).");
                report.ServerHealth = health;
            }
            else
            {
                string dll = _options.ServerDll ?? ServerLocator.FindServerDll(_options.RepoRoot)
                    ?? throw new QaToolException($"ProjectH.Server.dll not found under {Path.Combine(_options.RepoRoot, "Server", "src", "ProjectH.Server", "bin")}. Build first: dotnet build Server/ProjectH.Server.slnx (or pass --server-dll).");
                Action<string>? serverLog = null;
                if (_options.Verbose || _options.LogSink != null)
                {
                    serverLog = line =>
                    {
                        if (Quiet(line)) return;
                        if (_options.Verbose) _out.WriteLine("   [server] " + line);
                        _options.LogSink?.Invoke("Server", line);
                    };
                }
                launched = new LaunchedServer(dll, seed, LaunchOptions(scenario), clientFactory, serverLog, Log);
                report.ServerArguments = launched.Arguments;
                ProjectH.QA.Faults.ServerStartInfo started = await launched.LaunchAsync(userToken).ConfigureAwait(false);
                report.ServerPid = started.Pid;
                gamePort = started.GamePort;
                qaUrl = launched.QaUrl!;
                gameHost = "127.0.0.1";
                client = launched.Client!;
                report.ServerHealth = await HealthAsync(client, userToken).ConfigureAwait(false);
            }
            report.QaUrl = qaUrl.ToString();
            report.GamePort = gamePort;
            report.ServerVersion = report.ServerHealth is JsonElement h && JsonPath.Child(h, "version") is JsonElement v ? QaJson.Text(v) : null;
            Log($"server {report.ServerMode} qa={qaUrl} game={gameHost}:{gamePort} version={report.ServerVersion}");

            var events = new EventCursor(client);
            if (attach) await events.SkipExistingAsync(userToken).ConfigureAwait(false);

            actors = new ActorManager(seed, ActorLog, _options.ActorFactory)
            {
                Unity = new UnitySettings
                {
                    RepoRoot = _options.RepoRoot,
                    DefaultExe = _options.UnityExe,
                    ShotDir = Path.Combine(reportDir, "screenshots"),
                    LogDir = reportDir,
                    HandlerFactory = _options.UnityHandlerFactory,
                },
            };
            run = new RunContext(report.RunId, seed, client, actors, _markers, variables, Log)
            {
                RepoRoot = _options.RepoRoot,
                ScenarioPath = scenario.SourcePath,
                Control = control,
                ReportDirectory = reportDir,
                Events = events,
                GameHost = gameHost,
                GamePort = gamePort,
                PollIntervalMs = _options.PollMs,
                Db = new ProjectH.QA.Faults.DbFaultHub(_options.DockerFactory),
                EventsEnabled = !(launched != null ? LaunchOptions(scenario) : scenario.Server.Options).TryGetValue("Qa:Events", out string? ev)
                    || !string.Equals(ev, "false", StringComparison.OrdinalIgnoreCase),
            };
            run.Stress.StressMode = stress;
            if (launched != null)
            {
                run.ServerControl = launched;
                launched.Run = run;
            }
            foreach (ActorSpec a in scenario.Actors)
            {
                await actors.CreateAsync(a.Id, a.Type, userToken, a.Unity).ConfigureAwait(false);
                if (a.Proxy) run.Network.EnableProxy(a.Id);
            }
            await TryMarkAsync(client, $"QA run {report.RunId} start: {scenario.Name} seed {seed}", report.RunId).ConfigureAwait(false);
            _options.OnLive?.Invoke(new LiveRun(report.RunId, qaUrl, actors) { Stress = stress });

            using var scenarioCts = CancellationTokenSource.CreateLinkedTokenSource(userToken);
            var deadline = new PausableDeadline(scenarioCts, TimeSpan.FromSeconds(scenario.TimeoutSeconds));
            // Chained: the UI session listens too (it set its own handler before the run).
            Action<bool>? listener = control.PausedChanged;
            control.PausedChanged = paused =>
            {
                deadline.SetPaused(paused);
                listener?.Invoke(paused);
            };
            var runner = new ScenarioRunner(_registry, control, PrintStep, new RunnerOptions
            {
                StartAtStep = _options.StartAtStep,
                DebugRun = _options.DebugRun,
                OnStepStarted = _options.OnStepStarted,
            });
            try
            {
                report.Status = await runner.RunAsync(scenario, run, report, userToken, scenarioCts.Token).ConfigureAwait(false);
            }
            finally
            {
                control.PausedChanged = listener;
            }
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested)
        {
            report.Status = RunStatus.Cancelled;
            report.ToolError = "Stopped by the user before the steps finished.";
        }
        catch (QaToolException e)
        {
            report.Status = RunStatus.Error;
            report.ToolError = e.Message;
        }
        catch (QaApiException e)
        {
            report.Status = RunStatus.Error;
            report.ToolError = $"QA API: {e.Message}";
        }
        catch (QaStepException e)
        {
            report.Status = RunStatus.Error;
            report.ToolError = e.Message;
        }
        catch (Exception e)
        {
            report.Status = RunStatus.Error;
            report.ToolError = $"Internal error: {e}";
        }
        finally
        {
            _options.OnLive?.Invoke(null);
            // Stress D38: group workloads (re-arm, churn) first, before anything they use is closed.
            if (run != null && run.Groups.WorkloadCount > 0)
            {
                bool groupsStopped = await run.Groups.StopAsync(null, GroupRegistry.StopTimeout).ConfigureAwait(false);
                report.Cleanup.Add(new CleanupResult("groups", groupsStopped, groupsStopped ? "group workloads stopped" : "a group workload did not stop in time"));
            }
            // A workload that ended on an error is a warning even when no stopGroup step looked at it.
            if (run != null)
            {
                foreach (ActorGroup g in run.Groups.All.Where(g => Interlocked.Read(ref g.Stats.WorkloadFailures) > 0))
                    report.Warnings.Add($"Stress: a workload of group '{g.Name}' ended with an error: {g.Stats.LastFailure}");
            }
            // D41: the launched server died on its own (not a stopServer / killServer step).
            if (run != null && launched != null && !launched.Running && !launched.StoppedByScenario && launched.Starts > 0)
            {
                run.Stress.Crash = new CrashRecord
                {
                    Utc = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    ExitCode = launched.ExitCode, StepId = run.CurrentStepId, Actors = actors?.Count ?? 0,
                    ActorsJoined = actors?.All.Count(a => a.State.Joined) ?? 0, LastSample = run.LastSample, LastPhase = run.LastPhase,
                    LogTail = launched.Log.Tail(ReportLogLines),
                };
                if (report.Status is RunStatus.Passed or RunStatus.Skipped) report.Status = RunStatus.Failed;
                report.Warnings.Add($"The server process exited during step {run.CurrentStepId} (exit code {launched.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}): see Crash.");
            }
            // A restart step may have replaced the server: talk to the current one.
            if (run != null) client = run.Server;
            // State at the failure (request §103-104), before anything is closed.
            if (report.Status is not (RunStatus.Passed or RunStatus.Skipped) && actors != null) report.StateDump = await DumpAsync(launched == null || launched.Running ? client : null, actors).ConfigureAwait(false);
            // A scenario may have stopped the launched server (stopServer, killServer): nothing to ask then, and each
            // refused connection would cost seconds.
            if (client != null && (launched == null || launched.Running))
            {
                report.Metrics = await TryAsync(t => client.GetMetricsAsync(null, t)).ConfigureAwait(false);
                if (run?.Events != null)
                {
                    await TryAsync(async t => { await run.Events.FetchAsync(t).ConfigureAwait(false); return 0; }).ConfigureAwait(false);
                    report.Events.AddRange(run.Events.Recent.TakeLast(ReportEventCount).Select(e => e.Raw));
                    report.EventsDroppedByServer = run.Events.ServerDropped;
                }
                await TryMarkAsync(client, $"QA run {report.RunId} end: {report.Status}", report.RunId).ConfigureAwait(false);
            }
            if (run != null)
            {
                foreach (var pair in run.Variables) report.Variables[pair.Key] = pair.Value;
                report.Screenshots.AddRange(run.Screenshots);
                foreach (ManualCheckRecord m in run.ManualChecks) report.ManualChecks.Add(JsonPath.From(m));
                report.Warnings.AddRange(run.Warnings.Items);
                if (run.Warnings.Dropped > 0) report.Warnings.Add($"{run.Warnings.Dropped} more warnings were not kept.");
                if (stress || run.Stress.Phases.Count > 0 || run.Stress.Crash != null)
                {
                    if (run.Groups.All.Any()) run.Stress.Groups = run.Groups.All.ToDictionary(g => g.Name, g => (object)run.Groups.Describe(g.Name));
                    report.Stress = run.Stress;
                }
            }

            if (actors != null)
            {
                // QA-4: launched Unity players first (they are connected to the server that is stopped below).
                foreach (CleanupResult unityLine in await actors.StopUnityAsync().ConfigureAwait(false)) report.Cleanup.Add(unityLine);
                try
                {
                    bool stopped = await actors.StopAsync().ConfigureAwait(false);
                    report.Cleanup.Add(new CleanupResult("actors", stopped, stopped ? $"{actors.Count} closed" : "actor pump did not stop in time"));
                }
                catch (Exception e)
                {
                    report.Cleanup.Add(new CleanupResult("actors", false, e.Message));
                }
                await actors.DisposeAsync().ConfigureAwait(false);
            }
            if (run != null)
            {
                // Request §113: faults are always cleared and every proxy closed; DB containers this run stopped are
                // started again (own timeouts). Reported apart from the result.
                try
                {
                    bool hadProxies = run.Network.ProxiedAliases.Any();
                    List<string> problems = await run.Network.CloseAllAsync().ConfigureAwait(false);
                    if (hadProxies) report.Cleanup.Add(new CleanupResult("network", problems.Count == 0, problems.Count == 0 ? "faults cleared, proxies closed" : string.Join("; ", problems)));
                }
                catch (Exception e)
                {
                    report.Cleanup.Add(new CleanupResult("network", false, e.Message));
                }
                foreach (CleanupResult r in await run.Db.RestoreAllAsync().ConfigureAwait(false)) report.Cleanup.Add(r);
            }
            if (launched != null)
            {
                (bool stopped, string message) = await launched.StopForCleanupAsync().ConfigureAwait(false);
                serverStopFailed = !stopped;
                if (launched.Starts > 1) message += $" ({launched.Starts} starts in this run)";
                report.Cleanup.Add(new CleanupResult("server", stopped, message));
                report.ServerLogTail = launched.Log.Tail(ReportLogLines);
                launched.Dispose();
            }
            else
            {
                (client as IDisposable)?.Dispose();
            }
        }

        report.ExitCode = serverStopFailed ? 2 : RunReport.ExitCodeFor(report.Status, _options.FailOnSkip);
        report.DurationMs = clock.ElapsedMilliseconds;
        if (report.Stress != null) report.Stress.Summary = StressSummary.From(report.Stress.Phases, report.Status.ToString().ToUpperInvariant());
        RecordBaseline(scenario, report, reportRoot, run?.Variables ?? (IReadOnlyDictionary<string, JsonElement>)variables);
        PrintSummary(report);
        if (report.Stress?.Summary is StressSummary ss)
        {
            _out.WriteLine("   " + ss.Line());
            _options.LogSink?.Invoke("QA", ss.Line());
        }
        if (report.Baseline != null)
        {
            foreach (string w in report.Baseline.Warnings) _out.WriteLine($"   WARNING {w}");
            if (report.Baseline.PreviousRunId != null && report.Baseline.Warnings.Count == 0)
                _out.WriteLine($"   baseline: compared with {report.Baseline.PreviousRunId}, no metric worse by more than {report.Baseline.WarnPercent:0.#}%");
        }
        if (_options.WriteReport)
        {
            try
            {
                string dir = reportDir;
                report.ReportDirectory = dir;
                ReportWriter.Write(report, dir);
                _out.WriteLine($"   report: {Path.Combine(dir, "report.html")}");
            }
            catch (Exception e)
            {
                _out.WriteLine($"   report could not be written: {e.Message}");
                report.ExitCode = 2;
            }
        }
        return report;
    }

    // 기능: 실행을 기준선 히스토리에 기록하고 이전 PASSED 실행과 비교해 report.Baseline과 경고를 채운다. 보고서·히스토리가 꺼져 있으면 아무것도 하지 않고, 파일이 없거나 미저장 텍스트·Run From Step이면 Note만 남기고 기록하지 않는다.
    // 입력: scenario - 실행한 시나리오, report - 채울 보고서, reportRoot - 보고서 루트(history 폴더의 부모), variables - 실행 종료 시점의 변수.
    // 출력: 반환값 없음. report.Baseline·Warnings가 갱신된다. 결과·종료 코드는 바뀌지 않는다.
    // D33: one history line per run and the comparison with the previous PASSED run (same scenario file, same
    // parameter set). Skipped when the run is not the file's content (unsaved editor text, Run From Step) or has no
    // file. Never changes the result or the exit code: problems become warnings.
    private void RecordBaseline(ScenarioDefinition scenario, RunReport report, string reportRoot, IReadOnlyDictionary<string, JsonElement> variables)
    {
        if (!_options.WriteReport || !_options.History) return;
        string? skip = scenario.SourcePath.Length == 0 ? "the scenario has no file (new editor text)"
            : _options.UnsavedText ? "the run used unsaved editor text"
            : _options.StartAtStep > 0 ? "Run From Step did not run the whole scenario"
            : null;
        if (skip != null)
        {
            report.Baseline = new BaselineReport { Note = $"Not recorded in the history: {skip}." };
            return;
        }
        try
        {
            string key = BaselineHistory.Key(_options.RepoRoot, scenario.SourcePath);
            string dir = Path.Combine(reportRoot, "history");
            HistoryEntry entry = BaselineHistory.EntryFor(report, scenario, BaselineHistory.ScenarioFile(_options.RepoRoot, scenario.SourcePath), variables);
            (HistoryEntry? previous, string? warning) = BaselineHistory.Record(dir, key, entry);
            BaselineReport baseline = BaselineHistory.Compare(entry, previous, scenario);
            baseline.HistoryFile = Path.Combine(dir, key + ".jsonl");
            baseline.Recorded = warning == null || !warning.StartsWith("Baseline history not written", StringComparison.Ordinal);
            if (warning != null) baseline.Warnings.Insert(0, warning);
            report.Baseline = baseline;
            report.Warnings.AddRange(baseline.Warnings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            report.Baseline = new BaselineReport { Note = $"Baseline failed: {e.Message}" };
            report.Warnings.Add($"Baseline failed: {e.Message}");
        }
    }

    // 기능: 서버 실행 옵션을 정한다. 스트레스 시나리오이고 Qa:Events가 지정되지 않았으면 "Qa:Events"="false"를 더한다.
    // 입력: scenario - 시나리오 정의.
    // 출력: 서버에 넘길 설정 재정의 사전.
    // D39: a stress scenario's server has no QA events unless the scenario turns them on (tick diff cost, §150).
    public static IReadOnlyDictionary<string, string> LaunchOptions(ScenarioDefinition scenario)
    {
        if (!scenario.Stress || scenario.Server.Options.ContainsKey("Qa:Events")) return scenario.Server.Options;
        var options = new Dictionary<string, string>(scenario.Server.Options, StringComparer.OrdinalIgnoreCase) { ["Qa:Events"] = "false" };
        return options;
    }

    // 기능: 로그 줄이 warn/fail/error/exception/crit 중 하나를 담고 있는지(대소문자 무시) 판정한다.
    // 입력: line - 로그 줄.
    // 출력: 경고성 줄이면 true.
    // A line worth showing in a stress run's quiet live log.
    public static bool IsWarning(string line) =>
        line.Contains("warn", StringComparison.OrdinalIgnoreCase) || line.Contains("fail", StringComparison.OrdinalIgnoreCase)
        || line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
        || line.Contains("crit", StringComparison.OrdinalIgnoreCase);

    // 기능: /qa/health를 읽어 ok 응답인지와 QA 모드가 켜져 있는지 확인한다.
    // 입력: client - QA 서버 Client, token - 취소 토큰.
    // 출력: health 응답 JSON. ok=false면 QaApiException, qaMode=false면 QaToolException.
    private static async Task<JsonElement> HealthAsync(IQaServerClient client, CancellationToken token)
    {
        JsonElement health = await client.GetHealthAsync(token).ConfigureAwait(false);
        JsonElementHelpers.RequireOk(health);
        if (JsonPath.Child(health, "qaMode") is { ValueKind: JsonValueKind.False }) throw new QaToolException("The server answered but QA mode is off.");
        return health;
    }

    // 기능: 실패 시점의 상태 덤프를 만든다: 모든 액터의 상태와, Client가 주어지면 /qa/players·/qa/match.
    // 입력: client - 질의할 QA Client(null이면 액터 상태만), actors - 액터 관리자.
    // 출력: StateDump. 서버 질의가 실패하면 Error에 메시지만 남긴다.
    private async Task<StateDump> DumpAsync(IQaServerClient? client, ActorManager actors)
    {
        var dump = new StateDump();
        foreach (IQaActor a in actors.All) dump.Actors.Add(a.State);
        if (client == null) return dump;
        try
        {
            using var cts = new CancellationTokenSource(CleanupRequestTimeout);
            dump.Players = await client.GetPlayersAsync(cts.Token).ConfigureAwait(false);
            dump.Match = await client.GetMatchAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            dump.Error = e.Message;
        }
        return dump;
    }

    // 기능: 정리 단계의 서버 호출을 CleanupRequestTimeout 안에서 실행하고 어떤 예외도 삼킨다.
    // 입력: call - 취소 토큰을 받아 값을 돌려주는 비동기 호출.
    // 출력: 호출 결과. 실패·시간 초과면 null.
    private static async Task<T?> TryAsync<T>(Func<CancellationToken, Task<T>> call) where T : struct
    {
        try
        {
            using var cts = new CancellationTokenSource(CleanupRequestTimeout);
            return await call(cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // 기능: 서버 로그에 실행 ID 표식을 남기는 "mark" QA 명령을 보낸다(최선 노력).
    // 입력: client - QA 서버 Client, text - 표식 문장, runId - 명령에 붙일 실행 ID.
    // 출력: 반환값 없음. 실패는 무시된다.
    // Request §69: the run id in the server log at start and end (best effort).
    private static async Task TryMarkAsync(IQaServerClient client, string text, string runId)
    {
        try
        {
            using var cts = new CancellationTokenSource(CleanupRequestTimeout);
            await client.CommandAsync("mark", null, JsonPath.From(new { text }), runId, cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Correlation only.
        }
    }

    // 기능: JSON 객체의 숫자 필드를 int로 읽는다.
    // 입력: e - JSON 객체, name - 필드 이름(대소문자 무시).
    // 출력: 정수로 잘라낸 값. 없거나 숫자가 아니면 null.
    private static int? ReadInt(JsonElement e, string name) =>
        JsonPath.Child(e, name) is JsonElement v && Comparison.TryNumber(v, out double d) ? (int)d : null;

    // 기능: 끝난 단계의 결과 줄(실패·오류면 메시지·Expected·Actual 포함)을 LogSink 또는 콘솔에 내보내고 OnStepFinished를 호출한다.
    // 입력: r - 단계 결과.
    // 출력: 반환값 없음. 줄이 LogSink(Action별 분류)나 콘솔에 기록된다.
    private void PrintStep(StepResult r)
    {
        _options.OnStepFinished?.Invoke(r);
        var lines = new List<string> { r.Line() };
        if (r.Status is StepStatus.Failed or StepStatus.Error)
        {
            if (r.Message != null) lines.Add($"   {r.Message}");
            if (r.Expected != null) lines.Add($"   Expected: {r.Expected}");
            if (r.Actual != null) lines.Add($"   Actual:   {r.Actual}");
        }
        else if ((_options.Verbose || _options.LogSink != null) && r.Message != null)
        {
            lines.Add($"   {r.Message}");
        }
        if (_options.LogSink != null)
        {
            string category = LogCategory(r.Action);
            foreach (string line in lines) _options.LogSink(category, line);
            return;
        }
        foreach (string line in lines) _out.WriteLine(line);
    }

    // 기능: 단계 Action 이름을 UI 로그 분류(Assertion / Network / Actor / QA)로 바꾼다.
    // 입력: action - 단계 Action 이름.
    // 출력: 분류 문자열.
    // D21 categories for step lines.
    public static string LogCategory(string action) => action switch
    {
        "assert" or "waitFor" or "save" or "waitForEvent" => "Assertion",
        "connect" or "disconnect" or "reconnect" or "connectAll" or "disconnectAll" or "pauseInput" or "resumeInput"
            or "networkFault" or "clearNetworkFault" or "blockNetwork" or "unblockNetwork" or "dropConnection" or "sendInvalidPackets" => "Network",
        "moveTo" or "moveVector" or "look" or "aim" or "fire" or "stopFire" or "press" or "release" or "switchWeapon" or "jump"
            or "sprint" or "crouch" or "holdInteract" or "build" or "buildEdit" or "spawnActors" or "playInputs" or "moveVectorAll" => "Actor",
        _ => "QA",
    };

    // 기능: 실행 요약(도구 오류, 실패한 정리, 상태·실패 단계·시드·runId·소요 ms)을 콘솔에 쓴다.
    // 입력: r - 완성된 실행 보고서.
    // 출력: 반환값 없음. 요약 줄이 output에 기록된다.
    private void PrintSummary(RunReport r)
    {
        if (r.ToolError != null) _out.WriteLine($"   {r.ToolError}");
        foreach (CleanupResult c in r.Cleanup.Where(c => !c.Ok)) _out.WriteLine($"   cleanup {c.Name} FAILED: {c.Message}");
        string failed = r.Failure != null ? $"  failed at step {r.Failure.StepIndex + 1:00} ({r.Failure.StepId})" : string.Empty;
        string skipped = r.Status == RunStatus.Skipped && r.SkipReason != null ? $" ({r.SkipReason})" : string.Empty;
        _out.WriteLine($"== {r.Scenario}: {r.Status.ToString().ToUpperInvariant()}{skipped}{failed}  seed {r.Seed}  runId {r.RunId}  {r.DurationMs} ms");
    }
}

public static class ServerLocator
{
    // 기능: Server/src/ProjectH.Server/bin 아래 Debug·Release 중 가장 최근에 빌드된 ProjectH.Server.dll을 찾는다.
    // 입력: repoRoot - 저장소 루트.
    // 출력: DLL 경로. 둘 다 없으면 null.
    // The newest built ProjectH.Server.dll (Debug or Release) under Server/src/ProjectH.Server/bin.
    public static string? FindServerDll(string repoRoot)
    {
        string bin = Path.Combine(repoRoot, "Server", "src", "ProjectH.Server", "bin");
        string? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (string config in new[] { "Debug", "Release" })
        {
            string path = Path.Combine(bin, config, "net10.0", "ProjectH.Server.dll");
            if (!File.Exists(path)) continue;
            DateTime time = File.GetLastWriteTimeUtc(path);
            if (time <= bestTime) continue;
            best = path;
            bestTime = time;
        }
        return best;
    }

    // 기능: 시작 폴더에서 위로 올라가며 Server/ProjectH.Server.slnx가 있는 폴더를 찾는다.
    // 입력: start - 탐색을 시작할 폴더.
    // 출력: 저장소 루트 경로. 못 찾으면 null.
    // The repository root: the nearest folder up from `start` that has Server/ProjectH.Server.slnx.
    public static string? FindRepoRoot(string start)
    {
        for (DirectoryInfo? d = new(start); d != null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "Server", "ProjectH.Server.slnx"))) return d.FullName;
        }
        return null;
    }
}

// Request §99-100. Git missing or not a repository: recorded, never fatal.
public static class GitInfo
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // 기능: git rev-parse HEAD와 git status --porcelain으로 커밋 해시와 작업 트리 변경 여부를 읽는다.
    // 입력: repoRoot - git을 실행할 폴더.
    // 출력: (커밋 해시, 변경 있음 여부, 오류). git이 없거나 실패·시간 초과면 (null, null, 오류 메시지).
    public static async Task<(string? Commit, bool? Dirty, string? Error)> ReadAsync(string repoRoot)
    {
        try
        {
            string? commit = (await RunAsync(repoRoot, "rev-parse HEAD").ConfigureAwait(false))?.Trim();
            string? status = await RunAsync(repoRoot, "status --porcelain").ConfigureAwait(false);
            return (commit, status == null ? null : status.Trim().Length > 0, null);
        }
        catch (Exception e)
        {
            return (null, null, e.Message);
        }
    }

    // 기능: git 명령 하나를 실행해 stdout을 읽고 Timeout 안에 끝나기를 기다린다.
    // 입력: dir - 작업 폴더, args - git 인자 문자열.
    // 출력: stdout 텍스트. 시작 실패·0이 아닌 종료 코드면 InvalidOperationException, 시간 초과면 프로세스를 죽이고 TimeoutException.
    private static async Task<string?> RunAsync(string dir, string args)
    {
        var info = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process p = Process.Start(info) ?? throw new InvalidOperationException("git did not start");
        using var cts = new CancellationTokenSource(Timeout);
        Task<string> output = p.StandardOutput.ReadToEndAsync(cts.Token);
        Task<string> error = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(); } catch (Exception) { }
            throw new TimeoutException($"git {args} timed out");
        }
        string text = await output.ConfigureAwait(false);
        await error.ConfigureAwait(false);
        return p.ExitCode == 0 ? text : throw new InvalidOperationException($"git {args} exited with {p.ExitCode}");
    }
}
