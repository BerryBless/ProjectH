using System.Text.Json;

namespace ProjectH.QA;

public sealed record UiStep(int Index, string Id, string Title, string Action, string? Actor, string? Phase, string Status,
    long DurationMs, string? Message, string? Expected, string? Actual, int Attempts);

public sealed record UiRunState(string Status, string? RunStatus, string? RunId, string? Scenario, string? Path, int? Seed,
    int WaitingAt, bool FailedWaiting, bool Unsaved, string? ReportUrl, int? ExitCode, string? Error,
    IReadOnlyList<UiStep> Steps, IReadOnlyList<string> Warnings);

// POST /api/run. Mode: run, from (Run From Step), until (Run Until Step), single (start paused before the first step).
public sealed class UiRunRequest
{
    public string? Path { get; set; }
    public string? Text { get; set; }
    public string Mode { get; set; } = "run";
    public int StepIndex { get; set; }
    public int? Seed { get; set; }
    public bool Debug { get; set; }
    public int[]? Breakpoints { get; set; }
}

public sealed class UiActorRow
{
    public string Alias { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public bool Connected { get; init; }
    public bool Alive { get; init; }
    public int Health { get; init; }
    public int Shield { get; init; }
}

// D20: the UI's one run at a time. HTTP threads call Start/Pause/Resume/...; the run itself goes on a Task with the
// orchestrator. State for the browser is copied into immutable DTOs (UiStep, UiRunState); live StepResults are never
// serialized.
// Lock order (deadlock review): _lock is the session lock. It is never held while calling the RunGate, the hub, the
// orchestrator or any I/O; the gate invokes PausedChanged outside its own lock, and the hub takes no other lock. So the
// three locks (session, gate, hub) are never held together.
public sealed class RunSession
{
    // Longer than the orchestrator's worst-case cleanup (state dump, metrics, events, mark 5 s each; actors 5 s; server
    // stop request 5 s + exit wait 10 s + kill 5 s), so closing the UI never leaves a launched server behind.
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(60);

    private readonly UiHostOptions _host;
    private readonly UiHub _hub;
    private readonly ActionRegistry _registry;
    private readonly MarkerStore _markers;
    private readonly object _lock = new();

    private string _status = "idle";   // idle, starting, running, paused, finished
    private string? _runStatus;
    private UiStep[] _steps = Array.Empty<UiStep>();
    private string? _runId;
    private string? _scenario;
    private string? _path;
    private int? _seed;
    private int _waitingAt = -1;
    private bool _failedWaiting;
    private bool _unsaved;
    private string? _reportUrl;
    private int? _exitCode;
    private string? _error;
    private IReadOnlyList<string> _warnings = Array.Empty<string>();
    // Completed by the run task's finally (after cleanup). Created in the same locked block that sets "starting", so a
    // shutdown that sees the run also sees what to wait for.
    private TaskCompletionSource? _done;
    // Set by StopAndWaitAsync: no run may start after the UI began closing (its server would outlive the UI).
    private bool _closing;
    private CancellationTokenSource? _cts;
    private RunGate? _gate;
    private LiveRun? _live;
    private IQaServerClient? _inspector;
    private UiRunRequest? _last;

    public RunSession(UiHostOptions host, UiHub hub, ActionRegistry registry, MarkerStore markers)
    {
        _host = host;
        _hub = hub;
        _registry = registry;
        _markers = markers;
    }

    public bool Busy
    {
        get { lock (_lock) return _status is "starting" or "running" or "paused"; }
    }

    public UiRunRequest? LastRequest
    {
        get { lock (_lock) return _last; }
    }

    public UiRunState State()
    {
        lock (_lock)
        {
            return new UiRunState(_status, _runStatus, _runId, _scenario, _path, _seed, _waitingAt, _failedWaiting, _unsaved,
                _reportUrl, _exitCode, _error, _steps.ToArray(), _warnings);
        }
    }

