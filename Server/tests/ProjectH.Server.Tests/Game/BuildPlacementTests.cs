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

// Phase 13 D8-D10, D13 (request §174, §175): placing pieces in the dev sandbox. The builder stands in the plaza at
// (2.5, 0, -3), facing north into cell 16 (x 0..5, z 0..5, flat terrain at 0, no map box near), with wood to spend.
public class BuildPlacementTests
{
    private const int C = 16;
    private static readonly Vector3 Builder = new(2.5f, 0f, -3f);

    private readonly SandboxHarness _h = new();
    private readonly Dictionary<int, ushort> _seq = new();

    private PlayerEntity Ready(int peer, Vector3 feet, int wood = 100)
    {
        PlayerEntity p = _h.Join(peer, feet);
        _h.Press(p, InputButtons.ToolBuild);
        p.Inventory.SetResource(BuildMaterialType.Wood, wood);
        return p;
    }

    private static BuildRequest Request(BuildPieceType type, int x, int y, int z, int rotation = 0, BuildMaterialType material = BuildMaterialType.Wood) =>
        new() { Piece = (byte)type, Material = (byte)material, X = (byte)x, Y = (byte)y, Z = (byte)z, Rotation = (byte)rotation };

    private ushort NextSeq(PlayerEntity p)
    {
        _seq.TryGetValue(p.PeerId, out ushort s);
        _seq[p.PeerId] = ++s;
        return s;
    }

    // Aims at the piece (one tick, so it is the player's last input), then sends the request and runs the tick that
    // processes it. Returns the result the player got.
    private BuildResult Build(PlayerEntity p, BuildRequest request, Vector3? aimAt = null, bool newSequence = true)
    {
        Vector3 at = aimAt ?? CentreOf(request);
        _h.Act(p, InputButtons.None, at);
        if (newSequence) request.Sequence = NextSeq(p);
        _h.Match.EnqueueBuild(p.PeerId, request);
        _h.Act(p, InputButtons.None, at);
        return LastResult(p);
    }

    private BuildResult LastResult(PlayerEntity p)
    {
        PacketReader r = SandboxHarness.Body(_h.To(p.PeerId, PacketId.BuildResult).Last());
        Assert.True(BuildResult.TryRead(ref r, out BuildResult result));
        return result;
    }

    private static Vector3 CentreOf(in BuildRequest r)
    {
        if (!BuildGrid.TryNormalize((BuildPieceType)r.Piece, r.X, r.Y, r.Z, r.Rotation, out BuildPieceShape shape)) return new Vector3(2.5f, 1f, 2.5f);
        return BuildGrid.CenterOf(shape);
    }

    private List<BuildPieceRecord> PlacedTo(int peer)
    {
        var list = new List<BuildPieceRecord>();
        foreach (SandboxHarness.Sent sent in _h.To(peer, PacketId.BuildEvents))
        {
            PacketReader r = SandboxHarness.Body(sent);
            Assert.True(BuildEventsPacket.TryReadHeader(ref r, out _, out int placed, out _, out _, out _));
            for (int i = 0; i < placed; i++)
            {
                Assert.True(BuildPieceRecord.TryReadPlaced(ref r, out BuildPieceRecord p));
                list.Add(p);
            }
        }
        return list;
    }

    // ---- Valid pieces ----

