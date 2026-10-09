using System.Text.Json;
using ProjectH.QA.Faults;
using ProjectH.Shared.Protocol;

namespace ProjectH.QA.Tests.Faults;

// QA-3 actions, runner skip and cleanup against fakes: FakeQaServer, MockActor, a fake server process control and a
// fake docker CLI (no Docker, no server process).
public sealed class FaultActionTests
{
    private sealed class FakeServerControl : IServerControl
    {
        public bool Running { get; set; } = true;
        public List<string> Calls { get; } = new();
        public bool ExitFails { get; set; }

        // 기능: 서버 정지 호출을 기록하고 ExitFails 설정에 따라 정상 종료 또는 강제 종료 결과를 돌려준다.
        // 입력: token - 취소 토큰(쓰지 않음).
        // 출력: ExitFails면 10초 내 미종료·killed 정보(Running 유지), 아니면 exit 0으로 120 ms 만에 멈춘 정보.
        public Task<ServerExitInfo> StopAsync(CancellationToken token)
        {
            Calls.Add("stop");
            Running = ExitFails;
            return Task.FromResult(ExitFails
                ? new ServerExitInfo(false, 10_000, null, true, "no exit within 10 s, killed")
                : new ServerExitInfo(true, 120, 0, false, "stop requested"));
        }

        // 기능: 강제 종료 호출을 기록하고 서버를 멈춘 것으로 표시한다.
        // 입력: token - 취소 토큰(쓰지 않음).
        // 출력: killed로 표시된 종료 정보. Running이 false가 된다.
        public Task<ServerExitInfo> KillAsync(CancellationToken token)
        {
            Calls.Add("kill");
            Running = false;
            return Task.FromResult(new ServerExitInfo(true, 15, -1, true, "killed"));
        }

        // 기능: 시작 호출을 기록하고 서버를 실행 중으로 표시한다.
        // 입력: token - 취소 토큰(쓰지 않음).
        // 출력: 게임 포트 40001, QA 포트 40002의 고정 시작 정보. Running이 true가 된다.
        public Task<ServerStartInfo> StartAsync(CancellationToken token)
        {
            Calls.Add("start");
            Running = true;
            return Task.FromResult(new ServerStartInfo(40001, 40002, 1234, 800));
        }
    }

    // `docker` answers: the container exists and is running/healthy unless told otherwise.
    private sealed class FakeDocker
    {
        public bool Available { get; set; } = true;
        public bool Running { get; set; } = true;
        public List<string> Calls { get; } = new();

        // 기능: 호출을 기록하고 Available·Running 상태대로 답하는 가짜 docker CLI를 꽂은 DockerDbController를 만든다.
        // 입력: name - 컨테이너 이름.
        // 출력: stop/start가 Running을 바꾸고 inspect가 그 상태를 보고하는 DockerDbController.
        public DockerDbController Create(string name) => new(name, (args, timeout, ct) =>
        {
            Calls.Add(string.Join(' ', args));
            if (!Available) return Task.FromResult(new DockerCommandResult(-1, "", "no docker", false, true));
            string format = args.Contains("--format") ? args[args.ToList().IndexOf("--format") + 1] : "";
            string value = args[0] switch
            {
                "stop" => SetRunning(false),
                "start" => SetRunning(true),
                _ when format.EndsWith("{{.State.Running}}") => Running ? "true" : "false",
                _ when format.EndsWith(DockerDbController.HealthFormat) => Running ? "healthy" : "exited",
                _ => "/" + name,
            };
            string stdout = args[0] == "inspect" ? $"/{name}|{value}\n" : value;
            return Task.FromResult(new DockerCommandResult(0, stdout, "", false, false));
        });

        // 기능: 가짜 컨테이너의 실행 상태를 바꾼다.
        // 입력: running - 새 실행 상태.
        // 출력: docker stop/start가 출력하는 컨테이너 이름 "projecth-mysql".
        private string SetRunning(bool running)
        {
            Running = running;
            return "projecth-mysql";
        }
    }

