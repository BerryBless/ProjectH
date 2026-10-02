using System.Text.Json;
using ProjectH.QA;

namespace ProjectH.QA.Tests;

public class ScenarioLoaderTests
{
    [Fact]
    public void ParsesCommonFieldsParamsAndShorthand()
    {
        ScenarioLoadResult r = ScenarioLoader.Parse("""
        {
          "schemaVersion": 1, "name": "N", "seed": 7, "timeoutSeconds": 30, "tags": ["smoke"],
          "server": { "mode": "launch", "options": { "Server:MinPlayers": 2 } },
          "variables": { "hp": 60 },
          "actors": [ { "id": "playerA" } ],
          "steps": [
            { "id": "c", "action": "connect", "actor": "playerA", "timeoutMilliseconds": 500, "continueOnFailure": true },
            { "assert": "player.health", "actor": "playerA", "equals": 60, },  // trailing comma and comment allowed
            { "phase": "act", "action": "wait", "milliseconds": 10 },
            { "action": "wait", "milliseconds": 10 }
          ]
        }
        """);
        Assert.Empty(r.Errors);
        ScenarioDefinition s = r.Scenario!;
        Assert.Equal(7, s.Seed);
        Assert.Equal("2", s.Server.Options["Server:MinPlayers"]);
        Assert.Equal("HeadlessClient", s.Actors[0].Type);
        Assert.Equal(500, s.Steps[0].TimeoutMilliseconds);
        Assert.True(s.Steps[0].ContinueOnFailure);
        Assert.False(s.Steps[0].Params.ContainsKey("actor"));
        Assert.Equal("assert", s.Steps[1].Action);
        Assert.Equal("player.health", s.Steps[1].Params["path"].GetString());
        Assert.Equal("02-assert", s.Steps[1].Id);
        // An explicit phase lasts until the next one.
        Assert.Null(s.Steps[1].EffectivePhase);
        Assert.Equal("act", s.Steps[3].EffectivePhase);
        Assert.Null(s.Steps[3].Phase);
    }

    [Fact]
    public void MalformedJsonIsOneError()
    {
        ScenarioLoadResult r = ScenarioLoader.Parse("{ \"schemaVersion\": 1, \"steps\": [ ");
        Assert.True(r.Malformed);
        Assert.Contains("Malformed JSON", r.Errors.Single());
    }

    [Fact]
    public void ShapeErrorsAreCollected()
    {
        ScenarioLoadResult r = ScenarioLoader.Parse("""{ "schemaVersion": "one", "steps": [ { "actor": "a" }, 5 ] }""");
        Assert.False(r.Malformed);
        Assert.Contains(r.Errors, e => e.Contains("schemaVersion"));
        Assert.Contains(r.Errors, e => e.Contains("'action' is required"));
        Assert.Contains(r.Errors, e => e.Contains("steps[1] must be an object"));
    }
}

public class ScenarioValidatorTests
{
    private static readonly ActionRegistry Registry = ActionRegistry.CreateDefault();

    private static IReadOnlyList<ValidationIssue> Validate(string steps, string actors = """[ { "id": "playerA" } ]""", string extra = "")
    {
        ScenarioLoadResult r = ScenarioLoader.Parse($$"""{ "schemaVersion": 1, "name": "t", {{extra}} "actors": {{actors}}, "steps": {{steps}} }""");
        Assert.Empty(r.Errors);
        MarkerStore markers = MarkerStore.Parse("""{ "markers": [ { "name": "QA_Combat_A", "x": -5, "z": 0 } ] }""");
        return ScenarioValidator.Validate(r.Scenario!, Registry, markers);
    }

