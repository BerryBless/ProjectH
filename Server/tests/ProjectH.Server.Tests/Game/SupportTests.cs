using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Build;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 13 D12 (request §177): connections, grounding, placement support and collapse. Plaza cell 16 is x 0..5, z 0..5 on
// flat terrain at 0; level 1 is 3 m up, out of the ground's reach.
public class SupportTests
{
    private const int C = 16;
    private readonly SandboxHarness _h = new();

    // 기능: 격자 좌표의 조각 모양을 정규화해 만든다. 유효하지 않으면 테스트를 실패시킨다.
    // 입력: type - 조각 종류, x·y·z - 격자 칸, r - 회전.
    // 출력: 정규화된 BuildPieceShape.
    private static BuildPieceShape S(BuildPieceType type, int x, int y, int z, int r = 0)
    {
        Assert.True(BuildGrid.TryNormalize(type, x, y, z, r, out BuildPieceShape s));
        return s;
    }

    // 기능: 두 조각이 지지 간선을 하나라도 공유하는지 본다.
    // 입력: a - 첫 조각, b - 둘째 조각.
    // 출력: 공유하는 간선이 있으면 true.
    private static bool Connected(BuildPieceShape a, BuildPieceShape b)
    {
        Span<uint> ka = stackalloc uint[BuildSupport.MaxEdges];
        Span<uint> kb = stackalloc uint[BuildSupport.MaxEdges];
        int na = BuildSupport.Edges(a, ka);
        int nb = BuildSupport.Edges(b, kb);
        for (int i = 0; i < na; i++)
            for (int j = 0; j < nb; j++)
                if (ka[i] == kb[j]) return true;
        return false;
    }

    // ---- The connection table ----

    [Fact]
    public void Connections_FollowSharedEdges()
    {
        BuildPieceShape wall = S(BuildPieceType.Wall, C, 0, C, 0);
        Assert.True(Connected(wall, S(BuildPieceType.Floor, C, 1, C)));        // a floor on its top edge
        Assert.True(Connected(wall, S(BuildPieceType.Floor, C, 0, C)));        // the floor it stands on
        Assert.True(Connected(wall, S(BuildPieceType.Floor, C, 0, 15)));       // and the one on its other side
        Assert.True(Connected(wall, S(BuildPieceType.Wall, C, 1, C, 0)));      // stacked
        Assert.True(Connected(wall, S(BuildPieceType.Wall, 17, 0, C, 0)));     // end to end
        Assert.True(Connected(wall, S(BuildPieceType.Wall, C, 0, C, 1)));      // a corner
        Assert.True(Connected(wall, S(BuildPieceType.Roof, C, 0, C)));         // the roof on its top
        Assert.True(Connected(wall, S(BuildPieceType.Ramp, C, 1, C, 0)));      // a ramp starting on its top
        Assert.True(Connected(wall, S(BuildPieceType.Ramp, C, 0, 15, 0)));     // a ramp ending at its top
        Assert.True(Connected(S(BuildPieceType.Wall, C, 0, C, 1), S(BuildPieceType.Ramp, C, 0, C, 0)));   // a ramp's side on a wall
        Assert.True(Connected(S(BuildPieceType.Ramp, C, 0, C, 0), S(BuildPieceType.Ramp, C, 1, 17, 0)));  // a ramp chain
        Assert.True(Connected(S(BuildPieceType.Ramp, C, 0, C, 0), S(BuildPieceType.Ramp, 17, 0, C, 0)));  // ramps side by side
        Assert.True(Connected(S(BuildPieceType.Ramp, C, 0, C, 0), S(BuildPieceType.Floor, C, 1, 17)));    // ramp top to a floor
        Assert.True(Connected(S(BuildPieceType.Roof, C, 0, C), S(BuildPieceType.Floor, 17, 1, C)));       // eaves meet a floor
        Assert.False(Connected(S(BuildPieceType.Floor, C, 1, C), S(BuildPieceType.Floor, 17, 1, 17)));    // a corner point only
        Assert.False(Connected(wall, S(BuildPieceType.Wall, C, 2, C, 0)));
        Assert.True(Connected(S(BuildPieceType.Wall, C, 0, C, 1), S(BuildPieceType.Ramp, C, 0, C, 1)));   // a ramp starting at its foot
        Assert.True(Connected(S(BuildPieceType.Wall, C, 1, C, 1), S(BuildPieceType.Ramp, C, 0, C, 3)));   // a ramp ending at its foot
        Assert.False(Connected(S(BuildPieceType.Wall, C, 2, C, 1), S(BuildPieceType.Ramp, C, 0, C, 3)));  // a level apart
    }