    [Theory]
    [InlineData(BuildPieceType.Wall, 0)]
    [InlineData(BuildPieceType.Floor, 0)]
    [InlineData(BuildPieceType.Ramp, 0)]
    [InlineData(BuildPieceType.Roof, 0)]
    public void AValidPiece_IsPlaced_Paid_AndAnnouncedToEveryone(BuildPieceType type, int rotation)
    {
        PlayerEntity p = Ready(1, Builder);
        _h.Join(2, new Vector3(-6f, 0f, -6f));
        // A roof needs something under its eaves (D12): a wall already standing there.
        int before = type == BuildPieceType.Roof ? 1 : 0;
        if (type == BuildPieceType.Roof) _h.AddPiece(new BuildPieceShape(BuildPieceType.Wall, C, 0, C, 1));
        BuildResult result = Build(p, Request(type, C, 0, C, rotation));
        Assert.Equal(BuildResultCode.Ok, result.Code);
        Assert.NotEqual(0u, result.PieceId);
        Assert.Equal(before + 1, _h.Match.BuildPieces);
        Assert.Equal(90, p.Inventory.Resource(BuildMaterialType.Wood));
        Assert.Equal(1, _h.Match.BuildResults(BuildResultCode.Ok));
        foreach (int peer in new[] { 1, 2 })
        {
            BuildPieceRecord placed = Assert.Single(PlacedTo(peer));
            Assert.Equal(result.PieceId, placed.Id);
            Assert.Equal(type, placed.Shape.Type);
            Assert.Equal(BuildMaterialType.Wood, placed.Material);
            Assert.Equal(p.EntityId, placed.Owner);
            Assert.Equal(_h.Match.ServerTick, placed.CreatedTick);
        }
        // The owner heard of the cost at the end of that tick.
        PacketReader r = SandboxHarness.Body(_h.To(1, PacketId.ResourcesState).Last());
        Assert.True(ResourcesState.TryRead(ref r, out ResourcesState resources));
        Assert.Equal(90, resources.Wood);
    }

    [Fact]
    public void EveryMaterial_CostsItsOwnResource()
    {
        PlayerEntity p = Ready(1, Builder);
        p.Inventory.SetResource(BuildMaterialType.Stone, 15);
        p.Inventory.SetResource(BuildMaterialType.Metal, 10);
        Assert.Equal(BuildResultCode.Ok, Build(p, Request(BuildPieceType.Wall, C, 0, C, 0, BuildMaterialType.Stone)).Code);
        _h.Ticks(3);
        Assert.Equal(BuildResultCode.Ok, Build(p, Request(BuildPieceType.Floor, C, 0, C, 0, BuildMaterialType.Metal)).Code);
        Assert.Equal(5, p.Inventory.Resource(BuildMaterialType.Stone));
        Assert.Equal(0, p.Inventory.Resource(BuildMaterialType.Metal));
        Assert.Equal(100, p.Inventory.Resource(BuildMaterialType.Wood));
    }

    [Fact]
    public void APlacedWall_StopsAMoveInTheSameTick()
    {
        PlayerEntity p = Ready(1, Builder);
        PlayerEntity walker = _h.Join(2, new Vector3(2.5f, 0f, -1f));
        _h.Act(p, InputButtons.None, CentreOf(Request(BuildPieceType.Wall, C, 0, C)));
        var request = Request(BuildPieceType.Wall, C, 0, C);
        request.Sequence = NextSeq(p);
        _h.Match.EnqueueBuild(1, request);
        for (int i = 0; i < 30; i++)
        {
            _h.Send(walker, new InputCommand { MoveY = 1f, Yaw = 0f });
            _h.Match.Tick();
        }
        Assert.True(walker.State.Position.Z <= -BuildGrid.WallThickness * 0.5f - MoveSettings.HalfWidth);
    }

    // ---- Refusals: nothing changes ----

    private void AssertNothingChanged(PlayerEntity p, int wood = 100)
    {
        Assert.Equal(0, _h.Match.BuildPieces);
        Assert.Equal(wood, p.Inventory.Resource(BuildMaterialType.Wood));
        Assert.Empty(PlacedTo(p.PeerId));
    }

    [Fact]
    public void NoResource_IsRefused()
    {
        PlayerEntity p = Ready(1, Builder, wood: 9);
        Assert.Equal(BuildResultCode.NoResource, Build(p, Request(BuildPieceType.Wall, C, 0, C)).Code);
        AssertNothingChanged(p, wood: 9);
    }