    private static void HasError(IReadOnlyList<ValidationIssue> issues, string text) =>
        Assert.Contains(issues, i => i.IsError && i.Message.Contains(text, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void ValidScenarioHasNoIssues()
    {
        var issues = Validate("""
        [ { "action": "connect", "actor": "playerA" },
          { "action": "setPosition", "actor": "playerA", "position": "QA_Combat_A", "phase": "arrange" },
          { "action": "setPosition", "actor": "playerA", "position": "Crossroads" },
          { "action": "save", "path": "player.health", "actor": "playerA", "saveAs": "hp" },
          { "phase": "assert", "assert": "player.health", "actor": "playerA", "lessThan": "${hp}" },
          { "action": "waitFor", "condition": "match.state", "equals": "Playing", "timeoutMilliseconds": 100 } ]
        """);
        Assert.Empty(issues);
    }

    [Fact]
    public void UnknownActionAndActor()
    {
        var issues = Validate("""[ { "action": "teleport", "actor": "playerA" }, { "action": "connect", "actor": "ghost" } ]""");
        HasError(issues, "Unknown action 'teleport'");
        HasError(issues, "Unknown actor 'ghost'");
    }

    [Fact]
    public void BadTimeoutsAndMissingParams()
    {
        var issues = Validate("""
        [ { "action": "wait", "milliseconds": -5 },
          { "action": "connect", "actor": "playerA", "timeoutMilliseconds": -1 },
          { "action": "giveWeapon", "actor": "playerA" },
          { "action": "connect" },
          { "action": "assert", "path": "player.health", "actor": "playerA" } ]
        """, extra: "\"timeoutSeconds\": -3,");
        HasError(issues, "non-negative");
        HasError(issues, "timeoutMilliseconds must be");
        HasError(issues, "timeoutSeconds must be");
        HasError(issues, "Missing parameter 'weapon'");
        HasError(issues, "needs 'actor'");
        HasError(issues, "No operator");
    }

    [Fact]
    public void UnknownVariableButSaveAsDefinesOne()
    {
        var issues = Validate("""
        [ { "assert": "player.health", "actor": "playerA", "equals": "${later}" },
          { "action": "save", "path": "player.health", "actor": "playerA", "saveAs": "later" },
          { "assert": "player.health", "actor": "playerA", "equals": "${later}" },
          { "action": "mark", "text": "run ${runId} seed ${seed}" } ]
        """);
        Assert.Single(issues, i => i.IsError);
        HasError(issues, "Unknown variable '${later}'");
    }

    [Fact]
    public void DuplicateStepIdsAndBadAliases()
    {
        var issues = Validate("""[ { "id": "x", "action": "wait", "milliseconds": 1 }, { "id": "x", "action": "wait", "milliseconds": 1 } ]""",
            actors: """[ { "id": "averyveryveryveryverylongalias01" }, { "id": "bad\u0007" }, { "id": "u", "type": "UnityClient" } ]""");
        HasError(issues, "Duplicate step id");
        HasError(issues, "too long");
        HasError(issues, "not a valid player name");
        HasError(issues, "not supported yet");
    }

    [Fact]
    public void ArrangeCommandsInActOrAssertWarn()
    {
        var issues = Validate("""
        [ { "phase": "arrange", "action": "giveWeapon", "actor": "playerA", "weapon": "Vesper AR" },
          { "phase": "act", "action": "damagePlayer", "actor": "playerA", "amount": 10 },
          { "action": "setHealth", "actor": "playerA", "value": 50 } ]
        """);
        Assert.DoesNotContain(issues, i => i.IsError);
        Assert.Contains(issues, i => !i.IsError && i.Where.Contains("02") && i.Message.Contains("Arrange-only"));
        Assert.Contains(issues, i => !i.IsError && i.Where.Contains("03") && i.Message.Contains("act phase"));
        Assert.DoesNotContain(issues, i => i.Where.Contains("01"));
    }

    [Fact]
    public void UnknownPositionNameAndSpawnedActors()
    {
        var issues = Validate("""
        [ { "action": "setPosition", "actor": "playerA", "position": "Nowhere" },
          { "action": "spawnActors", "count": 3, "prefix": "bot" },
          { "action": "connect", "actor": "bot-003" },
          { "action": "connect", "actor": "bot-004" } ]
        """);
        HasError(issues, "Unknown position name 'Nowhere'");
        HasError(issues, "Unknown actor 'bot-004'");
        Assert.DoesNotContain(issues, i => i.Message.Contains("bot-003"));
    }
}

public class VariablesTests
{
    private static readonly Dictionary<string, JsonElement> Vars = new()
    {
        ["hp"] = JsonSerializer.SerializeToElement(60),
        ["name"] = JsonSerializer.SerializeToElement("Vesper AR"),
        ["spawn"] = JsonSerializer.SerializeToElement(new { itemId = 17, pos = new { x = 1.5 } }),
    };

    [Fact]
    public void WholeReferenceKeepsTheType()
    {
        JsonElement v = Variables.Substitute(JsonSerializer.SerializeToElement("${hp}"), Vars);
        Assert.Equal(JsonValueKind.Number, v.ValueKind);
        Assert.Equal(60, v.GetInt32());
        Assert.Equal(17, Variables.Substitute(JsonSerializer.SerializeToElement("${spawn.itemId}"), Vars).GetInt32());
    }

    [Fact]
    public void InlineReferencesBecomeText()
    {
        JsonElement v = Variables.Substitute(JsonSerializer.SerializeToElement("hp=${hp} gun=${name} x=${spawn.pos.x}"), Vars);
        Assert.Equal("hp=60 gun=Vesper AR x=1.5", v.GetString());
    }

    [Fact]
    public void NestedObjectsAreSubstituted()
    {
        JsonElement v = Variables.Substitute(JsonSerializer.SerializeToElement(new { a = "${hp}", b = new[] { "${name}" } }), Vars);
        Assert.Equal(60, v.GetProperty("a").GetInt32());
        Assert.Equal("Vesper AR", v.GetProperty("b")[0].GetString());
    }

    [Fact]
    public void UnknownVariableIsAStepFailure()
    {
        Assert.Throws<QaStepException>(() => Variables.Substitute(JsonSerializer.SerializeToElement("${nope}"), Vars));
        Assert.Throws<QaStepException>(() => Variables.Substitute(JsonSerializer.SerializeToElement("${spawn.missing}"), Vars));
    }
}

public class ComparisonTests
{
    private static JsonElement J(object? o) => JsonSerializer.SerializeToElement(o);

    private static bool Eval(string op, object? expected, object? actual, object? tolerance = null, Type? hint = null) =>
        Comparison.Evaluate(op, J(expected), tolerance == null ? null : J(tolerance), actual == null ? null : J(actual), hint).Passed;

    [Fact]
    public void Operators()
    {
        Assert.True(Eval("equals", 60, 60));
        Assert.True(Eval("equals", 60, "60"));            // the server may send numeric strings
        Assert.False(Eval("equals", 60, 61));
        Assert.True(Eval("equals", "Playing", "playing"));  // enum names case-insensitively
        Assert.True(Eval("equals", true, true));
        Assert.False(Eval("equals", true, false));
        Assert.True(Eval("notEquals", 1, 2));
        Assert.True(Eval("greaterThan", 10, 11));
        Assert.False(Eval("greaterThan", 10, 10));
        Assert.True(Eval("lessThan", 100, 74.5));
        Assert.False(Eval("lessThan", 100, "abc"));
        Assert.True(Eval("between", new[] { 1, 5 }, 5));
        Assert.False(Eval("between", new[] { 1, 5 }, 6));
        Assert.True(Eval("exists", true, 0));
        Assert.False(Eval("exists", true, null));
        Assert.True(Eval("notExists", true, null));
        Assert.True(Eval("approximately", 100.0, 100.15, 0.2));
        Assert.False(Eval("approximately", 100.0, 100.25, 0.2));
        Assert.True(Eval("contains", "AR", "Vesper AR"));
        Assert.True(Eval("contains", 3, new[] { 1, 2, 3 }));
        Assert.False(Eval("contains", 4, new[] { 1, 2, 3 }));
    }

    [Fact]
    public void NumericEnumIsComparedByName()
    {
        Assert.True(Eval("equals", "Playing", 2, hint: typeof(ProjectH.Shared.Protocol.MatchFlowState)));
        Assert.False(Eval("equals", "Playing", 1, hint: typeof(ProjectH.Shared.Protocol.MatchFlowState)));
    }

    [Fact]
    public void ExpectedTextIsReadable()
    {
        (bool ok, string expected) = Comparison.Evaluate("lessThan", J(60), null, J(100), null);
        Assert.False(ok);
        Assert.Equal("< 60", expected);
    }
}