    [Fact]
    public void Grounded_IsTheTerrainOrAMapBoxTop_NotTheAir()
    {
        Assert.True(BuildSupport.IsGrounded(S(BuildPieceType.Wall, C, 0, C), GameMap.Terrain, GameMap.Boxes));
        Assert.True(BuildSupport.IsGrounded(S(BuildPieceType.Ramp, C, 0, C, 2), GameMap.Terrain, GameMap.Boxes));
        Assert.False(BuildSupport.IsGrounded(S(BuildPieceType.Floor, C, 1, C), GameMap.Terrain, GameMap.Boxes));
        Assert.False(BuildSupport.IsGrounded(S(BuildPieceType.Roof, C, 0, C), GameMap.Terrain, GameMap.Boxes));
        // A level 1 floor on a Rustvale house roof (top 3.25, within 0.5 of 3).
        Assert.True(BuildSupport.IsGrounded(S(BuildPieceType.Floor, 5, 1, 26), GameMap.Terrain, GameMap.Boxes));
        // A floor's level-0 foot under terrain (a slope rising over it) also stands.
        Assert.True(BuildSupport.IsGrounded(S(BuildPieceType.Floor, 24, 0, 7), GameMap.Terrain, GameMap.Boxes));
    }

    // ---- Placement through requests ----

    // 기능: Peer 1을 칸 16 남쪽에 참가시켜 건설 도구를 들게 하고 나무 200을 준다.
    // 입력: 없음.
    // 출력: 건설 준비가 된 PlayerEntity.
    private PlayerEntity Builder()
    {
        PlayerEntity p = _h.Join(1, new Vector3(2.5f, 0f, -3f));
        _h.Press(p, InputButtons.ToolBuild);
        p.Inventory.SetResource(BuildMaterialType.Wood, 200);
        return p;
    }

    private ushort _seq;

    // 기능: 조각 중심을 겨냥한 채 다음 Sequence의 건설 요청을 Peer 1로 넣고 처리·최소 건설 간격만큼 Tick을 돌린 뒤 마지막 BuildResult를 읽는다. 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: p - 건설자(Peer 1), s - 놓을 조각 모양.
    // 출력: 서버가 보낸 BuildResult 코드.
    private BuildResultCode Place(PlayerEntity p, BuildPieceShape s)
    {
        Vector3 at = BuildGrid.CenterOf(s);
        _h.Act(p, InputButtons.None, at);
        _h.Match.EnqueueBuild(1, new BuildRequest
        {
            Sequence = ++_seq, Piece = (byte)s.Type, Material = 0, X = s.X, Y = s.Y, Z = s.Z, Rotation = s.Rotation,
        });
        _h.Act(p, InputButtons.None, at);
        _h.Ticks(_h.Match.Building.MinBuildIntervalTicks);
        PacketReader r = SandboxHarness.Body(_h.To(1, PacketId.BuildResult).Last());
        Assert.True(BuildResult.TryRead(ref r, out BuildResult result));
        return result.Code;
    }

    [Fact]
    public void AGroundWall_ThenAFloor_ThenARamp_AreSupported_ButAFloorInTheAirIsNot()
    {
        PlayerEntity p = Builder();
        Assert.Equal(BuildResultCode.Unsupported, Place(p, S(BuildPieceType.Floor, C, 1, C)));
        Assert.Equal(200, p.Inventory.Resource(BuildMaterialType.Wood));
        Assert.Equal(BuildResultCode.Ok, Place(p, S(BuildPieceType.Wall, C, 0, C)));
        Assert.Equal(BuildResultCode.Ok, Place(p, S(BuildPieceType.Floor, C, 1, C)));
        Assert.Equal(BuildResultCode.Ok, Place(p, S(BuildPieceType.Ramp, C, 1, C, 0)));
        Assert.Equal(3, _h.Match.BuildPieces);
        Assert.Equal(170, p.Inventory.Resource(BuildMaterialType.Wood));
    }

