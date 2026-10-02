using System;
using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 13 D2, D3: moving on and against building pieces, with the world gathered around the character before every
// step exactly as Match and the client prediction do. Every structure stands in the plaza (cells 15-17, flat terrain at
// 0, no map box within the gather radius), so only the pieces matter. Cell 16 is x 0..5, z 0..5.
public class PieceCollisionTests
{
    private const float Dt = 1f / 30f;
    private const int C = 16;   // the plaza's build cell (x 0..5, z 0..5)

    private static readonly InputCommand Idle = new();

    private static BuildPieceShape Wall(int x, int y, int z, int rotation)
    {
        Assert.True(BuildGrid.TryNormalize(BuildPieceType.Wall, x, y, z, rotation, out BuildPieceShape shape));
        return shape;
    }

    private static BuildPieceShape Piece(BuildPieceType type, int x, int y, int z, int rotation = 0)
    {
        Assert.True(BuildGrid.TryNormalize(type, x, y, z, rotation, out BuildPieceShape shape));
        return shape;
    }

    private static PieceGrid Grid(params BuildPieceShape[] shapes)
    {
        var grid = new PieceGrid(256);
        for (int i = 0; i < shapes.Length; i++) Assert.True(grid.TryAdd((uint)(i + 1), shapes[i], out _));
        return grid;
    }

    // One step as Match.Move runs it.
    private static StepResult StepOnce(ref MoveState s, InputCommand input, PieceGrid grid, CollisionWorld world)
    {
        world.Gather(s.Position, 0, 0UL, grid);
        MovementSimulation.Step(ref s, input, Dt, world, GameMap.Terrain, out StepResult result);
        return result;
    }

    private static bool Penetrates(in MoveState s, PieceGrid grid, CollisionWorld world)
    {
        world.Gather(s.Position, 0, 0UL, grid);
        return MovementSimulation.Penetrates(s.Position, MovementSimulation.CollisionHeight(s.Mode), world);
    }

    private static bool Grounded(in MoveState s, PieceGrid grid, CollisionWorld world)
    {
        world.Gather(s.Position, 0, 0UL, grid);
        return MovementSimulation.IsGrounded(s, world, GameMap.Terrain);
    }

    private static float X0(int x) => BuildGrid.CellMinX(x);
    private static float Z0(int z) => BuildGrid.CellMinZ(z);

    // ---- Walls ----

    [Fact]
    public void WalkingAlongAWallLine_PressedAgainstIt_NeverSticksAtAJoint()
    {
        // Six walls side by side on the south edges of cells 13..18 (one line at z = 0, touching end to end).
        PieceGrid grid = Grid(Wall(13, 0, C, 0), Wall(14, 0, C, 0), Wall(15, 0, C, 0), Wall(16, 0, C, 0), Wall(17, 0, C, 0), Wall(18, 0, C, 0));
        var world = new CollisionWorld();
        foreach (float side in new[] { -1f, 1f })
        {
            // Diagonally into the wall from its south (or north) side, moving along +X.
            var s = new MoveState { Position = new Vector3(X0(13) + 1f, 0f, side * 0.6f) };
            var into = new InputCommand { MoveX = side < 0f ? -1f : 1f, MoveY = 1f, Yaw = 90f };   // forward +X, strafe into the wall
            float lastX = s.Position.X;
            for (int i = 0; i < 300; i++)
            {
                StepOnce(ref s, into, grid, world);
                Assert.False(Penetrates(s, grid, world), $"side {side}, tick {i}: {s.Position}");
                Assert.True(s.Position.X > lastX + 0.08f, $"side {side}, tick {i}: stuck at {s.Position}");
                lastX = s.Position.X;
                if (s.Position.X > X0(18) + 4f) break;
            }
            Assert.True(s.Position.X > X0(18) + 4f, $"side {side}: reached {s.Position}");
            Assert.True(MathF.Abs(s.Position.Z) >= BuildGrid.WallThickness * 0.5f + MoveSettings.HalfWidth);
        }
    }

