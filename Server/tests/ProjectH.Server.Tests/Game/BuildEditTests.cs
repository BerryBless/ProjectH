using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Build;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 13.5 D4-D9: editing pieces in the dev sandbox. The editor stands in the plaza at (2.5, 0, -3), facing north to
// its own wall on the south edge of cell 16 (x 0..5 at z = 0, centre (2.5, 1.5, 0)).
public class BuildEditTests
{
    private const int C = 16;
    private const int Door = (1 << 1) | (1 << 4);
    private const int TallOpening = (1 << 1) | (1 << 4) | (1 << 7);
    private const int Window = 1 << 4;
    private static readonly Vector3 Editor = new(2.5f, 0f, -3f);
    private static readonly BuildPieceShape SouthWall = new(BuildPieceType.Wall, C, 0, C, 0);

    private readonly SandboxHarness _h = new();
    private ushort _seq;

    // 기능: 건설 간격(MinBuildInterval)만큼 Tick을 보낸 뒤 조준 Tick 한 번, 편집 요청을 넣고 처리하는 Tick을 돌린다(BuildPlacementTests.Build와 같은 순서).
    // 입력: p - 편집자, id - 대상 조각, state - 새 상태, aimAt - 조준점(기본 조각 중심), sequence - 순번(기본 다음 순번),
    //   beforeTick - 처리 Tick 직전에 할 일.
    // 출력: 그 플레이어가 받은 마지막 BuildResult.
    private BuildResult Edit(PlayerEntity p, uint id, ushort state, Vector3? aimAt = null, ushort? sequence = null, Action? beforeTick = null)
    {
        Vector3 at = aimAt ?? CentreOf(id);
        // Past the previous edit's interval (MinBuildInterval), so this one is processed in its tick.
        _h.Ticks(_h.Match.Building.MinBuildIntervalTicks);
        _h.Act(p, InputButtons.None, at);
        _h.Match.EnqueueEdit(p.PeerId, new BuildEditRequest { Sequence = sequence ?? ++_seq, PieceId = id, State = state });
        beforeTick?.Invoke();
        _h.Act(p, InputButtons.None, at);
        return LastResult(p);
    }

    // 기능: 조각의 경계 상자 중심을 구한다(조준점으로 쓴다).
    // 입력: id - 조각 id.
    // 출력: 조각이 있으면 그 경계 상자 중심, 없으면 남쪽 벽 중심 (2.5, 1.5, 0).
    private Vector3 CentreOf(uint id) =>
        _h.Match.Build.TryGetSlot(id, out int slot) ? BuildGrid.BoundsOf(_h.Match.Build.At(slot).Shape).Center : new Vector3(2.5f, 1.5f, 0f);

    // 기능: 플레이어가 받은 마지막 BuildResult 패킷을 읽는다.
    // 입력: p - 받은 플레이어.
    // 출력: 마지막 BuildResult 내용. 하나도 없거나 읽기에 실패하면 테스트가 실패한다.
    private BuildResult LastResult(PlayerEntity p)
    {
        PacketReader r = SandboxHarness.Body(_h.To(p.PeerId, PacketId.BuildResult).Last());
        Assert.True(BuildResult.TryRead(ref r, out BuildResult result));
        return result;
    }

    // 기능: 경기의 건설 저장소에서 조각을 찾는다.
    // 입력: id - 조각 id.
    // 출력: 그 조각의 현재 상태. 없으면 테스트가 실패한다.
    private BuildPiece PieceOf(uint id)
    {
        Assert.True(_h.Match.Build.TryGetSlot(id, out int slot));
        return _h.Match.Build.At(slot);
    }

