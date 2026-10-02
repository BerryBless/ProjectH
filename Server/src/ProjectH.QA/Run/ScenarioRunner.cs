using System.Diagnostics;
using System.Text.Json;

namespace ProjectH.QA;

// Runs a scenario's steps in order on one async flow (D9). Each step gets a soft limit (the handler reports its own
// timeout with Expected/Actual) and a hard one (limit + HardGraceMs: cancels a handler stuck in I/O). Stops at the
// first failure unless the step says continueOnFailure (request §59-60). The scenario token covers user Stop and the
// scenario timeout; which one fired decides Cancelled vs Failed.
public sealed class ScenarioRunner
{
    public const int HardGraceMs = 2000;

    private readonly ActionRegistry _registry;
    private readonly IRunControl _control;
    private readonly Action<StepResult> _onStep;

    public ScenarioRunner(ActionRegistry registry, IRunControl control, Action<StepResult> onStep)
    {
        _registry = registry;
        _control = control;
        _onStep = onStep;
    }

    // userToken: Stop / Ctrl+C. scenarioToken: userToken + the scenario timeout.
    public async Task<RunStatus> RunAsync(ScenarioDefinition scenario, RunContext run, RunReport report,
        CancellationToken userToken, CancellationToken scenarioToken)
    {
        RunStatus status = RunStatus.Passed;
        bool stop = false;
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

            var clock = Stopwatch.StartNew();
            try
            {
                if (step.Breakpoint && _control is RunGate { HonorBreakpoints: false }) run.Log($"breakpoint at {step.Id} (ignored in a CLI run)");
                await _control.BeforeStepAsync(step, scenarioToken).ConfigureAwait(false);
                clock.Restart();
                await ExecuteAsync(step, run, result, scenarioToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (userToken.IsCancellationRequested)
            {
                result.Status = StepStatus.Cancelled;
                result.Message = "Stopped by the user.";
            }
            catch (OperationCanceledException) when (scenarioToken.IsCancellationRequested)
            {
                result.Status = StepStatus.Failed;
                result.Message = $"Scenario timeout ({scenario.TimeoutSeconds:0.#} s) during this step.";
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Anything outside the handler's own try (timeout setup, the run control): a tool error, never a PASS.
                result.Status = StepStatus.Error;
                result.Message = $"{e.GetType().Name}: {e.Message}";
            }
            if (result.Status == StepStatus.Pending)
            {
                result.Status = StepStatus.Error;
                result.Message ??= "The step ended without a result.";
            }
            result.DurationMs = clock.ElapsedMilliseconds;
            _onStep(result);

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