    [Fact]
    public void InsideAOneCellBox_ACharacterNeverLeavesItOrEntersAWall()
    {
        // Four walls, a floor and a roof around cell 16 at level 0.
        PieceGrid grid = Grid(Wall(C, 0, C, 0), Wall(C, 0, C, 1), Wall(C, 0, C, 2), Wall(C, 0, C, 3), Piece(BuildPieceType.Floor, C, 0, C),
            Piece(BuildPieceType.Roof, C, 0, C));
        var world = new CollisionWorld();
        var rng = new Random(13);
        var s = new MoveState { Position = new Vector3(2.5f, 0f, 2.5f) };
        const float inner = BuildGrid.WallThickness * 0.5f + MoveSettings.HalfWidth;
        for (int i = 0; i < 900; i++)
        {
            var input = new InputCommand
            {
                MoveX = (float)rng.NextDouble() * 2f - 1f, MoveY = (float)rng.NextDouble() * 2f - 1f, Yaw = (float)rng.NextDouble() * 360f,
                Buttons = (rng.Next(4) == 0 ? InputButtons.Jump : 0) | (rng.Next(2) == 0 ? InputButtons.Sprint : 0) |
                          (rng.Next(8) == 0 ? InputButtons.Crouch : 0),
            };
            StepOnce(ref s, input, grid, world);
            Assert.False(Penetrates(s, grid, world), $"tick {i}: {s.Position}");
            Assert.InRange(s.Position.X, inner - 0.01f, BuildGrid.CellSize - inner + 0.01f);
            Assert.InRange(s.Position.Z, inner - 0.01f, BuildGrid.CellSize - inner + 0.01f);
            Assert.True(s.Position.Y + MovementSimulation.CollisionHeight(s.Mode) <= BuildGrid.LevelHeight - BuildGrid.SlopeThickness + 0.01f);
        }
    }

