using System.Diagnostics;
using System.Text.Json;

namespace ProjectH.QA;

// Runner extras (D23, D24) used by the UI; the CLI leaves them at their defaults.
public sealed class RunnerOptions
{
    // Run From Step: steps before this index are recorded Skipped and not run.
    public int StartAtStep { get; init; }
    // D24: server state (/qa/match, /qa/players) before and after every step into the report (at most MaxDebugSnapshots).
    public bool DebugRun { get; init; }
    // A step starts (status Running). Called on the run flow; the receiver must copy what it keeps.
    public Action<StepResult>? OnStepStarted { get; init; }
    public const int MaxDebugSnapshots = 400;
}

// Runs a scenario's steps in order on one async flow (D9). Each step gets a soft limit (the handler reports its own
// timeout with Expected/Actual) and a hard one (limit + HardGraceMs: cancels a handler stuck in I/O). Stops at the
// first failure unless the step says continueOnFailure (request §59-60); the UI's gate may instead hold the failure and
// have the step retried (D23). The scenario token covers user Stop and the scenario timeout; which one fired decides
// Cancelled vs Failed.
public sealed class ScenarioRunner
{
    public const int HardGraceMs = 2000;

    private readonly ActionRegistry _registry;
    private readonly IRunControl _control;
    private readonly Action<StepResult> _onStep;
    private readonly RunnerOptions _options;

    // 기능: 단계 실행기를 만든다.
    // 입력: registry - Action 목록, control - 단계 전·실패 후 게이트, onStep - 단계가 끝날 때 결과를 받을 콜백, options - UI용 옵션(null이면 기본값).
    // 출력: RunAsync를 기다리는 실행기.
    public ScenarioRunner(ActionRegistry registry, IRunControl control, Action<StepResult> onStep, RunnerOptions? options = null)
    {
        _registry = registry;
        _control = control;
        _onStep = onStep;
        _options = options ?? new RunnerOptions();
    }