    // 기능: 한 Client가 받은 BuildEvents 기록을 종류별로 모은다(패킷 순서대로).
    // 입력: peer - 받는 연결 id.
    // 출력: Edited·Health·Destroyed 기록 목록과 Placed 수.
    private (List<(uint Id, ushort State)> Edited, List<(uint Id, ushort Damage)> Health, List<uint> Destroyed, int Placed) EventsTo(int peer)
    {
        var edited = new List<(uint, ushort)>();
        var health = new List<(uint, ushort)>();
        var destroyed = new List<uint>();
        int placedCount = 0;
        foreach (SandboxHarness.Sent sent in _h.To(peer, PacketId.BuildEvents))
        {
            PacketReader r = SandboxHarness.Body(sent);
            Assert.True(BuildEventsPacket.TryReadHeader(ref r, out _, out int placed, out int e, out int h, out int d));
            placedCount += placed;
            for (int i = 0; i < placed; i++) Assert.True(BuildPieceRecord.TryReadPlaced(ref r, out _));
            for (int i = 0; i < e; i++)
            {
                Assert.True(BuildEventsPacket.TryReadEdited(ref r, out uint id, out ushort state));
                edited.Add((id, state));
            }
            for (int i = 0; i < h; i++)
            {
                Assert.True(BuildEventsPacket.TryReadHealth(ref r, out uint id, out ushort damage));
                health.Add((id, damage));
            }
            for (int i = 0; i < d; i++)
            {
                Assert.True(BuildEventsPacket.TryReadDestroyed(ref r, out uint id));
                destroyed.Add(id);
            }
        }
        return (edited, health, destroyed, placedCount);
    }

    // 기능: 편집자를 들여보내고 그가 소유한 남쪽 벽 하나를 세운다.
    // 입력: material - 벽 재료, created - CreatedTick.
    // 출력: 편집자와 벽 id.
    private (PlayerEntity p, uint wall) OwnWall(BuildMaterialType material = BuildMaterialType.Stone, uint created = 0)
    {
        PlayerEntity p = _h.Join(1, Editor);
        uint wall = _h.AddPiece(SouthWall, material, created, p.EntityId);
        return (p, wall);
    }

    // ---- Accepted edits ----

    [Fact]
    public void AnEdit_ChangesOnlyTheShape_AndIsAnnouncedOnce()
    {
        var (p, wall) = OwnWall(created: 5);
        _h.Join(2, new Vector3(-6f, 0f, -6f));
        _h.Match.Build.At(_h.Match.Build.Grid.SlotOf(wall)).Damage = 30;
        BuildPiece before = PieceOf(wall);
        _h.Clear();

        BuildResult result = Edit(p, wall, BuildEdit.PackState(Door, 0));
        Assert.Equal(BuildResultCode.Ok, result.Code);
        Assert.Equal(wall, result.PieceId);
        BuildPiece after = PieceOf(wall);
        Assert.Equal(Door, after.Shape.Edit);
        Assert.Equal((before.Id, before.Owner, before.Material, before.CreatedTick, before.Damage, before.Grounded),
            (after.Id, after.Owner, after.Material, after.CreatedTick, after.Damage, after.Grounded));
        Assert.Equal(before.Shape.WithEdit(Door, 0), _h.Match.Build.Grid.ShapeAt(_h.Match.Build.Grid.SlotOf(wall)));
        Assert.Equal(1, _h.Match.BuildEdits);
        Assert.Equal(1, _h.Match.BuildPieces);
        foreach (int peer in new[] { 1, 2 })
        {
            var events = EventsTo(peer);
            Assert.Equal((wall, BuildEdit.PackState(Door, 0)), Assert.Single(events.Edited));
            Assert.Equal(0, events.Placed);
        }
    }

    // §29: no tool is needed: an editor holding a weapon edits (and keeps the weapon).
    [Fact]
    public void AnEditor_NeedsNoBuildTool()
    {
        var (p, wall) = OwnWall();
        Assert.NotEqual(ToolKind.Build, p.Inventory.Tool);
        Assert.Equal(BuildResultCode.Ok, Edit(p, wall, BuildEdit.PackState(Window, 0)).Code);
        Assert.NotEqual(ToolKind.Build, p.Inventory.Tool);
    }