    [Fact]
    public void TooFar_OrNotLookedAt_IsOutOfRange()
    {
        PlayerEntity p = Ready(1, Builder);
        Assert.Equal(BuildResultCode.OutOfRange, Build(p, Request(BuildPieceType.Wall, C, 0, 19)).Code);   // 15 m north
        Assert.Equal(BuildResultCode.OutOfRange, Build(p, Request(BuildPieceType.Wall, C, 0, C), aimAt: Builder + new Vector3(0f, 1.6f, -5f)).Code);
        AssertNothingChanged(p);
    }

    [Fact]
    public void AWallThroughAPlayer_IsBlocked()
    {
        PlayerEntity p = Ready(1, Builder);
        _h.Join(2, new Vector3(2.5f, 0f, 0f));   // standing on the cell's south edge
        Assert.Equal(BuildResultCode.Blocked, Build(p, Request(BuildPieceType.Wall, C, 0, C)).Code);
        AssertNothingChanged(p);
    }

    [Fact]
    public void ARampOverAPlayer_IsAllowed_AndLiftsThem()
    {
        PlayerEntity p = Ready(1, Builder);
        PlayerEntity other = _h.Join(2, new Vector3(2.5f, 0f, 2.5f));
        Assert.Equal(BuildResultCode.Ok, Build(p, Request(BuildPieceType.Ramp, C, 0, C)).Code);
        _h.Ticks(2);
        Assert.True(other.State.Position.Y > 1.5f);
    }

    // Final review B6: a ramp that would lift a player into a level 1 floor above it is refused; a vault in progress
    // blocks a piece across its path.
    [Fact]
    public void ARampThatWouldLiftAPlayerIntoAFloorAbove_IsBlocked()
    {
        PlayerEntity p = Ready(1, Builder);
        _h.AddPiece(new BuildPieceShape(BuildPieceType.Floor, C, 1, C, 0));   // underside 2.75
        _h.Join(2, new Vector3(2.5f, 0f, 3.5f));                            // the ramp's surface there is about 2.3 m up
        Assert.Equal(BuildResultCode.Blocked, Build(p, Request(BuildPieceType.Ramp, C, 0, C)).Code);
        Assert.Equal(1, _h.Match.BuildPieces);   // the floor only
        Assert.Equal(100, p.Inventory.Resource(BuildMaterialType.Wood));
        // Near the ramp's low edge the body fits under the floor: allowed.
        _h.Match.TryGetPlayer(2, out PlayerEntity other);
        other.State.Position = new Vector3(2.5f, 0f, 0.5f);
        Assert.Equal(BuildResultCode.Ok, Build(p, Request(BuildPieceType.Ramp, C, 0, C)).Code);
    }

    [Fact]
    public void APieceAcrossAVaultInProgress_IsBlocked()
    {
        PlayerEntity p = Ready(1, Builder);
        PlayerEntity vaulter = _h.Join(2, new Vector3(4f, 0f, -1f));
        vaulter.State.Mode = MovementMode.Vault;
        vaulter.State.HorizontalVelocity = new Vector2(0f, 5f);   // north, across the cell's south edge
        vaulter.State.ModeTicks = 20;
        Assert.Equal(BuildResultCode.Blocked, Build(p, Request(BuildPieceType.Wall, C, 0, C)).Code);
    }

    // Final review B9: an accepted placement interrupts a heal, and so do the tool keys.
    [Fact]
    public void APlacement_OrAToolKey_CancelsAHeal()
    {
        PlayerEntity p = Ready(1, Builder);
        p.Health = 50;
        p.Inventory.Medkits = 1;
        _h.Press(p, InputButtons.UseMedkit);
        Assert.Equal(ConsumableType.Medkit, p.Inventory.Using);
        Assert.Equal(BuildResultCode.Ok, Build(p, Request(BuildPieceType.Wall, C, 0, C)).Code);
        Assert.Equal(ConsumableType.None, p.Inventory.Using);
        Assert.Equal(50, p.Health);

        _h.Press(p, InputButtons.UseMedkit);
        Assert.Equal(ConsumableType.Medkit, p.Inventory.Using);
        _h.Press(p, InputButtons.ToolHarvest);
        Assert.Equal(ConsumableType.None, p.Inventory.Using);
        Assert.Equal(1, p.Inventory.Medkits);
    }

