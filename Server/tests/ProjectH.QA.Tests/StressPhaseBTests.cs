using System.Numerics;
using System.Text.Json;
using ProjectH.QA;
using ProjectH.Shared.Simulation;
using Xunit.Abstractions;

namespace ProjectH.QA.Tests;

// Stress phase B (request §18-49): piece layouts (dependency order, the hanging structure's single foundation), the bulk
// spawn through spawnBuildPiece (budget cap, refusals, parallel bound), the join workload, the rejoin workload of abuse
// groups, the per-tick rate budget, validation of the new actions, and the phase B scenario and suite files.
public class StressPhaseBTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stressb-" + Guid.NewGuid().ToString("N"));
    private readonly FakeQaServer _fake = new();
    private readonly List<MockActor> _actors = new();

    public StressPhaseBTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(Path.Combine(_root, "QA", "Scenarios", "T"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Reports => Path.Combine(_root, "out");

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private async Task<(int Exit, string Output)> Cli(params string[] args)
    {
        var output = new StringWriter();
        int exit = await QaCli.RunAsync(args.Concat(new[] { "--repo", _root }).ToArray(), output, default, _ => _fake, (alias, _) =>
        {
            var a = new MockActor(alias);
            lock (_actors) _actors.Add(a);
            return a;
        });
        _out.WriteLine(output.ToString());
        return (exit, output.ToString());
    }

    private static IReadOnlyList<ValidationIssue> Validate(string json)
    {
        ScenarioLoadResult load = ScenarioLoader.Parse(json, "x.json");
        var issues = load.Errors.Select(e => new ValidationIssue(true, "file", e)).ToList();
        if (load.Scenario != null) issues.AddRange(ScenarioValidator.Validate(load.Scenario, ActionRegistry.CreateDefault(), MarkerStore.Empty()));
        return issues;
    }

    private static JsonElement Report(string reports, string runId)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(reports, runId, "report.json")));
        return doc.RootElement.Clone();
    }

    // ---- layouts ----

    [Fact]
    public void FieldWavesOnlyStandOnEarlierWavesAndHoldMoreThanTheBudget()
    {
        List<IReadOnlyList<PieceSpec>> waves = BuildLayouts.Field(Vector2.Zero).ToList();
        Assert.Equal(waves.Select(w => w.Count), BuildLayouts.Field(Vector2.Zero).Select(w => w.Count));   // deterministic
        var placed = new HashSet<(BuildPieceType, int, int, int, int)>();
        var columns = new Dictionary<(int, int), int>();
        int total = 0;
        foreach (IReadOnlyList<PieceSpec> wave in waves)
        {
            foreach (PieceSpec p in wave)
            {
                // A wall stands on its cell's floor (same level); a floor above the column's base rests on the walls below.
                if (p.Piece == BuildPieceType.Wall) Assert.Contains((BuildPieceType.Floor, p.X, p.Y, p.Z, 0), placed);
                else if (columns.ContainsKey((p.X, p.Z))) Assert.Contains((BuildPieceType.Wall, p.X, p.Y - 1, p.Z, 0), placed);
            }
            foreach (PieceSpec p in wave)
            {
                Assert.True(placed.Add((p.Piece, p.X, p.Y, p.Z, p.Rotation)), $"{p} twice");
                columns[(p.X, p.Z)] = p.Y;
            }
            total += wave.Count;
        }
        Assert.True(total > 20_000, $"{total}");
        // Nearest the centre first: the first wave's cells are the closest ones.
        Assert.All(waves[0], p => Assert.True(MathF.Abs(BuildGrid.CellMinX(p.X)) < 30 && MathF.Abs(BuildGrid.CellMinZ(p.Z)) < 30));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1000)]
    public void HangingStructureHasExactlyOneGroundedPieceAndConnectsInOrder(int count)
    {
        BuildLayouts.HangingStructure? s = BuildLayouts.Hanging(Vector2.Zero, count);
        Assert.NotNull(s);
        List<PieceSpec> all = s!.Waves.SelectMany(w => w).ToList();
        Assert.Equal(count, all.Count);
        Assert.Equal(count, s.Count);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(s.Foundation, all[0]);
        Assert.True(BuildLayouts.WouldRest(s.Foundation));
        Assert.All(all.Skip(1), p => Assert.False(BuildLayouts.WouldRest(p), $"{p} rests on the ground"));
        // Every piece after the foundation shares a lattice edge with a piece of an earlier wave.
        var earlier = new List<PieceSpec>();
        foreach (IReadOnlyList<PieceSpec> wave in s.Waves)
        {
            foreach (PieceSpec p in wave)
                if (earlier.Count > 0) Assert.Contains(earlier, q => SharesEdge(p, q));
            earlier.AddRange(wave);
        }
        Assert.True(StressMap.CanStand(s.Shooter));
        Assert.True(Vector3.Distance(s.Shooter, s.Aim) < 6f);
        Assert.Equal(s.Waves.Select(w => w.Count), BuildLayouts.Hanging(Vector2.Zero, count)!.Waves.Select(w => w.Count));   // deterministic
    }

    private static bool SharesEdge(PieceSpec a, PieceSpec b) => Edges(a).Intersect(Edges(b)).Any();

    // Lattice edges of walls and floors (the server's BuildSupport.Edges for these two types): points (x, y, z) at cell
    // corners and levels.
    private static List<uint> Edges(PieceSpec p)
    {
        BuildGrid.TryNormalize(p.Piece, p.X, p.Y, p.Z, p.Rotation, out BuildPieceShape s);
        int x = s.X, y = s.Y, z = s.Z;
        var keys = new List<uint>();
        uint P(int px, int py, int pz) => (uint)(px + 40 * (pz + 40 * py));
        void E(uint u, uint v) => keys.Add(Math.Min(u, v) | (Math.Max(u, v) << 16));
        if (s.Type == BuildPieceType.Floor)
        {
            E(P(x, y, z), P(x + 1, y, z)); E(P(x, y, z + 1), P(x + 1, y, z + 1)); E(P(x, y, z), P(x, y, z + 1)); E(P(x + 1, y, z), P(x + 1, y, z + 1));
        }
        else if (s.Rotation == 0)
        {
            E(P(x, y, z), P(x + 1, y, z)); E(P(x, y + 1, z), P(x + 1, y + 1, z)); E(P(x, y, z), P(x, y + 1, z)); E(P(x + 1, y, z), P(x + 1, y + 1, z));
        }
        else
        {
            E(P(x, y, z), P(x, y, z + 1)); E(P(x, y + 1, z), P(x, y + 1, z + 1)); E(P(x, y, z), P(x, y + 1, z)); E(P(x, y, z + 1), P(x, y + 1, z + 1));
        }
        return keys;
    }

    // ---- spawnBuildPieces ----

    [Fact]
    public async Task BulkSpawnStopsAtTheBudgetAndCountsRefusals()
    {
        int pieces = 0, calls = 0;
        _fake.CommandHandler = (command, _, args) =>
        {
            if (command == "mark") return new CommandResponse(true, null, null, 200);
            Assert.Equal("spawnBuildPiece", command);
            int call = Interlocked.Increment(ref calls);
            if (call % 50 == 0) return new CommandResponse(false, "The piece was refused: Occupied.", null, 409);
            if (Volatile.Read(ref pieces) >= 300) return new CommandResponse(false, "The piece was refused: BudgetFull.", null, 409);
            int id = Interlocked.Increment(ref pieces);
            return new CommandResponse(true, null, JsonSerializer.SerializeToElement(new { pieceId = id }), 200);
        };
        _fake.Metrics = new { tickP95Ms = 0.1, buildPieces = 300 };
        string file = Write("QA/Scenarios/T/bulk.json", """
        { "schemaVersion": 1, "name": "bulk", "seed": 5,
          "steps": [
            { "id": "spawn", "action": "spawnBuildPieces", "count": 400, "saveAs": "b" },
            { "id": "placed", "assert": "var.b.placed", "equals": 300 },
            { "id": "full", "assert": "var.b.budgetFull", "equals": true },
            { "id": "occupied", "assert": "var.b.refused.Occupied", "greaterThan": 0 },
            { "id": "server", "assert": "var.b.serverPieces", "equals": 300 }
          ] }
        """);
        (int exit, _) = await Cli("run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(0, exit);
        // Stopped at the budget: at most one parallel batch beyond the first BudgetFull.
        Assert.True(calls < 300 + 300 / 50 + 2 * StressActions.BulkParallel, $"{calls}");
    }

    [Fact]
    public async Task BulkSpawnPlacesExactlyTheCountAndHangingNeedsEveryPiece()
    {
        int pieces = 0;
        // The tool's own start / end marks are commands too.
        _fake.CommandHandler = (command, _, _) => command == "mark" ? new CommandResponse(true, null, null, 200)
            : new CommandResponse(true, null, JsonSerializer.SerializeToElement(new { pieceId = Interlocked.Increment(ref pieces) }), 200);
        string file = Write("QA/Scenarios/T/bulk2.json", """
        { "schemaVersion": 1, "name": "bulk2", "seed": 5,
          "steps": [
            { "id": "field", "action": "spawnBuildPieces", "count": 250, "saveAs": "f" },
            { "id": "f_placed", "assert": "var.f.placed", "equals": 250 },
            { "id": "hang", "action": "spawnBuildPieces", "count": 100, "layout": "hanging", "saveAs": "h" },
            { "id": "h_placed", "assert": "var.h.placed", "equals": 100 },
            { "id": "h_foundation", "assert": "var.h.structure.pieceId", "greaterThan": 250 },
            { "id": "h_aim", "assert": "var.h.structure.foundation.y", "greaterThan": -1 }
          ] }
        """);
        (int exit, _) = await Cli("run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(0, exit);
        Assert.Equal(350, pieces);

        // A refused piece leaves the structure without its support: the step fails.
        int n = 0;
        _fake.CommandHandler = (command, _, _) => command == "mark" ? new CommandResponse(true, null, null, 200) : Interlocked.Increment(ref n) == 5
            ? new CommandResponse(false, "The piece was refused: Unsupported.", null, 409)
            : new CommandResponse(true, null, JsonSerializer.SerializeToElement(new { pieceId = n }), 200);
        string bad = Write("QA/Scenarios/T/bulk3.json", """
        { "schemaVersion": 1, "name": "bulk3", "seed": 5, "steps": [ { "id": "hang", "action": "spawnBuildPieces", "count": 100, "layout": "hanging" } ] }
        """);
        (int exit2, string output2) = await Cli("run", bad, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(1, exit2);
        Assert.Contains("incomplete", output2);
    }

    [Fact]
    public void NewActionsValidateTheirArguments()
    {
        Assert.Contains(Validate("""{ "schemaVersion": 1, "steps": [ { "phase": "arrange", "action": "spawnBuildPieces", "count": 60000 } ] }"""), i => i.IsError && i.Message.Contains("'count'"));
        Assert.Contains(Validate("""{ "schemaVersion": 1, "steps": [ { "phase": "arrange", "action": "spawnBuildPieces", "count": 10, "layout": "tower" } ] }"""), i => i.IsError && i.Message.Contains("'layout'"));
        // A server command: outside arrange it is a D11 warning, like spawnBuildPiece.
        Assert.Contains(Validate("""{ "schemaVersion": 1, "steps": [ { "phase": "act", "action": "spawnBuildPieces", "count": 10 } ] }"""), i => !i.IsError);
        Assert.Contains(Validate("""{ "schemaVersion": 1, "steps": [ { "action": "actorGroup", "groups": [ { "name": "g", "rest": true } ] }, { "action": "groupFireAt", "group": "g" } ] }"""), i => i.IsError && i.Message.Contains("at"));
        Assert.Contains(Validate("""{ "schemaVersion": 1, "steps": [ { "action": "actorGroup", "groups": [ { "name": "g", "rest": true } ] }, { "action": "groupInvalidPackets", "group": "g", "kinds": ["inputFlood"] } ] }"""), i => i.IsError && i.Message.Contains("inputFlood"));
        Assert.DoesNotContain(Validate("""{ "schemaVersion": 1, "steps": [ { "action": "actorGroup", "groups": [ { "name": "g", "rest": true } ] }, { "action": "groupBuildSpam", "group": "g", "ratePerSecond": 30 }, { "action": "groupConnect", "group": "g", "perSecond": 10 } ] }"""), i => i.IsError);
    }

    // ---- rate budget ----

    [Fact]
    public void RateBudgetSpreadsTheRateAndCapsATick()
    {
        var b = new RateBudget();
        int sent = 0;
        for (int t = 0; t <= 30; t++) sent += b.Take(15f, t / 30f);
        Assert.InRange(sent, 14, 15);
        var burst = new RateBudget();
        burst.Take(200f, 0f);
        Assert.Equal(RateBudget.MaxPerTick, burst.Take(200f, 1f));
    }

    // ---- workloads ----

    [Fact]
    public async Task GroupConnectJoinsAtTheRateAndCountsFailures()
    {
        string file = Write("QA/Scenarios/T/ramp.json", """
        { "schemaVersion": 1, "name": "ramp", "seed": 5,
          "steps": [
            { "id": "spawn", "action": "spawnActors", "count": 6, "prefix": "bot" },
            { "id": "groups", "action": "actorGroup", "prefix": "bot", "groups": [ { "name": "all", "rest": true } ] },
            { "id": "ramp", "action": "groupConnect", "group": "all", "perSecond": 10 },
            { "id": "w", "action": "waitFor", "condition": "group.all.stats.connects", "equals": 6, "timeoutMilliseconds": 5000 },
            { "id": "stop", "action": "stopGroup", "group": "all", "saveAs": "s" },
            { "id": "ok", "assert": "var.s.connectFailures", "equals": 0 },
            { "id": "joined", "assert": "group.all.joined", "equals": 6 }
          ] }
        """);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        (int exit, _) = await Cli("run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(0, exit);
        // 6 at 10/s: the last one is sent about 0.5 s after the first.
        Assert.True(clock.ElapsedMilliseconds >= 450, $"{clock.ElapsedMilliseconds}");

        // A member that never joins is a failure after timeoutMs.
        var group = new ActorGroup("g", new IQaActor[] { new MockActor("a"), new MockActor("b") { NeverJoin = true } }, new[] { 0, 1 });
        await StressActions.ConnectLoopAsync(group, new[] { ("h", 1), ("h", 1) }, 0, 1000, CancellationToken.None);
        Assert.Equal(1, Interlocked.Read(ref group.Stats.Connects));
        Assert.Equal(1, Interlocked.Read(ref group.Stats.ConnectFailures));
    }

    [Fact]
    public async Task RejoinBringsKickedMembersBackAndCountsTheKicks()
    {
        var a = new MockActor("a");
        var b = new MockActor("b");
        await a.SendAsync(new ConnectCommand("h", 1, false), default);
        await b.SendAsync(new ConnectCommand("h", 1, false), default);
        var group = new ActorGroup("g", new IQaActor[] { a, b }, new[] { 0, 1 });
        a.Set(s => s with { Status = ActorStatus.Disconnected, Joined = false, Connected = false, Disconnected = true, DisconnectCode = "Kicked" });
        using var cts = new CancellationTokenSource();
        Task loop = StressActions.RejoinLoopAsync(group, "h", 1, cts.Token);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (Interlocked.Read(ref group.Stats.Rejoins) < 1 && clock.ElapsedMilliseconds < 5000) await Task.Delay(50);
        Assert.Equal(1, Interlocked.Read(ref group.Stats.Kicks));
        Assert.Equal(1, Interlocked.Read(ref group.Stats.Rejoins));
        Assert.True(a.State.Joined);
        Assert.Equal(2, a.State.Connections);
        // Kicked again on the new connection: counted again.
        a.Set(s => s with { Status = ActorStatus.Disconnected, Joined = false, Connected = false, Disconnected = true, DisconnectCode = "Kicked" });
        while (Interlocked.Read(ref group.Stats.Rejoins) < 2 && clock.ElapsedMilliseconds < 10000) await Task.Delay(50);
        cts.Cancel();
        await loop;
        Assert.Equal(2, Interlocked.Read(ref group.Stats.Kicks));
        Assert.Equal(1, b.State.Connections);
        Assert.Equal(0, Interlocked.Read(ref group.Stats.WorkloadFailures));
    }

    [Fact]
    public async Task AbuseGroupsStartTheirRejoinWorkloadAndStopGroupEndsIt()
    {
        string file = Write("QA/Scenarios/T/abuse.json", """
        { "schemaVersion": 1, "name": "abuse", "seed": 5,
          "steps": [
            { "id": "spawn", "action": "spawnActors", "count": 4, "prefix": "bot" },
            { "id": "join", "action": "connectAll", "prefix": "bot" },
            { "id": "groups", "action": "actorGroup", "prefix": "bot", "groups": [ { "name": "bad", "count": 2 }, { "name": "spam", "rest": true } ] },
            { "id": "bad", "action": "groupInvalidPackets", "group": "bad", "ratePerSecond": 5 },
            { "id": "spam", "action": "groupBuildSpam", "group": "spam", "ratePerSecond": 30, "resources": 0 },
            { "id": "running", "assert": "group.bad.workloads", "equals": 1 },
            { "id": "again", "action": "groupInvalidPackets", "group": "bad", "ratePerSecond": 10 },
            { "id": "one_rejoin", "assert": "group.bad.workloads", "equals": 1 },
            { "id": "role", "assert": "group.spam.role", "equals": "buildSpam" },
            { "id": "stop", "action": "stopGroup", "group": "all" },
            { "id": "ended", "assert": "group.bad.workloads", "equals": 0 }
          ] }
        """);
        (int exit, _) = await Cli("run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(0, exit);
        Assert.Contains(_actors.SelectMany(a => a.Commands), c => c is SetBrainCommand { Brain: InvalidPacketBrain });
        Assert.Contains(_actors.SelectMany(a => a.Commands), c => c is SetBrainCommand { Brain: BuildSpamBrain });
    }

    // ---- the phase B files ----

    public static readonly string[] PhaseBScenarios =
    {
        "lag_compensation", "turbo_build", "build_count", "build_destruction", "loot", "zone", "elimination_burst", "join_ramp", "join_spike",
        "disconnect_spike", "input_timeout", "invalid_packets", "build_spam", "network_fault_mixed", "db_persistence", "db_down", "hotspot",
        "restart_repeat",
    };

    [Fact]
    public void PhaseBScenariosAndSuitesAreValid()
    {
        string repo = ServerLocator.FindRepoRoot(AppContext.BaseDirectory) ?? throw new InvalidOperationException("repo root");
        MarkerStore markers = MarkerStore.LoadFile(Path.Combine(repo, "QA", "Markers.json"));
        foreach (string name in PhaseBScenarios)
        {
            ScenarioLoadResult load = ScenarioLoader.LoadFile(Path.Combine(repo, "QA", "Scenarios", "Stress", name + ".json"));
            Assert.Empty(load.Errors);
            Assert.True(load.Scenario!.Stress, name);
            Assert.Contains("stress", load.Scenario.Tags);
            Assert.DoesNotContain(ScenarioValidator.Validate(load.Scenario, ActionRegistry.CreateDefault(), markers), i => i.IsError);
            Assert.Contains(load.Scenario.Steps, s => s.Action == "measure");
        }
        // Every phase B scenario is in stress-full except restart_repeat: it has its own suite (§65) and is not gameplay.
        IReadOnlyList<string> full = ScenarioCatalog.Select(repo, "suite:stress-full").Files;
        foreach (string name in PhaseBScenarios.Where(n => n != "restart_repeat"))
            Assert.Contains(full, f => f.Replace('\\', '/').EndsWith($"Stress/{name}.json", StringComparison.Ordinal));
        Assert.DoesNotContain(full, f => f.Contains("restart_repeat", StringComparison.Ordinal));
        // The suites' parameter-set entries point at existing sets (a wrong index fails here, not in a nightly run).
        foreach (string suite in new[] { "stress-quick", "stress-gameplay", "stress-network", "stress-building", "stress-fault", "stress-soak" })
        {
            foreach (var item in ScenarioCatalog.Select(repo, "suite:" + suite).Items)
            {
                if (item.ParameterSet is not int set) continue;
                ScenarioLoadResult load = ScenarioLoader.LoadFile(item.File);
                Assert.True(set >= 1 && set <= load.Scenario!.Parameters.Count, $"{suite}: {item.File} set {set}");
            }
        }
        Assert.Single(ScenarioCatalog.Select(repo, "suite:stress-restart").Files);
        Assert.DoesNotContain(ScenarioCatalog.Select(repo, "suite:pre-push").Files, f => f.Contains("Stress", StringComparison.OrdinalIgnoreCase));
    }
}
