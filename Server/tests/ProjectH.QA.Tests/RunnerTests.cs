using System.Diagnostics;
using System.Text.Json;
using ProjectH.QA;

namespace ProjectH.QA.Tests;

// Runner, orchestrator and CLI against the fake QA server and MockActors (request §137).
public class RunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeQaServer _server = new();
    private readonly Dictionary<string, MockActor> _actors = new();

    public RunnerTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private IQaActor CreateActor(string alias, string type)
    {
        var actor = new MockActor(alias);
        actor.OnCommand = (a, c) =>
        {
            // The fake server follows the actor: joined → a player; a graceful leave → graced.
            if (c is ConnectCommand && a.State.Joined)
            {
                if (!_server.Players.TryGetValue(a.DevPlayerId, out var p)) _server.AddPlayer(a.DevPlayerId);
                else { p["connected"] = true; p["graced"] = false; }
            }
            if (c is DisconnectCommand && _server.Players.TryGetValue(a.DevPlayerId, out var q)) { q["connected"] = false; q["graced"] = true; }
            // B's trigger presses hit A (the fake's stand-in for the server's hit validation): 10 each, one event each.
            if (c is ScriptCommand sc && a.Alias == "playerB" && _server.Players.TryGetValue("qa-playerA", out var target))
            {
                foreach (InputStep step in sc.Steps.Where(x => x.FirePress))
                {
                    target["health"] = (int)target["health"]! - 10;
                    _server.AddEvent("PlayerDamaged", "qa-playerA", new { amount = 10 });
                }
            }
        };
        _actors[alias] = actor;
        return actor;
    }

    private async Task<(RunReport Report, string Output)> RunAsync(string steps, string extra = "", CancellationToken token = default,
        string actors = """[ { "id": "playerA" }, { "id": "playerB" } ]""", bool writeReport = false)
    {
        ScenarioLoadResult load = ScenarioLoader.Parse($$"""{ "schemaVersion": 1, "name": "test", "seed": 4242, {{extra}} "actors": {{actors}}, "steps": {{steps}} }""");
        Assert.Empty(load.Errors);
        MarkerStore markers = MarkerStore.Parse("""{ "markers": [ { "name": "QA_Combat_A", "x": -5, "y": 0, "z": 0, "yaw": 90 } ] }""");
        var registry = ActionRegistry.CreateDefault();
        IReadOnlyList<ValidationIssue> issues = ScenarioValidator.Validate(load.Scenario!, registry, markers);
        Assert.DoesNotContain(issues, i => i.IsError);
        var output = new StringWriter();
        var options = new QaRunOptions
        {
            RepoRoot = _root,
            AttachUrl = "http://127.0.0.1:1/",
            PollMs = 20,
            WriteReport = writeReport,
            ReportDir = Path.Combine(_root, "reports"),
            ServerClientFactory = _ => _server,
            ActorFactory = CreateActor,
        };
        RunReport report = await new QaOrchestrator(options, registry, markers, output).RunAsync(load.Scenario!, issues, token);
        return (report, output.ToString());
    }

    [Fact]
    public async Task FullPassWithDamageFromFire()
    {
        (RunReport r, string output) = await RunWithDamageAsync();
        Assert.Equal(RunStatus.Passed, r.Status);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("01 Connect playerA PASS", output);
        // D7: the server is asked about qa-<alias>.
        Assert.Contains("qa-playerA", _server.PlayerQueries);
        var setPosition = _server.Commands.Single(c => c.Command == "setPosition");
        Assert.Equal("qa-playerA", setPosition.Player);
        using var args = JsonDocument.Parse(setPosition.Args);
        Assert.Equal(-5, args.RootElement.GetProperty("x").GetDouble());
        Assert.Equal(90, args.RootElement.GetProperty("yaw").GetDouble());
        // The run id goes to the server log (mark) at start and end.
        Assert.Equal(2, _server.Commands.Count(c => c.Command == "mark"));
        Assert.Equal(4242, r.Seed);
        Assert.Matches(@"^qa-\d{8}-\d{6}-[0-9a-f]{4}$", r.RunId);
    }

    private Task<(RunReport Report, string Output)> RunWithDamageAsync() => RunAsync("""
        [ { "action": "connect", "actor": "playerA" },
          { "action": "connect", "actor": "playerB" },
          { "phase": "arrange", "action": "setPosition", "actor": "playerA", "position": "QA_Combat_A" },
          { "action": "save", "path": "player.health", "actor": "playerA", "saveAs": "hpBefore" },
          { "phase": "act", "action": "fire", "actor": "playerB", "target": "playerA", "count": 2 },
          { "phase": "assert", "action": "waitFor", "condition": "player.health", "actor": "playerA", "lessThan": "${hpBefore}", "timeoutMilliseconds": 2000 },
          { "action": "assert", "path": "player.health", "actor": "playerA", "equals": 80 },
          { "action": "waitForEvent", "event": "PlayerDamaged", "actor": "playerA", "timeoutMilliseconds": 2000 } ]
        """);

    [Fact]
    public async Task FailureStopsTheRunAndRecordsStepSeedAndState()
    {
        (RunReport r, string output) = await RunAsync("""
        [ { "id": "a_connect", "action": "connect", "actor": "playerA" },
          { "id": "check", "assert": "player.health", "actor": "playerA", "equals": 60 },
          { "id": "never", "action": "wait", "milliseconds": 1 } ]
        """, writeReport: true);
        Assert.Equal(RunStatus.Failed, r.Status);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("check", r.Failure!.StepId);
        Assert.Equal("60", r.Failure.Expected);
        Assert.Equal("100", r.Failure.Actual);
        Assert.Equal(StepStatus.Skipped, r.Steps[2].Status);
        Assert.Contains("Expected: 60", output);
        Assert.Contains("Actual:   100", output);
        Assert.NotNull(r.StateDump);
        Assert.Contains(r.StateDump!.Actors, a => a.Alias == "playerA");

        string json = File.ReadAllText(Path.Combine(r.ReportDirectory!, "report.json"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(4242, doc.RootElement.GetProperty("seed").GetInt32());
        Assert.Equal("check", doc.RootElement.GetProperty("failure").GetProperty("stepId").GetString());
        Assert.Equal(r.RunId, doc.RootElement.GetProperty("runId").GetString());
        string html = File.ReadAllText(Path.Combine(r.ReportDirectory!, "report.html"));
        Assert.Contains("check", html);
        Assert.Contains(r.RunId, html);
    }

    [Fact]
    public async Task ContinueOnFailureKeepsGoingButFails()
    {
        (RunReport r, _) = await RunAsync("""
        [ { "action": "connect", "actor": "playerA" },
          { "id": "soft", "assert": "player.health", "actor": "playerA", "equals": 1, "continueOnFailure": true },
          { "id": "after", "action": "wait", "milliseconds": 1 } ]
        """);
        Assert.Equal(RunStatus.Failed, r.Status);
        Assert.Equal(StepStatus.Passed, r.Steps[2].Status);
        Assert.Equal("soft", r.Failure!.StepId);
    }

    [Fact]
    public async Task WaitForTimeoutReportsLastValue()
    {
        var sw = Stopwatch.StartNew();
        (RunReport r, _) = await RunAsync("""
        [ { "action": "connect", "actor": "playerA" },
          { "id": "w", "action": "waitFor", "condition": "player.health", "actor": "playerA", "lessThan": 50, "timeoutMilliseconds": 200 } ]
        """);
        Assert.Equal("w", r.Failure!.StepId);
        Assert.Contains("Timeout after 200 ms", r.Failure.Message);
        Assert.Equal("100", r.Failure.Actual);
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }

    [Fact]
    public async Task StepHardTimeoutCancelsAStuckStep()
    {
        _server.CommandHandler = null;
        (RunReport r, _) = await RunAsync("""
        [ { "id": "slow", "action": "wait", "milliseconds": 20000, "timeoutMilliseconds": 100 } ]
        """);
        Assert.Equal(RunStatus.Failed, r.Status);
        Assert.Equal("slow", r.Failure!.StepId);
        Assert.Contains("Step timeout", r.Failure.Message);
        Assert.InRange(r.Steps[0].DurationMs, 100, 100 + ScenarioRunner.HardGraceMs + 2000);
    }

    [Fact]
    public async Task ConnectThatNeverJoinsFailsAtItsTimeout()
    {
        var output = new StringWriter();
        ScenarioLoadResult load = ScenarioLoader.Parse("""{ "schemaVersion": 1, "name": "nj", "actors": [ { "id": "playerA" } ], "steps": [ { "id": "c", "action": "connect", "actor": "playerA", "timeoutMilliseconds": 150 } ] }""");
        var options = new QaRunOptions
        {
            RepoRoot = _root, AttachUrl = "http://127.0.0.1:1/", WriteReport = false, PollMs = 20,
            ServerClientFactory = _ => _server,
            ActorFactory = (alias, _) => new MockActor(alias) { NeverJoin = true },
        };
        RunReport report = await new QaOrchestrator(options, ActionRegistry.CreateDefault(), MarkerStore.Empty(), output).RunAsync(load.Scenario!, Array.Empty<ValidationIssue>(), default);
        Assert.Equal("c", report.Failure!.StepId);
        Assert.Contains("Not joined within 150 ms", report.Failure.Message);
    }

    [Fact]
    public async Task ScenarioTimeoutFailsTheRunningStep()
    {
        var sw = Stopwatch.StartNew();
        (RunReport r, _) = await RunAsync("""[ { "id": "long", "action": "wait", "milliseconds": 30000, "timeoutMilliseconds": 60000 } ]""",
            extra: "\"timeoutSeconds\": 0.3,");
        Assert.Equal(RunStatus.Failed, r.Status);
        Assert.Contains("Scenario timeout", r.Failure!.Message);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task CancellationStopsPromptlyAndStillCleansUp()
    {
        using var cts = new CancellationTokenSource(300);
        var sw = Stopwatch.StartNew();
        (RunReport r, _) = await RunAsync("""
        [ { "action": "connect", "actor": "playerA" },
          { "id": "long", "action": "wait", "milliseconds": 30000 },
          { "id": "next", "action": "wait", "milliseconds": 1 } ]
        """, token: cts.Token);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(RunStatus.Cancelled, r.Status);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal(StepStatus.Cancelled, r.Steps[1].Status);
        Assert.Equal(StepStatus.Skipped, r.Steps[2].Status);
        Assert.Contains(r.Cleanup, c => c.Name == "actors" && c.Ok);
        // The end mark still reached the server: cleanup does not use the cancelled token.
        Assert.Equal(2, _server.Commands.Count(c => c.Command == "mark"));
    }

    [Fact]
    public async Task EventsFromBeforeAnAttachedRunAreSkipped()
    {
        _server.AddEvent("PlayerDamaged", "qa-playerA", new { amount = 10 });
        _server.AddEvent("PlayerDamaged", "qa-playerA", new { amount = 25 });
        (RunReport r, string output) = await RunAsync("""
        [ { "action": "connect", "actor": "playerA" },
          { "action": "waitForEvent", "event": "PlayerDamaged", "actor": "playerA", "saveAs": "first", "timeoutMilliseconds": 150 },
          { "action": "waitForEvent", "event": "PlayerDamaged", "actor": "playerA", "where": { "amount": 25 } },
          { "id": "third", "action": "waitForEvent", "event": "PlayerDamaged", "actor": "playerA", "timeoutMilliseconds": 150 } ]
        """);
        // Attach mode skips events from before the run: these two were already there, so even the first wait times out.
        Assert.Equal("02-waitForEvent", r.Failure!.StepId);
    }

    [Fact]
    public async Task EventsDuringTheRunAreMatched()
    {
        _server.CommandHandler = (command, player, args) =>
        {
            if (command == "mark" && args.GetProperty("text").GetString() == "go")
            {
                _server.AddEvent("PlayerDamaged", "qa-playerA", new { amount = 10 });
                _server.AddEvent("PlayerDamaged", "qa-playerB", new { amount = 25 });
                _server.AddEvent("PlayerDamaged", "qa-playerA", new { amount = 25 });
            }
            return new CommandResponse(true, null, null, 200);
        };
        (RunReport r, string output) = await RunAsync("""
        [ { "action": "connect", "actor": "playerA" },
          { "action": "mark", "text": "go" },
          { "action": "waitForEvent", "event": "PlayerDamaged", "actor": "playerA", "where": { "amount": 25 }, "saveAs": "e" },
          { "assert": "var.e.data.amount", "equals": 25 },
          { "assert": "event.PlayerDamaged", "actor": "playerA", "equals": 2 },
          { "id": "none_left", "action": "waitForEvent", "event": "PlayerDamaged", "actor": "playerA", "timeoutMilliseconds": 150 } ]
        """);
        Assert.Equal("none_left", r.Failure?.StepId);
        Assert.Equal(5, r.Steps.Count(s => s.Status == StepStatus.Passed));
    }
}
