using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ProjectH.QA;

namespace ProjectH.QA.Tests;

// QA-2: the UI host in-process on a free port, against the fake QA server and MockActors.
public sealed class UiTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-ui-" + Guid.NewGuid().ToString("N"));
    private readonly FakeQaServer _server = new();
    private QaUiHost _host = null!;
    private HttpClient _http = null!;

    // 기능: 임시 저장소에 QA/Scenarios/Smoke와 Server 폴더를 만들고 가짜 QA 서버·MockActor를 꽂은 UI Host를 빈 포트로 띄운 뒤 HttpClient를 연결한다.
    // 입력: 없음.
    // 출력: 반환값 없음. _host와 _http가 준비된다.
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "QA", "Scenarios", "Smoke"));
        Directory.CreateDirectory(Path.Combine(_root, "Server"));
        _host = await QaUiHost.StartAsync(new UiHostOptions
        {
            RepoRoot = _root,
            Port = 0,
            AttachUrl = "http://127.0.0.1:1/",
            PollMs = 20,
            WriteReports = false,
            ServerClientFactory = _ => _server,
            ActorFactory = (alias, _) => new MockActor(alias),
        }, default);
        _http = new HttpClient { BaseAddress = new Uri(_host.Url), Timeout = TimeSpan.FromSeconds(10) };
    }

    // 기능: HttpClient와 UI Host를 닫고 임시 저장소를 지운다.
    // 입력: 없음.
    // 출력: 반환값 없음. Host가 내려가고 임시 폴더가 삭제된다(IO 오류는 무시).
    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // 기능: playerA 한 명과 주어진 단계·추가 필드로 시나리오 JSON을 만든다.
    // 입력: steps - steps 배열 JSON, extra - 루트에 끼울 추가 필드 JSON.
    // 출력: 시나리오 JSON 문자열.
    private static string Scenario(string steps, string extra = "") =>
        $$"""{ "schemaVersion": 1, "name": "ui", {{extra}} "actors": [ { "id": "playerA" } ], "steps": {{steps}} }""";

    // 기능: 본문을 JSON으로 직렬화한 POST 요청을 만들고, 필요하면 보내기 전에 요청을 고친 뒤 UI Host로 보낸다.
    // 입력: path - 요청 경로, body - JSON으로 보낼 객체, edit - 보내기 전에 헤더 등을 바꾸는 함수(없으면 null).
    // 출력: HTTP 응답.
    private Task<HttpResponseMessage> Post(string path, object body, Action<HttpRequestMessage>? edit = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        edit?.Invoke(request);
        return _http.SendAsync(request);
    }

    // 기능: /api/run 상태를 한 번 읽는다.
    // 입력: 없음.
    // 출력: 상태 JSON(문서와 분리된 복사본).
    private async Task<JsonElement> State()
    {
        using JsonDocument doc = JsonDocument.Parse(await _http.GetStringAsync("/api/run"));
        return doc.RootElement.Clone();
    }

    // 기능: /api/run 상태를 20 ms 간격으로 읽어 조건을 만족할 때까지 기다린다.
    // 입력: condition - 상태 JSON에 대한 조건, timeoutMs - 최대 대기 시간.
    // 출력: 조건을 만족한 상태 JSON. 제한 시간을 넘기면 TimeoutException.
    private async Task<JsonElement> WaitState(Func<JsonElement, bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            JsonElement s = await State();
            if (condition(s)) return s;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("state never matched: " + s.GetRawText());
            await Task.Delay(20);
        }
    }

    // 기능: 실행 상태 JSON에서 status 값을 꺼낸다.
    // 입력: s - /api/run 상태 JSON.
    // 출력: status 문자열.
    private static string Status(JsonElement s) => s.GetProperty("status").GetString()!;
    // 기능: 실행 상태 JSON에서 i번째 단계의 status 값을 꺼낸다.
    // 입력: s - /api/run 상태 JSON, i - 단계 인덱스.
    // 출력: 단계 status 문자열.
    private static string StepStatus(JsonElement s, int i) => s.GetProperty("steps")[i].GetProperty("status").GetString()!;

    [Theory]
    [InlineData("../x.json")]
    [InlineData("..\\..\\x.json")]
    [InlineData("Smoke/../../x.json")]
    [InlineData("C:/x.json")]
    [InlineData("/abs.json")]
    [InlineData("Smoke/a.txt")]
    public async Task SaveRejectsPathsOutsideTheScenarioFolder(string path)
    {
        HttpResponseMessage r = await Post("/api/save", new { path, text = Scenario("""[ { "action": "wait", "milliseconds": 1 } ]""") });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.False(File.Exists(Path.Combine(_root, "QA", "x.json")));
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync("/api/scenario?path=" + Uri.EscapeDataString(path))).StatusCode);
    }

    [Fact]
    public async Task SaveValidatesFirst()
    {
        HttpResponseMessage bad = await Post("/api/save", new { path = "Smoke/new.json", text = Scenario("""[ { "action": "fly" } ]""") });
        Assert.Equal((HttpStatusCode)422, bad.StatusCode);
        Assert.False(File.Exists(Path.Combine(_root, "QA", "Scenarios", "Smoke", "new.json")));

        HttpResponseMessage ok = await Post("/api/save", new { path = "Smoke/new.json", text = Scenario("""[ { "action": "wait", "milliseconds": 1 } ]""") });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(File.Exists(Path.Combine(_root, "QA", "Scenarios", "Smoke", "new.json")));
        string tree = await _http.GetStringAsync("/api/tree");
        Assert.Contains("Smoke/new.json", tree);
    }

    [Fact]
    public async Task CrossSiteAndForeignHostRequestsAreRefused()
    {
        object body = new { };
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/api/run/stop", body, r => r.Headers.Add("Origin", "http://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/api/run/stop", body, r => r.Headers.Add("Sec-Fetch-Site", "cross-site"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/api/run/stop", body, r => r.Headers.Host = "evil.example:" + _host.Port)).StatusCode);
        var get = new HttpRequestMessage(HttpMethod.Get, "/api/tree");
        get.Headers.Host = "rebind.example:" + _host.Port;
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(get)).StatusCode);
        var plain = new HttpRequestMessage(HttpMethod.Post, "/api/run/stop") { Content = new StringContent("{}", Encoding.UTF8, "text/plain") };
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _http.SendAsync(plain)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post("/api/run/stop", body, r => r.Headers.Add("Origin", $"http://127.0.0.1:{_host.Port}"))).StatusCode);
    }

    [Fact]
    public async Task SingleStepPauseStepResume()
    {
        string text = Scenario("""
        [ { "id": "a", "action": "wait", "milliseconds": 10 },
          { "id": "b", "action": "wait", "milliseconds": 10 },
          { "id": "c", "action": "wait", "milliseconds": 10 } ]
        """);
        Assert.Equal(HttpStatusCode.OK, (await Post("/api/run", new { text, mode = "single" })).StatusCode);
        await WaitState(s => Status(s) == "paused" && s.GetProperty("waitingAt").GetInt32() == 0);
        // One run at a time.
        Assert.Equal(HttpStatusCode.Conflict, (await Post("/api/run", new { text })).StatusCode);

        await Post("/api/run/step", new { });
        JsonElement s1 = await WaitState(s => Status(s) == "paused" && s.GetProperty("waitingAt").GetInt32() == 1);
        Assert.Equal("Passed", StepStatus(s1, 0));
        Assert.Equal("Pending", StepStatus(s1, 1));

        await Post("/api/run/resume", new { });
        JsonElement done = await WaitState(s => Status(s) == "finished");
        Assert.Equal("Passed", done.GetProperty("runStatus").GetString());
        Assert.True(done.GetProperty("unsaved").GetBoolean());
    }

    [Fact]
    public async Task RunUntilPausesAfterTheStepAndRunFromSkipsEarlierSteps()
    {
        string text = Scenario("""
        [ { "id": "a", "action": "wait", "milliseconds": 1 },
          { "id": "b", "action": "wait", "milliseconds": 1 },
          { "id": "c", "action": "wait", "milliseconds": 1 } ]
        """);
        await Post("/api/run", new { text, mode = "until", stepIndex = 0 });
        JsonElement paused = await WaitState(s => Status(s) == "paused");
        Assert.Equal(1, paused.GetProperty("waitingAt").GetInt32());
        await Post("/api/run/resume", new { });
        await WaitState(s => Status(s) == "finished");

        await Post("/api/run", new { text, mode = "from", stepIndex = 2 });
        JsonElement done = await WaitState(s => Status(s) == "finished");
        Assert.Equal("Skipped", StepStatus(done, 0));
        Assert.Equal("Skipped", StepStatus(done, 1));
        Assert.Equal("Passed", StepStatus(done, 2));
        Assert.Contains(done.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("Run From Step 03"));
    }

    [Fact]
    public async Task StopCancelsPromptly()
    {
        string text = Scenario("""[ { "id": "long", "action": "wait", "milliseconds": 30000 }, { "action": "wait", "milliseconds": 1 } ]""");
        await Post("/api/run", new { text });
        await WaitState(s => StepStatus(s, 0) == "Running");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Post("/api/run/stop", new { });
        JsonElement done = await WaitState(s => Status(s) == "finished", 5000);
        Assert.True(clock.ElapsedMilliseconds < 5000);
        Assert.Equal("Cancelled", done.GetProperty("runStatus").GetString());
        Assert.Equal("Cancelled", StepStatus(done, 0));
        Assert.Equal("Skipped", StepStatus(done, 1));
    }

    [Fact]
    public async Task FailedStepIsHeldAndCanBeRetried()
    {
        _server.AddPlayer("qa-playerA");
        string text = Scenario("""
        [ { "action": "connect", "actor": "playerA" },
          { "id": "hp", "assert": "player.health", "actor": "playerA", "equals": 50 },
          { "id": "after", "action": "wait", "milliseconds": 1 } ]
        """);
        await Post("/api/run", new { text });
        JsonElement held = await WaitState(s => s.GetProperty("failedWaiting").GetBoolean());
        Assert.Equal("Failed", StepStatus(held, 1));
        Assert.Equal("Pending", StepStatus(held, 2));

        // Fix the world, then retry the held step in the same run.
        _server.Players["qa-playerA"]["health"] = 50;
        HttpResponseMessage retry = await Post("/api/run/retry", new { });
        Assert.Contains("may have changed", await retry.Content.ReadAsStringAsync());
        JsonElement done = await WaitState(s => Status(s) == "finished");
        Assert.Equal("Passed", done.GetProperty("runStatus").GetString());
        Assert.Equal(1, done.GetProperty("steps")[1].GetProperty("attempts").GetInt32());
        Assert.Contains(done.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("retried"));
        Assert.Equal(HttpStatusCode.Conflict, (await Post("/api/run/retry", new { })).StatusCode);
    }

    [Fact]
    public async Task ResumeAfterAHeldFailureFinishesAsFailed()
    {
        _server.AddPlayer("qa-playerA");
        string text = Scenario("""[ { "action": "connect", "actor": "playerA" }, { "id": "hp", "assert": "player.health", "actor": "playerA", "equals": 1 } ]""");
        await Post("/api/run", new { text });
        await WaitState(s => s.GetProperty("failedWaiting").GetBoolean());
        await Post("/api/run/resume", new { });
        JsonElement done = await WaitState(s => Status(s) == "finished");
        Assert.Equal("Failed", done.GetProperty("runStatus").GetString());
        Assert.Equal(1, done.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task StopWhileAFailureIsHeldKeepsTheFailure()
    {
        _server.AddPlayer("qa-playerA");
        string text = Scenario("""
        [ { "action": "connect", "actor": "playerA" },
          { "id": "hp", "assert": "player.health", "actor": "playerA", "equals": 50 },
          { "id": "after", "action": "wait", "milliseconds": 1 } ]
        """);
        await Post("/api/run", new { text });
        await WaitState(s => s.GetProperty("failedWaiting").GetBoolean());
        await Post("/api/run/stop", new { });
        JsonElement done = await WaitState(s => Status(s) == "finished");
        Assert.Equal("Failed", done.GetProperty("runStatus").GetString());
        Assert.Equal(1, done.GetProperty("exitCode").GetInt32());
        JsonElement step = done.GetProperty("steps")[1];
        Assert.Equal("Failed", step.GetProperty("status").GetString());
        Assert.Equal("50", step.GetProperty("expected").GetString());
        Assert.Equal("100", step.GetProperty("actual").GetString());
        Assert.Equal("Skipped", StepStatus(done, 2));
    }

    [Fact]
    public async Task ManualCheckWaitsForTheAnswerInTheUi()
    {
        string text = Scenario("""
        [ { "id": "ime", "action": "manualCheck", "description": "Korean IME composes in the name field" },
          { "id": "after", "action": "wait", "milliseconds": 1 } ]
        """);
        Assert.Equal(HttpStatusCode.Conflict, (await Post("/api/run/manual", new { passed = true })).StatusCode);
        await Post("/api/run", new { text });
        JsonElement waiting = await WaitState(s => s.TryGetProperty("manualCheck", out JsonElement m) && m.ValueKind == JsonValueKind.String);
        Assert.Equal("Korean IME composes in the name field", waiting.GetProperty("manualCheck").GetString());
        Assert.Equal("paused", Status(waiting));
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("/api/run/manual", new { passed = "yes" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post("/api/run/manual", new { passed = false, note = "composition box missing" })).StatusCode);
        // A FAIL is a failed step like any other in a UI run: held (Retry asks again), Resume finishes.
        await WaitState(s => s.GetProperty("failedWaiting").GetBoolean());
        await Post("/api/run/resume", new { });
        JsonElement done = await WaitState(s => Status(s) == "finished");
        Assert.Equal("Failed", done.GetProperty("runStatus").GetString());
        Assert.Contains("composition box missing", done.GetProperty("steps")[0].GetProperty("actual").GetString());
    }

    [Fact]
    public async Task PausedRunDoesNotTimeOut()
    {
        string text = Scenario("""[ { "action": "wait", "milliseconds": 1 }, { "action": "wait", "milliseconds": 1 } ]""", "\"timeoutSeconds\": 0.5,");
        await Post("/api/run", new { text, mode = "single" });
        await WaitState(s => Status(s) == "paused");
        await Task.Delay(1200);   // well past the scenario timeout, but paused
        await Post("/api/run/resume", new { });
        JsonElement done = await WaitState(s => Status(s) == "finished");
        Assert.Equal("Passed", done.GetProperty("runStatus").GetString());
    }

    [Fact]
    public async Task InvalidScenarioDoesNotStart()
    {
        HttpResponseMessage r = await Post("/api/run", new { text = Scenario("""[ { "action": "fly" } ]""") });
        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("idle", Status(await State()));
    }

    [Fact]
    public async Task StreamStartsWithTheState()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/stream");
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        string? first = await reader.ReadLineAsync(cts.Token);
        Assert.Equal("event: state", first);
    }

    [Fact]
    public async Task ReportsAreListedAndServed()
    {
        string dir = Path.Combine(_root, "QA", "Reports", "qa-20261002-000000-abcd");
        ReportWriter.Write(new RunReport { RunId = "qa-20261002-000000-abcd", Scenario = "listed", Status = RunStatus.Passed }, dir);
        string list = await _http.GetStringAsync("/api/reports");
        Assert.Contains("\"scenario\":\"listed\"", list);
        Assert.Contains("\"status\":\"Passed\"", list);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/reports/qa-20261002-000000-abcd/report.html")).StatusCode);
        Directory.CreateDirectory(Path.Combine(dir, "screenshots"));
        File.WriteAllBytes(Path.Combine(dir, "screenshots", "004_viewer_hud.png"), new byte[] { 0x89, 0x50 });
        HttpResponseMessage shot = await _http.GetAsync("/reports/qa-20261002-000000-abcd/screenshots/004_viewer_hud.png");
        Assert.Equal(HttpStatusCode.OK, shot.StatusCode);
        Assert.Equal("image/png", shot.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/reports/qa-20261002-000000-abcd/screenshots/report.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/reports/qa-20261002-000000-abcd/screenshots/..%2Freport.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/reports/bad-id/screenshots/004_viewer_hud.png")).StatusCode);
    }

    [Fact]
    public async Task EveryResponseForbidsFraming()
    {
        foreach (string url in new[] { "/", "/api/tree", "/missing.txt" })
        {
            HttpResponseMessage r = await _http.GetAsync(url);
            Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("frame-ancestors 'none'", r.Headers.GetValues("Content-Security-Policy").Single());
        }
        HttpResponseMessage refused = await Post("/api/run/stop", new { }, r => r.Headers.Add("Origin", "http://evil.example"));
        Assert.Equal("DENY", refused.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task ActionsAndMarkersAreListed()
    {
        string actions = await _http.GetStringAsync("/api/actions");
        Assert.Contains("\"name\":\"build\"", actions);
        Assert.Contains("\"serverCommand\":true", actions);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/api/markers")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/secrets.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/reports/..%2F..%2Fx/report.html")).StatusCode);
    }
}

