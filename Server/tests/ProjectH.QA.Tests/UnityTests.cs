using System.Net;
using System.Text;
using System.Text.Json;
using ProjectH.QA;

namespace ProjectH.QA.Tests;

// A Unity player's QA receiver in memory (Docs/QA.md "Unity Client"): status, screenshot (writes a PNG-named file into
// ShotDir), UI commands with the player's screen rules (openMenu only in game, 409 otherwise).
public sealed class FakeUnity : HttpMessageHandler
{
    private readonly object _gate = new();

    public FakeUnity(string shotDir)
    {
        ShotDir = shotDir;
        Directory.CreateDirectory(shotDir);
    }

    public string ShotDir { get; }
    public string Screen { get; set; } = "InGame";
    public bool Joined { get; set; } = true;
    public bool StatsOpen { get; set; }
    public List<string> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath;
        string body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_gate) Requests.Add($"{request.Method} {path} {body}");
        switch (path)
        {
            case "/qa/status":
                return Json(200, new { ok = true, devPlayerId = "qa-viewer", connected = Joined, joined = Joined, screen = Screen, statsOpen = StatsOpen, debugVisible = false, alive = Joined, health = 100, fps = 60.0, frame = 1234 });
            case "/qa/screenshot":
            {
                string name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString()!;
                string file = Path.Combine(ShotDir, name + ".png");
                await File.WriteAllBytesAsync(file, new byte[] { 0x89, 0x50, 0x4E, 0x47 }, cancellationToken);
                return Json(200, new { ok = true, path = file });
            }
            case "/qa/ui":
            {
                string command = JsonDocument.Parse(body).RootElement.GetProperty("command").GetString()!;
                bool applied = command switch
                {
                    "openMenu" when Screen == "InGame" => Set("Menu"),
                    "closeMenu" when Screen == "Menu" && !StatsOpen => Set("InGame"),
                    "openStats" when Screen == "Menu" => StatsOpen = true,
                    "closeStats" when StatsOpen => !(StatsOpen = false),
                    _ => false,
                };
                return applied
                    ? Json(200, new { ok = true, command, screen = Screen, statsOpen = StatsOpen, debugVisible = false })
                    : Json(409, new { ok = false, error = $"{command} does not apply to {Screen}" });
            }
            default:
                return Json(404, new { ok = false, error = "not found" });
        }
    }

    private bool Set(string screen)
    {
        Screen = screen;
        return true;
    }

    private static HttpResponseMessage Json(int status, object value) => new((HttpStatusCode)status)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };
}

