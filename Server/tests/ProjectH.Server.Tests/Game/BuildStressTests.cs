using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace ProjectH.Server.Tests.Game;

// Runs only with PROJECTH_BUILD_STRESS=1 (Phase 13 request §137-§140: the synthetic build load test). Measures, not checks.
public sealed class BuildStressFactAttribute : FactAttribute
{
    public const string Variable = "PROJECTH_BUILD_STRESS";

    // 기능: 환경 변수 PROJECTH_BUILD_STRESS가 1이 아니면 테스트를 건너뛰게 표시한다.
    // 입력: 없음.
    // 출력: 환경 변수가 없으면 Skip 사유가 설정된 Fact attribute.
    public BuildStressFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1") Skip = $"Set {Variable}=1 to run the build stress measurements.";
    }
}

// Phase 13 request §137-§140, §184-§187: the server's Match driven in-process (no sockets: the send delegates count
// packets and bytes), with 50 (or 100) players moving, shooting and building, over 1,000 / 5,000 / 10,000 / 20,000
// pieces spread over the map, and one mass collapse. Prints one line per run: Tick p50/p95/p99, allocation per tick,
// GC counts, memory per piece, packets and bytes per second on each channel. Run in Release:
//   PROJECTH_BUILD_STRESS=1 dotnet test Server/ProjectH.Server.slnx -c Release --filter "FullyQualifiedName~BuildStressTests" --logger "console;verbosity=detailed"
public sealed class BuildStressTests
{
    private const int SimHz = 30;
    private readonly ITestOutputHelper _out;
    private long _packets;
    private long _bytes;
    private long _buildPackets;
    private long _buildBytes;

    // 기능: 측정 결과를 적을 xUnit 출력을 받아 둔다.
    // 입력: output - 테스트 출력.
    // 출력: 계수기가 0인 테스트 인스턴스.
    public BuildStressTests(ITestOutputHelper output) => _out = output;

    // 기능: 패킷 수와 바이트를 채널별로 세는 송신 대리자를 단 개발 모드 Match를 만든다(자원 무한).
    // 입력: players - 최대 인원.
    // 출력: 참가자 없는 Match.
    private Match NewMatch(int players)
    {
        return new Match(new ServerOptions { MaxPlayers = players, DevRespawn = true, BuildInfiniteResources = true }, TestGameData.Create(),
            (_, data, _) =>
            {
                _packets++;
                _bytes += data.Length;
            }, TestGameData.CombatLoadout, Array.Empty<LootPoint>(),
            sendBuild: (_, data, _) =>
            {
                _buildPackets++;
                _buildBytes += data.Length;
            });
    }

    // 기능: 맵 전체에 바닥, 남쪽 벽, 서쪽 벽 순으로 층마다 칸마다 조각을 채운다.
    // 입력: match - 채울 경기, count - 세울 조각 수 상한.
    // 출력: 실제로 세운 조각 수.
    // Floors, then south and west walls, cell by cell and level by level over the whole map.
    private static int Fill(Match match, int count)
    {
        int added = 0;
        for (int y = 0; y < BuildGrid.Levels && added < count; y++)
            for (int kind = 0; kind < 3 && added < count; kind++)
                for (int z = 0; z < BuildGrid.CellsZ && added < count; z++)
                    for (int x = 0; x < BuildGrid.CellsX && added < count; x++)
                    {
                        var shape = kind == 0 ? new BuildPieceShape(BuildPieceType.Floor, x, y, z, 0) : new BuildPieceShape(BuildPieceType.Wall, x, y, z, kind - 1);
                        SandboxHarness.AddPiece(match, shape);
                        added++;
                    }
        return added;
    }

    // 기능: 플레이어들을 들여보내 맵 위 10 x 10 격자(14 m 간격)에 흩어 놓는다.
    // 입력: match - 경기, players - 들여보낼 인원.
    // 출력: 연결 id 목록 1..players. 입장이 거절되면 테스트가 실패한다.
    private static List<int> Join(Match match, int players)
    {
        var peers = new List<int>();
        for (int i = 1; i <= players; i++)
        {
            Assert.Equal(JoinResult.Ok, match.TryJoin(i, "s" + i));
            match.TryGetPlayer(i, out PlayerEntity p);
            // Spread over the map on a 10 x 10 grid, 14 m apart.
            float x = -63f + 14f * ((i - 1) % 10);
            float z = -63f + 14f * ((i - 1) / 10);
            p.State.Position = new Vector3(x, GameMap.Terrain.Height(x, z), z);
            p.History.Reset(match.ServerTick, p.State.Position);
            peers.Add(i);
        }
        return peers;
    }