    private readonly FakeQaServer _server = new();
    private readonly FakeServerControl _control = new();
    private readonly FakeDocker _docker = new();
    private readonly Dictionary<string, MockActor> _actors = new();

    // 기능: 단계·Actor·추가 필드를 끼운 시나리오 JSON을 파싱해 오류가 없는지 확인한다.
    // 입력: steps - steps 배열 JSON, actors - actors 배열 JSON(기본은 proxy를 켠 playerA와 playerB), extra - 루트에 끼울 추가 필드 JSON.
    // 출력: 파싱된 ScenarioDefinition. 파싱 오류가 있으면 Assert 실패.
    private static ScenarioDefinition Load(string steps, string actors = """[ { "id": "playerA", "network": { "proxy": true } }, { "id": "playerB" } ]""",
        string extra = "")
    {
        ScenarioLoadResult load = ScenarioLoader.Parse($$"""{ "schemaVersion": 1, "name": "faults", "seed": 77, {{extra}} "actors": {{actors}}, "steps": {{steps}} }""");
        Assert.Empty(load.Errors);
        return load.Scenario!;
    }

    // 기능: 시나리오를 검증한 뒤 가짜 서버·Actor·docker·서버 제어를 꽂은 RunContext에서 단계를 끝까지 실행한다.
    // 입력: scenario - 실행할 시나리오, withServerControl - true면 가짜 서버 프로세스 제어를 붙인다(attach 모드 흉내는 false).
    // 출력: 단계 결과가 채워진 RunReport와 사용한 RunContext. 정리(cleanup)는 호출자 몫이다.
    // Runs the steps on a RunContext like the orchestrator builds, with the fakes plugged in. Cleanup is the caller's.
    private async Task<(RunReport Report, RunContext Run)> RunStepsAsync(ScenarioDefinition scenario, bool withServerControl = true)
    {
        var registry = ActionRegistry.CreateDefault();
        IReadOnlyList<ValidationIssue> issues = ScenarioValidator.Validate(scenario, registry, MarkerStore.Parse("""{ "markers": [] }"""));
        Assert.DoesNotContain(issues, i => i.IsError);
        var manager = new ActorManager(scenario.Seed ?? 1, _ => { }, (alias, _) => _actors[alias] = new MockActor(alias));
        var run = new RunContext("run-1", scenario.Seed ?? 1, _server, manager, MarkerStore.Parse("""{ "markers": [] }"""),
            scenario.Variables, _ => { })
        {
            GamePort = 7777,
            PollIntervalMs = 10,
            Db = new DbFaultHub(_docker.Create),
        };
        if (withServerControl) run.ServerControl = _control;
        foreach (ActorSpec a in scenario.Actors)
        {
            await manager.CreateAsync(a.Id, a.Type, default);
            if (a.Proxy) run.Network.EnableProxy(a.Id);
        }
        var report = new RunReport();
        var runner = new ScenarioRunner(registry, new RunGate(honorBreakpoints: false), _ => { });
        report.Status = await runner.RunAsync(scenario, run, report, default, default);
        return (report, run);
    }

    // ---- loading and validation ----

    [Fact]
    public void ActorNetworkProxyIsParsedAndCheckedStrictly()
    {
        ScenarioDefinition s = Load("""[ { "action": "wait", "milliseconds": 1 } ]""");
        Assert.True(s.Actors[0].Proxy);
        Assert.False(s.Actors[1].Proxy);

        ScenarioLoadResult bad = ScenarioLoader.Parse("""{ "schemaVersion": 1, "name": "x", "actors": [ { "id": "a", "network": { "proxi": true } } ], "steps": [ { "action": "wait", "milliseconds": 1 } ] }""");
        Assert.Contains(bad.Errors, e => e.Contains("Unknown 'network' field 'proxi'"));
        ScenarioLoadResult notBool = ScenarioLoader.Parse("""{ "schemaVersion": 1, "name": "x", "actors": [ { "id": "a", "network": { "proxy": "yes" } } ], "steps": [ { "action": "wait", "milliseconds": 1 } ] }""");
        Assert.Contains(notBool.Errors, e => e.Contains("'network.proxy' must be true or false"));
    }

