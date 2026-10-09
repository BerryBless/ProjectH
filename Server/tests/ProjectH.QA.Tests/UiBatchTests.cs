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

    // 기능: 임시 저장소에 QA/Scenarios/Smoke를 만들고 가짜 QA 서버·MockActor를 꽂은 UI Host를 빈 포트로 띄운 뒤 HttpClient를 연결한다.
    // 입력: 없음.
    // 출력: 반환값 없음. _host와 _http가 준비된다.
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

    // 기능: 본문을 JSON으로 직렬화해 UI Host에 POST한다.
    // 입력: path - 요청 경로, body - JSON으로 보낼 객체.
    // 출력: HTTP 응답.
    private Task<HttpResponseMessage> Post(string path, object body) =>
        _http.PostAsync(path, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

    // 기능: /api/run 상태를 20 ms 간격으로 읽어 조건을 만족할 때까지 기다린다.
    // 입력: condition - 상태 JSON에 대한 조건, timeoutMs - 최대 대기 시간.
    // 출력: 조건을 만족한 상태 JSON(문서와 분리된 복사본). 제한 시간을 넘기면 TimeoutException.
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

    // 기능: 실행 상태 JSON에서 status 값을 꺼낸다.
    // 입력: s - /api/run 상태 JSON.
    // 출력: status 문자열.
    private static string Status(JsonElement s) => s.GetProperty("status").GetString()!;
    // 기능: 실행 상태 JSON에서 i번째 단계의 status 값을 꺼낸다.
    // 입력: s - /api/run 상태 JSON, i - 단계 인덱스.
    // 출력: 단계 status 문자열.
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