    [Fact]
    public void ConfirmingTheSameState_IsOk_AndChangesNothing()
    {
        var (p, wall) = OwnWall();
        Assert.Equal(BuildResultCode.Ok, Edit(p, wall, BuildEdit.PackState(Door, 0)).Code);
        _h.Clear();
        for (int i = 0; i < 3; i++)
        {
            BuildResult again = Edit(p, wall, BuildEdit.PackState(Door, 0));
            Assert.Equal((BuildResultCode.Ok, wall), (again.Code, again.PieceId));
        }
        Assert.Empty(EventsTo(1).Edited);
        Assert.Equal(1, _h.Match.BuildEdits);
        // Reset is the same request with Edit 0.
        Assert.Equal(BuildResultCode.Ok, Edit(p, wall, BuildEdit.PackState(0, 0)).Code);
        Assert.Equal(SouthWall, PieceOf(wall).Shape);
        Assert.Equal((wall, BuildEdit.PackState(0, 0)), Assert.Single(EventsTo(1).Edited));
    }

    [Fact]
    public void APieceUnderConstruction_CanBeEdited_AndKeepsGrowing()
    {
        PlayerEntity p = _h.Join(1, Editor);
        _h.Ticks(2);
        uint wall = _h.AddPiece(SouthWall, BuildMaterialType.Metal, _h.Match.ServerTick, p.EntityId);
        uint later = _h.Match.ServerTick + 20;
        int healthBefore = _h.Match.Build.Health(PieceOf(wall), later);
        Assert.Equal(BuildResultCode.Ok, Edit(p, wall, BuildEdit.PackState(Door, 0)).Code);
        Assert.Equal(healthBefore, _h.Match.Build.Health(PieceOf(wall), later));
    }

    // ---- Refusals, in the D5 order ----

    [Fact]
    public void ARefusal_SaysWhy_CarriesIdZero_AndChangesNothing()
    {
        var (p, wall) = OwnWall();
        uint others = _h.AddPiece(new BuildPieceShape(BuildPieceType.Wall, C, 0, C, 1), owner: 99);
        uint spawned = _h.AddPiece(new BuildPieceShape(BuildPieceType.Floor, C, 0, C, 0), owner: 0);

        Assert.Equal(BuildResultCode.NotFound, Edit(p, 9999, BuildEdit.PackState(Door, 0), new Vector3(2.5f, 1.5f, 0f)).Code);
        Assert.Equal(BuildResultCode.NotFound, Edit(p, 0, BuildEdit.PackState(Door, 0), new Vector3(2.5f, 1.5f, 0f)).Code);
        Assert.Equal(BuildResultCode.NotOwner, Edit(p, others, BuildEdit.PackState(Door, 1)).Code);
        Assert.Equal(BuildResultCode.NotOwner, Edit(p, spawned, BuildEdit.PackState(1, 0)).Code);
        Assert.Equal(BuildResultCode.InvalidRequest, Edit(p, wall, BuildEdit.PackState(Door, 1)).Code);         // a wall's rotation
        Assert.Equal(BuildResultCode.InvalidRequest, Edit(p, wall, BuildEdit.PackState(0b101, 0)).Code);        // two holes
        Assert.Equal(BuildResultCode.InvalidRequest, Edit(p, wall, BuildEdit.PackState(511, 0)).Code);          // nothing left
        Assert.Equal(BuildResultCode.InvalidRequest, Edit(p, wall, (ushort)(BuildEdit.PackState(Door, 0) | 0x4000)).Code);
        BuildResult refused = Edit(p, wall, BuildEdit.PackState(Door, 1));
        Assert.Equal(0u, refused.PieceId);

        _h.Place(p, new Vector3(2.5f, 0f, -15f));
        Assert.Equal(BuildResultCode.OutOfRange, Edit(p, wall, BuildEdit.PackState(Door, 0)).Code);
        _h.Place(p, Editor);
        Assert.Equal(BuildResultCode.OutOfRange, Edit(p, wall, BuildEdit.PackState(Door, 0), aimAt: new Vector3(2.5f, 1.5f, -10f)).Code);   // looking away

        Assert.Equal(BuildResultCode.InvalidState, Edit(p, wall, BuildEdit.PackState(Door, 0), beforeTick: () =>
        {
            p.Alive = false;
            p.RespawnAtTick = uint.MaxValue;   // dead, not respawned this tick
        }).Code);
        p.Alive = true;

        Assert.Equal(SouthWall, PieceOf(wall).Shape);
        Assert.Equal(0, _h.Match.BuildEdits);
        Assert.Empty(EventsTo(1).Edited);
        Assert.Equal(2, _h.Match.BuildResults(BuildResultCode.NotFound));
        Assert.Equal(2, _h.Match.BuildResults(BuildResultCode.NotOwner));
        Assert.True(_h.Match.BuildCounts().Rejected >= 11);
    }