    [Fact]
    public void NetworkStepsNeedAProxiedActorAndServerStepsALaunchedServer()
    {
        var registry = ActionRegistry.CreateDefault();
        var markers = MarkerStore.Parse("""{ "markers": [] }""");
        ScenarioDefinition s = Load("""
            [ { "action": "networkFault", "actor": "playerB", "latencyMs": 100 },
              { "action": "blockNetwork", "actor": "playerA", "direction": "sideways" },
              { "action": "networkFault", "actor": "playerA", "lossPercent": 140, "latencyMs": 20000 },
              { "action": "sendInvalidPackets", "actor": "playerA", "kinds": ["unknownId", "bogus"], "count": 900 } ]
            """);
        var errors = ScenarioValidator.Validate(s, registry, markers).Where(i => i.IsError).Select(i => i.ToString()).ToList();
        Assert.Contains(errors, e => e.Contains("needs actor 'playerB' to have"));
        Assert.Contains(errors, e => e.Contains("'direction' must be one of"));
        Assert.Contains(errors, e => e.Contains("'lossPercent' must be 0-100"));
        Assert.Contains(errors, e => e.Contains("'latencyMs' must be an integer"));
        Assert.Contains(errors, e => e.Contains("Unknown packet kind \"bogus\""));
        Assert.Contains(errors, e => e.Contains("'count' must be an integer 1-500"));

        ScenarioDefinition attach = Load("""[ { "action": "stopServer" } ]""", extra: """ "server": { "mode": "attach", "qaUrl": "http://127.0.0.1:1/" }, """);
        Assert.Contains(ScenarioValidator.Validate(attach, registry, markers), i => i.IsError && i.ToString().Contains("not in attach mode"));
    }

    // ---- network ----

    [Fact]
    public async Task ConnectGoesThroughAFreshProxyAndFaultsFollowIt()
    {
        (RunReport r, RunContext run) = await RunStepsAsync(Load("""
            [ { "action": "connect", "actor": "playerA" },
              { "action": "connect", "actor": "playerB" },
              { "action": "networkFault", "actor": "playerA", "latencyMs": 150, "jitterMs": 10, "lossPercent": 5, "direction": "toServer" },
              { "action": "blockNetwork", "actor": "playerA", "direction": "toClient" },
              { "assert": "network.proxy.toServer.latencyMs", "actor": "playerA", "equals": 150 },
              { "assert": "network.proxy.toClient.blocked", "actor": "playerA", "equals": true },
              { "assert": "network.proxy.toClient.latencyMs", "actor": "playerA", "equals": 0 },
              { "action": "reconnect", "actor": "playerA" },
              { "assert": "network.proxy.attempts", "actor": "playerA", "equals": 2 },
              { "assert": "network.proxy.toServer.packetLossPercent", "actor": "playerA", "equals": 5 },
              { "action": "unblockNetwork", "actor": "playerA" },
              { "action": "clearNetworkFault", "actor": "playerA" },
              { "assert": "network.proxy.toServer.latencyMs", "actor": "playerA", "equals": 0 },
              { "assert": "network.proxy.enabled", "actor": "playerB", "equals": false } ]
            """));
        try
        {
            Assert.True(r.Status == RunStatus.Passed, string.Join("\n", r.Steps.Select(s => s.Line() + " " + s.Message)));
            // A's connect went to its proxy on loopback, not to the game port; B's straight to the game port.
            var aConnects = _actors["playerA"].Commands.OfType<ConnectCommand>().ToList();
            Assert.Equal(2, aConnects.Count);
            Assert.All(aConnects, c => Assert.NotEqual(7777, c.Port));
            Assert.NotEqual(aConnects[0].Port, aConnects[1].Port);
            Assert.Equal(7777, _actors["playerB"].Commands.OfType<ConnectCommand>().Single().Port);
        }
        finally
        {
            Assert.Empty(await run.Network.CloseAllAsync());
        }
    }

