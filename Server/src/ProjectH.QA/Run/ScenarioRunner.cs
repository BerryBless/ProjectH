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

    public ScenarioRunner(ActionRegistry registry, IRunControl control, Action<StepResult> onStep, RunnerOptions? options = null)
    {
        _registry = registry;
        _control = control;
        _onStep = onStep;
        _options = options ?? new RunnerOptions();
    }

    // userToken: Stop / Ctrl+C. scenarioToken: userToken + the scenario timeout.
    public async Task<RunStatus> RunAsync(ScenarioDefinition scenario, RunContext run, RunReport report,
        CancellationToken userToken, CancellationToken scenarioToken)
    {
        RunStatus status = RunStatus.Passed;
        bool stop = false;
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
                    await ExecuteAsync(step, run, result, scenarioToken).ConfigureAwait(false);
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
        return status;
    }

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

    private async Task ExecuteAsync(StepDefinition step, RunContext run, StepResult result, CancellationToken scenarioToken)
    {
        if (!_registry.TryGet(step.Action, out IScenarioActionHandler? handler))
        {
            result.Status = StepStatus.Error;
            result.Message = $"Unknown action '{step.Action}'.";
            return;
        }
        int timeoutMs = handler.Spec.TimeoutFor(step);
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(scenarioToken);
        stepCts.CancelAfter(timeoutMs + HardGraceMs);
        var context = new StepContext(run, step, timeoutMs);
        try
        {
            StepOutcome outcome = await handler.ExecuteAsync(context, stepCts.Token).ConfigureAwait(false);
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
    }

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