    // D5-6: a piece in front of the target blocks the edit (the target itself never does: its own window does not count).
    [Fact]
    public void AnotherPieceInTheWay_BlocksTheEdit()
    {
        var (p, near) = OwnWall();
        uint far = _h.AddPiece(new BuildPieceShape(BuildPieceType.Wall, C, 0, C + 1, 0), owner: p.EntityId);   // z = 5
        Assert.Equal(BuildResultCode.Blocked, Edit(p, far, BuildEdit.PackState(Door, 0)).Code);
        // A door in the near wall opens the line to the far wall's centre.
        Assert.Equal(BuildResultCode.Ok, Edit(p, near, BuildEdit.PackState(TallOpening, 0)).Code);
        _h.Ticks(3);
        Assert.Equal(BuildResultCode.Ok, Edit(p, far, BuildEdit.PackState(Door, 0)).Code);
    }

    // D5-7: refilling a door on someone standing in it is refused; opening more around them is fine.
    [Fact]
    public void RefillingAroundABody_IsBlocked_OpeningIsNot()
    {
        var (p, wall) = OwnWall();
        Assert.Equal(BuildResultCode.Ok, Edit(p, wall, BuildEdit.PackState(Door, 0)).Code);
        PlayerEntity inDoor = _h.Join(2, new Vector3(2.5f, 0f, 0f));
        _h.Ticks(3);
        Assert.Equal(BuildResultCode.Blocked, Edit(p, wall, BuildEdit.PackState(0, 0)).Code);
        Assert.Equal(BuildResultCode.Ok, Edit(p, wall, BuildEdit.PackState(TallOpening, 0)).Code);
        _h.Ticks(3);
        // Refilling only the top tile, above the head, is fine.
        Assert.Equal(BuildResultCode.Ok, Edit(p, wall, BuildEdit.PackState(Door, 0)).Code);
        Assert.Equal(new Vector3(2.5f, 0f, 0f), inDoor.State.Position);
    }

    // ---- Ramps: turning, support (D7) ----

    [Fact]
    public void TurningARamp_ThatLosesItsOnlySupport_IsUnsupported()
    {
        PlayerEntity p = _h.Join(1, new Vector3(2.5f, 0f, 2.5f));
        _h.AddPiece(SouthWall, owner: p.EntityId);
        uint ramp = _h.AddPiece(new BuildPieceShape(BuildPieceType.Ramp, C, 1, C, 0), owner: p.EntityId);   // on the wall's top edge
        Assert.Equal(BuildResultCode.Unsupported, Edit(p, ramp, BuildEdit.PackState(0, 1)).Code);
        Assert.Equal(BuildResultCode.InvalidRequest, Edit(p, ramp, BuildEdit.PackState(1, 0)).Code);
        Assert.Equal(0, PieceOf(ramp).Shape.Rotation);
    }