    // Phase 13 prototype (Spec과 다른 점 2): pieces touch side by side and overlap at wall corners, unlike the map's boxes.
    // Characters pushed into them through every face, as deep as the placement rule allows (the body centre outside every
    // piece, so up to about 0.35 m), always come out in one step, can always walk away, and the result is deterministic.
    // The last layout stands on a Rustvale house roof, so the pieces also overlap map boxes (the roof slab and the walls).
    [Fact]
    public void PushedIntoTouchingPieces_ACharacterComesOutInOneStep_AndCanWalkAway()
    {
        BuildPieceShape[][] layouts =
        {
            new[] { Wall(15, 0, C, 0), Wall(C, 0, C, 0), Wall(17, 0, C, 0) },                                     // a wall line
            new[] { Wall(C, 0, C, 0), Wall(C, 0, C, 1) },                                                          // an L corner
            new[] { Wall(C, 0, C, 0), Wall(C, 0, C, 1), Wall(C, 0, C, 2), Wall(C, 0, C, 3), Piece(BuildPieceType.Floor, C, 0, C) },
            new[] { Piece(BuildPieceType.Floor, C, 1, C), Piece(BuildPieceType.Floor, 17, 1, C), Wall(C, 1, C, 0), Wall(17, 1, C, 0) }, // walls on floors
            new[] { Wall(C, 0, C, 0), Wall(17, 0, C, 0), Piece(BuildPieceType.Floor, C, 1, C), Piece(BuildPieceType.Floor, 17, 1, C) }, // floors over walls
            new[] { Wall(5, 1, 27, 0), Wall(5, 1, 26, 1), Piece(BuildPieceType.Floor, 5, 1, 26) },                // on a house roof
            // Fix round 1: ramps and roofs in the cells (the pushes are out of the walls and floors).
            new[] { Wall(C, 0, C, 0), Wall(C, 0, C, 1), Wall(C, 0, C, 2), Wall(C, 0, C, 3), Piece(BuildPieceType.Ramp, C, 0, C, 0),
                Piece(BuildPieceType.Roof, C, 0, C) },                                                             // a roofed cell with a ramp
            new[] { Piece(BuildPieceType.Ramp, C, 0, C, 1), Piece(BuildPieceType.Floor, C, 1, C), Wall(C, 0, C, 1), Wall(17, 0, C, 1) }, // a ramp under a floor
            new[] { Piece(BuildPieceType.Ramp, C, 0, C, 0), Wall(C, 0, 17, 0), Piece(BuildPieceType.Floor, C, 1, 17), Piece(BuildPieceType.Roof, C, 1, 17) },
        };
        var world = new CollisionWorld();
        var rng = new Random(1301);
        int cases = 0;
        foreach (BuildPieceShape[] layout in layouts)
        {
            PieceGrid grid = Grid(layout);
            for (int n = 0; n < 300; n++)
            {
                // A body overlapping one piece's box (a wall or a floor) through one face by up to 0.35 m, anywhere along that face.
                BuildPieceShape target = layout[rng.Next(layout.Length)];
                if (BuildGrid.IsSlope(target.Type)) continue;
                Box box = BuildGrid.BoxOf(target);
                float depth = 0.002f + (float)rng.NextDouble() * 0.35f;
                float along = (float)rng.NextDouble();
                float up = (float)rng.NextDouble();
                float x = box.Min.X - 0.3f + along * (box.Max.X - box.Min.X + 0.6f);
                float z = box.Min.Z - 0.3f + along * (box.Max.Z - box.Min.Z + 0.6f);
                float y = MathF.Max(0f, box.Min.Y - 1.5f + up * (box.Max.Y - box.Min.Y + 1.4f));
                Vector3 feet;
                switch (rng.Next(6))
                {
                    case 0: feet = new Vector3(box.Min.X + depth - MoveSettings.HalfWidth, y, z); break;
                    case 1: feet = new Vector3(box.Max.X - depth + MoveSettings.HalfWidth, y, z); break;
                    case 2: feet = new Vector3(x, y, box.Min.Z + depth - MoveSettings.HalfWidth); break;
                    case 3: feet = new Vector3(x, y, box.Max.Z - depth + MoveSettings.HalfWidth); break;
                    case 4: feet = new Vector3(x, box.Max.Y - depth, z); break;
                    default: feet = new Vector3(x, MathF.Max(0f, box.Min.Y - MoveSettings.Height + depth), z); break;
                }
                feet.Y = MathF.Max(feet.Y, GameMap.Terrain.Height(feet.X, feet.Z));
                world.Gather(feet, 0, 0UL, grid);
                if (!MovementSimulation.Penetrates(feet, MoveSettings.Height, world) || CentreInAPiece(feet, world) || InASlab(feet, world)) continue;
                cases++;

                var a = new MoveState { Position = feet };
                StepOnce(ref a, Idle, grid, world);
                Assert.False(Penetrates(a, grid, world), $"case {cases}: {feet} -> {a.Position}");
                var b = new MoveState { Position = feet };
                StepOnce(ref b, Idle, grid, world);
                Assert.Equal(a.Position, b.Position);

                // Some direction walks at least half a metre away within 20 ticks, never into a piece.
                bool away = false;
                for (int d = 0; d < 4 && !away; d++)
                {
                    MoveState w = a;
                    for (int t = 0; t < 20; t++)
                    {
                        StepOnce(ref w, new InputCommand { MoveY = 1f, Yaw = d * 90f }, grid, world);
                        Assert.False(Penetrates(w, grid, world), $"case {cases} walking {d * 90}: {w.Position}");
                    }
                    away = Vector2.Distance(new Vector2(w.Position.X, w.Position.Z), new Vector2(a.Position.X, a.Position.Z)) >= 0.5f;
                }
                Assert.True(away, $"case {cases}: trapped at {a.Position}");
            }
        }
        Assert.True(cases > 1000, $"only {cases} cases");
    }

    private static bool CentreInAPiece(Vector3 feet, CollisionWorld world)
    {
        Vector3 centre = feet + new Vector3(0f, MoveSettings.Height * 0.5f, 0f);
        foreach (Box b in world.Boxes)
        {
            if (centre.X > b.Min.X && centre.X < b.Max.X && centre.Y > b.Min.Y && centre.Y < b.Max.Y && centre.Z > b.Min.Z && centre.Z < b.Max.Z)
                return true;
        }
        return false;
    }

    // The standing body is inside a slope's slab (the touching-box cases start outside every slab: a slab start is the
    // "built onto a character" case, tested on its own).
    private static bool InASlab(Vector3 feet, CollisionWorld world)
    {
        foreach (Slope slope in world.Slopes)
        {
            if (!slope.Range(feet.X - MoveSettings.HalfWidth, feet.Z - MoveSettings.HalfWidth, feet.X + MoveSettings.HalfWidth,
                    feet.Z + MoveSettings.HalfWidth, out _, out float high, out float bottom)) continue;
            if (feet.Y < high - MoveSettings.Skin && feet.Y + MoveSettings.Height > bottom + MoveSettings.Skin) return true;
        }
        return false;
    }

