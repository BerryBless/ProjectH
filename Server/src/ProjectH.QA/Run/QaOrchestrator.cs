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
}

// What the UI inspector may touch from its HTTP threads: the QA URL (it builds its own client) and the actors'
// published snapshot (ActorManager.Snapshot + immutable ActorState). Nothing else of the run.
public sealed record LiveRun(string RunId, Uri QaUrl, ActorManager Actors);

// The scenario timeout counts running time only: while the gate holds the run (pause, breakpoint, held failure) the
// deadline is disarmed and re-armed with what was left. Used only from the run flow (IRunControl.PausedChanged).
internal sealed class PausableDeadline
{
    private readonly CancellationTokenSource _cts;
    private readonly Stopwatch _since = Stopwatch.StartNew();
    private TimeSpan _remaining;

    public PausableDeadline(CancellationTokenSource cts, TimeSpan timeout)
    {
        _cts = cts;
        _remaining = timeout;
        cts.CancelAfter(timeout);
    }

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

    public QaOrchestrator(QaRunOptions options, ActionRegistry registry, MarkerStore markers, TextWriter output)
    {
        _options = options;
        _registry = registry;
        _markers = markers;
        _out = output;
    }

    public static string NewRunId(DateTime now) => $"qa-{now:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x10000):x4}";

    public async Task<RunReport> RunAsync(ScenarioDefinition scenario, IReadOnlyList<ValidationIssue> warnings, CancellationToken userToken)
    {
        var clock = Stopwatch.StartNew();
        int seed = _options.SeedOverride ?? scenario.Seed ?? Random.Shared.Next(1, int.MaxValue);
        var report = new RunReport
        {
            RunId = NewRunId(DateTime.Now),
            Scenario = scenario.Name,
            ScenarioFile = scenario.SourcePath,
            Description = scenario.Description,
            Tags = scenario.Tags,
            Seed = seed,
            Started = DateTimeOffset.Now,
        };
        foreach (ValidationIssue w in warnings) report.Warnings.Add(w.ToString());
        _out.WriteLine($"== {scenario.Name}  runId {report.RunId}  seed {seed}");

        (report.GitCommit, report.GitDirty, report.GitError) = await GitInfo.ReadAsync(_options.RepoRoot).ConfigureAwait(false);

        void Log(string line)
        {
            if (_options.Verbose) _out.WriteLine("   " + line);
            _options.LogSink?.Invoke("QA", line);
        }

        void ActorLog(string line)
        {
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
                        if (_options.Verbose) _out.WriteLine("   [server] " + line);
                        _options.LogSink?.Invoke("Server", line);
                    };
                }
                launched = new LaunchedServer(dll, seed, scenario.Server.Options, clientFactory, serverLog, Log);
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

            actors = new ActorManager(seed, ActorLog, _options.ActorFactory);
            run = new RunContext(report.RunId, seed, client, actors, _markers, scenario.Variables, Log)
            {
                Events = events,
                GameHost = gameHost,
                GamePort = gamePort,
                PollIntervalMs = _options.PollMs,
                Db = new ProjectH.QA.Faults.DbFaultHub(_options.DockerFactory),
            };
            if (launched != null)
            {
                run.ServerControl = launched;
                launched.Run = run;
            }
            foreach (ActorSpec a in scenario.Actors)
            {
                await actors.CreateAsync(a.Id, a.Type, userToken).ConfigureAwait(false);
                if (a.Proxy) run.Network.EnableProxy(a.Id);
            }
            await TryMarkAsync(client, $"QA run {report.RunId} start: {scenario.Name} seed {seed}", report.RunId).ConfigureAwait(false);
            _options.OnLive?.Invoke(new LiveRun(report.RunId, qaUrl, actors));

            using var scenarioCts = CancellationTokenSource.CreateLinkedTokenSource(userToken);
            var deadline = new PausableDeadline(scenarioCts, TimeSpan.FromSeconds(scenario.TimeoutSeconds));
            IRunControl control = _options.Control ?? new RunGate(honorBreakpoints: false);
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
            }

            if (actors != null)
            {
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
        PrintSummary(report);
        if (_options.WriteReport)
        {
            try
            {
                string dir = Path.Combine(_options.ReportDir ?? Path.Combine(_options.RepoRoot, "QA", "Reports"), report.RunId);
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

    private static async Task<JsonElement> HealthAsync(IQaServerClient client, CancellationToken token)
    {
        JsonElement health = await client.GetHealthAsync(token).ConfigureAwait(false);
        JsonElementHelpers.RequireOk(health);
        if (JsonPath.Child(health, "qaMode") is { ValueKind: JsonValueKind.False }) throw new QaToolException("The server answered but QA mode is off.");
        return health;
    }

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

    private static int? ReadInt(JsonElement e, string name) =>
        JsonPath.Child(e, name) is JsonElement v && Comparison.TryNumber(v, out double d) ? (int)d : null;

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

    // D21 categories for step lines.
    public static string LogCategory(string action) => action switch
    {
        "assert" or "waitFor" or "save" or "waitForEvent" => "Assertion",
        "connect" or "disconnect" or "reconnect" or "connectAll" or "disconnectAll" or "pauseInput" or "resumeInput"
            or "networkFault" or "clearNetworkFault" or "blockNetwork" or "unblockNetwork" or "dropConnection" or "sendInvalidPackets" => "Network",
        "moveTo" or "moveVector" or "look" or "aim" or "fire" or "stopFire" or "press" or "release" or "switchWeapon" or "jump"
            or "sprint" or "crouch" or "build" or "spawnActors" => "Actor",
        _ => "QA",
    };

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