    // ---- Collapse ----

    // 기능: Peer 1에게 보낸(기록된) BuildEvents 패킷의 파괴 기록을 보낸 순서대로 모은다. 헤더나 기록 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: 없음.
    // 출력: 파괴된 조각 ID 목록.
    private List<uint> DestroyedThisTick()
    {
        var ids = new List<uint>();
        foreach (SandboxHarness.Sent sent in _h.To(1, PacketId.BuildEvents))
        {
            PacketReader r = SandboxHarness.Body(sent);
            Assert.True(BuildEventsPacket.TryReadHeader(ref r, out _, out int placed, out int edited, out int health, out int destroyed));
            for (int i = 0; i < placed; i++) BuildPieceRecord.TryReadPlaced(ref r, out _);
            for (int i = 0; i < edited; i++) BuildEventsPacket.TryReadEdited(ref r, out _, out _);
            for (int i = 0; i < health; i++) BuildEventsPacket.TryReadHealth(ref r, out _, out _);
            for (int i = 0; i < destroyed; i++)
            {
                Assert.True(BuildEventsPacket.TryReadDestroyed(ref r, out uint id));
                ids.Add(id);
            }
        }
        return ids;
    }

    [Fact]
    public void DestroyingTheFoundation_CollapsesWhatItHeld_InOneTick()
    {
        _h.Join(1, new Vector3(-6f, 0f, -6f));
        uint wall = _h.AddPiece(S(BuildPieceType.Wall, C, 0, C));
        uint floor = _h.AddPiece(S(BuildPieceType.Floor, C, 1, C));
        uint ramp = _h.AddPiece(S(BuildPieceType.Ramp, C, 1, C, 0));
        uint other = _h.AddPiece(S(BuildPieceType.Wall, 10, 0, 10));   // not connected: stays
        _h.Clear();
        _h.Match.DestroyPiece(wall);
        _h.Match.Tick();
        Assert.Equal(new[] { wall, floor, ramp }.OrderBy(i => i), DestroyedThisTick().OrderBy(i => i));
        Assert.Equal(1, _h.Match.BuildPieces);
        Assert.True(_h.Match.Build.Contains(other));
        Assert.Equal(2, _h.Match.PiecesCollapsed);
    }

    [Fact]
    public void AFloorHeldByTwoWalls_StaysWhenOneGoes_AndFallsWithTheSecond()
    {
        uint south = _h.AddPiece(S(BuildPieceType.Wall, C, 0, C, 0));
        uint north = _h.AddPiece(S(BuildPieceType.Wall, C, 0, C, 2));
        uint floor = _h.AddPiece(S(BuildPieceType.Floor, C, 1, C));
        _h.Match.DestroyPiece(south);
        Assert.True(_h.Match.Build.Contains(floor));
        _h.Match.DestroyPiece(north);
        Assert.False(_h.Match.Build.Contains(floor));
    }

    [Fact]
    public void ABridge_PartlySupported_KeepsWhatAWallStillHolds()
    {
        // Walls on x = 0 (cell 16's west edge) and x = 15 (cell 19's west edge); floors over cells 16, 17, 18 at level 1.
        uint west = _h.AddPiece(S(BuildPieceType.Wall, 16, 0, C, 1));
        _h.AddPiece(S(BuildPieceType.Wall, 19, 0, C, 1));
        uint a = _h.AddPiece(S(BuildPieceType.Floor, 16, 1, C));
        uint b = _h.AddPiece(S(BuildPieceType.Floor, 17, 1, C));
        uint c = _h.AddPiece(S(BuildPieceType.Floor, 18, 1, C));
        _h.Match.DestroyPiece(b);
        Assert.True(_h.Match.Build.Contains(a) && _h.Match.Build.Contains(c));
        _h.Match.DestroyPiece(west);
        Assert.False(_h.Match.Build.Contains(a));
        Assert.True(_h.Match.Build.Contains(c));
        Assert.False(_h.Match.Build.Contains(west));
    }