    [Fact]
    public void AWallCuttingACrate_IsBlocked()
    {
        // High crate (34, 0.75, 60): x 33..35, z 59..61. The south wall of cell (22, 28) runs along z 60 over x 30..35.
        PlayerEntity p = Ready(1, new Vector3(32f, 0f, 56f));
        Assert.Equal(BuildResultCode.Blocked, Build(p, Request(BuildPieceType.Wall, 22, 0, 28)).Code);
        AssertNothingChanged(p);
    }

    [Fact]
    public void BehindAMapWall_IsBlocked()
    {
        // The cover wall at (-26, 0): x -26.25..-25.75, z -2.5..2.5, 3 m high. West of it, the west edge of cell (10, 15).
        PlayerEntity p = Ready(1, SandboxHarness.Ground(-24f, 0f));
        Assert.Equal(BuildResultCode.Blocked, Build(p, Request(BuildPieceType.Wall, 10, 0, 15, 1)).Code);
        AssertNothingChanged(p);
    }

    [Fact]
    public void AFloorUnderThePlateau_IsBuried()
    {
        // The Lookout plateau's flat top is 6 m high at (44, -44); a level 0 floor there is under it.
        PlayerEntity p = Ready(1, SandboxHarness.Ground(44f, -42f));
        Assert.Equal(BuildResultCode.Blocked, Build(p, Request(BuildPieceType.Floor, 24, 0, 7)).Code);
        AssertNothingChanged(p);
    }

    [Fact]
    public void ATakenSlot_IsOccupied_WhicheverSideItIsAskedFrom()
    {
        PlayerEntity p = Ready(1, Builder);
        Assert.Equal(BuildResultCode.Ok, Build(p, Request(BuildPieceType.Wall, C, 0, C, 0)).Code);
        _h.Ticks(3);
        Assert.Equal(BuildResultCode.Occupied, Build(p, Request(BuildPieceType.Wall, C, 0, C, 0)).Code);
        Assert.Equal(BuildResultCode.Occupied, Build(p, Request(BuildPieceType.Wall, C, 0, 15, 2)).Code);   // the north edge of the cell below
        Assert.Equal(1, _h.Match.BuildPieces);
        Assert.Equal(90, p.Inventory.Resource(BuildMaterialType.Wood));
    }

    [Fact]
    public void AFloorAndTheRoofBelowIt_ShareTheirSlab()
    {
        PlayerEntity p = Ready(1, Builder);
        _h.AddPiece(new BuildPieceShape(BuildPieceType.Wall, C, 0, C, 1));
        Assert.Equal(BuildResultCode.Ok, Build(p, Request(BuildPieceType.Roof, C, 0, C)).Code);
        _h.Ticks(3);
        Assert.Equal(BuildResultCode.Occupied, Build(p, Request(BuildPieceType.Floor, C, 1, C)).Code);
        _h.Ticks(3);
        Assert.Equal(BuildResultCode.Ok, Build(p, Request(BuildPieceType.Ramp, C, 0, C)).Code);   // D1: a ramp under a roof
    }

    [Theory]
    [InlineData(4, 0, 16, 0, 16, 0)]      // no such piece
    [InlineData(0, 3, 16, 0, 16, 0)]      // no such material
    [InlineData(2, 0, 16, 0, 16, 4)]      // rotation 4
    [InlineData(1, 0, 40, 0, 16, 0)]      // off the grid
    [InlineData(1, 0, 16, 16, 16, 0)]     // level 16
    [InlineData(255, 255, 255, 255, 255, 255)]
    public void AnImpossibleRequest_IsInvalid(int piece, int material, int x, int y, int z, int rotation)
    {
        PlayerEntity p = Ready(1, Builder);
        var request = new BuildRequest { Piece = (byte)piece, Material = (byte)material, X = (byte)x, Y = (byte)y, Z = (byte)z, Rotation = (byte)rotation };
        Assert.Equal(BuildResultCode.InvalidRequest, Build(p, request, aimAt: new Vector3(2.5f, 1f, 2.5f)).Code);
        AssertNothingChanged(p);
    }