    [Fact]
    public void AWallBuiltDeepIntoTheBody_PushesItOutTheShortWay()
    {
        // The wall on z = 0, the character's body 0.3 m into it from the south: out to the south.
        PieceGrid grid = Grid(Wall(C, 0, C, 0));
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(2.5f, 0f, -0.125f - MoveSettings.HalfWidth + 0.3f) };
        StepOnce(ref s, Idle, grid, world);
        Assert.False(Penetrates(s, grid, world));
        Assert.True(s.Position.Z <= -0.125f - MoveSettings.HalfWidth);
        Assert.True(s.Position.Z > -0.125f - MoveSettings.HalfWidth - 0.01f);
    }

    [Fact]
    public void AWallOnAHouseRoof_StopsAWalkerOnTheRoof_WithoutTrapping()
    {
        // Rustvale house at (-54, 54): x -58..-50, z 50..58, roof top 3.25. A level 1 wall (y 3..6) on the south edge of
        // cell (5, 27) runs along z = 55 over x -55..-50 and overlaps the roof slab's top 0.25 m.
        Box roof = GameMap.Boxes[0];
        foreach (Box b in GameMap.Boxes)
        {
            if (b.Min.X == -58f && b.Min.Z == 50f && b.Max.Y == 3.25f) roof = b;
        }
        Assert.Equal(3.25f, roof.Max.Y);
        PieceGrid grid = Grid(Wall(5, 1, 27, 0));
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(-52.5f, 3.25f, 53f) };
        for (int i = 0; i < 40; i++)
        {
            StepOnce(ref s, new InputCommand { MoveY = 1f, Yaw = 0f }, grid, world);
            Assert.False(Penetrates(s, grid, world), $"tick {i}: {s.Position}");
        }
        Assert.Equal(3.25f, s.Position.Y);
        Assert.InRange(s.Position.Z, 55f - 0.125f - MoveSettings.HalfWidth - 0.01f, 55f - 0.125f - MoveSettings.HalfWidth);
        for (int i = 0; i < 20; i++) StepOnce(ref s, new InputCommand { MoveY = 1f, Yaw = 180f }, grid, world);
        Assert.True(s.Position.Z < 53f);
    }

    // ---- Ramps ----

    // Ramp at cell (16, 0, 16) rising +Z from 0 to 3, then a level 1 floor on cell (16, 17): z 5..10, top 3.
    private static PieceGrid RampToFloor() => Grid(Piece(BuildPieceType.Ramp, C, 0, C, 0), Piece(BuildPieceType.Floor, C, 1, 17));

    [Fact]
    public void WalkingUpARamp_OntoAFloor_StaysGroundedEveryTick_AndEndsExactlyOnTheNextLevel()
    {
        PieceGrid grid = RampToFloor();
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(2.5f, 0f, -3f) };
        float lastY = 0f;
        for (int i = 0; i < 90 && s.Position.Z < 9f; i++)
        {
            StepOnce(ref s, new InputCommand { MoveY = 1f, Yaw = 0f }, grid, world);
            Assert.True(Grounded(s, grid, world), $"tick {i}: airborne at {s.Position}");
            Assert.False(Penetrates(s, grid, world), $"tick {i}: {s.Position}");
            Assert.True(s.Position.Y >= lastY);
            lastY = s.Position.Y;
        }
        Assert.True(s.Position.Z > 7f, $"stopped at {s.Position}");
        Assert.Equal(BuildGrid.LevelBase(1), s.Position.Y);
    }

    [Fact]
    public void WalkingDownARamp_FromTheFloor_StaysGroundedEveryTick()
    {
        PieceGrid grid = RampToFloor();
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(2.5f, 3f, 8f), Yaw = 180f };
        for (int i = 0; i < 90; i++)
        {
            StepOnce(ref s, new InputCommand { MoveY = 1f, Yaw = 180f }, grid, world);
            Assert.True(Grounded(s, grid, world), $"tick {i}: airborne at {s.Position}");
            Assert.False(Penetrates(s, grid, world));
        }
        Assert.True(s.Position.Z < -1f);
        Assert.Equal(0f, s.Position.Y);
    }

    [Fact]
    public void SprintingDiagonallyAcrossARamp_StaysGrounded()
    {
        PieceGrid grid = Grid(Piece(BuildPieceType.Ramp, C, 0, C, 0), Piece(BuildPieceType.Ramp, 17, 0, C, 0));
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(0.5f, 0f, -1f) };
        bool climbed = false;
        for (int i = 0; i < 45; i++)
        {
            StepOnce(ref s, new InputCommand { MoveY = 1f, Yaw = 45f, Buttons = InputButtons.Sprint }, grid, world);
            if (s.Position.Z < 5f - MoveSettings.HalfWidth) Assert.True(Grounded(s, grid, world), $"tick {i}: airborne at {s.Position}");
            climbed |= s.Position.Y > 2f;
        }
        Assert.True(climbed);
    }

    [Fact]
    public void AJumpOntoARamp_LandsOnItsSurface()
    {
        PieceGrid grid = Grid(Piece(BuildPieceType.Ramp, C, 0, C, 1));   // rising +X
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(2.5f, 4f, 2.5f) };
        for (int i = 0; i < 40; i++) StepOnce(ref s, Idle, grid, world);
        // The footprint's highest point: along = 2.5 + 0.35.
        Assert.Equal(0f + (2.5f + MoveSettings.HalfWidth) * 3f / 5f, s.Position.Y, 4);
        Assert.Equal(0f, s.VelocityY);
        Assert.True(Grounded(s, grid, world));
    }

    [Fact]
    public void UnderARamp_AWalkerIsStoppedByItsSlab_NotSwallowed()
    {
        // Ramp at level 0 rising +Z; a walker from the north at ground level passes under the high end and is stopped where
        // the slab comes down to its head.
        PieceGrid grid = Grid(Piece(BuildPieceType.Ramp, C, 0, C, 0));
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(2.5f, 0f, 8f) };
        for (int i = 0; i < 90; i++)
        {
            StepOnce(ref s, new InputCommand { MoveY = 1f, Yaw = 180f }, grid, world);
            Assert.False(Penetrates(s, grid, world), $"tick {i}: {s.Position}");
        }
        Assert.Equal(0f, s.Position.Y);
        Assert.True(s.Position.Z > 3f, $"walked to {s.Position}");
    }

    [Fact]
    public void AJumpUnderARamp_HitsItsUnderside()
    {
        PieceGrid grid = Grid(Piece(BuildPieceType.Ramp, C, 0, C, 0));
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(2.5f, 0f, 4.5f) };
        float top = 0f;
        StepOnce(ref s, new InputCommand { Buttons = InputButtons.Jump }, grid, world);
        for (int i = 0; i < 40; i++)
        {
            StepOnce(ref s, Idle, grid, world);
            top = MathF.Max(top, s.Position.Y);
            Assert.False(Penetrates(s, grid, world), $"tick {i}: {s.Position}");
        }
        // The slab's bottom over the footprint (along 4.15..4.85) is lowest at 4.15: 2.49 - 0.25.
        Assert.True(top + MoveSettings.Height <= 4.15f * 3f / 5f - BuildGrid.SlopeThickness + 0.001f, $"head reached {top + MoveSettings.Height}");
    }

    [Fact]
    public void ARampBuiltOnACharacter_LiftsItOntoTheSurface()
    {
        PieceGrid grid = Grid(Piece(BuildPieceType.Ramp, C, 0, C, 0));
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(2.5f, 0f, 2.5f) };
        StepOnce(ref s, Idle, grid, world);
        Assert.Equal((2.5f + MoveSettings.HalfWidth) * 3f / 5f, s.Position.Y, 4);
        Assert.True(Grounded(s, grid, world));
    }

    [Fact]
    public void ARampChain_ClimbsTwoLevels()
    {
        PieceGrid grid = Grid(Piece(BuildPieceType.Ramp, C, 0, C, 0), Piece(BuildPieceType.Ramp, C, 1, 17, 0), Piece(BuildPieceType.Floor, C, 2, 18));
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(2.5f, 0f, -2f) };
        for (int i = 0; i < 120 && s.Position.Z < 12f; i++)
        {
            StepOnce(ref s, new InputCommand { MoveY = 1f, Yaw = 0f, Buttons = InputButtons.Sprint }, grid, world);
            Assert.True(Grounded(s, grid, world), $"tick {i}: airborne at {s.Position}");
        }
        Assert.True(s.Position.Z >= 12f);
        Assert.Equal(BuildGrid.LevelBase(2), s.Position.Y);
    }

    // ---- Roofs ----

    [Fact]
    public void UpARamp_OntoARoof_OverItsPeak_AndDownTheOtherSide()
    {
        // A box of four walls with a roof (eaves at 3, peak 4.5 at the centre), reached by a ramp from the south.
        PieceGrid grid = Grid(Piece(BuildPieceType.Ramp, C, 0, 15, 0), Wall(C, 0, C, 0), Wall(C, 0, C, 1), Wall(C, 0, C, 2), Wall(C, 0, C, 3),
            Piece(BuildPieceType.Roof, C, 0, C));
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(2.5f, 0f, -8f) };
        float peak = 0f;
        for (int i = 0; i < 70; i++)
        {
            StepOnce(ref s, new InputCommand { MoveY = 1f, Yaw = 0f }, grid, world);
            Assert.False(Penetrates(s, grid, world), $"tick {i}: {s.Position}");
            if (s.Position.Z < 4.5f) Assert.True(Grounded(s, grid, world), $"tick {i}: airborne at {s.Position}");
            peak = MathF.Max(peak, s.Position.Y);
        }
        Assert.Equal(BuildGrid.LevelBase(1) + BuildGrid.RoofRise, peak, 4);
    }

    [Fact]
    public void AGliderLandsOnARoof()
    {
        PieceGrid grid = Grid(Piece(BuildPieceType.Roof, C, 0, C));
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(2.5f, 6f, 2.5f), Mode = MovementMode.Glide };
        for (int i = 0; i < 60 && s.Mode == MovementMode.Glide; i++) StepOnce(ref s, Idle, grid, world);
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.Equal(BuildGrid.LevelBase(1) + BuildGrid.RoofRise, s.Position.Y, 4);
    }

    [Fact]
    public void StandingUnderARoof_NoJumpPassesItsCeiling()
    {
        PieceGrid grid = Grid(Piece(BuildPieceType.Roof, C, 0, C));
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(2.5f, 0f, 2.5f) };
        StepOnce(ref s, new InputCommand { Buttons = InputButtons.Jump }, grid, world);
        for (int i = 0; i < 40; i++) StepOnce(ref s, Idle, grid, world);
        Assert.Equal(0f, s.Position.Y);
        Assert.False(Penetrates(s, grid, world));
    }

    [Fact]
    public void StepsOnPieces_AreDeterministic()
    {
        PieceGrid grid = Grid(Piece(BuildPieceType.Ramp, C, 0, C, 0), Wall(C, 0, C, 1), Piece(BuildPieceType.Floor, C, 1, 17),
            Piece(BuildPieceType.Roof, 17, 0, C));
        var rng = new Random(7);
        var inputs = new InputCommand[300];
        for (int i = 0; i < inputs.Length; i++)
        {
            inputs[i] = new InputCommand
            {
                MoveX = (float)rng.NextDouble() * 2f - 1f, MoveY = (float)rng.NextDouble() * 2f - 1f, Yaw = (float)rng.NextDouble() * 360f,
                Buttons = rng.Next(5) == 0 ? InputButtons.Jump : 0,
            };
        }
        MoveState Run()
        {
            var world = new CollisionWorld();
            var s = new MoveState { Position = new Vector3(2.5f, 0f, -1f) };
            foreach (InputCommand input in inputs) StepOnce(ref s, input, grid, world);
            return s;
        }
        Assert.Equal(Run(), Run());
    }

    // ---- Headroom (Task 1 fix round 1) ----

    // A level 0 roof's flat ceiling, and a level 1 floor's underside: 3 - 0.25.
    private const float Ceiling = BuildGrid.LevelHeight - BuildGrid.SlopeThickness;

    private static PieceGrid RoofedCellWithARamp() =>
        Grid(Wall(C, 0, C, 0), Wall(C, 0, C, 1), Wall(C, 0, C, 2), Wall(C, 0, C, 3), Piece(BuildPieceType.Ramp, C, 0, C, 0),
            Piece(BuildPieceType.Roof, C, 0, C));

    [Fact]
    public void UpARampInsideARoofedCell_ACharacterStopsUnderTheRoof_AndNeverLeavesTheCell()
    {
        PieceGrid grid = RoofedCellWithARamp();
        var world = new CollisionWorld();
        const float inner = BuildGrid.WallThickness * 0.5f + MoveSettings.HalfWidth;
        foreach (bool sprint in new[] { false, true })
        {
            for (int yaw = -40; yaw <= 40; yaw += 10)
            {
                // On the ramp's low edge (its surface under the footprint), facing up it.
                float y = BuildGrid.SlopeOf(Piece(BuildPieceType.Ramp, C, 0, C, 0)).HeightAt(2.5f, inner + 0.01f + MoveSettings.HalfWidth);
                var s = new MoveState { Position = new Vector3(2.5f, y, inner + 0.01f), Yaw = yaw };
                var input = new InputCommand { MoveY = 1f, Yaw = yaw, Buttons = sprint ? InputButtons.Sprint : 0 };
                for (int i = 0; i < 90; i++)
                {
                    Vector3 before = s.Position;
                    StepOnce(ref s, input, grid, world);
                    string at = $"sprint {sprint}, yaw {yaw}, tick {i}: {before} -> {s.Position}";
                    Assert.True(Vector3.Distance(before, s.Position) <= 0.5f, at);
                    Assert.True(s.Position.Y + MovementSimulation.CollisionHeight(s.Mode) <= Ceiling + 0.01f, at);
                    Assert.InRange(s.Position.X, inner - 0.01f, BuildGrid.CellSize - inner + 0.01f);
                    Assert.InRange(s.Position.Z, inner - 0.01f, BuildGrid.CellSize - inner + 0.01f);
                    Assert.False(Penetrates(s, grid, world), at);
                }
            }
        }
    }

    [Fact]
    public void InARoofedCellWithARamp_RandomInputs_NeverPassTheRoofOrLeaveTheCell()
    {
        PieceGrid grid = RoofedCellWithARamp();
        var world = new CollisionWorld();
        const float inner = BuildGrid.WallThickness * 0.5f + MoveSettings.HalfWidth;
        for (int seed = 0; seed < 20; seed++)
        {
            var rng = new Random(1300 + seed);
            var s = new MoveState { Position = new Vector3(2.5f, 0f, BuildGrid.CellSize - inner - 0.01f) };   // under the ramp's high end
            for (int i = 0; i < 300; i++)
            {
                var input = new InputCommand
                {
                    MoveX = (float)rng.NextDouble() * 2f - 1f, MoveY = (float)rng.NextDouble() * 2f - 1f, Yaw = (float)rng.NextDouble() * 360f,
                    Buttons = (rng.Next(4) == 0 ? InputButtons.Jump : 0) | (rng.Next(2) == 0 ? InputButtons.Sprint : 0) |
                              (rng.Next(8) == 0 ? InputButtons.Crouch : 0),
                };
                Vector3 before = s.Position;
                StepOnce(ref s, input, grid, world);
                string at = $"seed {seed}, tick {i}: {before} -> {s.Position}";
                Assert.True(Vector3.Distance(before, s.Position) <= 0.5f, at);
                Assert.True(s.Position.Y + MovementSimulation.CollisionHeight(s.Mode) <= Ceiling + 0.01f, at);
                Assert.InRange(s.Position.X, inner - 0.01f, BuildGrid.CellSize - inner + 0.01f);
                Assert.InRange(s.Position.Z, inner - 0.01f, BuildGrid.CellSize - inner + 0.01f);
                Assert.False(Penetrates(s, grid, world), at);
            }
        }
    }

    [Fact]
    public void UpARampUnderALevelOneFloor_AWalkerStopsUnderIt_WithoutRubberBanding()
    {
        // The ramp rises +Z under a level 1 floor on the same cell (underside 2.75).
        PieceGrid grid = Grid(Piece(BuildPieceType.Ramp, C, 0, C, 0), Piece(BuildPieceType.Floor, C, 1, C));
        var world = new CollisionWorld();
        foreach (bool sprint in new[] { false, true })
        {
            var s = new MoveState { Position = new Vector3(2.5f, 0f, -2f) };
            var input = new InputCommand { MoveY = 1f, Yaw = 0f, Buttons = sprint ? InputButtons.Sprint : 0 };
            Vector3 settled = default;
            for (int i = 0; i < 120; i++)
            {
                Vector3 before = s.Position;
                StepOnce(ref s, input, grid, world);
                string at = $"sprint {sprint}, tick {i}: {before} -> {s.Position}";
                Assert.True(s.Position.Z >= before.Z - 0.0001f, at);   // never pushed back down the ramp
                Assert.True(s.Position.Y + MoveSettings.Height <= Ceiling + 0.01f, at);
                Assert.False(Penetrates(s, grid, world), at);
                if (i == 90) settled = s.Position;
                if (i > 90) Assert.Equal(settled, s.Position);
            }
            Assert.True(settled.Z > 1f, $"stopped at {settled}");
        }
    }

    [Fact]
    public void ARampBuiltOnACharacterInARoofedCell_DoesNotLiftItThroughTheRoof()
    {
        // Standing under the roof where the ramp's slab cuts through the body and its surface under the footprint is about
        // 2 m high: the body cannot stand on it there (the head would be in the roof), so it is not lifted into (or onto)
        // the roof. It walks out downhill.
        PieceGrid grid = Grid(Wall(C, 0, C, 0), Wall(C, 0, C, 1), Wall(C, 0, C, 2), Wall(C, 0, C, 3), Piece(BuildPieceType.Roof, C, 0, C));
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(2.5f, 0f, 3f) };
        Assert.True(grid.TryAdd(100, Piece(BuildPieceType.Ramp, C, 0, C, 0), out _));
        StepOnce(ref s, Idle, grid, world);
        Assert.True(s.Position.Y + MoveSettings.Height <= Ceiling + 0.01f, $"{s.Position}");
        for (int i = 0; i < 60; i++)
        {
            StepOnce(ref s, new InputCommand { MoveY = 1f, Yaw = 180f }, grid, world);
            Assert.True(s.Position.Y + MoveSettings.Height <= Ceiling + 0.01f, $"tick {i}: {s.Position}");
            Assert.InRange(s.Position.Z, 0f, BuildGrid.CellSize);
        }
    }

    [Fact]
    public void APushOutOfAWall_DoesNotMoveTheBodyIntoARampSlab()
    {
        // A ramp rising +X on cell 16 and a wall on the cell's west edge (x -0.125..0.125). The body stands on the ramp's
        // surface with its west side 0.175 m into the wall: the shortest push (+X) would move it up the slope into the slab.
        Slope ramp = BuildGrid.SlopeOf(Piece(BuildPieceType.Ramp, C, 0, C, 1));
        PieceGrid grid = Grid(Piece(BuildPieceType.Ramp, C, 0, C, 1), Wall(C, 0, C, 1));
        var world = new CollisionWorld();
        float y = ramp.HeightAt(0.3f + MoveSettings.HalfWidth, 2.5f);
        var s = new MoveState { Position = new Vector3(0.3f, y, 2.5f) };
        world.Gather(s.Position, 0, 0UL, grid);
        Assert.True(MovementSimulation.Penetrates(s.Position, MoveSettings.Height, world));   // the wall
        StepOnce(ref s, Idle, grid, world);
        Assert.False(Penetrates(s, grid, world), $"{s.Position}");
    }

    [Fact]
    public void AStepUpThatASlopeThenBlocks_LeavesTheFeetWhereTheyWere()
    {
        // A ramp on cell 16 rising -X to 3 at its west edge, a wall on that edge (top 3) and, past it, a level 1 ramp on
        // cell 15 rising +Z whose slab is at head height. Sprinting -X from 0.09 m under the wall top: the sweep steps up
        // onto the wall, then the slab beyond blocks the move. The feet stay on the ramp, grounded.
        Slope ramp = BuildGrid.SlopeOf(Piece(BuildPieceType.Ramp, C, 0, C, 3));
        PieceGrid grid = Grid(Piece(BuildPieceType.Ramp, C, 0, C, 3), Wall(C, 0, C, 1), Piece(BuildPieceType.Ramp, 15, 1, C, 0));
        var world = new CollisionWorld();
        float y = ramp.HeightAt(0.5f - MoveSettings.HalfWidth, 2.5f);
        Assert.InRange(BuildGrid.LevelHeight - y, 0.01f, MovementTuning.StepUpHeight);
        var s = new MoveState { Position = new Vector3(0.5f, y, 2.5f), Yaw = 270f };
        StepOnce(ref s, new InputCommand { MoveY = 1f, Yaw = 270f, Buttons = InputButtons.Sprint }, grid, world);
        Assert.Equal(y, s.Position.Y);
        Assert.True(Grounded(s, grid, world), $"{s.Position}");
        Assert.False(Penetrates(s, grid, world));
    }
}
