using System.Numerics;
using System.Text.Json;
using ProjectH.QA;
using ProjectH.Shared.Simulation;
using Xunit.Abstractions;

namespace ProjectH.QA.Tests;

// Stress (request §117): actor group distribution, seed determinism of the behaviours and places, player-count
// parameters, measure windows and sampling bounds, stall records, group workload cancellation and cleanup, the batch
// comparison, suite entries with options, stress mode validation and the baseline's stress rows.
public class StressTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stress-" + Guid.NewGuid().ToString("N"));
    private readonly FakeQaServer _fake = new();
    private readonly List<MockActor> _actors = new();

    // 기능: 테스트 출력을 보관하고 임시 저장소 루트 아래에 QA/Scenarios/T와 QA/Suites 폴더를 만든다.
    // 입력: output - xUnit 테스트 출력.
    // 출력: 임시 저장소 루트와 가짜 QA 서버가 준비된 StressTests 객체.
    public StressTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(Path.Combine(_root, "QA", "Scenarios", "T"));
        Directory.CreateDirectory(Path.Combine(_root, "QA", "Suites"));
    }

    // 기능: 테스트가 만든 임시 저장소 루트를 통째로 지운다.
    // 입력: 없음.
    // 출력: 반환값 없음. 임시 폴더가 삭제되며 IO 오류는 무시한다.
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Reports => Path.Combine(_root, "out");

    // 기능: 임시 저장소 루트 아래 상대 경로에 폴더를 만들고 텍스트 파일을 쓴다.
    // 입력: relative - 루트 기준 상대 경로, text - 파일 내용.
    // 출력: 기록한 파일의 절대 경로.
    private string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    // 기능: 주어진 QA 서버 Client와 MockActor를 꽂은 QaCli를 임시 저장소 루트(--repo)로 실행하고 출력을 테스트 로그에도 쓴다.
    // 입력: server - CLI가 쓸 QA 서버 Client, token - 취소 토큰, args - CLI 인자(--repo는 자동으로 덧붙인다).
    // 출력: CLI 종료 코드와 표준 출력 문자열. 만들어진 Actor는 _actors에 쌓인다.
    private async Task<(int Exit, string Output)> Cli(IQaServerClient server, CancellationToken token, params string[] args)
    {
        var output = new StringWriter();
        int exit = await QaCli.RunAsync(args.Concat(new[] { "--repo", _root }).ToArray(), output, token, _ => server, (alias, _) =>
        {
            var a = new MockActor(alias);
            lock (_actors) _actors.Add(a);
            return a;
        });
        _out.WriteLine(output.ToString());
        return (exit, output.ToString());
    }

    // 기능: 기본 가짜 서버로, 취소 없이 CLI를 실행한다.
    // 입력: args - CLI 인자.
    // 출력: CLI 종료 코드와 표준 출력 문자열.
    private Task<(int Exit, string Output)> Cli(params string[] args) => Cli(_fake, default, args);

    // 기능: 시나리오 JSON을 파싱하고 파싱 오류와 검증 결과를 하나의 목록으로 합친다.
    // 입력: json - 시나리오 JSON 문자열.
    // 출력: 파싱 오류(IsError)와 검증 지적을 합친 목록.
    private static IReadOnlyList<ValidationIssue> Validate(string json)
    {
        ScenarioLoadResult load = ScenarioLoader.Parse(json, "x.json");
        var issues = load.Errors.Select(e => new ValidationIssue(true, "file", e)).ToList();
        if (load.Scenario != null) issues.AddRange(ScenarioValidator.Validate(load.Scenario, ActionRegistry.CreateDefault(), MarkerStore.Empty()));
        return issues;
    }

    // 기능: 보고서 폴더의 실행 ID 아래 report.json을 읽어 루트 요소의 복사본을 돌려준다.
    // 입력: runId - 실행 ID.
    // 출력: 문서와 분리된 report.json 루트 JsonElement.
    private JsonElement Report(string runId)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Reports, runId, "report.json")));
        return doc.RootElement.Clone();
    }

    // 기능: 보고서 폴더에서 qa-로 시작하는 실행 ID 폴더 이름을 모은다.
    // 입력: 없음.
    // 출력: 실행 ID를 이름순으로 정렬한 배열.
    private string[] RunIds() => Directory.GetDirectories(Reports).Select(Path.GetFileName).Where(n => n!.StartsWith("qa-", StringComparison.Ordinal)).OrderBy(n => n).ToArray()!;

    // ---- group distribution (D38) ----

    [Fact]
    public void DistributionIsDeterministicFromSeedAndIndex()
    {
        var shares = new[] { new GroupShare("combat", 30, null), new GroupShare("build", 20, null), new GroupShare("rest", null, null, Rest: true) };
        int[] a = GroupRegistry.Distribute(50, shares, 7);
        int[] b = GroupRegistry.Distribute(50, shares, 7);
        int[] c = GroupRegistry.Distribute(50, shares, 8);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(15, a.Count(x => x == 0));
        Assert.Equal(10, a.Count(x => x == 1));
        Assert.Equal(25, a.Count(x => x == 2));
        Assert.DoesNotContain(-1, a);
    }

    [Fact]
    public void DistributionRoundsByLargestRemainderAndCounts()
    {
        // 33/33/34 % of 10: 3/3/4, never 11 actors.
        int[] split = GroupRegistry.Distribute(10, new[] { new GroupShare("a", 33, null), new GroupShare("b", 33, null), new GroupShare("c", 34, null) }, 1);
        Assert.Equal(new[] { 3, 3, 4 }, new[] { split.Count(x => x == 0), split.Count(x => x == 1), split.Count(x => x == 2) });
        // Exact counts, and actors left without a group when nothing takes the rest.
        int[] counted = GroupRegistry.Distribute(10, new[] { new GroupShare("a", null, 2), new GroupShare("b", 10, null) }, 1);
        Assert.Equal(2, counted.Count(x => x == 0));
        Assert.Equal(1, counted.Count(x => x == 1));
        Assert.Equal(7, counted.Count(x => x == -1));
        // A percentage group of a small count may be empty (the group actions accept that).
        int[] tiny = GroupRegistry.Distribute(10, new[] { new GroupShare("churn", 5, null), new GroupShare("rest", null, null, Rest: true) }, 3);
        Assert.Equal(10, tiny.Count(x => x >= 0));
    }

    [Fact]
    public void MoveWaypointsAreDeterministicInsideTheMapAndSpread()
    {
        foreach (MovePattern pattern in Enum.GetValues<MovePattern>())
        {
            var plan = new MovePlan(pattern, Vector2.Zero, 60, false, 0, 3, 10, 42);
            Vector3[] first = Enumerable.Range(0, 12).Select(k => MoveBrain.Waypoint(plan, k, new Random(9))).ToArray();
            Vector3[] again = Enumerable.Range(0, 12).Select(k => MoveBrain.Waypoint(plan, k, new Random(9))).ToArray();
            Assert.Equal(first, again);
            Assert.All(first, p => Assert.True(StressMap.InsideMap(p.X, p.Z), $"{pattern} {p}"));
        }
        // Different actors start at different angles: the first clockwise waypoints of 8 actors are 8 different places.
        Vector3[] starts = Enumerable.Range(0, 8).Select(i => MoveBrain.Waypoint(new MovePlan(MovePattern.Clockwise, Vector2.Zero, 60, false, 0, i, 8, 42), 0, new Random(1))).ToArray();
        Assert.Equal(8, starts.Distinct().Count());
    }

    [Fact]
    public void RolesAreDeterministicPerSeedIndexAndPeriod()
    {
        var roles = new[] { new RoleShare("move", 50), new RoleShare("loot", 30), new RoleShare("idle", 20) };
        Assert.Equal(RoleBrain.RoleFor(roles, 5, 3, 7), RoleBrain.RoleFor(roles, 5, 3, 7));
        var counts = new Dictionary<string, int>();
        for (int i = 0; i < 2000; i++)
        {
            string r = RoleBrain.RoleFor(roles, 5, i % 50, i / 50);
            counts[r] = counts.GetValueOrDefault(r) + 1;
        }
        Assert.InRange(counts["move"], 850, 1150);
        Assert.InRange(counts["loot"], 450, 750);
        Assert.InRange(counts["idle"], 250, 550);
    }

    // ---- places on the map ----

    [Fact]
    public void FightSpotsAreDeterministicClearAndInSight()
    {
        List<Vector3[]> pairs = StressMap.FightSpots(Vector2.Zero, 70, 12, 10, 2, 50);
        List<Vector3[]> again = StressMap.FightSpots(Vector2.Zero, 70, 12, 10, 2, 50);
        Assert.Equal(50, pairs.Count);
        Assert.Equal(pairs.Select(p => p[0]), again.Select(p => p[0]));
        foreach (Vector3[] p in pairs)
        {
            Assert.Equal(10f, Vector2.Distance(new Vector2(p[0].X, p[0].Z), new Vector2(p[1].X, p[1].Z)), 2);
            Assert.True(StressMap.CanStand(p[0]) && StressMap.CanStand(p[1]));
            Assert.True(StressMap.InSight(p[0], p[1]) && StressMap.InSight(p[1], p[0]));
        }
        // Groups of 3 and the dense final zone area have room too.
        Assert.Equal(10, StressMap.FightSpots(Vector2.Zero, 60, 12, 10, 3, 10).Count);
        Assert.True(StressMap.FightSpots(Vector2.Zero, 25, 7, 6, 2, 100).Count >= 25);
        _out.WriteLine($"pairs in 70 m: {StressMap.FightSpots(Vector2.Zero, 70, 12, 10, 2, 200).Count}");
    }

    [Fact]
    public void BuildSitesNeverShareACellAndThePoolHandsEachOutOnce()
    {
        List<StressMap.BuildSite> sites = StressMap.BuildSites(Vector2.Zero, 1000, StressMap.AllPieces, BuildMaterialType.Wood);
        _out.WriteLine($"build sites: {sites.Count}");
        Assert.True(sites.Count >= 30, $"{sites.Count} sites");
        var cells = new HashSet<(int, int)>();
        foreach (StressMap.BuildSite s in sites)
        {
            for (int x = s.CellX - 1; x <= s.CellX + 1; x++)
                for (int z = s.CellZ - 1; z <= s.CellZ; z++) Assert.True(cells.Add((x, z)), $"cell ({x},{z}) in two sites");
            Assert.Equal(13, s.Pieces.Count);
            Assert.Contains(s.Pieces, p => p.Piece == BuildPieceType.Roof);
        }
        Assert.Equal(sites.Select(s => (s.CellX, s.CellZ)), StressMap.BuildSites(Vector2.Zero, 1000, StressMap.AllPieces, BuildMaterialType.Wood).Select(s => (s.CellX, s.CellZ)));

        // Claims: every site once, nearest first, then shared round the list.
        var pool = new BuildSitePool(sites);
        var order = Enumerable.Range(0, sites.Count).Select(_ => pool.Claim(Vector2.Zero)!).ToList();
        Assert.Equal(sites.Count, order.Select(s => (s.CellX, s.CellZ)).Distinct().Count());
        Assert.Equal(0, pool.Free);
        Assert.Equal(0, pool.Shared);
        Assert.NotNull(pool.Claim(Vector2.Zero));
        Assert.Equal(1, pool.Shared);
        // Only the asked-for piece types, in the asked-for material.
        BuildPlan[] walls = BuildSitePool.PiecesOf(sites[0], new[] { BuildPieceType.Wall }, BuildMaterialType.Stone);
        Assert.Equal(8, walls.Length);
        Assert.All(walls, w => Assert.Equal(BuildMaterialType.Stone, w.Material));
    }

    // ---- measure arithmetic and bounds (D37) ----

    [Fact]
    public void SampleIntervalIsBounded()
    {
        Assert.Equal(5, MeasureMath.SampleInterval(60, null));
        Assert.Equal(1, MeasureMath.SampleInterval(60, 0.2));
        Assert.Equal(120, MeasureMath.SampleInterval(60, 500));
        // 4 h at 5 s would be 2880 samples: the interval grows so at most 720 are taken.
        int interval = MeasureMath.SampleInterval(14_400, 5);
        Assert.Equal(20, interval);
        Assert.True(14_400 / interval <= MeasureMath.MaxSamples);
    }

    // 기능: p95를 기준으로 Tick 백분위(p50=절반, p99=1.5배, max=3배)와 자원 수치를 채운 측정 샘플을 만든다.
    // 입력: p95 - Tick p95 ms, cpu - CPU %, managed - 관리 힙 MB(WorkingSet은 4배, 누적 할당은 10배), gen0 - Gen0 GC 횟수, sendBytes - 초당 송신 바이트(수신은 절반), players - 플레이어 수(세션·생존도 같음), stalls - Stall 수.
    // 출력: 채워진 MeasureSample.
    private static MeasureSample Sample(double p95, double cpu = 1, double managed = 10, long gen0 = 0, double sendBytes = 1024, int players = 5, long stalls = 0) =>
        new() { TickP50Ms = p95 / 2, TickP95Ms = p95, TickP99Ms = p95 * 1.5, TickMaxMs = p95 * 3, CpuPercent = cpu, ManagedMB = managed, WorkingSetMB = managed * 4,
            Gen0 = gen0, BytesOutPerSec = sendBytes, BytesInPerSec = sendBytes / 2, Players = players, ActiveSessions = players, Alive = players, Stalls = stalls,
            AllocatedMBTotal = managed * 10 };

    [Fact]
    public void PhaseSummaryUsesTheWholeWindowWhenThereIsOne()
    {
        MeasureSample start = Sample(1, managed: 10, gen0: 2);
        var samples = new List<MeasureSample> { Sample(1, cpu: 2, managed: 12), Sample(4, cpu: 4, managed: 20, gen0: 3), Sample(2, cpu: 3, managed: 15, gen0: 5) };
        var exact = new MeasureResult();
        MeasureMath.Summarize(exact, start, samples, Sample(2.5, cpu: 3.2, managed: 16, gen0: 5, sendBytes: 2048), 30);
        Assert.True(exact.TicksExact);
        Assert.Equal(2.5, exact.TickP95Ms);
        Assert.Equal(3.2, exact.CpuAvgPercent);
        Assert.Equal(4, exact.CpuMaxPercent);
        Assert.Equal(2, exact.SendKBps);
        Assert.Equal(3, exact.Gen0);
        Assert.Equal(20, exact.ManagedMaxMB);
        Assert.Equal(16, exact.ManagedEndMB);

        var approx = new MeasureResult();
        MeasureMath.Summarize(approx, start, samples, null, 300);
        Assert.False(approx.TicksExact);
        Assert.Contains("approximate", approx.TicksNote);
        Assert.Equal(4, approx.TickP95Ms);           // the worst sample window
        Assert.Equal(12, approx.TickMaxMs);
        Assert.Equal((0.5 + 2 + 1) / 3, approx.TickP50Ms, 6);
        Assert.Equal(3, approx.CpuAvgPercent, 6);
        Assert.Equal(15, approx.ManagedEndMB);        // the last sample is the end

        // "steady" is the judged phase; without one, the longest (the last of equal ones).
        var phases = new List<MeasureResult> { new() { Name = "warmup", PlannedSeconds = 10 }, new() { Name = "match_01", PlannedSeconds = 30 }, new() { Name = "match_02", PlannedSeconds = 30 }, new() { Name = "cooldown", PlannedSeconds = 10 } };
        Assert.Equal("match_02", StressSummary.Pick(phases)!.Name);
        phases.Insert(1, new MeasureResult { Name = "steady", PlannedSeconds = 5 });
        Assert.Equal("steady", StressSummary.Pick(phases)!.Name);
    }

    [Fact]
    public void LatencyPercentilesFromBuckets()
    {
        var h = new InputLatencyHistogram();
        LatencyCounts before = h.Snapshot();
        for (int i = 1; i <= 100; i++) h.Record(i, 10);
        h.Record(99_999, 0);   // beyond the last bucket: counted in the overflow
        LatencyStats s = h.Snapshot().Since(before).Stats()!;
        Assert.Equal(101, s.Samples);
        Assert.Equal(51, s.P50Ms);
        Assert.Equal(96, s.P95Ms);
        Assert.Equal(InputLatencyHistogram.Buckets, s.MaxMs);
        Assert.Equal(41, s.MinusRttP50Ms);
        Assert.Null(h.Snapshot().Since(h.Snapshot()).Stats());   // nothing in between: "Not Available"
    }

    // A server whose metrics change per call (stalls appear on the third reading).
    private sealed class SequenceServer : IQaServerClient
    {
        private readonly FakeQaServer _inner;
        private int _calls;

        // 기능: Metrics 외의 요청을 넘길 가짜 서버를 감싼다.
        // 입력: inner - 위임 대상 가짜 서버.
        // 출력: 호출 수 0인 SequenceServer.
        public SequenceServer(FakeQaServer inner) => _inner = inner;

        public int MetricCalls => _calls;
        public List<int?> Windows { get; } = new();

        // 기능: 호출 횟수 n과 요청 창을 기록하고 n에 따라 달라지는 Metrics(세 번째 읽기 또는 3초 이상 창부터 tickMax 40과 Stall 1)를 돌려준다.
        // 입력: windowSeconds - 집계 창(초), token - 취소 토큰(쓰지 않음).
        // 출력: 서버 /qa/metrics 형식의 JSON.
        public Task<JsonElement> GetMetricsAsync(int? windowSeconds, CancellationToken token)
        {
            int n = Interlocked.Increment(ref _calls);
            lock (Windows) Windows.Add(windowSeconds);
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                ok = true, tickP50Ms = 0.1, tickP95Ms = 0.2 + n * 0.01, tickP99Ms = 0.3, tickMaxMs = n == 3 || windowSeconds >= 3 ? 40.0 : 1.0, cpuPercent = 1.5, workingSetMB = 80.0,
                managedMB = 10.0 + n, allocatedMBTotal = 100.0 + n, gcPauseMsTotal = 1.0, gc = new { gen0 = n, gen1 = 0, gen2 = 0 },
                pktInPerSec = 100.0, pktOutPerSec = 200.0, bytesInPerSec = 2048.0, bytesOutPerSec = 10240.0, qaCommandMs = 0.01,
                activeSessions = 2, players = 2, alive = 2, buildPieces = 3, stalls = n >= 3 ? 1 : 0, dbQueueLength = 0,
                health = new { stalls = n >= 3 ? 1 : 0, tickFailures = 0, badPackets = 0 },
            }));
        }

        // 기능: health 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: token - 취소 토큰.
        // 출력: 안쪽 서버의 health JSON.
        public Task<JsonElement> GetHealthAsync(CancellationToken token) => _inner.GetHealthAsync(token);
        // 기능: QA 명령을 안쪽 가짜 서버에 넘긴다.
        // 입력: command - 명령 이름, player - 대상 플레이어, args - 명령 인자, runId - 실행 ID, token - 취소 토큰.
        // 출력: 안쪽 서버의 CommandResponse.
        public Task<CommandResponse> CommandAsync(string command, string? player, JsonElement args, string runId, CancellationToken token) => _inner.CommandAsync(command, player, args, runId, token);
        // 기능: 플레이어 한 명 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: devPlayerId - 플레이어 Dev ID, token - 취소 토큰.
        // 출력: 안쪽 서버의 플레이어 JSON. 없으면 null.
        public Task<JsonElement?> GetPlayerAsync(string devPlayerId, CancellationToken token) => _inner.GetPlayerAsync(devPlayerId, token);
        // 기능: 플레이어 목록 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: token - 취소 토큰.
        // 출력: 안쪽 서버의 플레이어 배열 JSON.
        public Task<JsonElement> GetPlayersAsync(CancellationToken token) => _inner.GetPlayersAsync(token);
        // 기능: Match 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: token - 취소 토큰.
        // 출력: 안쪽 서버의 Match JSON.
        public Task<JsonElement> GetMatchAsync(CancellationToken token) => _inner.GetMatchAsync(token);
        // 기능: 건설 상태 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: x, z, radius, max - 조회 범위, token - 취소 토큰.
        // 출력: 안쪽 서버의 건설 JSON.
        public Task<JsonElement> GetBuildAsync(float? x, float? z, float? radius, int? max, CancellationToken token) => _inner.GetBuildAsync(x, z, radius, max, token);
        // 기능: Loot 상태 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: x, z, radius - 조회 범위, token - 취소 토큰.
        // 출력: 안쪽 서버의 Loot JSON.
        public Task<JsonElement> GetLootAsync(float? x, float? z, float? radius, CancellationToken token) => _inner.GetLootAsync(x, z, radius, token);
        // 기능: 투사체 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: token - 취소 토큰.
        // 출력: 안쪽 서버의 투사체 JSON.
        public Task<JsonElement> GetProjectilesAsync(CancellationToken token) => _inner.GetProjectilesAsync(token);
        // 기능: 차량 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: token - 취소 토큰.
        // 출력: 안쪽 서버의 차량 JSON.
        public Task<JsonElement> GetVehiclesAsync(CancellationToken token) => _inner.GetVehiclesAsync(token);
        // 기능: 이벤트 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: after - 마지막으로 받은 순번, max - 최대 개수, token - 취소 토큰.
        // 출력: 안쪽 서버의 이벤트 JSON.
        public Task<JsonElement> GetEventsAsync(long after, int max, CancellationToken token) => _inner.GetEventsAsync(after, max, token);
        // 기능: 서버 정지 요청을 안쪽 가짜 서버에 넘긴다.
        // 입력: token - 취소 토큰.
        // 출력: 반환값 없음. 안쪽 서버의 Stopped가 true가 된다.
        public Task StopServerAsync(CancellationToken token) => _inner.StopServerAsync(token);
    }

    [Fact]
    public async Task MeasurePhaseSamplesRecordsStallsAndWritesTheSummary()
    {
        string file = Write("QA/Scenarios/T/m.json", """
        { "schemaVersion": 1, "name": "measure", "stress": true, "seed": 3,
          "steps": [
            { "id": "spawn", "action": "spawnActors", "count": 2, "prefix": "bot" },
            { "id": "join", "action": "connectAll", "prefix": "bot" },
            { "id": "steady", "action": "measure", "name": "steady", "seconds": 3, "sampleSeconds": 1, "saveAs": "steady" },
            { "id": "p95", "assert": "var.steady.tickP95Ms", "greaterThan": 0 }
          ] }
        """);
        var server = new SequenceServer(_fake);
        (int exit, string output) = await Cli(server, default, "run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(0, exit);
        Assert.Contains("STRESS steady", output);
        JsonElement report = Report(RunIds().Single());
        JsonElement stress = report.GetProperty("stress");
        JsonElement phase = stress.GetProperty("phases")[0];
        Assert.Equal("steady", phase.GetProperty("name").GetString());
        Assert.Equal(3, phase.GetProperty("samples").GetArrayLength());       // one per second
        Assert.True(phase.GetProperty("ticksExact").GetBoolean());             // 3 s is inside the server's ring: one whole window
        Assert.Equal(1, phase.GetProperty("stalls").GetInt64());
        Assert.Equal(1, stress.GetProperty("stalls").GetArrayLength());       // D41: the stall with its moment
        Assert.Equal("steady", stress.GetProperty("stalls")[0].GetProperty("stepId").GetString());
        Assert.Equal(2, stress.GetProperty("stalls")[0].GetProperty("players").GetInt32());
        Assert.Equal("steady", stress.GetProperty("summary").GetProperty("phase").GetString());
        Assert.Contains(report.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("stall"));
        // start, 3 samples, the whole phase (then the report's own end-of-run reading).
        lock (server.Windows) Assert.Equal(new int?[] { null, 1, 1, 1, 3 }, server.Windows.Take(5).ToArray());
        string html = File.ReadAllText(Path.Combine(Reports, RunIds().Single(), "report.html"));
        Assert.Contains("Stress Summary", html);
        Assert.Contains("Measure phases", html);
    }

    [Fact]
    public async Task MeasureWarnsOverTheTickBudget()
    {
        // tickMaxMs 40 on the third reading: over the 33 ms budget (D42 warning, not a failure).
        string file = Write("QA/Scenarios/T/w.json", """
        { "schemaVersion": 1, "name": "budget", "seed": 3,
          "steps": [ { "id": "steady", "action": "measure", "name": "steady", "seconds": 3, "sampleSeconds": 1 } ] }
        """);
        (int exit, _) = await Cli(new SequenceServer(_fake), default, "run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(0, exit);
        JsonElement report = Report(RunIds().Single());
        Assert.Contains(report.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("tick budget"));
    }

    [Fact]
    public async Task MeasureCutShortStillReportsThePhase()
    {
        string file = Write("QA/Scenarios/T/c.json", """
        { "schemaVersion": 1, "name": "cut", "seed": 3, "steps": [ { "id": "long", "action": "measure", "name": "steady", "seconds": 600, "sampleSeconds": 1 } ] }
        """);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
        (int exit, _) = await Cli(new SequenceServer(_fake), cts.Token, "run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(1, exit);
        JsonElement phase = Report(RunIds().Single()).GetProperty("stress").GetProperty("phases")[0];
        Assert.True(phase.GetProperty("cancelled").GetBoolean());
        Assert.False(phase.GetProperty("ticksExact").GetBoolean());
        Assert.InRange(phase.GetProperty("samples").GetArrayLength(), 1, 3);
    }

    // ---- player-count parameters and the batch comparison (D40) ----

    [Fact]
    public async Task PlayerCountParametersSpawnThatManyAndTheBatchComparesThem()
    {
        string file = Write("QA/Scenarios/T/players.json", """
        { "schemaVersion": 1, "name": "players", "stress": true, "seed": 4, "variables": { "players": 1, "steadySeconds": 1 },
          "parameters": [ { "players": 2 }, { "players": 5 } ],
          "steps": [
            { "id": "spawn", "action": "spawnActors", "count": "${players}", "prefix": "bot" },
            { "id": "join", "action": "connectAll", "prefix": "bot" },
            { "id": "groups", "action": "actorGroup", "prefix": "bot", "groups": [ { "name": "all", "rest": true } ] },
            { "id": "size", "assert": "group.all.size", "equals": "${players}" },
            { "id": "move", "action": "groupMove", "group": "all" },
            { "id": "steady", "action": "measure", "name": "steady", "seconds": "${steadySeconds}" },
            { "id": "stop", "action": "stopGroup", "group": "all" }
          ] }
        """);
        (int exit, string output) = await Cli(new SequenceServer(_fake), default, "run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(0, exit);
        Assert.Equal(7, _actors.Count);
        Assert.Contains("Stress comparison", output);
        Assert.Contains("batch summary:", output);
        string batch = Directory.GetDirectories(Reports).Single(d => Path.GetFileName(d).StartsWith("batch-", StringComparison.Ordinal));
        using JsonDocument summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(batch, "summary.json")));
        JsonElement rows = summary.RootElement.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(1, rows[0].GetProperty("parameterSet").GetInt32());
        Assert.Contains("\"players\":5", rows[1].GetProperty("parameters").GetString());
        string html = File.ReadAllText(Path.Combine(batch, "summary.html"));
        Assert.Contains("Tick P50", html);
        Assert.Contains("WorkingSet MB", html);
        Assert.Contains("../" + rows[0].GetProperty("runId").GetString() + "/report.html", html);
    }

    // ---- workloads: cancellation and cleanup (D38) ----

    [Fact]
    public async Task ChurnRunsInTheBackgroundAndStopGroupEndsIt()
    {
        string file = Write("QA/Scenarios/T/churn.json", """
        { "schemaVersion": 1, "name": "churn", "seed": 5,
          "steps": [
            { "id": "spawn", "action": "spawnActors", "count": 4, "prefix": "bot" },
            { "id": "join", "action": "connectAll", "prefix": "bot" },
            { "id": "groups", "action": "actorGroup", "prefix": "bot", "groups": [ { "name": "c", "rest": true } ] },
            { "id": "churn", "action": "groupChurn", "group": "c", "percent": 50, "cycleSeconds": 1, "cycles": 1000, "offlineMs": 0 },
            { "id": "running", "assert": "group.c.workloads", "equals": 1 },
            { "id": "w", "action": "wait", "milliseconds": 2500 },
            { "id": "stop", "action": "stopGroup", "group": "c", "saveAs": "stats" },
            { "id": "ended", "assert": "group.c.workloads", "equals": 0 },
            { "id": "cycles", "assert": "var.stats.churnCycles", "greaterThan": 1 },
            { "id": "back", "assert": "var.stats.reconnectFailures", "equals": 0 },
            { "id": "joined", "assert": "group.c.joined", "equals": 4 }
          ] }
        """);
        (int exit, string output) = await Cli("run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(0, exit);
        // Each reconnect is a new connection of the same actor (Connections counts them).
        Assert.True(_actors.Sum(a => a.State.Connections) > 4);
        Assert.Contains(_actors.SelectMany(a => a.Commands), c => c is ConnectCommand { Reconnect: true });
    }

    [Fact]
    public async Task CleanupEndsWorkloadsTheScenarioLeftRunningAlsoOnCancel()
    {
        string file = Write("QA/Scenarios/T/left.json", """
        { "schemaVersion": 1, "name": "left running", "seed": 6,
          "steps": [
            { "id": "spawn", "action": "spawnActors", "count": 4, "prefix": "bot" },
            { "id": "join", "action": "connectAll", "prefix": "bot" },
            { "id": "groups", "action": "actorGroup", "prefix": "bot", "groups": [ { "name": "fight", "rest": true } ] },
            { "id": "fight", "action": "groupCombat", "group": "fight", "arrange": false },
            { "id": "churn", "action": "groupChurn", "group": "fight", "percent": 25, "cycleSeconds": 1, "cycles": 1000 },
            { "id": "w", "action": "wait", "seconds": 30 }
          ] }
        """);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        (int exit, _) = await Cli(_fake, cts.Token, "run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(1, exit);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"cleanup took {clock.Elapsed}");
        JsonElement cleanup = Report(RunIds().Single()).GetProperty("cleanup");
        JsonElement groups = cleanup.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "groups");
        Assert.True(groups.GetProperty("ok").GetBoolean());
        // The behaviours were handed to the actors (SetBrainCommand) and the run's cleanup stopped the pump-side state.
        Assert.Equal(4, _actors.Count(a => a.Commands.OfType<SetBrainCommand>().Any(b => b.Brain is CombatBrain)));
    }

    // A server whose /qa/players fails as told (review fix 1).
    private sealed class PlayersServer : IQaServerClient
    {
        private readonly FakeQaServer _inner;
        private readonly Func<int, Exception?> _fail;
        private int _calls;

        // 기능: /qa/players 호출 n번째에 던질 예외를 정하는 함수와 함께 가짜 서버를 감싼다.
        // 입력: inner - 위임 대상 가짜 서버, fail - 호출 번호를 받아 던질 예외(없으면 null)를 돌려주는 함수.
        // 출력: 호출 수 0인 PlayersServer.
        public PlayersServer(FakeQaServer inner, Func<int, Exception?> fail)
        {
            _inner = inner;
            _fail = fail;
        }

        public int PlayerCalls => _calls;

        // 기능: 호출 번호를 올리고 fail 함수가 예외를 주면 그 예외로 실패하는 Task를, 아니면 안쪽 서버의 플레이어 목록을 돌려준다.
        // 입력: token - 취소 토큰.
        // 출력: 플레이어 배열 JSON 또는 fail이 정한 예외로 실패한 Task.
        public Task<JsonElement> GetPlayersAsync(CancellationToken token)
        {
            Exception? e = _fail(Interlocked.Increment(ref _calls));
            return e != null ? Task.FromException<JsonElement>(e) : _inner.GetPlayersAsync(token);
        }

        // 기능: Metrics 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: windowSeconds - 집계 창, token - 취소 토큰.
        // 출력: 안쪽 서버의 Metrics JSON.
        public Task<JsonElement> GetMetricsAsync(int? windowSeconds, CancellationToken token) => _inner.GetMetricsAsync(windowSeconds, token);
        // 기능: health 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: token - 취소 토큰.
        // 출력: 안쪽 서버의 health JSON.
        public Task<JsonElement> GetHealthAsync(CancellationToken token) => _inner.GetHealthAsync(token);
        // 기능: QA 명령을 안쪽 가짜 서버에 넘긴다.
        // 입력: command - 명령 이름, player - 대상 플레이어, args - 명령 인자, runId - 실행 ID, token - 취소 토큰.
        // 출력: 안쪽 서버의 CommandResponse.
        public Task<CommandResponse> CommandAsync(string command, string? player, JsonElement args, string runId, CancellationToken token) => _inner.CommandAsync(command, player, args, runId, token);
        // 기능: 플레이어 한 명 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: devPlayerId - 플레이어 Dev ID, token - 취소 토큰.
        // 출력: 안쪽 서버의 플레이어 JSON. 없으면 null.
        public Task<JsonElement?> GetPlayerAsync(string devPlayerId, CancellationToken token) => _inner.GetPlayerAsync(devPlayerId, token);
        // 기능: Match 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: token - 취소 토큰.
        // 출력: 안쪽 서버의 Match JSON.
        public Task<JsonElement> GetMatchAsync(CancellationToken token) => _inner.GetMatchAsync(token);
        // 기능: 건설 상태 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: x, z, radius, max - 조회 범위, token - 취소 토큰.
        // 출력: 안쪽 서버의 건설 JSON.
        public Task<JsonElement> GetBuildAsync(float? x, float? z, float? radius, int? max, CancellationToken token) => _inner.GetBuildAsync(x, z, radius, max, token);
        // 기능: Loot 상태 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: x, z, radius - 조회 범위, token - 취소 토큰.
        // 출력: 안쪽 서버의 Loot JSON.
        public Task<JsonElement> GetLootAsync(float? x, float? z, float? radius, CancellationToken token) => _inner.GetLootAsync(x, z, radius, token);
        // 기능: 투사체 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: token - 취소 토큰.
        // 출력: 안쪽 서버의 투사체 JSON.
        public Task<JsonElement> GetProjectilesAsync(CancellationToken token) => _inner.GetProjectilesAsync(token);
        // 기능: 차량 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: token - 취소 토큰.
        // 출력: 안쪽 서버의 차량 JSON.
        public Task<JsonElement> GetVehiclesAsync(CancellationToken token) => _inner.GetVehiclesAsync(token);
        // 기능: 이벤트 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: after - 마지막으로 받은 순번, max - 최대 개수, token - 취소 토큰.
        // 출력: 안쪽 서버의 이벤트 JSON.
        public Task<JsonElement> GetEventsAsync(long after, int max, CancellationToken token) => _inner.GetEventsAsync(after, max, token);
        // 기능: 서버 정지 요청을 안쪽 가짜 서버에 넘긴다.
        // 입력: token - 취소 토큰.
        // 출력: 반환값 없음. 안쪽 서버의 Stopped가 true가 된다.
        public Task StopServerAsync(CancellationToken token) => _inner.StopServerAsync(token);
    }

    [Fact]
    public async Task ChurnThatFailsBringsItsActorsBackAndStopGroupFails()
    {
        string file = Write("QA/Scenarios/T/churnfail.json", """
        { "schemaVersion": 1, "name": "churn fails", "seed": 5,
          "steps": [
            { "id": "spawn", "action": "spawnActors", "count": 4, "prefix": "bot" },
            { "id": "join", "action": "connectAll", "prefix": "bot" },
            { "id": "groups", "action": "actorGroup", "prefix": "bot", "groups": [ { "name": "c", "rest": true } ] },
            { "id": "churn", "action": "groupChurn", "group": "c", "percent": 50, "cycleSeconds": 1, "cycles": 1000, "offlineMs": 0 },
            { "id": "w", "action": "wait", "milliseconds": 1500 },
            { "id": "back", "assert": "group.c.joined", "equals": 4 },
            { "id": "failed", "assert": "group.c.stats.workloadFailures", "equals": 1 },
            { "id": "stop", "action": "stopGroup", "group": "c", "continueOnFailure": true },
            { "id": "after", "assert": "group.c.joined", "equals": 4 }
          ] }
        """);
        // A 500 is not retried: the churn ends with an error right after sending two actors away.
        var server = new PlayersServer(_fake, _ => new QaApiException("GET /qa/players: HTTP 500", 500));
        (int exit, string output) = await Cli(server, default, "run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(1, exit);
        JsonElement report = Report(RunIds().Single());
        Assert.Equal("stop", report.GetProperty("failure").GetProperty("stepId").GetString());
        Assert.Contains("ended with an error", report.GetProperty("failure").GetProperty("message").GetString());
        Assert.Equal("Passed", report.GetProperty("steps")[5].GetProperty("status").GetString());   // back: nobody left offline
        Assert.Contains(report.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("ended with an error"));
    }

    // The server still lists every bot as connected for `holdMs` (a dropped peer before DisconnectTimeoutMs).
    private sealed class SlowDropServer : IQaServerClient
    {
        private readonly FakeQaServer _inner;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly int _holdMs;

        // 기능: 끊긴 Bot을 holdMs 동안 접속 중으로 보고할 가짜 서버를 감싸고 시계를 시작한다.
        // 입력: inner - 위임 대상 가짜 서버, holdMs - 접속 중으로 보고하는 시간(ms).
        // 출력: 시계가 시작된 SlowDropServer.
        public SlowDropServer(FakeQaServer inner, int holdMs)
        {
            _inner = inner;
            _holdMs = holdMs;
        }

        // 기능: Bot 네 명(qa-bot-001~004)을 시계가 holdMs를 넘기 전까지는 접속 중으로, 그 뒤에는 끊긴 것으로 보고한다.
        // 입력: token - 취소 토큰(쓰지 않음).
        // 출력: devPlayerId와 connected만 가진 플레이어 배열 JSON.
        public Task<JsonElement> GetPlayersAsync(CancellationToken token)
        {
            bool connected = _clock.ElapsedMilliseconds < _holdMs;
            return Task.FromResult(JsonSerializer.SerializeToElement(Enumerable.Range(1, 4).Select(i => new { devPlayerId = $"qa-bot-00{i}", connected })));
        }

        // 기능: Metrics 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: windowSeconds - 집계 창, token - 취소 토큰.
        // 출력: 안쪽 서버의 Metrics JSON.
        public Task<JsonElement> GetMetricsAsync(int? windowSeconds, CancellationToken token) => _inner.GetMetricsAsync(windowSeconds, token);
        // 기능: health 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: token - 취소 토큰.
        // 출력: 안쪽 서버의 health JSON.
        public Task<JsonElement> GetHealthAsync(CancellationToken token) => _inner.GetHealthAsync(token);
        // 기능: QA 명령을 안쪽 가짜 서버에 넘긴다.
        // 입력: command - 명령 이름, player - 대상 플레이어, args - 명령 인자, runId - 실행 ID, token - 취소 토큰.
        // 출력: 안쪽 서버의 CommandResponse.
        public Task<CommandResponse> CommandAsync(string command, string? player, JsonElement args, string runId, CancellationToken token) => _inner.CommandAsync(command, player, args, runId, token);
        // 기능: 플레이어 한 명 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: devPlayerId - 플레이어 Dev ID, token - 취소 토큰.
        // 출력: 안쪽 서버의 플레이어 JSON. 없으면 null.
        public Task<JsonElement?> GetPlayerAsync(string devPlayerId, CancellationToken token) => _inner.GetPlayerAsync(devPlayerId, token);
        // 기능: Match 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: token - 취소 토큰.
        // 출력: 안쪽 서버의 Match JSON.
        public Task<JsonElement> GetMatchAsync(CancellationToken token) => _inner.GetMatchAsync(token);
        // 기능: 건설 상태 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: x, z, radius, max - 조회 범위, token - 취소 토큰.
        // 출력: 안쪽 서버의 건설 JSON.
        public Task<JsonElement> GetBuildAsync(float? x, float? z, float? radius, int? max, CancellationToken token) => _inner.GetBuildAsync(x, z, radius, max, token);
        // 기능: Loot 상태 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: x, z, radius - 조회 범위, token - 취소 토큰.
        // 출력: 안쪽 서버의 Loot JSON.
        public Task<JsonElement> GetLootAsync(float? x, float? z, float? radius, CancellationToken token) => _inner.GetLootAsync(x, z, radius, token);
        // 기능: 투사체 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: token - 취소 토큰.
        // 출력: 안쪽 서버의 투사체 JSON.
        public Task<JsonElement> GetProjectilesAsync(CancellationToken token) => _inner.GetProjectilesAsync(token);
        // 기능: 차량 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: token - 취소 토큰.
        // 출력: 안쪽 서버의 차량 JSON.
        public Task<JsonElement> GetVehiclesAsync(CancellationToken token) => _inner.GetVehiclesAsync(token);
        // 기능: 이벤트 조회를 안쪽 가짜 서버에 넘긴다.
        // 입력: after - 마지막으로 받은 순번, max - 최대 개수, token - 취소 토큰.
        // 출력: 안쪽 서버의 이벤트 JSON.
        public Task<JsonElement> GetEventsAsync(long after, int max, CancellationToken token) => _inner.GetEventsAsync(after, max, token);
        // 기능: 서버 정지 요청을 안쪽 가짜 서버에 넘긴다.
        // 입력: token - 취소 토큰.
        // 출력: 반환값 없음. 안쪽 서버의 Stopped가 true가 된다.
        public Task StopServerAsync(CancellationToken token) => _inner.StopServerAsync(token);
    }

    [Fact]
    public async Task StoppingAChurnRightAfterADropWaitsOutTheServersDisconnectTimeout()
    {
        // The server keeps the dropped peers "connected" for 6 s (above the old 4 s bring-back bound, like the default
        // 5 s DisconnectTimeoutMs plus a slow request): the stop still brings everyone back, within the step's budget.
        string file = Write("QA/Scenarios/T/dropstop.json", """
        { "schemaVersion": 1, "name": "drop then stop", "seed": 5,
          "steps": [
            { "id": "spawn", "action": "spawnActors", "count": 4, "prefix": "bot" },
            { "id": "join", "action": "connectAll", "prefix": "bot" },
            { "id": "groups", "action": "actorGroup", "prefix": "bot", "groups": [ { "name": "c", "rest": true } ] },
            { "id": "churn", "action": "groupChurn", "group": "c", "percent": 50, "cycleSeconds": 30, "cycles": 10, "mode": "drop", "offlineMs": 0 },
            { "id": "w", "action": "wait", "milliseconds": 500 },
            { "id": "away", "assert": "group.c.joined", "equals": 2 },
            { "id": "stop", "action": "stopGroup", "group": "c" },
            { "id": "back", "assert": "group.c.joined", "equals": 4 },
            { "id": "clean", "assert": "group.c.stats.workloadFailures", "equals": 0 }
          ] }
        """);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        (int exit, _) = await Cli(new SlowDropServer(_fake, 6000), default, "run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(0, exit);
        Assert.True(StressActions.BringBackTimeout > TimeSpan.FromSeconds(5) && GroupRegistry.StopTimeout > StressActions.BringBackTimeout);
        Assert.True(StressActions.StopGroupTimeoutMs > GroupRegistry.StopTimeout.TotalMilliseconds);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task PlayersQueryRetries503AndTimeoutsButNotOtherErrors()
    {
        var once = new PlayersServer(_fake, n => n == 1 ? new QaApiException("busy", 503) : n == 2 ? new OperationCanceledException("own timeout") : null);
        await StressActions.PlayersAsync(once, default);
        Assert.Equal(3, once.PlayerCalls);
        var always = new PlayersServer(_fake, _ => new QaApiException("busy", 504));
        await Assert.ThrowsAsync<QaApiException>(() => StressActions.PlayersAsync(always, default));
        Assert.Equal(3, always.PlayerCalls);   // bounded: two retries
        var hard = new PlayersServer(_fake, _ => new QaApiException("bad", 500));
        await Assert.ThrowsAsync<QaApiException>(() => StressActions.PlayersAsync(hard, default));
        Assert.Equal(1, hard.PlayerCalls);
    }

    [Fact]
    public async Task LargestFightsAndANewBehaviourReplaceTheOldWorkload()
    {
        // groupSize 8 with 9 members: the leftover joins the last fight (a ring of 9), which used to overflow.
        string file = Write("QA/Scenarios/T/fights.json", """
        { "schemaVersion": 1, "name": "fights", "seed": 5,
          "steps": [
            { "id": "spawn", "action": "spawnActors", "count": 9, "prefix": "bot" },
            { "id": "join", "action": "connectAll", "prefix": "bot" },
            { "id": "groups", "action": "actorGroup", "prefix": "bot", "groups": [ { "name": "f", "rest": true } ] },
            { "id": "fight", "action": "groupCombat", "group": "f", "pairing": "groups", "groupSize": 8, "arrange": false },
            { "id": "again", "action": "groupCombat", "group": "f", "arrange": false },
            { "id": "one", "assert": "group.f.workloads", "equals": 1 },
            { "id": "move", "action": "groupMove", "group": "f" },
            { "id": "none", "assert": "group.f.workloads", "equals": 0 }
          ] }
        """);
        (int exit, _) = await Cli("run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task FightAreaIsBounded()
    {
        Assert.Contains(Validate("""{ "schemaVersion": 1, "steps": [ { "action": "actorGroup", "groups": [ { "name": "g", "rest": true } ] }, { "action": "groupCombat", "group": "g", "radius": 100000 } ] }"""), i => i.IsError && i.Message.Contains("'radius'"));
        Assert.Contains(Validate("""{ "schemaVersion": 1, "steps": [ { "action": "actorGroup", "groups": [ { "name": "g", "rest": true } ] }, { "action": "groupCombat", "group": "g", "spacing": 0.1 } ] }"""), i => i.IsError && i.Message.Contains("'spacing'"));
        Assert.Contains(Validate("""{ "schemaVersion": 1, "steps": [ { "action": "actorGroup", "groups": [ { "name": "g", "rest": true } ] }, { "action": "groupCombat", "group": "g", "radius": 0 } ] }"""), i => i.IsError && i.Message.Contains("'radius'"));
        // A ${variable} is checked when the step runs.
        string file = Write("QA/Scenarios/T/area.json", """
        { "schemaVersion": 1, "name": "area", "seed": 5, "variables": { "r": 5000 },
          "steps": [
            { "id": "spawn", "action": "spawnActors", "count": 2, "prefix": "bot" },
            { "id": "groups", "action": "actorGroup", "prefix": "bot", "groups": [ { "name": "f", "rest": true } ] },
            { "id": "fight", "action": "groupCombat", "group": "f", "radius": "${r}" }
          ] }
        """);
        (int exit, string output) = await Cli("run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(1, exit);
        Assert.Contains("'radius' must be above 0 and at most 120", output);
    }

    [Fact]
    public void PoolReleasesGivenUpSitesAndCountsSharing()
    {
        List<StressMap.BuildSite> sites = StressMap.BuildSites(Vector2.Zero, 1000, StressMap.AllPieces, BuildMaterialType.Wood).Take(3).ToList();
        var pool = new BuildSitePool(sites);
        StressMap.BuildSite a = pool.Claim(Vector2.Zero)!;
        Assert.Equal(2, pool.Free);
        // Given up: back to the pool, and the next claim avoids it.
        pool.Release(a);
        Assert.Equal(3, pool.Free);
        StressMap.BuildSite b = pool.Claim(Vector2.Zero, avoid: a)!;
        Assert.NotSame(a, b);
        pool.Claim(Vector2.Zero);
        pool.Claim(Vector2.Zero);
        Assert.Equal(0, pool.Free);
        Assert.Equal(0, pool.Shared);
        // All held: the next claims share the least-held sites, one each before any site gets a third holder.
        var shared = new[] { pool.Claim(Vector2.Zero)!, pool.Claim(Vector2.Zero)!, pool.Claim(Vector2.Zero)! };
        Assert.Equal(3, shared.Distinct().Count());
        Assert.Equal(3, pool.Shared);
        pool.Release(shared[0]);
        Assert.Equal(2, pool.Shared);
    }

    // ---- suites, --set, validation ----

    [Fact]
    public async Task SuiteEntriesPickAParameterSetAndSetVariables()
    {
        Write("QA/Scenarios/T/s.json", """
        { "schemaVersion": 1, "name": "suite entry", "seed": 1, "variables": { "limit": 10 },
          "parameters": [ { "players": 1 }, { "players": 50 } ],
          "steps": [ { "id": "check", "assert": "var.players", "lessThan": "${limit}" } ] }
        """);
        Write("QA/Suites/sq.json", """{ "scenarios": [ { "path": "T/s.json", "parameterSet": 2, "variables": { "limit": 100 } }, { "path": "T/s.json", "parameterSet": 1 } ] }""");
        (int exit, string output) = await Cli("run", "suite:sq", "--attach", "http://127.0.0.1:1", "--report-dir", Reports);
        Assert.Equal(0, exit);
        Assert.StartsWith("suite sq: 2 scenarios", output);
        Assert.Contains("parameters[2] {\"players\":50}  set {\"limit\":100}", output);
        Assert.Contains("parameters[1] {\"players\":1}", output);
        // --set wins over the entry; an unknown name is an error (exit 2), not silently ignored.
        Assert.Equal(1, (await Cli("run", "suite:sq", "--attach", "http://127.0.0.1:1", "--report-dir", Reports, "--set", "limit=5")).Exit);
        (exit, output) = await Cli("run", "T/s.json", "--attach", "http://127.0.0.1:1", "--report-dir", Reports, "--set", "limt=5");
        Assert.Equal(2, exit);
        Assert.Contains("'limt' is not a variable", output);
        Write("QA/Suites/bad.json", """{ "scenarios": [ { "path": "T", "parameterSet": 1 } ] }""");
        Assert.Equal(2, (await Cli("validate", "suite:bad")).Exit);
    }

    [Fact]
    public void StressModeValidation()
    {
        string steps(string s) => $$"""{ "schemaVersion": 1, "stress": true, "actors": [ { "id": "u", "type": "UnityClient" }, { "id": "h" } ], "steps": [ {{s}} ] }""";
        Assert.Contains(Validate(steps("""{ "action": "wait", "milliseconds": 1 }""")), i => i.IsError && i.Message.Contains("headless clients only"));
        Assert.Contains(Validate("""{ "schemaVersion": 1, "stress": true, "actors": [ { "id": "h" } ], "steps": [ { "action": "manualCheck", "description": "x" } ] }"""), i => i.IsError && i.Message.Contains("not allowed in a stress scenario"));
        Assert.Contains(Validate("""{ "schemaVersion": 1, "stress": true, "actors": [ { "id": "h" } ], "steps": [ { "action": "waitForEvent", "event": "PlayerJoined" } ] }"""), i => !i.IsError && i.Message.Contains("QA events are off"));
        Assert.DoesNotContain(Validate("""{ "schemaVersion": 1, "stress": true, "server": { "options": { "Qa:Events": "true" } }, "actors": [ { "id": "h" } ], "steps": [ { "action": "waitForEvent", "event": "PlayerJoined" } ] }"""), i => i.Message.Contains("QA events are off"));
        Assert.Contains(Validate("""{ "schemaVersion": 1, "steps": [ { "action": "groupMove", "group": "nope" } ] }"""), i => i.IsError && i.Message.Contains("Unknown group 'nope'"));
        Assert.DoesNotContain(Validate("""{ "schemaVersion": 1, "steps": [ { "action": "actorGroup", "groups": [ { "name": "g", "rest": true } ] }, { "action": "groupMove", "group": "g" }, { "action": "stopGroup", "group": "all" } ] }"""), i => i.IsError);
        Assert.Contains(Validate("""{ "schemaVersion": 1, "steps": [ { "action": "actorGroup", "groups": [ { "name": "a", "percent": 70 }, { "name": "b", "percent": 40 } ] } ] }"""), i => i.IsError && i.Message.Contains("add up to 110"));
        Assert.Contains(Validate("""{ "schemaVersion": 1, "steps": [ { "action": "measure", "name": "steady", "seconds": 0 } ] }"""), i => i.IsError && i.Message.Contains("'seconds' must be"));

        // D39: the launched server runs without QA events unless the scenario sets them.
        ScenarioDefinition stress = ScenarioLoader.Parse("""{ "schemaVersion": 1, "stress": true, "steps": [] }""").Scenario!;
        Assert.Equal("false", QaOrchestrator.LaunchOptions(stress)["Qa:Events"]);
        ScenarioDefinition own = ScenarioLoader.Parse("""{ "schemaVersion": 1, "stress": true, "server": { "options": { "Qa:Events": "true" } }, "steps": [] }""").Scenario!;
        Assert.Equal("true", QaOrchestrator.LaunchOptions(own)["Qa:Events"]);
        ScenarioDefinition plain = ScenarioLoader.Parse("""{ "schemaVersion": 1, "steps": [] }""").Scenario!;
        Assert.False(QaOrchestrator.LaunchOptions(plain).ContainsKey("Qa:Events"));
    }

    [Fact]
    public async Task BaselineComparesTheStressSummary()
    {
        string file = Write("QA/Scenarios/T/b.json", """
        { "schemaVersion": 1, "name": "baseline stress", "seed": 8, "steps": [ { "id": "steady", "action": "measure", "name": "steady", "seconds": 1 } ] }
        """);
        Assert.Equal(0, (await Cli(new SequenceServer(_fake), default, "run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports)).Exit);
        Assert.Equal(0, (await Cli(new SequenceServer(_fake), default, "run", file, "--attach", "http://127.0.0.1:1", "--report-dir", Reports)).Exit);
        JsonElement baseline = Report(RunIds().Last()).GetProperty("baseline");
        string[] rows = baseline.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("name").GetString()!).ToArray();
        Assert.Contains("stress.tickP95Ms (steady)", rows);
        Assert.Contains("stress.sendKBps (steady)", rows);
    }

    // ---- the real scenario files ----

    [Fact]
    public void StressScenariosAndSuitesAreValid()
    {
        string repo = RepoRoot();
        MarkerStore markers = MarkerStore.LoadFile(Path.Combine(repo, "QA", "Markers.json"));
        string[] names = { "baseline", "movement", "combat", "building", "mixed_match", "reconnect_churn", "final_zone", "soak", "soak_match_reset" };
        foreach (string name in names)
        {
            ScenarioLoadResult load = ScenarioLoader.LoadFile(Path.Combine(repo, "QA", "Scenarios", "Stress", name + ".json"));
            Assert.Empty(load.Errors);
            Assert.True(load.Scenario!.Stress, name);
            Assert.Contains("stress", load.Scenario.Tags);
            Assert.DoesNotContain(ScenarioValidator.Validate(load.Scenario, ActionRegistry.CreateDefault(), markers), i => i.IsError);
            Assert.Contains(load.Scenario.Steps, s => s.Action == "measure");
        }
        foreach (string suite in new[] { "stress-quick", "stress-gameplay", "stress-soak", "stress-network", "stress-building", "stress-fault", "stress-full" })
            Assert.NotEmpty(ScenarioCatalog.Select(repo, "suite:" + suite).Items);
        // Not in pre-push (§97); the old `stress` suite keeps its three load files.
        Assert.DoesNotContain(ScenarioCatalog.Select(repo, "suite:pre-push").Files, f => f.Contains("Stress", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, ScenarioCatalog.Select(repo, "suite:stress").Files.Count);
    }

    // 기능: 테스트 실행 폴더에서 저장소 루트를 찾는다.
    // 입력: 없음.
    // 출력: 저장소 루트 경로. 못 찾으면 InvalidOperationException.
    private static string RepoRoot() => ServerLocator.FindRepoRoot(AppContext.BaseDirectory) ?? throw new InvalidOperationException("repo root");
}
