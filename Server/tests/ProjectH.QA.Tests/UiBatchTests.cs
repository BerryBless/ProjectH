using System.Net;
using System.Text;
using System.Text.Json;
using ProjectH.QA;

namespace ProjectH.QA.Tests;

// QA-5 in the UI host (in-process, fake QA server, MockActors), like UiTests.
public sealed class UiBatchTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-ui5-" + Guid.NewGuid().ToString("N"));
    private readonly FakeQaServer _server = new();
    private QaUiHost _host = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "QA", "Scenarios", "Smoke"));
        _host = await QaUiHost.StartAsync(new UiHostOptions
        {
            RepoRoot = _root, Port = 0, AttachUrl = "http://127.0.0.1:1/", PollMs = 20, WriteReports = false,
            ServerClientFactory = _ => _server, ActorFactory = (alias, _) => new MockActor(alias),
        }, default);
        _http = new HttpClient { BaseAddress = new Uri(_host.Url), Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Scenario(string steps, string extra = "") =>
        $$"""{ "schemaVersion": 1, "name": "ui", {{extra}} "actors": [ { "id": "playerA" } ], "steps": {{steps}} }""";

    private Task<HttpResponseMessage> Post(string path, object body) =>
        _http.PostAsync(path, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

    private async Task<JsonElement> WaitState(Func<JsonElement, bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            using JsonDocument doc = JsonDocument.Parse(await _http.GetStringAsync("/api/run"));
            JsonElement s = doc.RootElement.Clone();
            if (condition(s)) return s;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("state never matched: " + s.GetRawText());
            await Task.Delay(20);
        }
    }

    private static string Status(JsonElement s) => s.GetProperty("status").GetString()!;
    private static string StepStatus(JsonElement s, int i) => s.GetProperty("steps")[i].GetProperty("status").GetString()!;

    // QA-5 D31-D32: the toolbar's Repeat / Seed sweep and a scenario's parameters run as a batch: one row per run with
    // its parameter set and seed, failures recorded (not held), the summary at the end.
    [Fact]
    public async Task BatchRunsShowEveryRunWithItsParametersAndSeed()
    {
        string text = Scenario("""[ { "action": "connect", "actor": "playerA" }, { "assert": "var.seed", "notEquals": 2 } ]""",
            """ "parameters": [ { "ping": 0 }, { "ping": 100 } ], """);
        HttpResponseMessage r = await Post("/api/run", new { text, mode = "run", seedSweep = "1..2" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        JsonElement s = await WaitState(x => Status(x) == "finished");
        JsonElement batch = s.GetProperty("batch");
        Assert.Equal(4, batch.GetProperty("total").GetInt32());
        Assert.Equal(4, batch.GetProperty("done").GetInt32());
        Assert.Equal(2, batch.GetProperty("failed").GetInt32());
        JsonElement[] rows = batch.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(new[] { "{\"ping\":0}", "{\"ping\":0}", "{\"ping\":100}", "{\"ping\":100}" }, rows.Select(x => x.GetProperty("parameters").GetString()));
        Assert.Equal(new[] { 1, 2, 1, 2 }, rows.Select(x => x.GetProperty("seed").GetInt32()));
        Assert.Contains(batch.GetProperty("summary").EnumerateArray(), l => l.GetString()!.Contains("failing seeds: 2"));
        Assert.Equal("Failed", s.GetProperty("runStatus").GetString());

        // Batches are for Run only, and the options are checked.
        r = await Post("/api/run", new { text, mode = "single", repeat = 2 });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        r = await Post("/api/run", new { text, mode = "run", seedSweep = "3..1" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        r = await Post("/api/run", new { text, mode = "run", repeat = 1001 });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task StopEndsABatchPromptly()
    {
        string text = Scenario("""[ { "action": "connect", "actor": "playerA" }, { "action": "wait", "seconds": 30 } ]""", """ "seed": 4, """);
        Assert.Equal(HttpStatusCode.OK, (await Post("/api/run", new { text, mode = "run", repeat = 20 })).StatusCode);
        await WaitState(x => Status(x) == "running" && StepStatus(x, 1) == "Running");
        Assert.Equal(HttpStatusCode.OK, (await Post("/api/run/stop", new { })).StatusCode);
        JsonElement s = await WaitState(x => Status(x) == "finished");
        Assert.Equal(1, s.GetProperty("batch").GetProperty("done").GetInt32());
        Assert.Contains(s.GetProperty("batch").GetProperty("summary").EnumerateArray(), l => l.GetString()!.Contains("stopped early"));
    }
}
