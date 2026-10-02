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

    private static BuildPieceShape S(BuildPieceType type, int x, int y, int z, int r = 0)
    {
        Assert.True(BuildGrid.TryNormalize(type, x, y, z, r, out BuildPieceShape s));
        return s;
    }

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

    private PlayerEntity Builder()
    {
        PlayerEntity p = _h.Join(1, new Vector3(2.5f, 0f, -3f));
        _h.Press(p, InputButtons.ToolBuild);
        p.Inventory.SetResource(BuildMaterialType.Wood, 200);
        return p;
    }

    private ushort _seq;

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

    private List<uint> DestroyedThisTick()
    {
        var ids = new List<uint>();
        foreach (SandboxHarness.Sent sent in _h.To(1, PacketId.BuildEvents))
        {
            PacketReader r = SandboxHarness.Body(sent);
            Assert.True(BuildEventsPacket.TryReadHeader(ref r, out _, out int placed, out int health, out int destroyed));
            for (int i = 0; i < placed; i++) BuildPieceRecord.TryReadPlaced(ref r, out _);
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
}