    [Fact]
    public async Task DropConnectionAbortsWithoutNotice()
    {
        (RunReport r, RunContext run) = await RunStepsAsync(Load("""
            [ { "action": "connect", "actor": "playerB" },
              { "action": "dropConnection", "actor": "playerB" },
              { "assert": "network.disconnected", "actor": "playerB", "equals": true } ]
            """));
        Assert.Equal(RunStatus.Passed, r.Status);
        Assert.False(_actors["playerB"].Commands.OfType<DisconnectCommand>().Single().Graceful);
    }

    // ---- invalid packets ----

    [Fact]
    public async Task InvalidPacketsAreSentThroughTheActor()
    {
        (RunReport r, RunContext run) = await RunStepsAsync(Load("""
            [ { "action": "connect", "actor": "playerB" },
              { "action": "sendInvalidPackets", "actor": "playerB", "kinds": ["unknownId", "truncated", "oversized", "garbage", "inputFlood"], "count": 4, "saveAs": "sent" },
              { "assert": "var.sent.total", "equals": 20 },
              { "assert": "var.sent.sent.garbage", "equals": 4 } ]
            """));
        Assert.Equal(RunStatus.Passed, r.Status);
        SendRawCommand raw = _actors["playerB"].Commands.OfType<SendRawCommand>().Single();
        Assert.Equal(20, raw.Packets.Count);
        Assert.Equal(20, _actors["playerB"].State.RawPacketsSent);
    }

    [Fact]
    public async Task InvalidPacketsNeedAJoinedActor()
    {
        (RunReport r, _) = await RunStepsAsync(Load("""[ { "action": "sendInvalidPackets", "actor": "playerB", "kinds": ["garbage"] } ]"""));
        Assert.Equal(RunStatus.Failed, r.Status);
        Assert.Contains("not in the game", r.Steps[0].Message);
    }

    [Fact]
    public async Task InvalidPacketTotalIsBoundedBeforeSending()
    {
        // Literal kinds x count: a validation error.
        ScenarioDefinition literal = Load("""[ { "action": "sendInvalidPackets", "actor": "playerB", "kinds": ["unknownId", "truncated", "oversized", "garbage", "inputFlood", "garbage"], "count": 500 } ]""");
        Assert.Contains(ScenarioValidator.Validate(literal, ActionRegistry.CreateDefault(), MarkerStore.Parse("""{ "markers": [] }""")),
            i => i.IsError && i.ToString().Contains("more than 2500"));

        // From a variable: refused at run time, before anything is built or sent.
        (RunReport r, _) = await RunStepsAsync(Load("""
            [ { "action": "connect", "actor": "playerB" },
              { "action": "sendInvalidPackets", "actor": "playerB", "kinds": ["unknownId", "truncated", "oversized", "garbage", "inputFlood", "garbage"], "count": "${n}" } ]
            """, extra: """ "variables": { "n": 500 }, """));
        Assert.Equal(RunStatus.Failed, r.Status);
        Assert.Contains("more than 2500", r.Steps[1].Message);
        Assert.Empty(_actors["playerB"].Commands.OfType<SendRawCommand>());
    }

