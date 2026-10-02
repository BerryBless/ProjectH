namespace ProjectH.QA;

public enum FailureDecision
{
    Finish,   // stop at this failure, as the CLI does (the remaining steps are skipped, cleanup runs)
    Retry,    // run the failed step again in the same live run (D23: the game state may have changed meanwhile)
}

// Request §54-55, §63, D23: the runner asks this before every step and after a failed step. The CLI uses a gate that
// never pauses; the QA-2 web UI drives Pause / Resume / StepOnce / Retry from its HTTP threads.
public interface IRunControl
{
    Task BeforeStepAsync(StepDefinition step, CancellationToken token);

    // A step failed (not continueOnFailure). The UI holds the live run here so the user can inspect it and retry.
    Task<FailureDecision> OnStepFailedAsync(StepDefinition step, CancellationToken token);

    // QA-4 hook (D16, manual checks): true PASS, false FAIL, null not answered (SKIPPED). Not used by any action yet.
    Task<bool?> ManualCheckAsync(StepDefinition step, string description, CancellationToken token);

    // Called on the run flow, outside any lock, when the runner starts (true) or stops (false) waiting here. The
    // orchestrator suspends the scenario timeout meanwhile: a paused run must not time out.
    Action<bool>? PausedChanged { get; set; }
}

// Pause gate.
// Lock rules (deadlock review):
//  - _gate is the only lock here; it is held for flag changes alone. Waiting happens outside it.
//  - PausedChanged is always invoked outside _gate. Callers must not call into the gate while holding their own lock
//    (the UI's session lock), so the order "session lock -> gate lock" can never meet "gate lock -> session lock".
//  - The TCS runs continuations asynchronously, so completing it inside the lock never runs the runner inline.
public sealed class RunGate : IRunControl
{
    private readonly object _gate = new();
    private readonly HashSet<int> _breakpoints = new();   // UI toggles by step index; bounded by the step count
    private bool _paused;
    private bool _stepOnce;
    private bool _failedWaiting;
    private bool _retry;
    private int _pauseBeforeIndex = -1;
    private int _waitingAt = -1;
    private TaskCompletionSource? _released;

    public RunGate(bool honorBreakpoints, bool holdOnFailure = false)
    {
        HonorBreakpoints = honorBreakpoints;
        HoldOnFailure = holdOnFailure;
    }

    public bool HonorBreakpoints { get; }
    public bool HoldOnFailure { get; }
    public Action<bool>? PausedChanged { get; set; }

    public bool IsPaused
    {
        get { lock (_gate) return _paused; }
    }

    // The step index the runner is waiting at (-1: running).
    public int WaitingAt
    {
        get { lock (_gate) return _waitingAt; }
    }

    // Waiting after a failed step (Retry or Resume decides).
    public bool FailedWaiting
    {
        get { lock (_gate) return _failedWaiting; }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
            _stepOnce = false;
        }
    }

    // Resume running. While holding a failed step it means "finish here" (FailureDecision.Finish).
    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
            _stepOnce = false;
            Release();
        }
    }

    // Runs the next step, then stays paused. Ignored while running (it would skip a later pause or breakpoint) and
    // while holding a failure (Retry or Resume decide there).
    public void StepOnce()
    {
        lock (_gate)
        {
            if (!_paused || _failedWaiting) return;
            _stepOnce = true;
            Release();
        }
    }

    // Re-run the failed step. False when no failure is held.
    public bool Retry()
    {
        lock (_gate)
        {
            if (!_failedWaiting) return false;
            _retry = true;
            Release();
            return true;
        }
    }

    // Run Until Step (D23): pause before the step after `index` (nothing happens if it is the last).
    public void PauseAfter(int index)
    {
        lock (_gate) _pauseBeforeIndex = index + 1;
    }

    public void SetBreakpoints(IEnumerable<int> indices)
    {
        lock (_gate)
        {
            _breakpoints.Clear();
            foreach (int i in indices.Take(ScenarioLoader.MaxSteps)) _breakpoints.Add(i);
        }
    }

    public async Task BeforeStepAsync(StepDefinition step, CancellationToken token)
    {
        lock (_gate)
        {
            if (HonorBreakpoints && (step.Breakpoint || _breakpoints.Contains(step.Index))) _paused = true;
            if (step.Index == _pauseBeforeIndex) _paused = true;
        }
        bool waited = false;
        try
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (!_paused) return;
                    if (_stepOnce)
                    {
                        _stepOnce = false;
                        return;
                    }
                    _released ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _released.Task;
                    _waitingAt = step.Index;
                }
                if (!waited)
                {
                    waited = true;
                    PausedChanged?.Invoke(true);
                }
                await wait.WaitAsync(token).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate) _waitingAt = -1;
            if (waited) PausedChanged?.Invoke(false);
        }
    }

    public async Task<FailureDecision> OnStepFailedAsync(StepDefinition step, CancellationToken token)
    {
        if (!HoldOnFailure) return FailureDecision.Finish;
        Task wait;
        lock (_gate)
        {
            _paused = true;
            _stepOnce = false;
            _retry = false;
            _failedWaiting = true;
            _waitingAt = step.Index;
            _released ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = _released.Task;
        }
        PausedChanged?.Invoke(true);
        try
        {
            await wait.WaitAsync(token).ConfigureAwait(false);
            lock (_gate)
            {
                bool retry = _retry;
                _retry = false;
                // A retry stays paused-capable: the step runs once and the gate goes back to running state.
                _paused = false;
                return retry ? FailureDecision.Retry : FailureDecision.Finish;
            }
        }
        finally
        {
            lock (_gate)
            {
                _failedWaiting = false;
                _waitingAt = -1;
            }
            PausedChanged?.Invoke(false);
        }
    }

    public Task<bool?> ManualCheckAsync(StepDefinition step, string description, CancellationToken token) =>
        Task.FromResult<bool?>(null);

    private void Release()
    {
        _released?.TrySetResult();
        _released = null;
    }
}