    [Fact]
    public void WithoutBuildMode_OrDead_OrFalling_IsInvalidState()
    {
        PlayerEntity p = Ready(1, Builder);
        _h.Press(p, InputButtons.Slot1);
        Assert.Equal(BuildResultCode.InvalidState, Build(p, Request(BuildPieceType.Wall, C, 0, C)).Code);
        _h.Press(p, InputButtons.ToolBuild);
        p.State.Mode = MovementMode.Freefall;
        p.State.Position = new Vector3(2.5f, 1.5f, -3f);
        _h.Match.EnqueueBuild(1, Request(BuildPieceType.Wall, C, 0, C) with { Sequence = NextSeq(p) });
        _h.Match.Tick();
        Assert.Equal(BuildResultCode.InvalidState, LastResult(p).Code);
        p.State = new MoveState { Position = Builder };
        p.Alive = false;
        p.RespawnAtTick = uint.MaxValue;   // stays dead in the sandbox
        Assert.Equal(BuildResultCode.InvalidState, Build(p, Request(BuildPieceType.Wall, C, 0, C)).Code);
        AssertNothingChanged(p);
    }

    [Fact]
    public void AJumpingPlayer_CanBuild()
    {
        PlayerEntity p = Ready(1, Builder);
        _h.Act(p, InputButtons.Jump, CentreOf(Request(BuildPieceType.Floor, C, 0, C)));
        Assert.True(p.State.Position.Y > 0f);
        Assert.Equal(BuildResultCode.Ok, Build(p, Request(BuildPieceType.Floor, C, 0, C)).Code);
    }

    // ---- Sequences, queue, interval ----

    [Fact]
    public void TheSameRequestTwice_PlacesAndCostsOnce()
    {
        PlayerEntity p = Ready(1, Builder);
        BuildRequest request = Request(BuildPieceType.Wall, C, 0, C);
        request.Sequence = NextSeq(p);
        Build(p, request, newSequence: false);
        _h.Ticks(3);
        int results = _h.To(1, PacketId.BuildResult).Count();
        Build(p, request, newSequence: false);                                        // a replay: dropped, no answer
        BuildRequest older = Request(BuildPieceType.Floor, C, 0, C);
        older.Sequence = (ushort)(request.Sequence - 1);
        Build(p, older, newSequence: false);
        Assert.Equal(results, _h.To(1, PacketId.BuildResult).Count());
        Assert.Equal(1, _h.Match.BuildPieces);
        Assert.Equal(90, p.Inventory.Resource(BuildMaterialType.Wood));
        Assert.Equal(2, _h.Match.BuildDuplicates);
    }

    [Fact]
    public void SequencesWrapAround()
    {
        Assert.True(BuildRequest.IsNewer(0, 65535));
        Assert.True(BuildRequest.IsNewer(5, 65530));
        Assert.False(BuildRequest.IsNewer(65530, 5));
        Assert.False(BuildRequest.IsNewer(7, 7));
        Assert.False(BuildRequest.IsNewer(0x8007, 7));
    }