    // Null when started, else why not.
    public string? Start(UiRunRequest request, ScenarioDefinition scenario, IReadOnlyList<ValidationIssue> warnings, bool unsaved)
    {
        int count = scenario.Steps.Count;
        if (request.Mode is "from" or "until" && (request.StepIndex < 0 || request.StepIndex >= count)) return $"Step index must be 0-{count - 1}.";
        int seed = request.Seed ?? scenario.Seed ?? Random.Shared.Next(1, int.MaxValue);
        var gate = new RunGate(honorBreakpoints: true, holdOnFailure: true);
        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            if (_closing)
            {
                cts.Dispose();
                return "The UI is shutting down.";
            }
            if (_status is "starting" or "running" or "paused")
            {
                cts.Dispose();
                return "A run is already active (one at a time, D20).";
            }
            _status = "starting";
            _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _runStatus = null;
            _runId = null;
            _scenario = scenario.Name;
            _path = request.Path;
            _seed = seed;
            _waitingAt = -1;
            _failedWaiting = false;
            _unsaved = unsaved;
            _reportUrl = null;
            _exitCode = null;
            _error = null;
            _warnings = warnings.Select(w => w.ToString()).ToArray();
            _steps = scenario.Steps.Select(s => new UiStep(s.Index, s.Id, ScenarioRunner.Title(s), s.Action, s.Actor, s.EffectivePhase,
                "Pending", 0, null, null, null, 0)).ToArray();
            _gate = gate;
            _cts = cts;
            _last = new UiRunRequest
            {
                Path = request.Path, Text = request.Text, Mode = request.Mode, StepIndex = request.StepIndex, Seed = seed,
                Debug = request.Debug, Breakpoints = request.Breakpoints,
            };
        }
        // Outside the session lock (lock order).
        gate.SetBreakpoints(request.Breakpoints ?? Array.Empty<int>());
        if (request.Mode == "until") gate.PauseAfter(request.StepIndex);
        if (request.Mode == "single") gate.Pause();
        gate.PausedChanged = paused => OnPausedChanged(gate, paused);

        var options = new QaRunOptions
        {
            RepoRoot = _host.RepoRoot,
            SeedOverride = seed,
            AttachUrl = _host.AttachUrl,
            ServerDll = _host.ServerDll,
            ReportDir = _host.ReportDir,
            PollMs = _host.PollMs,
            ServerClientFactory = _host.ServerClientFactory,
            ActorFactory = _host.ActorFactory,
            WriteReport = _host.WriteReports,
            Control = gate,
            LogSink = _hub.Log,
            OnStepStarted = StepChanged,
            OnStepFinished = StepChanged,
            OnLive = SetLive,
            StartAtStep = request.Mode == "from" ? request.StepIndex : 0,
            DebugRun = request.Debug,
            UnsavedText = unsaved,
        };
        // The run task publishes its own states ("running" first); publishing here could deliver a stale "starting" late.
        _ = Task.Run(() => RunAsync(scenario, warnings, options, cts.Token));
        return null;
    }

    private async Task RunAsync(ScenarioDefinition scenario, IReadOnlyList<ValidationIssue> warnings, QaRunOptions options, CancellationToken token)
    {
        lock (_lock)
        {
            if (_status == "starting") _status = "running";
        }
        _hub.Publish("run", new { phase = "started", scenario = scenario.Name });
        PublishState();
        try
        {
            RunReport report = await new QaOrchestrator(options, _registry, _markers, new HubWriter(_hub))
                .RunAsync(scenario, warnings, token).ConfigureAwait(false);
            lock (_lock)
            {
                _runId = report.RunId;
                _runStatus = report.Status.ToString();
                _exitCode = report.ExitCode;
                _warnings = report.Warnings.ToArray();
                _error = report.ToolError;
                _reportUrl = report.ReportDirectory != null ? $"/reports/{report.RunId}/report.html" : null;
                // Steps the runner never reached (a tool error before the first step) stay Pending in the report; show
                // what the report says for the ones it has.
                foreach (StepResult r in report.Steps) Store(Copy(r));
            }
        }
        catch (Exception e)
        {
            lock (_lock) _error = $"Internal error: {e.Message}";
        }
        finally
        {
            CancellationTokenSource? cts;
            TaskCompletionSource? done;
            lock (_lock)
            {
                _status = "finished";
                _waitingAt = -1;
                _failedWaiting = false;
                _gate = null;
                cts = _cts;
                _cts = null;
                done = _done;
            }
            cts?.Dispose();
            _hub.Publish("run", new { phase = "finished" });
            PublishState();
            done?.TrySetResult();
        }
    }

    public void Pause() => GateOrNull()?.Pause();

    public void Resume() => GateOrNull()?.Resume();

    public void StepOnce() => GateOrNull()?.StepOnce();

    public bool Retry() => GateOrNull()?.Retry() ?? false;