public class UiReportAndDebugTests
{
    // 기능: 새 임시 저장소에 QA/Scenarios를 만들고 가짜 QA 서버를 꽂은 UI Host를 빈 포트로 띄운다.
    // 입력: 없음.
    // 출력: 띄운 Host, 그 가짜 서버, 임시 저장소 루트 경로(정리는 호출자 몫).
    private static async Task<(QaUiHost Host, FakeQaServer Server, string Root)> NewHost()
    {
        string root = Path.Combine(Path.GetTempPath(), "qa-ui-x-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "QA", "Scenarios"));
        var server = new FakeQaServer();
        QaUiHost host = await QaUiHost.StartAsync(new UiHostOptions
        {
            RepoRoot = root, Port = 0, AttachUrl = "http://127.0.0.1:1/", WriteReports = false, ServerClientFactory = _ => server,
        }, default);
        return (host, server, root);
    }

    // 기능: 30초 wait 한 단계짜리 시나리오를 파싱한다.
    // 입력: 없음.
    // 출력: 파싱된 ScenarioDefinition.
    private static ScenarioDefinition LongScenario() =>
        ScenarioLoader.Parse("""{ "schemaVersion": 1, "name": "long", "steps": [ { "action": "wait", "milliseconds": 30000 } ] }""").Scenario!;

    [Fact]
    public async Task ShutdownWaitsForARunStartedJustBeforeAndRefusesNewOnes()
    {
        (QaUiHost host, FakeQaServer server, string root) = await NewHost();
        // Start and close back to back: the run's completion exists before Start returns, so the close waits for it.
        Assert.Null(host.Session.Start(new UiRunRequest(), LongScenario(), Array.Empty<ValidationIssue>(), unsaved: true));
        await host.Session.StopAndWaitAsync();
        Assert.Equal("finished", host.Session.State().Status);
        Assert.Equal("The UI is shutting down.", host.Session.Start(new UiRunRequest(), LongScenario(), Array.Empty<ValidationIssue>(), unsaved: true));
        Assert.Equal("finished", host.Session.State().Status);
        await host.DisposeAsync();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task AnOpenStreamDoesNotHoldTheShutdown()
    {
        (QaUiHost host, _, string root) = await NewHost();
        using var http = new HttpClient { BaseAddress = new Uri(host.Url) };
        using HttpResponseMessage stream = await http.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await host.DisposeAsync();
        Assert.True(clock.ElapsedMilliseconds < 3000, $"took {clock.ElapsedMilliseconds} ms");
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void LaunchArgumentsAlwaysCarryTheParentPid()
    {
        var overrides = new Dictionary<string, string> { ["Qa:ParentPid"] = "1" };
        IReadOnlyList<string> args = ServerProcessManager.BuildArguments("server.dll", 7, overrides);
        Assert.Contains($"--Qa:ParentPid={Environment.ProcessId}", args);
        Assert.DoesNotContain("--Qa:ParentPid=1", args);
    }

    [Fact]
    public async Task ClosingTheUiStopsTheActiveRunFirst()
    {
        string root = Path.Combine(Path.GetTempPath(), "qa-ui-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "QA", "Scenarios"));
        var server = new FakeQaServer();
        QaUiHost host = await QaUiHost.StartAsync(new UiHostOptions
        {
            RepoRoot = root, Port = 0, AttachUrl = "http://127.0.0.1:1/", WriteReports = false, ServerClientFactory = _ => server,
        }, default);
        ScenarioLoadResult load = ScenarioLoader.Parse("""{ "schemaVersion": 1, "name": "long", "steps": [ { "action": "wait", "milliseconds": 30000 } ] }""");
        Assert.Null(host.Session.Start(new UiRunRequest(), load.Scenario!, Array.Empty<ValidationIssue>(), unsaved: true));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (host.Session.State().Steps[0].Status != "Running" && DateTime.UtcNow < deadline) await Task.Delay(20);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await host.DisposeAsync();
        Assert.True(clock.ElapsedMilliseconds < 5000);
        UiRunState state = host.Session.State();
        Assert.Equal("finished", state.Status);
        Assert.Equal("Cancelled", state.RunStatus);
        // The run's end mark reached the server: cleanup ran before the host stopped.
        Assert.Equal(2, server.Commands.Count(c => c.Command == "mark"));
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task DebugRunSnapshotsEveryStep()
    {
        ScenarioLoadResult load = ScenarioLoader.Parse("""{ "schemaVersion": 1, "name": "d", "steps": [ { "action": "wait", "milliseconds": 1 }, { "action": "wait", "milliseconds": 1 } ] }""");
        var options = new QaRunOptions
        {
            RepoRoot = Path.GetTempPath(), AttachUrl = "http://127.0.0.1:1/", WriteReport = false, DebugRun = true,
            ServerClientFactory = _ => new FakeQaServer(),
        };
        RunReport r = await new QaOrchestrator(options, ActionRegistry.CreateDefault(), MarkerStore.Empty(), new StringWriter())
            .RunAsync(load.Scenario!, Array.Empty<ValidationIssue>(), default);
        Assert.Equal(4, r.DebugSnapshots.Count);
        Assert.Equal(new[] { "before", "after", "before", "after" }, r.DebugSnapshots.Select(s => s.When).ToArray());
        Assert.NotNull(r.DebugSnapshots[0].Match);
    }
}

public class UiHubTests
{
    [Fact]
    public void ABacklogIsCutToOneBatchAndCounted()
    {
        var hub = new UiHub();
        for (int i = 0; i < 1000; i++) hub.Log("QA", $"line {i}");
        UiLogBatch batch = hub.ReadLogs(0, 50);
        Assert.Equal(50, batch.Lines.Count);
        Assert.Equal(950, batch.Skipped);
        Assert.Equal("line 999", batch.Lines[^1].Text);
        Assert.Equal(1000, batch.Next);
        Assert.Empty(hub.ReadLogs(batch.Next, 50).Lines);
    }

    [Fact]
    public void TheRingDropsTheOldestAndReadersCountIt()
    {
        var hub = new UiHub();
        for (int i = 0; i < UiHub.LogCapacity + 500; i++) hub.Log("Server", $"l{i}");
        Assert.Equal(UiHub.LogCapacity, hub.TailLogs(int.MaxValue).Count);
        UiLogBatch batch = hub.ReadLogs(0, 50);
        Assert.Equal(UiHub.LogCapacity + 500 - 50, batch.Skipped);
        UiLogBatch small = hub.ReadLogs(UiHub.LogCapacity + 480, 50);
        Assert.Equal(20, small.Lines.Count);
        Assert.Equal(0, small.Skipped);
    }

    [Fact]
    public void UnknownCategoriesBecomeQaAndEventsAreBounded()
    {
        var hub = new UiHub();
        hub.Log("Bogus", "x");
        Assert.Equal("QA", hub.TailLogs(1)[0].Category);
        for (int i = 0; i < UiHub.EventCapacity + 10; i++) hub.Publish("step", new { i });
        (IReadOnlyList<UiEvent> events, long next, bool lost) = hub.ReadEvents(0);
        Assert.True(lost);
        Assert.Equal(UiHub.EventCapacity, events.Count);
        Assert.Equal(UiHub.EventCapacity + 10, next);
    }
}