    [Fact]
    public void TwoRequestsInOneTick_TheSecondWaitsForTheInterval()
    {
        PlayerEntity p = Ready(1, Builder);
        _h.Act(p, InputButtons.None, new Vector3(2.5f, 1f, 2.5f));
        _h.Match.EnqueueBuild(1, Request(BuildPieceType.Floor, C, 0, C) with { Sequence = NextSeq(p) });
        _h.Match.EnqueueBuild(1, Request(BuildPieceType.Wall, C, 0, C) with { Sequence = NextSeq(p) });
        _h.Act(p, InputButtons.None, new Vector3(2.5f, 1f, 2.5f));
        Assert.Equal(1, _h.Match.BuildPieces);
        Assert.Single(_h.To(1, PacketId.BuildResult));
        _h.Ticks(_h.Match.Building.MinBuildIntervalTicks);
        Assert.Equal(2, _h.Match.BuildPieces);
        Assert.Equal(2, _h.To(1, PacketId.BuildResult).Count());
    }

    [Fact]
    public void AFullQueue_RefusesAtOnce()
    {
        PlayerEntity p = Ready(1, Builder);
        for (int i = 0; i < BuildRequestQueue.Capacity + 1; i++)
            _h.Match.EnqueueBuild(1, Request(BuildPieceType.Wall, C, 0, C) with { Sequence = NextSeq(p) });
        PacketReader r = SandboxHarness.Body(Assert.Single(_h.To(1, PacketId.BuildResult)));
        Assert.True(BuildResult.TryRead(ref r, out BuildResult refused));
        Assert.Equal(BuildResultCode.RateLimited, refused.Code);
        Assert.Equal(BuildRequestQueue.Capacity + 1, refused.Sequence);
        // The dropped request is counted as a refusal and as a request.
        Assert.Equal(1, _h.Match.BuildResults(BuildResultCode.RateLimited));
        Assert.Equal(1, _h.Match.BuildCounts().Rejected);
        Assert.Equal(1, _h.Match.BuildCounts().Requests);
    }

    [Fact]
    public void APlayersBudget_IsEnforced()
    {
        string json = BuildingCatalog.DefaultJson.Replace("\"maxBuildPiecesPerPlayer\": 500", "\"maxBuildPiecesPerPlayer\": 2");
        Assert.True(BuildingCatalog.TryParse(json, 30, out BuildingCatalog? catalog, out _));
        var items = TestGameData.Items();
        var data = new GameData(TestWeapons.Create(), items, TestGameData.Loot(items), TestGameData.Zones(), catalog);
        var h = new SandboxHarness(data: data);
        PlayerEntity p = h.Join(1, Builder);
        h.Press(p, InputButtons.ToolBuild);
        p.Inventory.SetResource(BuildMaterialType.Wood, 100);
        BuildResultCode Place(BuildRequest request, ushort seq)
        {
            h.Act(p, InputButtons.None, CentreOf(request));
            h.Match.EnqueueBuild(1, request with { Sequence = seq });
            h.Ticks(4);
            PacketReader r = SandboxHarness.Body(h.To(1, PacketId.BuildResult).Last());
            BuildResult.TryRead(ref r, out BuildResult result);
            return result.Code;
        }
        Assert.Equal(BuildResultCode.Ok, Place(Request(BuildPieceType.Floor, C, 0, C), 1));
        Assert.Equal(BuildResultCode.Ok, Place(Request(BuildPieceType.Wall, C, 0, C), 2));
        Assert.Equal(BuildResultCode.BudgetFull, Place(Request(BuildPieceType.Wall, C, 0, C, 1), 3));
        Assert.Equal(80, p.Inventory.Resource(BuildMaterialType.Wood));
    }