    // Request §177 "Large Connected Component", §126: a tower of about 2,300 pieces over 144 cells and 15 levels, standing
    // on one row of walls. Taking the walls away collapses all of it in one tick, through shared edges only.
    [Fact]
    public void ALargeTower_CollapsesAtOnce_WhenItsLastFoundationGoes()
    {
        _h.Join(1, new Vector3(-6f, 0f, -6f));
        var foundations = new List<uint>();
        for (int x = 10; x <= 21; x++) foundations.Add(_h.AddPiece(S(BuildPieceType.Wall, x, 0, 10, 0)));
        for (int level = 1; level <= 15; level++)
        {
            for (int z = 10; z <= 21; z++)
                for (int x = 10; x <= 21; x++) _h.AddPiece(S(BuildPieceType.Floor, x, level, z));
            if (level < 15)
                for (int x = 10; x <= 21; x++) _h.AddPiece(S(BuildPieceType.Wall, x, level, 10, 0));
        }
        int total = _h.Match.BuildPieces;
        Assert.True(total > 2300, $"{total} pieces");
        for (int i = 0; i < foundations.Count - 1; i++) _h.Match.DestroyPiece(foundations[i]);
        Assert.Equal(total - foundations.Count + 1, _h.Match.BuildPieces);
        _h.Match.Tick();
        _h.Clear();
        var clock = Stopwatch.StartNew();
        _h.Match.DestroyPiece(foundations[^1]);
        _h.Match.Tick();
        clock.Stop();
        Assert.Equal(0, _h.Match.BuildPieces);
        Assert.Equal(total - foundations.Count + 1, DestroyedThisTick().Count);
        Assert.All(_h.To(1, PacketId.BuildEvents), s => Assert.True(s.Data.Length <= ProtocolConstants.MaxPacketSize));
        Assert.True(clock.ElapsedMilliseconds < 200, $"{clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ARoundReset_ForgetsTheSupportToo()
    {
        var h = new RoyaleHarness();
        h.Join(1);
        h.Join(2);
        SandboxHarness.AddPiece(h.Match, S(BuildPieceType.Wall, C, 0, C));
        h.RunToMatch();
        Assert.False(h.Match.Support.HasNeighbour(S(BuildPieceType.Floor, C, 1, C)));
        Assert.Equal(0, h.Match.BuildPieces);
    }

    // ---- Final review A2: one support search per tick ----

    // 기능: 칸 (16, 16) 서쪽 가장자리에 벽 8개를 쌓고 그 위 8단에 x 16..31, z 0..31의 바닥 512개를 바로 넣는다.
    // 입력: match - 대상 Match.
    // 출력: 벽 ID 목록(아래부터)과 바닥 ID 목록.
    // A grounded stack of eight walls on cell (16, 16)'s west edge holding a level 8 deck of floors over cells x 16..31,
    // z 0..31 (512 floors, far above the terrain). Returns the walls (bottom first) and the floors.
    private static (List<uint> Walls, List<uint> Floors) Deck(Match match)
    {
        var walls = new List<uint>();
        var floors = new List<uint>();
        for (int level = 0; level < 8; level++) walls.Add(SandboxHarness.AddPiece(match, S(BuildPieceType.Wall, 16, level, 16, 1)));
        for (int z = 0; z < 32; z++)
            for (int x = 16; x < 32; x++) floors.Add(SandboxHarness.AddPiece(match, S(BuildPieceType.Floor, x, 8, z)));
        return (walls, floors);
    }

    // 기능: Deck의 바닥 중 벽에서 먼 쪽(x 24..31, 홀수 z)에서 서로 붙지 않는 바닥 50개를 고른다.
    // 입력: floors - Deck이 돌려준 바닥 ID 목록.
    // 출력: 고른 바닥 ID 50개.
    // 50 floors far from the walls, never side by side (the deck stays in one piece).
    private static List<uint> FarHoles(List<uint> floors)
    {
        var holes = new List<uint>();
        for (int z = 1; z < 32 && holes.Count < 50; z += 2)
            for (int x = 24; x < 32 && holes.Count < 50; x += 2) holes.Add(floors[z * 16 + (x - 16)]);
        return holes;
    }

    // 기능: 조각 격자에 남아 있는 모든 조각 ID를 모아 정렬한다.
    // 입력: match - 대상 Match.
    // 출력: 오름차순 조각 ID 목록.
    private static List<uint> Standing(Match match)
    {
        var ids = new List<uint>();
        PieceGrid grid = match.Build.Grid;
        for (int z = 0; z < BuildGrid.CellsZ; z++)
            for (int x = 0; x < BuildGrid.CellsX; x++)
                for (int slot = grid.First(x, z); slot >= 0; slot = grid.Next(slot)) ids.Add(grid.IdAt(slot));
        ids.Sort();
        return ids;
    }

    [Fact]
    public void ManyDestroysInOneTick_LeaveTheSameAsOneAtATime()
    {
        var one = new SandboxHarness();
        var batch = new SandboxHarness();
        var (walls, floors) = Deck(one.Match);
        var (walls2, floors2) = Deck(batch.Match);
        Assert.Equal(floors, floors2);   // same ids in both
        Assert.All(floors, id =>
        {
            one.Match.Build.TryGetSlot(id, out int slot);
            Assert.False(one.Match.Build.At(slot).Grounded);
        });
        // Holes that leave the deck standing, then the fourth wall of the stack (everything above it falls), then a hole
        // in what already fell.
        var destroy = FarHoles(floors);
        destroy.Add(walls[3]);
        destroy.Add(floors[0]);
        foreach (uint id in destroy) one.Match.DestroyPiece(id);
        batch.Match.DestroyPieces(destroy.ToArray());
        Assert.Equal(Standing(one.Match), Standing(batch.Match));
        Assert.Equal(new[] { walls[0], walls[1], walls[2] }, Standing(batch.Match));
        Assert.Equal(one.Match.PiecesCollapsed, batch.Match.PiecesCollapsed + 1);   // one at a time, floors[0] had fallen before its destroy
        Assert.Equal(one.Match.PiecesDestroyed, batch.Match.PiecesDestroyed);
    }

    [Fact]
    public void ManyDestroysThatCollapseNothing_ShareOneSearch()
    {
        var one = new SandboxHarness();
        var batch = new SandboxHarness();
        var (_, floors) = Deck(one.Match);
        Deck(batch.Match);
        List<uint> holes = FarHoles(floors);
        int separately = 0;
        foreach (uint id in holes)
        {
            one.Match.DestroyPiece(id);
            separately += one.Match.Support.LastVisited;
        }
        var clock = Stopwatch.StartNew();
        batch.Match.DestroyPieces(holes.ToArray());
        clock.Stop();
        int together = batch.Match.Support.LastVisited;
        Assert.Equal(Standing(one.Match), Standing(batch.Match));
        Assert.Equal(0, batch.Match.PiecesCollapsed);
        // One search reaches the walls through the deck once; every later start stops at a floor it already reached.
        Assert.True(together <= floors.Count + 8, $"{together} nodes visited together");
        Assert.True(separately > 10 * together, $"{separately} nodes one at a time, {together} together");
        if (Environment.GetEnvironmentVariable(BuildStressFactAttribute.Variable) == "1")
            Assert.True(clock.ElapsedMilliseconds < 5, $"{clock.ElapsedMilliseconds} ms");
    }

    // The batch runs at the end of the tick: pieces destroyed by shots during the tick collapse what they held in the same
    // tick's events.
    [Fact]
    public void ADestroyInsideATick_CollapsesBeforeTheEventsGoOut()
    {
        PlayerEntity p = _h.Join(1, new Vector3(-6f, 0f, -6f));
        uint wall = _h.AddPiece(S(BuildPieceType.Wall, C, 0, C));
        uint floor = _h.AddPiece(S(BuildPieceType.Floor, C, 1, C));
        _h.Clear();
        _h.Match.Build.TryGetSlot(wall, out int slot);
        _h.Match.Build.At(slot).Damage = ushort.MaxValue - 1;   // one more hit destroys it
        _h.Act(p, InputButtons.Fire, BuildGrid.CenterOf(S(BuildPieceType.Wall, C, 0, C)));
        Assert.False(_h.Match.Build.Contains(wall));
        Assert.False(_h.Match.Build.Contains(floor));
        Assert.Equal(new[] { wall, floor }.OrderBy(i => i), DestroyedThisTick().OrderBy(i => i));
        Assert.Equal(0, _h.Match.Support.QueuedStarts);
    }
}
