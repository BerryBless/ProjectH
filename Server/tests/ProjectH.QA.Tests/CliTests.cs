using System.Numerics;
using System.Text.Json;
using ProjectH.Bots;
using ProjectH.QA;
using ProjectH.Shared.Simulation;

namespace ProjectH.QA.Tests;

public class CliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-cli-" + Guid.NewGuid().ToString("N"));
    private readonly FakeQaServer _server = new();

    public CliTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "QA", "Scenarios", "Smoke"));
        Directory.CreateDirectory(Path.Combine(_root, "QA", "Suites"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        File.WriteAllText(path, text);
        return path;
    }

    private async Task<(int Exit, string Output)> Cli(params string[] args)
    {
        var output = new StringWriter();
        int exit = await QaCli.RunAsync(args.Concat(new[] { "--repo", _root }).ToArray(), output, default,
            _ => _server, (alias, _) => new MockActor(alias));
        return (exit, output.ToString());
    }

    [Fact]
    public async Task MalformedJsonIsExitTwo()
    {
        string file = Write("QA/Scenarios/Smoke/broken.json", "{ \"schemaVersion\": 1, \"steps\": [");
        (int exit, string output) = await Cli("run", file, "--attach", "http://127.0.0.1:1");
        Assert.Equal(2, exit);
        Assert.Contains("Malformed JSON", output);
    }

    [Fact]
    public async Task InvalidScenarioIsExitTwoAndValidateListsIssues()
    {
        Write("QA/Scenarios/Smoke/bad.json", """{ "schemaVersion": 1, "steps": [ { "action": "fly" } ] }""");
        (int exit, string output) = await Cli("validate", "Smoke");
        Assert.Equal(2, exit);
        Assert.Contains("Unknown action 'fly'", output);
    }

    [Fact]
    public async Task FailingScenarioIsExitOneWithReport()
    {
        Write("QA/Scenarios/Smoke/fail.json", """
        { "schemaVersion": 1, "name": "fail", "seed": 99, "actors": [ { "id": "playerA" } ],
          "steps": [ { "id": "c", "action": "connect", "actor": "playerA" }, { "id": "boom", "assert": "match.state", "equals": "Finished" } ] }
        """);
        (int exit, string output) = await Cli("run", "Smoke", "--attach", "http://127.0.0.1:1", "--report-dir", Path.Combine(_root, "out"));
        Assert.Equal(1, exit);
        Assert.Contains("02 Assert match.state FAIL", output);
        string report = Directory.GetFiles(Path.Combine(_root, "out"), "report.json", SearchOption.AllDirectories).Single();
        using var doc = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal(99, doc.RootElement.GetProperty("seed").GetInt32());
        Assert.Equal("boom", doc.RootElement.GetProperty("failure").GetProperty("stepId").GetString());
    }

    [Fact]
    public async Task PassingSuiteIsExitZeroAndSeedOverride()
    {
        Write("QA/Scenarios/Smoke/ok.json", """
        { "schemaVersion": 1, "name": "ok", "seed": 1, "actors": [ { "id": "playerA" } ],
          "steps": [ { "action": "connect", "actor": "playerA" }, { "assert": "var.seed", "equals": 777 } ] }
        """);
        Write("QA/Suites/quick.json", """{ "scenarios": [ "Smoke/ok.json" ] }""");
        (int exit, string output) = await Cli("run", "quick", "--attach", "http://127.0.0.1:1", "--seed", "777", "--report-dir", Path.Combine(_root, "out"));
        Assert.Equal(0, exit);
        Assert.Contains("seed 777", output);
    }

    [Fact]
    public async Task ANameThatIsBothSuiteAndCategoryMustBeChosen()
    {
        const string ok = """{ "schemaVersion": 1, "name": "ok", "steps": [ { "action": "wait", "milliseconds": 1 } ] }""";
        Write("QA/Scenarios/Smoke/a.json", ok);
        Directory.CreateDirectory(Path.Combine(_root, "QA", "Scenarios", "Other"));
        Write("QA/Scenarios/Other/b.json", ok);
        Write("QA/Scenarios/Other/c.json", ok);
        Write("QA/Suites/smoke.json", """{ "scenarios": [ "Smoke/a.json", "Other" ] }""");

        (int exit, string output) = await Cli("validate", "smoke");
        Assert.Equal(2, exit);
        Assert.Contains("both a suite and a category", output);
        Assert.Contains("'suite:smoke'", output);
        Assert.Contains("'category:Smoke'", output);

        (exit, output) = await Cli("validate", "suite:smoke");
        Assert.Equal(0, exit);
        Assert.StartsWith("suite smoke: 3 scenarios", output);

        (exit, output) = await Cli("validate", "category:smoke");
        Assert.Equal(0, exit);
        Assert.StartsWith("category Smoke: 1 scenario" + Environment.NewLine, output);

        (exit, output) = await Cli("validate", "Other");
        Assert.Equal(0, exit);
        Assert.StartsWith("category Other: 2 scenarios", output);

        (exit, output) = await Cli("validate", "Smoke/a.json");
        Assert.Equal(0, exit);
        Assert.StartsWith("file Smoke/a.json: 1 scenario", output);

        Assert.Equal(2, (await Cli("validate", "suite:nope")).Exit);
        Assert.Equal(2, (await Cli("validate", "category:Nope")).Exit);
    }

    [Fact]
    public async Task UnknownTargetAndBadOptionsAreExitTwo()
    {
        Assert.Equal(2, (await Cli("run", "NoSuchThing")).Exit);
        Assert.Equal(2, (await Cli("run", "Smoke", "--poll-ms", "1")).Exit);
        Assert.Equal(2, (await Cli("explode")).Exit);
    }
}