    [Fact]
    public void TurningARamp_DropsWhatHungFromItsOldEdge_AtTheEndOfTheTick()
    {
        PlayerEntity p = _h.Join(1, new Vector3(2.5f, 0f, 2.5f));
        _h.AddPiece(SouthWall, owner: p.EntityId);
        _h.AddPiece(new BuildPieceShape(BuildPieceType.Wall, C, 0, C, 1), owner: p.EntityId);              // west wall: holds rotation 1
        uint ramp = _h.AddPiece(new BuildPieceShape(BuildPieceType.Ramp, C, 1, C, 0), owner: p.EntityId);
        uint floor = _h.AddPiece(new BuildPieceShape(BuildPieceType.Floor, C, 2, C + 1, 0), owner: p.EntityId); // on the ramp's high edge
        _h.Clear();
        Assert.Equal(BuildResultCode.Ok, Edit(p, ramp, BuildEdit.PackState(0, 1)).Code);
        Assert.Equal(1, PieceOf(ramp).Shape.Rotation);
        Assert.False(_h.Match.Build.Contains(floor));
        Assert.Equal(1, _h.Match.PiecesCollapsed);
        var events = EventsTo(1);
        Assert.Equal((ramp, BuildEdit.PackState(0, 1)), Assert.Single(events.Edited));
        Assert.Equal(floor, Assert.Single(events.Destroyed));
        // The support index follows the new edges: the floor's slot is free, the ramp now neighbours the west wall.
        Assert.True(_h.Match.Support.HasNeighbourOtherThan(PieceOf(ramp).Shape, _h.Match.Build.Grid.SlotOf(ramp)));
    }

    // D7: a wall's edit keeps its edges: the floor on it stays.
    [Fact]
    public void EditingAWall_KeepsWhatItHolds()
    {
        var (p, wall) = OwnWall();
        uint floor = _h.AddPiece(new BuildPieceShape(BuildPieceType.Floor, C, 1, C, 0), owner: p.EntityId);
        Assert.Equal(BuildResultCode.Ok, Edit(p, wall, BuildEdit.PackState(TallOpening, 0)).Code);
        Assert.True(_h.Match.Build.Contains(floor));
        Assert.Equal(0, _h.Match.PiecesCollapsed);
        // Destroying the edited wall still drops the floor (its edges are the wall's frame).
        _h.Match.DestroyPiece(wall);
        Assert.False(_h.Match.Build.Contains(floor));
    }

    // ---- With damage and destruction ----

    [Fact]
    public void AnEditAndDamage_InTheSameTick_BothArrive_AndTheHealthIsKept()
    {
        var (p, wall) = OwnWall();
        _h.Clear();
        BuildResult result = Edit(p, wall, BuildEdit.PackState(Door, 0), beforeTick: () => _h.Match.DamagePieceById(wall, 20f, out _));
        Assert.Equal(BuildResultCode.Ok, result.Code);
        Assert.Equal(20, PieceOf(wall).Damage);
        var events = EventsTo(1);
        Assert.Equal((wall, BuildEdit.PackState(Door, 0)), Assert.Single(events.Edited));
        Assert.Equal((wall, (ushort)20), Assert.Single(events.Health));
        Assert.Single(_h.To(1, PacketId.BuildEvents));   // one packet: Placed, Edited, Health, Destroyed in order
    }

    [Fact]
    public void EditingADestroyedPiece_IsNotFound()
    {
        var (p, wall) = OwnWall();
        Assert.Equal(BuildResultCode.NotFound, Edit(p, wall, BuildEdit.PackState(Door, 0), new Vector3(2.5f, 1.5f, 0f),
            beforeTick: () => _h.Match.DamagePieceById(wall, 100000f, out _)).Code);
        Assert.Empty(EventsTo(1).Edited);
    }

    // An edit and the piece's destruction in one tick: the destroyed record only (no state survives the piece).
    [Fact]
    public void AnEditedPieceDestroyedInTheSameTick_SendsOnlyItsDestroy()
    {
        var (p, wall) = OwnWall();
        _h.Act(p, InputButtons.None, new Vector3(2.5f, 1.5f, 0f));
        _h.Clear();
        _h.Match.Build.TryGetSlot(wall, out int slot);
        _h.Match.Replication.Edited(slot);
        _h.Match.DestroyPiece(wall);
        _h.Match.Tick();
        var events = EventsTo(1);
        Assert.Empty(events.Edited);
        Assert.Equal(wall, Assert.Single(events.Destroyed));
    }

