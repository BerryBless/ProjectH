using ProjectH.QA;

namespace ProjectH.QA.Tests;

// Tool review (QA-1) findings 1-4.
public class ReviewFixTests
{
    [Fact]
    public async Task PumpPublishesTheErrorWhenATickThrows()
    {
        var pump = new ActorPump(_ => { });
        var actor = new HeadlessActor("boom", pump, 1) { TickHook = () => throw new InvalidOperationException("tick failed") };
        await pump.AddAsync(actor, default);
        var command = new ClearAimCommand();
        await actor.SendAsync(command, default);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while ((actor.State.LastCommandId < command.Id || actor.State.Error == null) && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(actor.State.LastCommandId >= command.Id);
        Assert.Contains("tick failed", actor.State.Error);
        Assert.True(await pump.StopAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task UnfinishedFireClearsItsQueuedPresses()
    {
        var server = new FakeQaServer();
        MockActor? shooter = null;
        ScenarioLoadResult load = ScenarioLoader.Parse("""
        { "schemaVersion": 1, "name": "stall", "actors": [ { "id": "playerA" } ],
          "steps": [ { "action": "connect", "actor": "playerA" },
                     { "id": "f", "action": "fire", "actor": "playerA", "count": 5, "timeoutMilliseconds": 200 } ] }
        """);
        var options = new QaRunOptions
        {
            RepoRoot = Path.GetTempPath(), AttachUrl = "http://127.0.0.1:1/", WriteReport = false, PollMs = 20,
            ServerClientFactory = _ => server,
            ActorFactory = (alias, _) => shooter = new MockActor(alias) { StallScripts = true },
        };
        RunReport r = await new QaOrchestrator(options, ActionRegistry.CreateDefault(), MarkerStore.Empty(), new StringWriter())
            .RunAsync(load.Scenario!, Array.Empty<ValidationIssue>(), default);
        Assert.Equal("f", r.Failure!.StepId);
        Assert.Contains(shooter!.Commands, c => c is ClearInputQueueCommand);
        Assert.Equal(0, shooter.State.ScriptSteps);
    }

    [Fact]
    public async Task StepOnceWhileRunningDoesNotSkipALaterBreakpoint()
    {
        var gate = new RunGate(honorBreakpoints: true);
        gate.StepOnce();   // not paused: ignored
        var step = new StepDefinition { Id = "bp", Action = "wait", Breakpoint = true };
        Task wait = gate.BeforeStepAsync(step, default);
        await Task.Delay(100);
        Assert.False(wait.IsCompleted);
        Assert.True(gate.IsPaused);
        gate.StepOnce();
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(gate.IsPaused);   // single step: still paused for the next one
        gate.Resume();
        await gate.BeforeStepAsync(new StepDefinition { Id = "next", Action = "wait" }, default).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void BadMoveVectorDurationIsAValidationError()
    {
        ScenarioLoadResult load = ScenarioLoader.Parse("""
        { "schemaVersion": 1, "name": "mv", "actors": [ { "id": "playerA" } ],
          "steps": [ { "action": "moveVector", "actor": "playerA", "y": 1, "milliseconds": -5 } ] }
        """);
        var issues = ScenarioValidator.Validate(load.Scenario!, ActionRegistry.CreateDefault(), MarkerStore.Empty());
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("'milliseconds' must be an integer"));
    }

    [Fact]
    public async Task AnExceptionOutsideTheHandlerIsAStepErrorNotAPass()
    {
        // Unvalidated on purpose: a timeout so negative that setting up the step's own timer throws.
        ScenarioLoadResult load = ScenarioLoader.Parse("""
        { "schemaVersion": 1, "name": "bad", "actors": [],
          "steps": [ { "id": "w", "action": "wait", "milliseconds": 1, "timeoutMilliseconds": -100000 },
                     { "id": "after", "action": "wait", "milliseconds": 1 } ] }
        """);
        var options = new QaRunOptions
        {
            RepoRoot = Path.GetTempPath(), AttachUrl = "http://127.0.0.1:1/", WriteReport = false,
            ServerClientFactory = _ => new FakeQaServer(),
        };
        var output = new StringWriter();
        RunReport r = await new QaOrchestrator(options, ActionRegistry.CreateDefault(), MarkerStore.Empty(), output)
            .RunAsync(load.Scenario!, Array.Empty<ValidationIssue>(), default);
        Assert.Equal(StepStatus.Error, r.Steps[0].Status);
        Assert.Equal(StepStatus.Skipped, r.Steps[1].Status);
        Assert.Equal(RunStatus.Error, r.Status);
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("01 Wait 1 ERROR", output.ToString());
    }
}