// QA/Markers.json is real data: the combat pairs must be flat, 10/20 m apart and in clear sight (boxes, doors and
// harvestables), or combat scenarios would test the map instead of the hit path.
public class MarkersTests
{
    private static string RepoRoot() => ServerLocator.FindRepoRoot(AppContext.BaseDirectory) ?? throw new InvalidOperationException("repo root not found");

    [Theory]
    [InlineData("QA_Combat_A", "QA_Combat_B", 10f)]
    [InlineData("QA_Combat_20m_A", "QA_Combat_20m_B", 20f)]
    [InlineData("QA_Move_Start", "QA_Move_End", 20f)]
    public void PairsAreFlatApartAndInClearSight(string a, string b, float distance)
    {
        MarkerStore markers = MarkerStore.LoadFile(Path.Combine(RepoRoot(), "QA", "Markers.json"));
        Assert.True(markers.TryResolveName(a, out QaPosition pa));
        Assert.True(markers.TryResolveName(b, out QaPosition pb));
        Vector3 ga = pa.ToGround(), gb = pb.ToGround();
        Assert.Equal(distance, Vector3.Distance(ga, gb), 3);
        var blockers = new List<Box>(GameMap.Boxes.ToArray());
        blockers.AddRange(GameMap.Doors.ToArray());
        foreach (Harvestable h in GameMap.Harvestables) blockers.Add(h.Bounds);
        // Feet, chest and eye lines (a shot goes eye → chest).
        foreach (float y in new[] { 0.3f, BotAim.ChestHeight, BotAim.EyeHeight })
        {
            Vector3 up = new(0f, y, 0f);
            Assert.True(LineOfSight.Clear(ga + up, gb + up, blockers.ToArray(), GameMap.Terrain), $"{a} -> {b} blocked at {y} m");
        }
        for (float t = 0f; t <= 1f; t += 0.05f)
        {
            Vector3 p = Vector3.Lerp(ga, gb, t);
            Assert.Equal(0f, GameMap.Terrain.Height(p.X, p.Z));
        }
    }

    [Fact]
    public void EveryMarkerIsInsideTheWallsAndClearOfBoxes()
    {
        MarkerStore markers = MarkerStore.LoadFile(Path.Combine(RepoRoot(), "QA", "Markers.json"));
        Assert.NotEmpty(markers.Markers);
        foreach (var (name, p) in markers.Markers)
        {
            Vector3 g = p.ToGround();
            Assert.InRange(MathF.Abs(g.X), 0f, GameMap.HalfSize - 2f);
            Assert.InRange(MathF.Abs(g.Z), 0f, GameMap.HalfSize - 2f);
            foreach (Box box in GameMap.Boxes)
            {
                bool inside = g.X > box.Min.X - 1f && g.X < box.Max.X + 1f && g.Z > box.Min.Z - 1f && g.Z < box.Max.Z + 1f && box.Min.Y < g.Y + 1.8f;
                Assert.False(inside, $"{name} is within 1 m of a box");
            }
        }
    }

    [Fact]
    public void PoiNamesResolve()
    {
        Assert.True(MarkerStore.Empty().TryResolveName("crossroads", out QaPosition p));
        Assert.Equal(0f, p.X);
    }

    [Fact]
    public void RepositoryScenariosValidate()
    {
        string root = RepoRoot();
        MarkerStore markers = MarkerStore.LoadFile(Path.Combine(root, "QA", "Markers.json"));
        foreach (string file in Directory.GetFiles(Path.Combine(root, "QA", "Scenarios"), "*.json", SearchOption.AllDirectories))
        {
            ScenarioLoadResult load = ScenarioLoader.LoadFile(file);
            Assert.Empty(load.Errors);
            var issues = ScenarioValidator.Validate(load.Scenario!, ActionRegistry.CreateDefault(), markers);
            Assert.DoesNotContain(issues, i => i.IsError);
        }
    }
}