    // ---- The shared queue, sequence and interval (D4) ----

    [Fact]
    public void EditsAndPlacements_ShareTheSequence_TheQueueAndTheInterval()
    {
        var (p, wall) = OwnWall();
        _h.Act(p, InputButtons.None, CentreOf(wall));
        _h.Clear();
        // Two edits in one tick: the first changes the wall, the second waits for the interval (not refused).
        _h.Match.EnqueueEdit(p.PeerId, new BuildEditRequest { Sequence = 10, PieceId = wall, State = BuildEdit.PackState(Door, 0) });
        _h.Match.EnqueueEdit(p.PeerId, new BuildEditRequest { Sequence = 11, PieceId = wall, State = BuildEdit.PackState(Window, 0) });
        _h.Act(p, InputButtons.None, CentreOf(wall));
        Assert.Single(_h.To(1, PacketId.BuildResult));
        Assert.Equal(Door, PieceOf(wall).Shape.Edit);
        _h.Ticks(_h.Match.Building.MinBuildIntervalTicks);
        Assert.Equal(2, _h.To(1, PacketId.BuildResult).Count());
        Assert.Equal(Window, PieceOf(wall).Shape.Edit);

        // A build request reusing an edit's sequence is a duplicate: one sequence space.
        _h.Match.EnqueueBuild(p.PeerId, new BuildRequest { Sequence = 11, Piece = (byte)BuildPieceType.Floor, X = C, Y = 0, Z = C });
        _h.Match.EnqueueEdit(p.PeerId, new BuildEditRequest { Sequence = 9, PieceId = wall, State = BuildEdit.PackState(0, 0) });
        _h.Ticks(_h.Match.Building.MinBuildIntervalTicks);
        Assert.Equal(2, _h.Match.BuildDuplicates);
        Assert.Equal(Window, PieceOf(wall).Shape.Edit);

        // The queue holds BuildRequestQueue.Capacity requests of either kind; one more is RateLimited at once.
        for (int i = 0; i < BuildRequestQueue.Capacity + 1; i++)
            _h.Match.EnqueueEdit(p.PeerId, new BuildEditRequest { Sequence = (ushort)(20 + i), PieceId = wall, State = BuildEdit.PackState(Window, 0) });
        Assert.Equal(1, _h.Match.BuildResults(BuildResultCode.RateLimited));
    }

    // ---- Grace and resume (D8): the resumed client's sync carries the final state ----