    [Fact]
    public void InvalidPacketKindsHaveTheShapeTheServerRejects()
    {
        var rng = new Random(5);
        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(0xFF, FaultActions.InvalidPacket("unknownId", rng)[0]);
            byte first = FaultActions.InvalidPacket("garbage", rng)[0];
            Assert.DoesNotContain(first, new[] { (byte)PacketId.JoinMatchRequest, (byte)PacketId.PlayerInput, (byte)PacketId.StatsRequest, (byte)PacketId.BuildRequest });
        }
        Assert.Equal(new[] { (byte)PacketId.PlayerInput, (byte)3 }, FaultActions.InvalidPacket("truncated", rng));
        byte[] oversized = FaultActions.InvalidPacket("oversized", rng);
        Assert.True(oversized.Length > ProtocolConstants.MaxPacketSize);
        Assert.Equal((byte)PacketId.PlayerInput, oversized[0]);
        Assert.Equal(0xFF, oversized[1]);
        // A flood input is well formed (so only the rate rejects it) and carries Seq 0 (never applied).
        byte[] flood = FaultActions.InvalidPacket("inputFlood", rng);
        var reader = new PacketReader(flood);
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(PacketId.PlayerInput, id);
        Assert.True(PlayerInputPacket.TryRead(ref reader, out PlayerInputPacket packet));
        Assert.Equal(1, packet.Count);
        Assert.Equal(0u, packet.Get(0).Seq);
    }

    // ---- server process ----

    [Fact]
    public async Task ServerStopKillStartAndRestart()
    {
        (RunReport r, _) = await RunStepsAsync(Load("""
            [ { "action": "stopServer", "saveAs": "stop" },
              { "assert": "var.stop.exitMs", "equals": 120 },
              { "assert": "var.stop.exitCode", "equals": 0 },
              { "action": "startServer", "saveAs": "start" },
              { "assert": "var.start.gamePort", "equals": 40001 },
              { "action": "killServer" },
              { "action": "restartServer", "saveAs": "restart" },
              { "assert": "var.restart.start.qaPort", "equals": 40002 } ]
            """));
        Assert.True(r.Status == RunStatus.Passed, string.Join("\n", r.Steps.Select(s => s.Line() + " " + s.Message)));
        // restartServer on a stopped (killed) server only starts it.
        Assert.Equal(new[] { "stop", "start", "kill", "start" }, _control.Calls);
    }

    [Fact]
    public async Task ServerStepsFailClearly()
    {
        _control.ExitFails = true;
        (RunReport r, _) = await RunStepsAsync(Load("""[ { "action": "stopServer", "continueOnFailure": true }, { "action": "startServer" } ]"""));
        Assert.Equal(RunStatus.Failed, r.Status);
        Assert.Contains("did not exit", r.Steps[0].Message);
        Assert.Contains("already running", r.Steps[1].Message);

        (RunReport attach, _) = await RunStepsAsync(Load("""[ { "action": "killServer" } ]"""), withServerControl: false);
        Assert.Equal(RunStatus.Failed, attach.Status);
        Assert.Contains("needs a server the tool launched", attach.Steps[0].Message);
    }

    // ---- database ----

    [Fact]
    public async Task DbStopAndStartAreRecordedForCleanup()
    {
        (RunReport r, RunContext run) = await RunStepsAsync(Load("""
            [ { "action": "waitDbHealthy" },
              { "action": "stopDb", "saveAs": "stop" },
              { "assert": "var.stop.stoppedByRun", "equals": true } ]
            """));
        Assert.True(r.Status == RunStatus.Passed, string.Join("\n", r.Steps.Select(s => s.Line() + " " + s.Message)));
        Assert.False(_docker.Running);
        Assert.Contains("stop --time 10 projecth-mysql", _docker.Calls);

        List<CleanupResult> restored = await run.Db.RestoreAllAsync();
        CleanupResult line = Assert.Single(restored);
        Assert.True(line.Ok, line.Message);
        Assert.True(_docker.Running);
        Assert.DoesNotContain(_docker.Calls, c => c.Contains("down") || c.Contains(" rm") || c.Contains("-v"));
        // Nothing left to restore a second time.
        Assert.Empty(await run.Db.RestoreAllAsync());
    }

    [Fact]
    public async Task StartDbWaitsUntilHealthy()
    {
        _docker.Running = false;
        (RunReport r, RunContext run) = await RunStepsAsync(Load("""[ { "action": "startDb", "saveAs": "db" }, { "assert": "var.db.status", "equals": "healthy" } ]"""));
        Assert.Equal(RunStatus.Passed, r.Status);
        Assert.True(_docker.Running);
        // This run did not stop it, so cleanup has nothing to do.
        Assert.Empty(await run.Db.RestoreAllAsync());
    }

    [Fact]
    public async Task NoDockerSkipsTheRestOfTheScenario()
    {
        _docker.Available = false;
        (RunReport r, _) = await RunStepsAsync(Load("""
            [ { "action": "connect", "actor": "playerB" },
              { "action": "stopDb" },
              { "assert": "server.metrics.db.failed", "equals": 1 },
              { "action": "startDb" } ]
            """));
        Assert.Equal(RunStatus.Skipped, r.Status);
        Assert.Equal(0, RunReport.ExitCodeFor(r.Status));
        Assert.Equal(1, RunReport.ExitCodeFor(r.Status, failOnSkip: true));
        Assert.Equal(StepStatus.Passed, r.Steps[0].Status);
        Assert.All(r.Steps.Skip(1), s => Assert.Equal(StepStatus.Skipped, s.Status));
        Assert.Contains("not available", r.Steps[1].Message);
        Assert.StartsWith("Skipped:", r.Steps[2].Message);
        Assert.Contains("from step 02", r.SkipReason);
        Assert.Contains(r.Warnings, w => w.Contains("skipped the rest of the scenario"));
    }

    [Fact]
    public async Task AStoppedContainerIsSkippedNotStarted()
    {
        _docker.Running = false;
        (RunReport r, RunContext run) = await RunStepsAsync(Load("""
            [ { "action": "waitDbHealthy" },
              { "action": "stopDb" } ]
            """));
        Assert.Equal(RunStatus.Skipped, r.Status);
        Assert.All(r.Steps, s => Assert.Equal(StepStatus.Skipped, s.Status));
        Assert.Contains("exists but is not running", r.Steps[0].Message);
        Assert.False(_docker.Running);
        Assert.DoesNotContain(_docker.Calls, c => c.StartsWith("start") || c.StartsWith("stop"));
        Assert.Empty(await run.Db.RestoreAllAsync());
    }

    // ---- orchestrator cleanup ----

    [Fact]
    public async Task CleanupClosesProxiesAndRestartsTheStoppedDb()
    {
        string root = Path.Combine(Path.GetTempPath(), "qa-fault-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ScenarioDefinition scenario = Load("""
                [ { "action": "connect", "actor": "playerA" },
                  { "action": "networkFault", "actor": "playerA", "latencyMs": 50 },
                  { "action": "stopDb" },
                  { "assert": "player.health", "actor": "playerA", "equals": 1 } ]
                """);
            var registry = ActionRegistry.CreateDefault();
            var markers = MarkerStore.Parse("""{ "markers": [] }""");
            var options = new QaRunOptions
            {
                RepoRoot = root,
                AttachUrl = "http://127.0.0.1:1/",
                PollMs = 20,
                WriteReport = false,
                ServerClientFactory = _ => _server,
                ActorFactory = (alias, _) => _actors[alias] = new MockActor(alias),
                DockerFactory = _docker.Create,
            };
            _server.AddPlayer("qa-playerA", health: 100);
            RunReport r = await new QaOrchestrator(options, registry, markers, new StringWriter()).RunAsync(scenario, Array.Empty<ValidationIssue>(), default);

            Assert.Equal(RunStatus.Failed, r.Status);   // the last assertion fails on purpose: cleanup must still run
            CleanupResult network = Assert.Single(r.Cleanup, c => c.Name == "network");
            Assert.True(network.Ok, network.Message);
            CleanupResult db = Assert.Single(r.Cleanup, c => c.Name == "db projecth-mysql");
            Assert.True(db.Ok, db.Message);
            Assert.True(_docker.Running);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}

public sealed class QaServerClientDisposedTests
{
    // A restart replaces the run's client; a request on a closed client is a clear QA API error (step failure),
    // not an ObjectDisposedException (tool error).
    [Fact]
    public async Task ARequestOnAClosedClientIsAClearApiError()
    {
        var client = new QaServerClient(new Uri("http://127.0.0.1:1/"));
        client.Dispose();
        QaApiException e = await Assert.ThrowsAsync<QaApiException>(() => client.GetHealthAsync(default));
        Assert.Contains("was closed", e.Message);
        await Assert.ThrowsAsync<QaApiException>(() => client.StopServerAsync(default));
    }
}