    public void SetBreakpoints(int[] indices) => GateOrNull()?.SetBreakpoints(indices);

    // Request §57-58: cancel at once; cleanup (actors, launched server, report) runs in the run task.
    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_lock) cts = _status is "starting" or "running" or "paused" ? _cts : null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }
    }

    // Shutdown of the UI: stop the run and wait (bounded) for its cleanup, so a launched server is not left behind.
    public async Task StopAndWaitAsync()
    {
        Task? done;
        lock (_lock)
        {
            _closing = true;
            done = _done?.Task;
        }
        Stop();
        if (done != null) await Task.WhenAny(done, Task.Delay(StopWait)).ConfigureAwait(false);
    }

    // ---- inspector (D22): only the published actor snapshot and the session's own QA client ----

    public IReadOnlyList<UiActorRow>? Actors()
    {
        LiveRun? live;
        lock (_lock) live = _live;
        if (live == null) return null;
        return live.Actors.Snapshot.Take(ActorManager.MaxActors).Select(a =>
        {
            ActorState s = a.State;
            return new UiActorRow { Alias = a.Alias, Status = s.Status.ToString(), Connected = s.Connected, Alive = s.Alive, Health = s.Health, Shield = s.Shield };
        }).ToArray();
    }

    public async Task<object?> ActorAsync(string alias, CancellationToken token)
    {
        LiveRun? live;
        IQaServerClient? client;
        lock (_lock)
        {
            live = _live;
            client = _inspector;
        }
        if (live == null || client == null) return null;
        IQaActor? actor = live.Actors.Snapshot.FirstOrDefault(a => a.Alias == alias);
        if (actor == null) return null;
        JsonElement? player = await client.GetPlayerAsync(actor.DevPlayerId, token).ConfigureAwait(false);
        return new { alias, actor = actor.State, player };
    }

    public async Task<JsonElement?> MatchAsync(CancellationToken token)
    {
        IQaServerClient? client = Inspector();
        return client == null ? null : await client.GetMatchAsync(token).ConfigureAwait(false);
    }

    public async Task<object?> ServerAsync(CancellationToken token)
    {
        IQaServerClient? client = Inspector();
        if (client == null) return null;
        JsonElement metrics = await client.GetMetricsAsync(null, token).ConfigureAwait(false);
        JsonElement health = await client.GetHealthAsync(token).ConfigureAwait(false);
        return new { metrics, health };
    }

    private IQaServerClient? Inspector()
    {
        lock (_lock) return _inspector;
    }

    private RunGate? GateOrNull()
    {
        lock (_lock) return _gate;
    }

    // The inspector gets its own client for the run's QA URL: the run's client is disposed by the run's cleanup, and
    // inspector polling stays independent of it. Replaced/disposed here, outside the lock.
    private void SetLive(LiveRun? live)
    {
        IQaServerClient? old;
        IQaServerClient? created = live == null ? null : (_host.ServerClientFactory?.Invoke(live.QaUrl) ?? new QaServerClient(live.QaUrl));
        lock (_lock)
        {
            old = _inspector;
            _inspector = created;
            _live = live;
            if (live != null) _runId = live.RunId;
        }
        if (!ReferenceEquals(old, created)) (old as IDisposable)?.Dispose();
        PublishState();
    }

    private void StepChanged(StepResult r)
    {
        UiStep step = Copy(r);
        lock (_lock) Store(step);
        _hub.Publish("step", step);
    }

    // Caller holds _lock.
    private void Store(UiStep step)
    {
        if (step.Index >= 0 && step.Index < _steps.Length) _steps[step.Index] = step;
    }

    private static UiStep Copy(StepResult r) => new(r.Index, r.Id, r.Title, r.Action, r.Actor, r.Phase, r.Status.ToString(),
        r.DurationMs, r.Message, r.Expected, r.Actual, r.Attempts);

    private void OnPausedChanged(RunGate gate, bool paused)
    {
        // Gate first (its own lock), then the session lock: never both at once.
        int waitingAt = paused ? gate.WaitingAt : -1;
        bool failed = paused && gate.FailedWaiting;
        lock (_lock)
        {
            if (_status is not ("running" or "paused")) return;
            _status = paused ? "paused" : "running";
            _waitingAt = waitingAt;
            _failedWaiting = failed;
        }
        PublishState();
    }

    private void PublishState() => _hub.Publish("state", State());
}