    [Fact]
    public void AnEditMadeDuringTheGrace_IsInTheResumedClientsSync()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout, reconnectGraceSeconds: 10);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.Join(3);
        h.RunToMatch();
        h.Ticks(5);
        h.Place(a, new Vector3(-3f, 0f, -3f));
        a.State.Mode = MovementMode.Ground;
        h.Place(b, Editor);
        b.State.Mode = MovementMode.Ground;
        uint wall = SandboxHarness.AddPiece(h.Match, SouthWall, owner: b.EntityId);
        Assert.True(h.Match.Disconnect(1, allowGrace: true));
        h.Ticks(2);
        b.State.Mode = MovementMode.Ground;
        h.Send(b, new InputCommand { AimYaw = 0f, AimPitch = 0f, Yaw = 0f });
        h.Match.EnqueueEdit(2, new BuildEditRequest { Sequence = 1, PieceId = wall, State = BuildEdit.PackState(Door, 0) });
        h.Match.Tick();
        Assert.True(h.Match.Build.TryGetSlot(wall, out int slot));
        Assert.Equal(Door, h.Match.Build.At(slot).Shape.Edit);

        h.Packets.Clear();
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(11, "p1"));
        h.Ticks(3);
        BuildPieceShape? synced = null;
        foreach (RoyaleHarness.Sent sent in h.SentTo(11, PacketId.BuildSync))
        {
            PacketReader r = RoyaleHarness.Reader(sent);
            Assert.True(BuildSyncPacket.TryReadHeader(ref r, out _, out _, out int count));
            for (int i = 0; i < count; i++)
            {
                Assert.True(BuildPieceRecord.TryReadSync(ref r, out BuildPieceRecord piece));
                if (piece.Id == wall) synced = piece.Shape;
            }
        }
        Assert.Equal(SouthWall.WithEdit(Door, 0), synced);
    }

    // ---- The shot (PieceTrace) sees the edited parts ----

    [Fact]
    public void AShot_PassesThroughTheOpening_AndHitsTheRest()
    {
        var wall = new BuildPieceShape(BuildPieceType.Wall, C, 0, C, 0, 0b000_111_000);   // the eye-level row open (1-2 m)
        var ray = new Vector3(0f, 0f, 1f);
        Assert.False(PieceTrace.Hit(wall, new Vector3(2.5f, 1.5f, -3f), ray, 10f, out _));
        Assert.True(PieceTrace.Hit(wall, new Vector3(2.5f, 0.5f, -3f), ray, 10f, out float low));
        Assert.Equal(3f - BuildGrid.WallThickness * 0.5f, low, 3);
        Assert.True(PieceTrace.Hit(wall, new Vector3(2.5f, 2.5f, -3f), ray, 10f, out _));
        var door = wall.WithEdit(Door, 0);
        Assert.False(PieceTrace.Hit(door, new Vector3(2.5f, 1.5f, -3f), ray, 10f, out _));
        Assert.True(PieceTrace.Hit(door, new Vector3(1f, 1.5f, -3f), ray, 10f, out _));
        // Edit 0 hits like before.
        Assert.True(PieceTrace.Hit(wall.WithEdit(0, 0), new Vector3(2.5f, 1.5f, -3f), ray, 10f, out _));
    }

    [Fact]
    public void AShot_SeesFlatRoofs_PassagesAndOneWayRoofs()
    {
        var down = new Vector3(0f, -1f, 0f);
        float eaves = BuildGrid.LevelBase(1);
        var passage = new BuildPieceShape(BuildPieceType.Roof, C, 0, C, 0, BuildEdit.RoofPassage);
        Assert.False(PieceTrace.Hit(passage, new Vector3(2.5f, 6f, 2.5f), down, 10f, out _));
        Assert.True(PieceTrace.Hit(passage, new Vector3(0.5f, 6f, 2.5f), down, 10f, out float t));
        Assert.Equal(6f - eaves, t, 3);
        var flat = passage.WithEdit(BuildEdit.RoofFlat, 0);
        Assert.True(PieceTrace.Hit(flat, new Vector3(2.5f, 6f, 2.5f), down, 10f, out t));
        Assert.Equal(6f - eaves, t, 3);

        // One-way roof rising toward +X (Edit 2): 3.0 at x = 0, 4.5 at x = 5, solid down to the ceiling.
        var oneWay = passage.WithEdit(2, 0);
        Assert.True(PieceTrace.Hit(oneWay, new Vector3(4f, 6f, 2.5f), down, 10f, out t));
        Assert.Equal(6f - (eaves + 1.5f * 4f / 5f), t, 2);
        var east = new Vector3(1f, 0f, 0f);
        Assert.False(PieceTrace.Hit(oneWay, new Vector3(-1f, eaves + 0.6f, 2.5f), east, 2f, out _));       // over the low side
        Assert.True(PieceTrace.Hit(oneWay, new Vector3(-1f, eaves + 0.6f, 2.5f), east, 10f, out t));        // into the high side
        Assert.Equal(1f + 0.6f / 0.3f, t, 2);
        Assert.False(PieceTrace.Hit(oneWay, new Vector3(-1f, eaves - 0.4f, 2.5f), east, 10f, out _));       // under the ceiling
    }
}