public class UnityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-unity-" + Guid.NewGuid().ToString("N"));

    public UnityTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "QA", "Scenarios"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Scenario(string actors, string steps) =>
        $$"""{ "schemaVersion": 1, "name": "u", "seed": 3, "actors": {{actors}}, "steps": {{steps}} }""";

    private const string Viewer = """[ { "id": "viewer", "type": "UnityClient", "unity": { "attachPort": 18777 } } ]""";

    private async Task<(RunReport Report, string Output)> Run(string json, FakeUnity? unity, string? unityExe = null,
        Func<StepDefinition, string, CancellationToken, Task<ManualCheckAnswer>>? prompt = null)
    {
        ScenarioLoadResult load = ScenarioLoader.Parse(json);
        Assert.Empty(load.Errors);
        var issues = ScenarioValidator.Validate(load.Scenario!, ActionRegistry.CreateDefault(), MarkerStore.Empty());
        Assert.DoesNotContain(issues, i => i.IsError);
        var server = new FakeQaServer();
        server.AddPlayer("qa-viewer");
        var output = new StringWriter();
        var options = new QaRunOptions
        {
            RepoRoot = _root, AttachUrl = "http://127.0.0.1:1/", PollMs = 20, ReportDir = Path.Combine(_root, "reports"),
            ServerClientFactory = _ => server,
            ActorFactory = (alias, _) => new MockActor(alias),
            UnityHandlerFactory = _ => unity,
            UnityExe = unityExe,
            ManualPrompt = prompt,
        };
        RunReport report = await new QaOrchestrator(options, ActionRegistry.CreateDefault(), MarkerStore.Empty(), output).RunAsync(load.Scenario!, issues, default);
        return (report, output.ToString());
    }

    [Fact]
    public async Task UnityActorJoinsTakesScreenshotsAndDrivesTheUi()
    {
        var unity = new FakeUnity(Path.Combine(_root, "shots"));
        (RunReport r, string output) = await Run(Scenario(Viewer, """
        [ { "action": "connect", "actor": "viewer" },
          { "action": "waitForUnity", "actor": "viewer", "condition": "joined", "equals": true, "timeoutMilliseconds": 3000 },
          { "assert": "actor.unity.screen", "actor": "viewer", "equals": "InGame" },
          { "action": "captureScreenshot", "actor": "viewer", "name": "hud" },
          { "action": "uiCommand", "actor": "viewer", "command": "openMenu" },
          { "action": "waitForUnity", "actor": "viewer", "condition": "unity.screen", "equals": "Menu", "timeoutMilliseconds": 3000 },
          { "action": "uiCommand", "actor": "viewer", "command": "openStats" },
          { "action": "captureScreenshot", "actor": "viewer", "name": "stats" },
          { "action": "uiCommand", "actor": "viewer", "command": "closeStats" },
          { "action": "uiCommand", "actor": "viewer", "command": "closeMenu" },
          { "assert": "player.connected", "actor": "viewer", "equals": true },
          { "action": "disconnect", "actor": "viewer" } ]
        """), unity);
        Assert.True(r.Status == RunStatus.Passed, output);
        Assert.Equal(2, r.Screenshots.Count);
        Assert.All(r.Screenshots, s => Assert.True(File.Exists(s)));
        Assert.EndsWith("004_viewer_hud.png", r.Screenshots[0]);
        Assert.Contains(r.Cleanup, c => c.Name == "unity viewer" && c.Message.Contains("attached"));
        string html = File.ReadAllText(Path.Combine(r.ReportDirectory!, "report.html"));
        Assert.Contains("<img src=", html);
    }

    [Fact]
    public async Task AUiCommandThatDoesNotApplyFails()
    {
        var unity = new FakeUnity(Path.Combine(_root, "shots")) { Screen = "InGame" };
        (RunReport r, _) = await Run(Scenario(Viewer, """
        [ { "action": "connect", "actor": "viewer" },
          { "id": "close", "action": "uiCommand", "actor": "viewer", "command": "closeMenu" } ]
        """), unity);
        Assert.Equal("close", r.Failure!.StepId);
        Assert.Contains("refused on screen InGame", r.Failure.Message);
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public async Task NoUnityPlayerSkipsTheRestInsteadOfFailing()
    {
        (RunReport r, _) = await Run(Scenario("""[ { "id": "viewer", "type": "UnityClient" } ]""", """
        [ { "id": "c", "action": "connect", "actor": "viewer" },
          { "action": "captureScreenshot", "actor": "viewer", "name": "hud" } ]
        """), null);
        Assert.Equal(RunStatus.Skipped, r.Status);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("--unity-exe", r.Steps[0].Message);
        Assert.Equal(StepStatus.Skipped, r.Steps[1].Status);
    }

    [Fact]
    public void ValidatorKeepsGameplayInputOffUnityActors()
    {
        ScenarioLoadResult load = ScenarioLoader.Parse(Scenario("""
        [ { "id": "viewer", "type": "UnityClient" }, { "id": "bot", "unity": { "exe": "x.exe" } },
          { "id": "both", "type": "UnityClient", "unity": { "exe": "x.exe", "attachPort": 1 } } ]
        """, """
        [ { "action": "fire", "actor": "viewer" },
          { "action": "captureScreenshot", "actor": "bot", "name": "a" },
          { "action": "captureScreenshot", "actor": "viewer", "name": "../evil" },
          { "action": "uiCommand", "actor": "viewer", "command": "jump" } ]
        """));
        Assert.Empty(load.Errors);
        var issues = ScenarioValidator.Validate(load.Scenario!, ActionRegistry.CreateDefault(), MarkerStore.Empty());
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("takes none (§87)"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("needs a UnityClient actor"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("'unity' is only for UnityClient"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("not both"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("PNG file name"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("'command' must be one of"));
        // A Unity connect gets the long default timeout.
        StepDefinition connect = ScenarioLoader.Parse(Scenario(Viewer, """[ { "action": "connect", "actor": "viewer" } ]""")).Scenario!.Steps[0];
        Assert.True(ActionRegistry.CreateDefault().TryGet("connect", out IScenarioActionHandler? h));
        Assert.Equal(ActorActions.UnityConnectTimeoutMs, h.Spec.TimeoutFor(connect));
    }

    [Fact]
    public void ScreenshotNamesDoNotCollide()
    {
        // Two non-ASCII aliases sanitize to the same text; the step number keeps them apart.
        Assert.Equal("003_--_hud", UnityActions.ShotFileName(2, "가나", "hud", Array.Empty<string>()));
        Assert.NotEqual(UnityActions.ShotFileName(2, "가나", "hud", Array.Empty<string>()), UnityActions.ShotFileName(5, "다라", "hud", Array.Empty<string>()));
        // A retried step's shot gets a counter.
        Assert.Equal("003_viewer_hud_2", UnityActions.ShotFileName(2, "viewer", "hud", new[] { "screenshots/003_viewer_hud.png" }));
        // Always within the player's 64-character rule, and the name survives a long alias.
        string longOne = UnityActions.ShotFileName(9, new string('a', 40), new string('n', 64), Array.Empty<string>());
        Assert.True(longOne.Length <= 64);
        Assert.StartsWith("010_", longOne);
        Assert.Matches("^[A-Za-z0-9_-]{1,64}$", UnityActions.ShotFileName(9, "viewer", "x", Array.Empty<string>()));

        // A 64-character name colliding three times: always <= 64, alias kept, suffixes intact and distinct.
        string name64 = new string('n', 64);
        var taken = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            string file = UnityActions.ShotFileName(9, "viewer", name64, taken);
            Assert.True(file.Length <= 64, file);
            Assert.Matches("^010_v[A-Za-z0-9-]*_n+(_[234])?$", file);
            if (i > 0) Assert.EndsWith("_" + (i + 1), file);
            Assert.DoesNotContain(taken, t => t == "screenshots/" + file + ".png");
            taken.Add("screenshots/" + file + ".png");
        }
    }

    [Fact]
    public async Task ManualPromptReadEndsOnCancel()
    {
        // A terminal read that never returns must not hold a stopped run.
        var blocked = new BlockingReader();
        var prompt = QaCli.ManualPrompt("ask", blocked, interactive: true, new StringWriter())!;
        using var cts = new CancellationTokenSource(200);
        var step = new StepDefinition { Id = "m", Action = "manualCheck" };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prompt(step, "check", cts.Token));
        blocked.Release();
    }

    private sealed class BlockingReader : TextReader
    {
        private readonly ManualResetEventSlim _gate = new(false);

        public void Release() => _gate.Set();

        public override string? ReadLine()
        {
            _gate.Wait(TimeSpan.FromSeconds(10));
            return null;
        }
    }

    [Fact]
    public void PlayerArgumentsCarryTheQaPortShotDirAndIdentity()
    {
        IReadOnlyList<string> args = ClientProcessManager.BuildArguments("127.0.0.1", 7000, "qa-viewer", 18000, "C:/r/screenshots", "C:/r/unity-viewer.log", 800, 450);
        string line = string.Join(' ', args);
        Assert.Contains("-host 127.0.0.1 -port 7000 -devId qa-viewer -autoConnect", line);
        Assert.Contains("-qaPort 18000 -qaShotDir C:/r/screenshots", line);
        Assert.Contains("-screen-fullscreen 0 -screen-width 800 -screen-height 450", line);
        Assert.DoesNotContain("-batchmode", line);
    }

    [Fact]
    public async Task ManualChecksPassFailOrSkip()
    {
        const string steps = """
        [ { "id": "ime", "action": "manualCheck", "description": "IME composition shows in the name field" },
          { "id": "after", "action": "wait", "milliseconds": 1 } ]
        """;
        // Interactive CLI: p + note.
        var prompt = QaCli.ManualPrompt("ask", new StringReader("x\np\nlooks right\n"), interactive: true, new StringWriter())!;
        (RunReport pass, _) = await Run(Scenario("[]", steps), null, prompt: prompt);
        Assert.Equal(RunStatus.Passed, pass.Status);
        Assert.Contains("\"result\":\"PASS\"", pass.ManualChecks[0].GetRawText().Replace(" ", ""));
        Assert.Contains("looks right", pass.ManualChecks[0].GetRawText());

        // Non-interactive: SKIPPED for that step only, the run goes on and passes.
        (RunReport skipped, _) = await Run(Scenario("[]", steps), null, prompt: QaCli.ManualPrompt("ask", TextReader.Null, interactive: false, new StringWriter()));
        Assert.Equal(RunStatus.Passed, skipped.Status);
        Assert.Equal(StepStatus.Skipped, skipped.Steps[0].Status);
        Assert.Equal(StepStatus.Passed, skipped.Steps[1].Status);
        Assert.Contains("SKIPPED", skipped.ManualChecks[0].GetRawText());
        string html = File.ReadAllText(Path.Combine(skipped.ReportDirectory!, "report.html"));
        Assert.Contains("IME composition", html);

        // --manual fail without a terminal.
        (RunReport failed, _) = await Run(Scenario("[]", steps), null, prompt: QaCli.ManualPrompt("fail", TextReader.Null, interactive: false, new StringWriter()));
        Assert.Equal(RunStatus.Failed, failed.Status);
        Assert.Equal("ime", failed.Failure!.StepId);
    }
}