    [Fact]
    public void AResume_StartsTheSequenceOver()
    {
        var h = new RoyaleHarness(reconnectGraceSeconds: 10);
        PlayerEntity a = h.Join(1);
        h.Join(2);
        h.RunToMatch();
        a.LastBuildSequence = 500;
        a.HasBuildSequence = true;
        Assert.True(h.Match.Disconnect(1, allowGrace: true));
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(7, "p1"));
        Assert.False(a.HasBuildSequence);
    }

    // ---- Match states and the round reset ----

    [Fact]
    public void AFinishedMatch_TakesNoRequest_AndTheRoundResetClearsEveryPiece()
    {
        var h = new RoyaleHarness();
        PlayerEntity a = h.Join(1);
        h.Join(2);
        h.RunToMatch();
        Match m = h.Match;
        m.Build.Add(new BuildPieceShape(BuildPieceType.Floor, 16, 0, 16, 0), BuildMaterialType.Wood, a.EntityId, m.ServerTick, grounded: true);
        uint nextId = m.Build.NextId;
        Assert.Equal(1, m.BuildPieces);
        m.Leave(2);
        h.Ticks(1);
        Assert.Equal(MatchFlowState.Finished, m.Flow.State);
        a.Inventory.Tool = ToolKind.Build;
        a.Inventory.SetResource(BuildMaterialType.Wood, 100);
        m.EnqueueBuild(1, Request(BuildPieceType.Wall, C, 0, C) with { Sequence = 1 });
        h.Ticks(1);
        var result = h.Packets.Where(s => s.PeerId == 1 && s.Id == PacketId.BuildResult).Last();
        var r = new PacketReader(result.Data);
        r.TryReadPacketId(out _);
        Assert.True(BuildResult.TryRead(ref r, out BuildResult refused));
        Assert.Equal(BuildResultCode.InvalidState, refused.Code);

        h.TickUntil(() => m.Flow.State == MatchFlowState.WaitingForPlayers, 200);
        Assert.Equal(0, m.BuildPieces);
        Assert.Equal(nextId, m.Build.NextId);   // ids keep growing
        var reset = h.Packets.Where(s => s.PeerId == 1 && s.Id == PacketId.BuildSync).Last();
        r = new PacketReader(reset.Data);
        r.TryReadPacketId(out _);
        Assert.True(BuildSyncPacket.TryReadHeader(ref r, out _, out bool cleared, out int count));
        Assert.True(cleared);
        Assert.Equal(0, count);
    }

    [Fact]
    public void ALateJoiner_IsASpectator_AndCannotBuild()
    {
        var h = new RoyaleHarness();
        h.Join(1);
        h.Join(2);
        h.RunToMatch();
        PlayerEntity late = h.Join(3);
        Assert.False(late.Alive);
        late.Inventory.Tool = ToolKind.Build;
        late.Inventory.SetResource(BuildMaterialType.Wood, 100);
        h.Match.EnqueueBuild(3, Request(BuildPieceType.Wall, C, 0, C) with { Sequence = 1 });
        h.Ticks(1);
        Assert.Equal(0, h.Match.BuildPieces);
        Assert.Equal(1, h.Match.BuildResults(BuildResultCode.InvalidState));
    }

    // A ramp built through its builder's head lifts them onto its surface at once (here more than 2 m in one tick). That is the new
    // piece's push, so the movement self-check (Phase 12 D12) does not count it.
    [Fact]
    public void ARampBuiltUnderItsBuilder_LiftsThem_WithoutAMovementAnomaly()
    {
        PlayerEntity p = Ready(1, new Vector3(1.6f, 0f, -2.5f));                 // cell (16, 15): the slab crosses the head
        Assert.Equal(BuildResultCode.Ok, Build(p, Request(BuildPieceType.Ramp, C, 0, C - 1, rotation: 3)).Code);   // rising west
        _h.Ticks(1);
        Assert.True(p.State.Position.Y > 2.15f, $"lifted to {p.State.Position.Y}");
        Assert.Equal(0, _h.Match.MovementAnomalies);
    }

    [Fact]
    public void AMatchStart_ClearsTheLobbysPieces()
    {
        var h = new RoyaleHarness();
        PlayerEntity a = h.Join(1);
        h.Match.Build.Add(new BuildPieceShape(BuildPieceType.Floor, 16, 0, 16, 0), BuildMaterialType.Wood, a.EntityId, 0, grounded: true);
        h.Join(2);
        h.RunToMatch();
        Assert.Equal(0, h.Match.BuildPieces);
    }
}
