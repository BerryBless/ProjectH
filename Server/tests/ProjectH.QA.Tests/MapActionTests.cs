using ProjectH.QA;

namespace ProjectH.QA.Tests;

// Phase 15 D14: the ping and waypoint actions' literal checks (validate reports a broken step before any run).
public class MapActionTests
{
    // 기능: 배우 두 명과 주어진 Step들로 시나리오 JSON을 만든다.
    // 입력: steps - Step 배열 JSON.
    // 출력: 시나리오 JSON.
    private static string Scenario(string steps) =>
        $$"""{ "schemaVersion": 1, "name": "m", "seed": 3, "actors": [ { "id": "a" }, { "id": "b" } ], "steps": {{steps}} }""";

    [Fact]
    public void ValidatorChecksPingAndWaypointSteps()
    {
        ScenarioLoadResult load = ScenarioLoader.Parse(Scenario("""
        [ { "action": "ping", "actor": "a", "kind": "rocket", "position": { "x": 1, "z": 1 } },
          { "action": "ping", "actor": "a", "kind": "enemy" },
          { "action": "ping", "actor": "a", "kind": "item" },
          { "action": "ping", "actor": "a", "kind": "danger" },
          { "action": "ping", "actor": "a", "position": { "x": 1, "z": 1 }, "count": 31 },
          { "action": "waypoint", "actor": "a" },
          { "action": "ping", "actor": "a", "kind": "enemy", "target": "nobody" } ]
        """));
        Assert.Empty(load.Errors);
        var issues = ScenarioValidator.Validate(load.Scenario!, ActionRegistry.CreateDefault(), MarkerStore.Empty());
        void Has(int step, string text) =>
            Assert.Contains(issues, i => i.IsError && i.Where.StartsWith($"step {step:00} ", StringComparison.Ordinal) && i.Message.Contains(text));
        Has(1, "'kind' must be one of");
        Has(2, "needs 'target'");
        Has(3, "needs 'itemId'");
        Has(4, "needs 'position'");
        Has(5, "'count' must be an integer 1-30");
        Has(6, "position");
        Assert.Contains(issues, i => i.IsError && i.Where.StartsWith("step 07 ", StringComparison.Ordinal));   // an unknown target actor

        ScenarioLoadResult good = ScenarioLoader.Parse(Scenario("""
        [ { "action": "ping", "actor": "a", "position": { "x": 1, "z": 1 } },
          { "action": "ping", "actor": "a", "kind": "danger", "position": { "x": 1, "y": 9, "z": 1 }, "count": 6 },
          { "action": "ping", "actor": "a", "kind": "enemy", "target": "b" },
          { "action": "ping", "actor": "a", "kind": "item", "itemId": 12 },
          { "action": "waypoint", "actor": "a", "position": { "x": -5, "z": 5 } },
          { "action": "waypoint", "actor": "a", "clear": true } ]
        """));
        Assert.Empty(good.Errors);
        Assert.DoesNotContain(ScenarioValidator.Validate(good.Scenario!, ActionRegistry.CreateDefault(), MarkerStore.Empty()), i => i.IsError);
    }
}
