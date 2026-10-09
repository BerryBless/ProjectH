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

    // D30 manual check (request §88-90): a person answers PASS / FAIL (Passed true/false) or nobody can (null: SKIPPED).
    Task<ManualCheckAnswer> ManualCheckAsync(StepDefinition step, string description, CancellationToken token);

    // Called on the run flow, outside any lock, when the runner starts (true) or stops (false) waiting here. The
    // orchestrator suspends the scenario timeout meanwhile: a paused run must not time out.
    Action<bool>? PausedChanged { get; set; }
}

// By: "ui", "cli" or "auto" (nobody was asked).
public sealed record ManualCheckAnswer(bool? Passed, string? Note, string By);

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

    private bool _manualWaiting;
    private string? _manualDescription;
    private ManualCheckAnswer? _manualAnswer;

    // 기능: 일시정지 게이트를 만든다.
    // 입력: honorBreakpoints - 단계의 breakpoint·UI 중단점에서 멈출지(CLI는 false), holdOnFailure - 실패한 단계를 붙잡고 Retry/Resume을 기다릴지, holdManual - 수동 확인을 UI 답변(AnswerManual)으로 받을지.
    // 출력: 실행 중(일시정지 아님) 상태의 게이트.
    // holdManual: the UI answers manual checks (AnswerManual); otherwise Prompt (CLI) or nobody.
    public RunGate(bool honorBreakpoints, bool holdOnFailure = false, bool holdManual = false)
    {
        HonorBreakpoints = honorBreakpoints;
        HoldOnFailure = holdOnFailure;
        HoldManual = holdManual;
    }

    public bool HoldManual { get; }

    // CLI: asks on the terminal (null: nobody to ask, the check is SKIPPED).
    public Func<StepDefinition, string, CancellationToken, Task<ManualCheckAnswer>>? Prompt { get; set; }

    // The manual check the run waits for (UI), or null.
    public string? ManualWaiting
    {
        get { lock (_gate) return _manualWaiting ? _manualDescription : null; }
    }

    // 기능: UI가 대기 중인 수동 확인에 답을 주고 기다리는 Runner를 깨운다.
    // 입력: passed - PASS면 true, FAIL이면 false, note - 메모.
    // 출력: 답이 전달됐으면 true, 대기 중인 수동 확인이 없으면 false.
    // The UI's answer. False when no manual check is waiting.
    public bool AnswerManual(bool passed, string? note)
    {
        lock (_gate)
        {
            if (!_manualWaiting) return false;
            _manualAnswer = new ManualCheckAnswer(passed, note, "ui");
            Release();
            return true;
        }
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

    // 기능: 다음 단계 전에 멈추도록 일시정지를 켠다(StepOnce 요청은 취소).
    // 입력: 없음.
    // 출력: 반환값 없음. 다음 BeforeStepAsync가 대기한다.
    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
            _stepOnce = false;
        }
    }

    // 기능: 일시정지를 풀고 기다리는 Runner를 깨운다. 실패한 단계를 붙잡고 있는 중이면 "여기서 끝"(Finish)이 된다.
    // 입력: 없음.
    // 출력: 반환값 없음. 대기 중인 BeforeStepAsync·OnStepFailedAsync가 풀린다.
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

    // 기능: 일시정지 중에 단계 하나만 실행하고 다시 멈추게 한다. 실행 중이거나 실패를 붙잡고 있으면 무시한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 대기 중인 BeforeStepAsync가 한 번 풀린다.
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

    // 기능: 붙잡고 있는 실패 단계를 다시 실행하게 한다.
    // 입력: 없음.
    // 출력: 요청됐으면 true, 붙잡은 실패가 없으면 false.
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

    // 기능: Run Until Step: index 다음 단계 앞에서 멈추도록 예약한다(마지막 단계면 아무 일도 없다).
    // 입력: index - 끝까지 실행할 단계 인덱스.
    // 출력: 반환값 없음. 멈출 단계 인덱스가 기록된다.
    // Run Until Step (D23): pause before the step after `index` (nothing happens if it is the last).
    public void PauseAfter(int index)
    {
        lock (_gate) _pauseBeforeIndex = index + 1;
    }

    // 기능: UI 중단점 집합을 교체한다(MaxSteps개까지).
    // 입력: indices - 중단점을 둘 단계 인덱스들.
    // 출력: 반환값 없음. 중단점 집합이 바뀐다.
    public void SetBreakpoints(IEnumerable<int> indices)
    {
        lock (_gate)
        {
            _breakpoints.Clear();
            foreach (int i in indices.Take(ScenarioLoader.MaxSteps)) _breakpoints.Add(i);
        }
    }

    // 기능: 단계 실행 전 게이트. 중단점·예약된 멈춤이면 일시정지로 바꾸고, 일시정지 중이면 Resume·StepOnce까지 기다린다(기다리는 동안 PausedChanged(true/false)).
    // 입력: step - 실행하려는 단계, token - 시나리오 취소 토큰.
    // 출력: 반환값 없음. 단계를 실행해도 될 때 돌아온다. 취소되면 OperationCanceledException.
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

    // 기능: 실패한 단계를 붙잡고 UI의 Retry 또는 Resume을 기다린다. HoldOnFailure가 꺼져 있으면 바로 Finish.
    // 입력: step - 실패한 단계, token - 시나리오 취소 토큰.
    // 출력: Retry가 눌렸으면 Retry, Resume이면 Finish. 취소되면 OperationCanceledException.
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

    // 기능: 수동 확인 답을 받는다. HoldManual이면 UI의 AnswerManual을 기다리고, 아니면 Prompt(CLI)에 묻거나 Prompt가 없으면 SKIPPED 답을 준다. 기다리는 동안은 일시정지로 친다.
    // 입력: step - 수동 확인 단계, description - 사람에게 보일 확인 내용, token - 시나리오 취소 토큰.
    // 출력: PASS/FAIL/SKIPPED(null)와 메모·답한 주체가 담긴 ManualCheckAnswer. 취소되면 OperationCanceledException.
    // Waiting for a person counts as paused: the scenario timeout stops meanwhile (PausedChanged).
    public async Task<ManualCheckAnswer> ManualCheckAsync(StepDefinition step, string description, CancellationToken token)
    {
        if (!HoldManual)
        {
            if (Prompt == null) return new ManualCheckAnswer(null, "Nobody to ask (non-interactive run).", "auto");
            PausedChanged?.Invoke(true);
            try
            {
                return await Prompt(step, description, token).ConfigureAwait(false);
            }
            finally
            {
                PausedChanged?.Invoke(false);
            }
        }
        lock (_gate)
        {
            _manualWaiting = true;
            _manualDescription = description;
            _manualAnswer = null;
            _waitingAt = step.Index;
        }
        PausedChanged?.Invoke(true);
        try
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (_manualAnswer != null) return _manualAnswer;
                    _released ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _released.Task;
                }
                // Resume or Step release the gate too; only an answer ends the wait.
                await wait.WaitAsync(token).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate)
            {
                _manualWaiting = false;
                _manualDescription = null;
                _manualAnswer = null;
                _waitingAt = -1;
            }
            PausedChanged?.Invoke(false);
        }
    }

    // 기능: 대기 중인 TaskCompletionSource를 완료해 기다리는 쪽을 깨우고 비운다(_gate 안에서 호출).
    // 입력: 없음.
    // 출력: 반환값 없음. _released가 null이 된다.
    private void Release()
    {
        _released?.TrySetResult();
        _released = null;
    }
}
