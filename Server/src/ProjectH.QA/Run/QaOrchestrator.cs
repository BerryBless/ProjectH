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
    public IRunControl? Control { get; init; }
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
        }

        bool attach = _options.AttachUrl != null || string.Equals(scenario.Server.Mode, ServerSpec.Attach, StringComparison.OrdinalIgnoreCase);
        report.ServerMode = attach ? ServerSpec.Attach : ServerSpec.Launch;
        Func<Uri, IQaServerClient> clientFactory = _options.ServerClientFactory ?? (uri => new QaServerClient(uri));
        ServerProcessManager? process = null;
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
                process = new ServerProcessManager(_options.Verbose ? line => _out.WriteLine("   [server] " + line) : null);
                report.ServerArguments = ServerProcessManager.BuildArguments(dll, seed, scenario.Server.Options);
                (gamePort, int qaPort) = await process.StartAsync(dll, seed, scenario.Server.Options, clientFactory, userToken).ConfigureAwait(false);
                report.ServerPid = process.Pid;
                qaUrl = new Uri($"http://127.0.0.1:{qaPort}/");
                gameHost = "127.0.0.1";
                client = clientFactory(qaUrl);
                report.ServerHealth = await HealthAsync(client, userToken).ConfigureAwait(false);
            }
            report.QaUrl = qaUrl.ToString();
            report.GamePort = gamePort;
            report.ServerVersion = report.ServerHealth is JsonElement h && JsonPath.Child(h, "version") is JsonElement v ? QaJson.Text(v) : null;
            Log($"server {report.ServerMode} qa={qaUrl} game={gameHost}:{gamePort} version={report.ServerVersion}");

            var events = new EventCursor(client);
            if (attach) await events.SkipExistingAsync(userToken).ConfigureAwait(false);

            actors = new ActorManager(seed, Log, _options.ActorFactory);
            run = new RunContext(report.RunId, seed, client, actors, _markers, scenario.Variables, Log)
            {
                Events = events,
                GameHost = gameHost,
                GamePort = gamePort,
                PollIntervalMs = _options.PollMs,
            };
            foreach (ActorSpec a in scenario.Actors) await actors.CreateAsync(a.Id, a.Type, userToken).ConfigureAwait(false);
            await TryMarkAsync(client, $"QA run {report.RunId} start: {scenario.Name} seed {seed}", report.RunId).ConfigureAwait(false);

            using var scenarioCts = CancellationTokenSource.CreateLinkedTokenSource(userToken);
            scenarioCts.CancelAfter(TimeSpan.FromSeconds(scenario.TimeoutSeconds));
            var runner = new ScenarioRunner(_registry, _options.Control ?? new RunGate(honorBreakpoints: false), PrintStep);
            report.Status = await runner.RunAsync(scenario, run, report, userToken, scenarioCts.Token).ConfigureAwait(false);
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
            // State at the failure (request §103-104), before anything is closed.
            if (report.Status != RunStatus.Passed && actors != null) report.StateDump = await DumpAsync(client, actors).ConfigureAwait(false);
            if (client != null)
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
            if (process != null)
            {
                (bool stopped, string message) = await process.StopAsync(client).ConfigureAwait(false);
                serverStopFailed = !stopped;
                report.Cleanup.Add(new CleanupResult("server", stopped, message));
                report.ServerLogTail = process.Log.Tail(ReportLogLines);
                process.Dispose();
            }
            (client as IDisposable)?.Dispose();
        }

        report.ExitCode = serverStopFailed ? 2 : RunReport.ExitCodeFor(report.Status);
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
        _out.WriteLine(r.Line());
        if (r.Status is StepStatus.Failed or StepStatus.Error)
        {
            if (r.Message != null) _out.WriteLine($"   {r.Message}");
            if (r.Expected != null) _out.WriteLine($"   Expected: {r.Expected}");
            if (r.Actual != null) _out.WriteLine($"   Actual:   {r.Actual}");
        }
        else if (_options.Verbose && r.Message != null)
        {
            _out.WriteLine($"   {r.Message}");
        }
    }

    private void PrintSummary(RunReport r)
    {
        if (r.ToolError != null) _out.WriteLine($"   {r.ToolError}");
        foreach (CleanupResult c in r.Cleanup.Where(c => !c.Ok)) _out.WriteLine($"   cleanup {c.Name} FAILED: {c.Message}");
        string failed = r.Failure != null ? $"  failed at step {r.Failure.StepIndex + 1:00} ({r.Failure.StepId})" : string.Empty;
        _out.WriteLine($"== {r.Scenario}: {r.Status.ToString().ToUpperInvariant()}{failed}  seed {r.Seed}  runId {r.RunId}  {r.DurationMs} ms");
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