    // 기능: 시나리오 단계를 순서대로 실행한다. 단계마다 게이트를 거치고, 실패하면 continueOnFailure가 아닌 한 나머지를 Skipped로 기록하며, 게이트가 Retry를 주면 같은 단계를 다시 돈다. 어떤 토큰이 취소됐는지로 Cancelled와 Failed를 가른다.
    // 입력: scenario - 실행할 시나리오, run - 실행 상태, report - 단계 결과·실패·경고를 기록할 보고서, userToken - 사용자 중단 토큰, scenarioToken - userToken + 시나리오 시간 한도.
    // 출력: 실행 상태(Passed / Failed / Error / Cancelled, 어떤 단계가 나머지를 건너뛰게 했으면 Skipped). 보고서의 Steps·Failure·SkipReason·Warnings가 채워진다.
    // userToken: Stop / Ctrl+C. scenarioToken: userToken + the scenario timeout.
    public async Task<RunStatus> RunAsync(ScenarioDefinition scenario, RunContext run, RunReport report,
        CancellationToken userToken, CancellationToken scenarioToken)
    {
        RunStatus status = RunStatus.Passed;
        bool stop = false;
        string? skipReason = null;   // a step skipped the rest of the scenario (StepOutcome.SkipRest)
        if (_options.StartAtStep > 0)
        {
            report.Warnings.Add($"Run From Step {_options.StartAtStep + 1:00}: steps 01-{_options.StartAtStep:00} were not run, so their Arrange setup and saveAs variables are missing.");
        }
        foreach (StepDefinition step in scenario.Steps)
        {
            var result = new StepResult
            {
                Index = step.Index,
                Id = step.Id,
                Action = step.Action,
                Actor = step.Actor,
                Phase = step.EffectivePhase,
                Title = Title(step),
                ContinueOnFailure = step.ContinueOnFailure,
            };
            report.Steps.Add(result);
            if (stop)
            {
                result.Status = StepStatus.Skipped;
                if (skipReason != null)
                {
                    result.Message = $"Skipped: {skipReason}";
                    _onStep(result);
                }
                continue;
            }
            if (step.Index < _options.StartAtStep)
            {
                result.Status = StepStatus.Skipped;
                result.Message = "Not run (Run From Step).";
                _onStep(result);
                continue;
            }

            var clock = Stopwatch.StartNew();
            bool notified = false;
            try
            {
                if (step.Breakpoint && _control is RunGate { HonorBreakpoints: false }) run.Log($"breakpoint at {step.Id} (ignored in a CLI run)");
                await _control.BeforeStepAsync(step, scenarioToken).ConfigureAwait(false);
                while (true)
                {
                    result.Status = StepStatus.Running;
                    result.Message = result.Expected = result.Actual = null;
                    _options.OnStepStarted?.Invoke(result);
                    if (_options.DebugRun) await SnapshotAsync(run, report, step, "before").ConfigureAwait(false);
                    clock.Restart();
                    bool skipRest = await ExecuteAsync(step, run, result, scenarioToken).ConfigureAwait(false);
                    if (skipRest)
                    {
                        skipReason = result.Message;
                        report.SkipReason = $"from step {step.Index + 1:00} ({step.Id}): {result.Message}";
                        string skipWarning = $"Step {step.Index + 1:00} ({step.Id}) skipped the rest of the scenario: {result.Message}";
                        report.Warnings.Add(skipWarning);
                        run.Log(skipWarning);
                    }
                    result.DurationMs = clock.ElapsedMilliseconds;
                    if (_options.DebugRun) await SnapshotAsync(run, report, step, "after").ConfigureAwait(false);
                    // Hold only a real step failure; a scenario timeout or a Stop is not something to retry.
                    if (result.Status != StepStatus.Failed || step.ContinueOnFailure || scenarioToken.IsCancellationRequested) break;
                    _onStep(result);
                    notified = true;
                    if (await _control.OnStepFailedAsync(step, scenarioToken).ConfigureAwait(false) != FailureDecision.Retry) break;
                    result.Attempts++;
                    notified = false;
                    string warning = $"Step {step.Index + 1:00} ({step.Id}) retried (attempt {result.Attempts + 1}): the game state may have changed since it failed.";
                    report.Warnings.Add(warning);
                    run.Log(warning);
                }
            }
            catch (OperationCanceledException) when (userToken.IsCancellationRequested)
            {
                // Stop while a failed step is held ends the run as that failure (its Expected/Actual stay in the
                // report); anything else in progress is Cancelled.
                if (!(result.Status == StepStatus.Failed && notified))
                {
                    result.Status = StepStatus.Cancelled;
                    result.Message = "Stopped by the user.";
                    notified = false;
                }
            }
            catch (OperationCanceledException) when (scenarioToken.IsCancellationRequested)
            {
                // A Failed step held by the UI keeps its own result; anything still running failed by the timeout.
                if (result.Status != StepStatus.Failed)
                {
                    result.Status = StepStatus.Failed;
                    result.Message = $"Scenario timeout ({scenario.TimeoutSeconds:0.#} s) during this step.";
                    notified = false;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Anything outside the handler's own try (timeout setup, the run control): a tool error, never a PASS.
                result.Status = StepStatus.Error;
                result.Message = $"{e.GetType().Name}: {e.Message}";
                notified = false;
            }
            if (result.Status is StepStatus.Pending or StepStatus.Running)
            {
                result.Status = StepStatus.Error;
                result.Message ??= "The step ended without a result.";
                notified = false;
            }
            if (result.DurationMs == 0) result.DurationMs = clock.ElapsedMilliseconds;
            if (!notified) _onStep(result);

            switch (result.Status)
            {
                case StepStatus.Cancelled:
                    status = RunStatus.Cancelled;
                    stop = true;
                    break;
                case StepStatus.Error:
                    status = RunStatus.Error;
                    stop = true;
                    break;
                case StepStatus.Skipped when skipReason != null:
                    stop = true;
                    break;
                case StepStatus.Failed:
                    if (status == RunStatus.Passed) status = RunStatus.Failed;
                    report.Failure ??= new FailureInfo
                    {
                        StepIndex = step.Index,
                        StepId = step.Id,
                        Action = step.Action,
                        Message = result.Message,
                        Expected = result.Expected,
                        Actual = result.Actual,
                    };
                    if (!step.ContinueOnFailure || scenarioToken.IsCancellationRequested) stop = true;
                    break;
            }
        }
        // Every step that ran passed, but the scenario did not really run to its end: SKIPPED, not PASSED.
        if (status == RunStatus.Passed && report.SkipReason != null) status = RunStatus.Skipped;
        return status;
    }

    // 기능: Debug Run용으로 단계 전후의 서버 상태(/qa/match, /qa/players)를 보고서에 담는다(MaxDebugSnapshots까지).
    // 입력: run - 실행 상태, report - 스냅샷을 담을 보고서, step - 대상 단계, when - "before" 또는 "after".
    // 출력: 반환값 없음. report.DebugSnapshots에 추가된다. 질의 실패는 스냅샷의 Error에만 남는다.
    // D24: best effort, bounded; a failing query is recorded in the snapshot, never fails the step.
    private static async Task SnapshotAsync(RunContext run, RunReport report, StepDefinition step, string when)
    {
        if (report.DebugSnapshots.Count >= RunnerOptions.MaxDebugSnapshots) return;
        var snapshot = new DebugSnapshot { StepIndex = step.Index, StepId = step.Id, When = when };
        try
        {
            using var cts = new CancellationTokenSource(QaServerClient.RequestTimeout);
            snapshot.Match = await run.Server.GetMatchAsync(cts.Token).ConfigureAwait(false);
            snapshot.Players = await run.Server.GetPlayersAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is QaApiException or OperationCanceledException or HttpRequestException)
        {
            snapshot.Error = e.Message;
        }
        report.DebugSnapshots.Add(snapshot);
    }

    // 기능: 단계 하나를 Handler로 실행하고 결과(상태·메시지·Expected/Actual)를 result에 채운다. 소프트 한도는 Handler가, 하드 한도(+HardGraceMs)는 연결 토큰이 맡으며, 통과한 단계의 saveAs 값을 변수에 저장한다.
    // 입력: step - 실행할 단계, run - 실행 상태, result - 채울 단계 결과, scenarioToken - 시나리오 취소 토큰.
    // 출력: 단계가 나머지 시나리오를 건너뛰게 했으면 true, 아니면 false. 모르는 Action·Handler 예외는 Error, 시간 초과·QaStepException·QaApiException은 Failed로 기록된다.
    // Returns true when the step skipped the rest of the scenario.
    private async Task<bool> ExecuteAsync(StepDefinition step, RunContext run, StepResult result, CancellationToken scenarioToken)
    {
        if (!_registry.TryGet(step.Action, out IScenarioActionHandler? handler))
        {
            result.Status = StepStatus.Error;
            result.Message = $"Unknown action '{step.Action}'.";
            return false;
        }
        int timeoutMs = handler.Spec.TimeoutFor(step);
        run.CurrentStepId = step.Id;   // stall and crash records (stress D41)
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(scenarioToken);
        stepCts.CancelAfter(timeoutMs + HardGraceMs);
        var context = new StepContext(run, step, timeoutMs);
        try
        {
            StepOutcome outcome = await handler.ExecuteAsync(context, stepCts.Token).ConfigureAwait(false);
            if (outcome.Skipped)
            {
                result.Status = StepStatus.Skipped;
                result.Message = outcome.Message;
                return outcome.SkipRest;
            }
            result.Status = outcome.Passed ? StepStatus.Passed : StepStatus.Failed;
            result.Message = outcome.Message;
            result.Expected = outcome.Expected;
            result.Actual = outcome.Actual;
            if (outcome.Passed && step.SaveAs != null)
            {
                if (outcome.Value == null)
                {
                    result.Status = StepStatus.Failed;
                    result.Message = $"'{step.Action}' produced no value for saveAs '{step.SaveAs}'.";
                }
                else
                {
                    run.SetVariable(step.SaveAs, outcome.Value.Value);
                }
            }
        }
        catch (OperationCanceledException) when (!scenarioToken.IsCancellationRequested)
        {
            result.Status = StepStatus.Failed;
            result.Message = $"Step timeout after {timeoutMs} ms.";
            result.Expected = $"done within {timeoutMs} ms";
            result.Actual = "still running";
        }
        catch (QaStepException e)
        {
            result.Status = StepStatus.Failed;
            result.Message = e.Message;
        }
        catch (QaApiException e)
        {
            // The server did not answer as the contract says: a failure of the scenario (the server is under test).
            result.Status = StepStatus.Failed;
            result.Message = $"QA API: {e.Message}";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A bug in the tool or in a handler: exit 2, not a game failure.
            result.Status = StepStatus.Error;
            result.Message = $"{e.GetType().Name}: {e.Message}";
        }
        return false;
    }

    // 기능: 단계의 표시 제목을 만든다: description이 있으면 그것, 없으면 "Action 액터 주요 파라미터(40자)".
    // 입력: step - 단계 정의.
    // 출력: 제목 문자열.
    public static string Title(StepDefinition step)
    {
        if (!string.IsNullOrWhiteSpace(step.Description)) return step.Description!;
        string action = step.Action.Length > 0 ? char.ToUpperInvariant(step.Action[0]) + step.Action[1..] : step.Action;
        string detail = string.Empty;
        foreach (string key in new[] { "path", "condition", "event", "position", "target", "button", "weapon", "state", "milliseconds", "count" })
        {
            if (!step.Params.TryGetValue(key, out JsonElement v)) continue;
            detail = " " + JsonPath.Truncate(QaJson.Text(v), 40);
            break;
        }
        return step.Actor != null ? $"{action} {step.Actor}{detail}" : $"{action}{detail}";
    }
}