    // 기능: 한 Tick 분의 입력을 모든 플레이어에게 넣는다: 모두 걷고, 짝수 peer는 쏘며, 홀수 peer는 건설 모드로 주기마다 요청을 보낸다.
    // 입력: match - 경기, peers - 연결 id 목록, tick - 현재 Tick 번호, buildEveryTicks - 건설 요청 주기(0이면 아무도 짓지 않음),
    //   rng - 조각 종류·층·회전 난수, seq - peer별 입력 순번, buildSeq - peer별 건설 순번.
    // 출력: 반환값 없음. 입력·건설 요청이 큐에 쌓이고 순번 배열이 올라간다.
    // Every player walks in a slowly turning direction. Even peers shoot (Fire held, their reserve topped up so they keep
    // shooting); odd peers stay in build mode and, every buildEveryTicks, send one request in their own cell at level 0-3
    // (a wall, a floor or a ramp, any rotation; many are refused, which is part of the load).
    private static void Drive(Match match, List<int> peers, int tick, int buildEveryTicks, Random rng, uint[] seq, ushort[] buildSeq)
    {
        foreach (int peer in peers)
        {
            match.TryGetPlayer(peer, out PlayerEntity p);
            float yaw = (peer * 37 + tick * 2) % 360;
            bool builder = buildEveryTicks > 0 && peer % 2 == 1;
            var input = new InputCommand
            {
                Seq = ++seq[peer], MoveY = 1f, Yaw = yaw, AimYaw = yaw, AimPitch = builder ? 30f : 5f, ViewTick = match.ServerTick,
                Buttons = builder ? (p.Inventory.Tool == ToolKind.Build ? InputButtons.None : InputButtons.ToolBuild) : InputButtons.Fire,
            };
            if (!builder && tick % 30 == 0) p.Inventory.SetAmmo(AmmoType.Medium, TestGameData.LoadoutMediumAmmo);
            var packet = new PlayerInputPacket { Count = 1 };
            packet.Set(0, input);
            match.EnqueueInput(peer, packet);
            if (builder && p.Inventory.Tool == ToolKind.Build && (tick + peer) % buildEveryTicks == 0)
            {
                int x = Math.Clamp(BuildGrid.CellX(p.State.Position.X), 0, 31);
                int z = Math.Clamp(BuildGrid.CellZ(p.State.Position.Z), 0, 31);
                int kind = rng.Next(3);
                match.EnqueueBuild(peer, new BuildRequest
                {
                    Sequence = ++buildSeq[peer], Piece = (byte)(kind == 0 ? BuildPieceType.Wall : kind == 1 ? BuildPieceType.Floor : BuildPieceType.Ramp),
                    X = (byte)x, Y = (byte)rng.Next(4), Z = (byte)z, Rotation = (byte)rng.Next(4),
                });
            }
        }
    }

