using System.Text.Json;
using ProjectH.QA;

namespace ProjectH.QA.Tests;

// QA-1 gap closing: setZone zonePhase, pauseInput/resumeInput, build.
public class GapTests
{
    private static async Task<(RunReport Report, FakeQaServer Server, MockActor Actor)> Run(string steps, string? refusal = null)
    {
        var server = new FakeQaServer();
        MockActor? actor = null;
        ScenarioLoadResult load = ScenarioLoader.Parse($$"""{ "schemaVersion": 1, "name": "g", "actors": [ { "id": "playerA" } ], "steps": {{steps}} }""");
        Assert.Empty(load.Errors);
        var issues = ScenarioValidator.Validate(load.Scenario!, ActionRegistry.CreateDefault(), MarkerStore.Empty());
        Assert.DoesNotContain(issues, i => i.IsError);
        var options = new QaRunOptions
        {
            RepoRoot = Path.GetTempPath(), AttachUrl = "http://127.0.0.1:1/", WriteReport = false, PollMs = 20,
            ServerClientFactory = _ => server,
            ActorFactory = (alias, _) => actor = new MockActor(alias) { BuildRefusal = refusal },
        };
        RunReport r = await new QaOrchestrator(options, ActionRegistry.CreateDefault(), MarkerStore.Empty(), new StringWriter())
            .RunAsync(load.Scenario!, issues, default);
        return (r, server, actor!);
    }

    [Fact]
    public async Task ZonePhaseIsSentAsPhase()
    {
        (RunReport r, FakeQaServer server, _) = await Run("""[ { "phase": "arrange", "action": "setZone", "zonePhase": 3 } ]""");
        Assert.Equal(RunStatus.Passed, r.Status);
        using var args = JsonDocument.Parse(server.Commands.Single(c => c.Command == "setZone").Args);
        Assert.Equal(3, args.RootElement.GetProperty("phase").GetInt32());
        Assert.False(args.RootElement.TryGetProperty("zonePhase", out _));
    }

    [Fact]
    public void SetZoneNeedsZonePhaseOrAdvance()
    {
        ScenarioLoadResult load = ScenarioLoader.Parse("""{ "schemaVersion": 1, "name": "z", "steps": [ { "action": "setZone" } ] }""");
        var issues = ScenarioValidator.Validate(load.Scenario!, ActionRegistry.CreateDefault(), MarkerStore.Empty());
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("'zonePhase' or 'advance'"));
    }

    [Fact]
    public async Task PauseAndResumeInput()
    {
        (RunReport r, _, MockActor a) = await Run("""
        [ { "action": "connect", "actor": "playerA" },
          { "action": "pauseInput", "actor": "playerA" },
          { "assert": "actor.inputPaused", "actor": "playerA", "equals": true },
          { "action": "resumeInput", "actor": "playerA" },
          { "assert": "actor.inputPaused", "actor": "playerA", "equals": false } ]
        """);
        Assert.Equal(RunStatus.Passed, r.Status);
    }

    [Fact]
    public async Task BuildRowReportsEveryResult()
    {
        (RunReport r, _, MockActor a) = await Run("""
        [ { "action": "connect", "actor": "playerA" },
          { "action": "build", "actor": "playerA", "piece": "wall", "cellX": 18, "level": 0, "cellZ": 13, "count": 3, "dx": 1, "saveAs": "row" },
          { "assert": "var.row.accepted", "equals": 3 },
          { "assert": "var.row.pieceIds", "contains": 101 } ]
        """);
        Assert.Equal(RunStatus.Passed, r.Status);
        BuildCommand cmd = a.Commands.OfType<BuildCommand>().Single();
        Assert.Equal(new byte[] { 18, 19, 20 }, cmd.Pieces.Select(p => p.X).ToArray());
    }

    [Fact]
    public async Task RefusedBuildFailsWithTheCode()
    {
        (RunReport r, _, _) = await Run("""
        [ { "action": "connect", "actor": "playerA" },
          { "id": "b", "action": "build", "actor": "playerA", "piece": "floor", "cellX": 1, "level": 0, "cellZ": 1 } ]
        """, refusal: "NoResource");
        Assert.Equal("b", r.Failure!.StepId);
        Assert.Equal("NoResource", r.Failure.Actual);
    }

    [Fact]
    public void BuildValidation()
    {
        ScenarioLoadResult load = ScenarioLoader.Parse("""
        { "schemaVersion": 1, "name": "b", "actors": [ { "id": "playerA" } ],
          "steps": [ { "action": "build", "actor": "playerA", "piece": "tower", "cellX": 40, "level": 0, "cellZ": 1, "count": 9 },
                     { "action": "build", "actor": "playerA", "piece": "wall", "cellX": 1 } ] }
        """);
        var issues = ScenarioValidator.Validate(load.Scenario!, ActionRegistry.CreateDefault(), MarkerStore.Empty());
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("Unknown piece 'tower'"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("'cellX' must be"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("'count' must be"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("needs 'level' and 'cellZ'"));
    }
}
