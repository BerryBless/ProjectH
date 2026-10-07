using System.Text.Json;
using ProjectH.QA;
using ProjectH.Shared.Simulation;

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

    // Phase 13.5 D13: buildEdit sends the state and target it was given and checks the codes.
    [Fact]
    public async Task BuildEditSendsThePresetAndReportsTheCodes()
    {
        (RunReport r, _, MockActor a) = await Run("""
        [ { "action": "connect", "actor": "playerA" },
          { "action": "buildEdit", "actor": "playerA", "pieceId": 42, "preset": "door", "saveAs": "e" },
          { "assert": "var.e.pieceId", "equals": 42 },
          { "assert": "var.e.counts.Ok", "equals": 1 },
          { "action": "buildEdit", "actor": "playerA", "piece": "wall", "cellX": 3, "level": 0, "cellZ": 4, "rotation": 2, "edit": 0, "count": 3, "burst": true },
          { "action": "buildEdit", "actor": "playerA", "pieceId": 42, "preset": "reset", "duplicate": true } ]
        """);
        Assert.Equal(RunStatus.Passed, r.Status);
        BuildEditCommand[] edits = a.Commands.OfType<BuildEditCommand>().ToArray();
        Assert.Equal(3, edits.Length);
        Assert.Equal(new EditPlan(42, null, (1 << 1) | (1 << 4), -1, -1), edits[0].Edits.Single());
        Assert.False(edits[0].Burst);
        Assert.Equal(3, edits[1].Edits.Count);
        Assert.True(edits[1].Burst);
        // A north wall is its neighbour's south wall (the canonical slot).
        Assert.Equal((BuildPieceType.Wall, (byte)3, (byte)5, (byte)0), (edits[1].Edits[0].Slot!.Value.Type, edits[1].Edits[0].Slot!.Value.X,
            edits[1].Edits[0].Slot!.Value.Z, edits[1].Edits[0].Slot!.Value.Rotation));
        Assert.True(edits[2].ReuseSequence);
    }

    [Fact]
    public async Task ARefusedEditFailsWithTheCode_UnlessExpected()
    {
        (RunReport r, _, _) = await Run("""
        [ { "action": "connect", "actor": "playerA" },
          { "action": "buildEdit", "actor": "playerA", "pieceId": 7, "edit": 16, "expect": "NotOwner" },
          { "id": "e", "action": "buildEdit", "actor": "playerA", "pieceId": 7, "edit": 16 } ]
        """, refusal: "NotOwner");
        Assert.Equal("e", r.Failure!.StepId);
        Assert.Equal("NotOwner", r.Failure.Actual);
    }

    [Fact]
    public void BuildEditValidation()
    {
        ScenarioLoadResult load = ScenarioLoader.Parse("""
        { "schemaVersion": 1, "name": "e", "actors": [ { "id": "playerA" } ],
          "steps": [ { "action": "buildEdit", "actor": "playerA", "pieceId": 1, "preset": "arch" },
                     { "action": "buildEdit", "actor": "playerA", "pieceId": 1 },
                     { "action": "buildEdit", "actor": "playerA", "cellX": 1, "edit": 1 },
                     { "action": "buildEdit", "actor": "playerA", "pieceId": 1, "edit": 4096, "expect": "Maybe", "count": 17 } ] }
        """);
        var issues = ScenarioValidator.Validate(load.Scenario!, ActionRegistry.CreateDefault(), MarkerStore.Empty());
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("Unknown preset 'arch'"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("exactly one of 'edit', 'preset' or 'rawState'"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("'cellX' needs 'level', 'cellZ' and 'piece'"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("'edit' must be"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("Unknown result 'Maybe'"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("'count' must be"));
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