    // 기능: 30 Tick 예열 뒤 주어진 Tick 수를 돌리며 Tick 시간·할당·GC·채널별 패킷·건설 요청 수를 잰다.
    // 입력: match - 경기, peers - 연결 id 목록, ticks - 측정 Tick 수, buildEveryTicks - 건설 요청 주기.
    // 출력: 측정값을 한 줄로 적은 문자열. 채널 계수기는 0으로 되돌아간 뒤 다시 센 값이 된다.
    private string Measure(Match match, List<int> peers, int ticks, int buildEveryTicks)
    {
        var rng = new Random(13);
        var seq = new uint[peers.Count + 2];
        var buildSeq = new ushort[peers.Count + 2];
        for (int t = 0; t < 30; t++)
        {
            Drive(match, peers, t, buildEveryTicks, rng, seq, buildSeq);
            match.Tick();
        }
        _packets = _bytes = _buildPackets = _buildBytes = 0;
        var samples = new double[ticks];
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        long accepted = match.BuildResults(BuildResultCode.Ok);
        long requests = match.BuildCounts().Requests;
        var clock = new Stopwatch();
        for (int t = 0; t < ticks; t++)
        {
            Drive(match, peers, 30 + t, buildEveryTicks, rng, seq, buildSeq);
            clock.Restart();
            match.Tick();
            samples[t] = clock.Elapsed.TotalMilliseconds;
        }
        double seconds = ticks / (double)SimHz;
        Array.Sort(samples);
        double P(double q) => samples[Math.Min(samples.Length - 1, (int)(q * samples.Length))];
        return $"tickMs p50={P(0.50):F3} p95={P(0.95):F3} p99={P(0.99):F3} max={samples[^1]:F3} " +
               $"alloc/tick={(GC.GetTotalAllocatedBytes(precise: true) - allocated) / ticks:F0}B gc={GC.CollectionCount(0) - gc0}/{GC.CollectionCount(1) - gc1}/{GC.CollectionCount(2) - gc2} " +
               $"ch0 pkt/s={_packets / seconds:F0} bytes/s={_bytes / seconds:F0} ch1 pkt/s={_buildPackets / seconds:F0} bytes/s={_buildBytes / seconds:F0} " +
               $"buildReq/s={(match.BuildCounts().Requests - requests) / seconds:F1} accepted/s={(match.BuildResults(BuildResultCode.Ok) - accepted) / seconds:F1} " +
               $"pieces={match.BuildPieces} workingSetMB={Environment.WorkingSet / 1048576.0:F0}";
    }

    [BuildStressFact]
    public void PieceCountSteps_With50PlayersBuilding()
    {
        foreach (int count in new[] { 0, 1000, 5000, 10000, 20000 })
        {
            GC.Collect();
            long before = GC.GetTotalMemory(forceFullCollection: true);
            Match match = NewMatch(50);
            long empty = GC.GetTotalMemory(forceFullCollection: true);
            int added = Fill(match, Math.Min(count, 19_000));   // room left for the players' own pieces
            long filled = GC.GetTotalMemory(forceFullCollection: true);
            List<int> peers = Join(match, 50);
            string line = Measure(match, peers, 600, count == 0 ? 0 : 10);
            _out.WriteLine($"pieces={added,5} matchMB={(empty - before) / 1048576.0:F1} bytes/piece={(added > 0 ? (filled - empty) / added : 0)} {line}");
            GC.KeepAlive(match);
        }
    }

    [BuildStressFact]
    public void Turbo_And100Players()
    {
        foreach ((int players, int every) in new[] { (50, 3), (100, 6) })
        {
            Match match = NewMatch(players);
            Fill(match, 2000);
            List<int> peers = Join(match, players);
            _out.WriteLine($"players={players} buildEvery={every}ticks {Measure(match, peers, 600, every)}");
        }
    }

    [BuildStressFact]
    public void AMassCollapse()
    {
        Match match = NewMatch(50);
        List<int> peers = Join(match, 50);
        var foundations = new List<uint>();
        for (int x = 10; x <= 21; x++) foundations.Add(SandboxHarness.AddPiece(match, new BuildPieceShape(BuildPieceType.Wall, x, 0, 10, 0)));
        for (int level = 1; level <= 15; level++)
        {
            for (int z = 10; z <= 21; z++)
                for (int x = 10; x <= 21; x++) SandboxHarness.AddPiece(match, new BuildPieceShape(BuildPieceType.Floor, x, level, z, 0));
            if (level < 15)
                for (int x = 10; x <= 21; x++) SandboxHarness.AddPiece(match, new BuildPieceShape(BuildPieceType.Wall, x, level, 10, 0));
        }
        for (int i = 0; i < foundations.Count - 1; i++) match.DestroyPiece(foundations[i]);
        for (int t = 0; t < 30; t++) match.Tick();
        int pieces = match.BuildPieces;
        _buildPackets = _buildBytes = 0;
        var clock = Stopwatch.StartNew();
        match.DestroyPiece(foundations[^1]);
        match.Tick();
        clock.Stop();
        _out.WriteLine($"collapse pieces={pieces} tickMs={clock.Elapsed.TotalMilliseconds:F2} ch1 packets={_buildPackets} bytes={_buildBytes} " +
                       $"(to {peers.Count} players) left={match.BuildPieces}");
        Assert.Equal(0, match.BuildPieces);
    }
}
