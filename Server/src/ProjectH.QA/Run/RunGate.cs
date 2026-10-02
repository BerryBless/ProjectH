namespace ProjectH.QA;

// Request §54-55, §63: the runner asks this before every step. The CLI uses a gate that never pauses; the QA-2 web
// UI drives Pause / Resume / StepOnce from its HTTP threads and turns breakpoints on.
public interface IRunControl
{
    Task BeforeStepAsync(StepDefinition step, CancellationToken token);
}

// Pause gate. Lock: _gate only, never nested, held for flag changes alone; the waiting is outside it. The TCS runs its
// continuations asynchronously, so completing it inside the lock cannot run the runner inline (no re-entry).
public sealed class RunGate : IRunControl
{
    private readonly object _gate = new();
    private bool _paused;
    private bool _stepOnce;
    private TaskCompletionSource? _released;

    public RunGate(bool honorBreakpoints)
    {
        HonorBreakpoints = honorBreakpoints;
    }

    public bool HonorBreakpoints { get; }

    public bool IsPaused
    {
        get { lock (_gate) return _paused; }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
            _stepOnce = false;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
            _stepOnce = false;
            Release();
        }
    }

    // Runs the next step, then stays paused. Ignored while running (it would skip a later pause or breakpoint).
    public void StepOnce()
    {
        lock (_gate)
        {
            if (!_paused) return;
            _stepOnce = true;
            Release();
        }
    }

    public async Task BeforeStepAsync(StepDefinition step, CancellationToken token)
    {
        lock (_gate)
        {
            if (HonorBreakpoints && step.Breakpoint) _paused = true;
        }
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
            }
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    private void Release()
    {
        _released?.TrySetResult();
        _released = null;
    }
}
