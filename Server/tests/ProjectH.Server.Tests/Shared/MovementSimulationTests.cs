using System;
using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class MovementSimulationTests
{
    private const float Dt = 1f / 30f;

    // 기능: 상자 없는 평지에서 같은 입력으로 이동 Step을 steps번 돌린다.
    // 입력: input - 매 Step에 줄 입력, steps - 반복 횟수, start - 시작 상태(기본값은 초기 상태).
    // 출력: steps Step 진행된 MoveState 복사본. start는 바뀌지 않는다.
    private static MoveState Run(InputCommand input, int steps, MoveState start = default)
    {
        MoveState state = start;
        for (int i = 0; i < steps; i++) MovementSimulation.Step(ref state, input, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        return state;
    }

    [Fact]
    public void WalkForward_AtYaw0_MovesAlongPositiveZ_AtWalkSpeed()
    {
        var s = Run(new InputCommand { MoveY = 1f, Yaw = 0f }, 30);
        Assert.Equal(MoveSettings.WalkSpeed, s.Position.Z, 3);
        Assert.Equal(0f, s.Position.X, 3);
        Assert.Equal(0f, s.Position.Y, 5);
    }

    [Fact]
    public void WalkForward_AtYaw90_MovesAlongPositiveX()
    {
        var s = Run(new InputCommand { MoveY = 1f, Yaw = 90f }, 30);
        Assert.Equal(MoveSettings.WalkSpeed, s.Position.X, 3);
        Assert.Equal(0f, s.Position.Z, 3);
    }

    [Fact]
    public void StrafeRight_AtYaw0_MovesAlongPositiveX()
    {
        var s = Run(new InputCommand { MoveX = 1f, Yaw = 0f }, 30);
        Assert.Equal(MoveSettings.WalkSpeed, s.Position.X, 3);
    }

    [Fact]
    public void Diagonal_IsNormalized()
    {
        var s = Run(new InputCommand { MoveX = 1f, MoveY = 1f }, 30);
        Assert.Equal(MoveSettings.WalkSpeed, new Vector2(s.Position.X, s.Position.Z).Length(), 3);
    }

    [Fact]
    public void OversizedInput_IsClampedToMaxSpeed()
    {
        var s = Run(new InputCommand { MoveY = 1000f, Buttons = InputButtons.Sprint }, 30);
        Assert.Equal(MoveSettings.SprintSpeed, s.Position.Z, 3);
    }

    [Fact]
    public void NonFiniteInput_IsIgnored()
    {
        var start = new MoveState { Yaw = 30f };
        var s = Run(new InputCommand { MoveX = float.NaN, MoveY = float.PositiveInfinity, Yaw = float.NaN }, 10, start);
        Assert.Equal(Vector3.Zero, s.Position);
        Assert.Equal(30f, s.Yaw);
    }

    [Fact]
    public void Jump_RisesThenLands()
    {
        var s = new MoveState();
        MovementSimulation.Step(ref s, new InputCommand { Buttons = InputButtons.Jump }, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.True(s.Position.Y > 0f);

        float peak = s.Position.Y;
        for (int i = 0; i < 60; i++)
        {
            MovementSimulation.Step(ref s, new InputCommand(), Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
            if (s.Position.Y > peak) peak = s.Position.Y;
        }
        Assert.InRange(peak, 1.0f, 1.5f);          // v^2 / 2g = 49 / 40 ≈ 1.2 m
        Assert.Equal(0f, s.Position.Y);
        Assert.Equal(0f, s.VelocityY);
    }

    [Fact]
    public void HoldingJump_InAir_DoesNotDoubleJump()
    {
        var s = new MoveState();
        var jump = new InputCommand { Buttons = InputButtons.Jump };
        MovementSimulation.Step(ref s, jump, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        float vAfterFirst = s.VelocityY;
        MovementSimulation.Step(ref s, jump, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.True(s.VelocityY < vAfterFirst);
    }

    [Fact]
    public void SameInputs_ProduceIdenticalState()
    {
        var a = new MoveState();
        var b = new MoveState();
        for (int i = 0; i < 100; i++)
        {
            var input = new InputCommand { Seq = (uint)i, MoveX = (i % 7) / 7f, MoveY = 1f, Yaw = i * 3.3f, Buttons = i % 20 == 0 ? InputButtons.Jump : InputButtons.None };
            MovementSimulation.Step(ref a, input, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
            MovementSimulation.Step(ref b, input, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        }
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.VelocityY, b.VelocityY);
        Assert.Equal(a.Yaw, b.Yaw);
    }

    [Fact]
    public void Yaw_IsNormalizedTo0_360()
    {
        var s = Run(new InputCommand { Yaw = -90f }, 1);
        Assert.Equal(270f, s.Yaw, 3);
    }

    // ---- Review fix D1 (STB-0): the outer walls are 4 m high, so a raised floor, a ramp or a vault can carry a player over
    // them. Every mode now clamps the feet inside the walls (the same bound the air modes used), on the server and in the
    // prediction alike. East outer wall: x = 80; build cell 31 is x 75..80, cell 16 is z 0..5. ----

    private const float Bound = GameMap.HalfSize - MoveSettings.HalfWidth - MoveSettings.Skin;

    // 기능: 건설 조각 하나를 정규화한다(실패하면 테스트 실패).
    // 입력: type - 조각 종류, x/y/z - 격자 칸 좌표, rotation - 회전(기본 0).
    // 출력: 정규화된 BuildPieceShape.
    private static BuildPieceShape Piece(BuildPieceType type, int x, int y, int z, int rotation = 0)
    {
        Assert.True(BuildGrid.TryNormalize(type, x, y, z, rotation, out BuildPieceShape shape));
        return shape;
    }

    // 기능: 조각들로 PieceGrid를 만든다.
    // 입력: shapes - 조각들.
    // 출력: 조각이 든 PieceGrid.
    private static PieceGrid Grid(params BuildPieceShape[] shapes)
    {
        var grid = new PieceGrid(256);
        for (int i = 0; i < shapes.Length; i++) Assert.True(grid.TryAdd((uint)(i + 1), shapes[i], out _));
        return grid;
    }

    // 기능: Match.Move처럼 주변 세계(맵 상자 + 조각)를 모은 뒤 한 Step을 돈다.
    // 입력: s - 상태, input - 입력, grid - 조각, world - 재사용할 충돌 세계.
    // 출력: Step 결과.
    private static StepResult StepIn(ref MoveState s, InputCommand input, PieceGrid grid, CollisionWorld world)
    {
        world.Gather(s.Position, 0, 0UL, grid);
        MovementSimulation.Step(ref s, input, Dt, world, GameMap.Terrain, out StepResult result);
        return result;
    }

    // 기능: 아무 입력 없이 30 Tick 돌려 발을 바닥에 내려놓는다.
    // 입력: s - 상태, grid - 조각, world - 충돌 세계.
    // 출력: 반환값 없음. s가 30 Tick 진행되어 바닥 위에 선다.
    private static void Settle(ref MoveState s, PieceGrid grid, CollisionWorld world)
    {
        var idle = new InputCommand { Yaw = s.Yaw };
        for (int i = 0; i < 30; i++) StepIn(ref s, idle, grid, world);
    }

    [Fact]
    public void ARampAtTheOuterWall_DoesNotLetARunningJumpLeaveTheMap()
    {
        PieceGrid grid = Grid(Piece(BuildPieceType.Ramp, 31, 0, 16, 1));   // rises +X from 0 at x 75 to 3 m at the wall
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(BuildGrid.CellMinX(31) - 3f, GameMap.Terrain.Height(72f, 2.5f), 2.5f), Yaw = 90f };
        Settle(ref s, grid, world);
        var run = new InputCommand { MoveY = 1f, Yaw = 90f, Buttons = InputButtons.Sprint | InputButtons.Jump };
        for (int i = 0; i < 60; i++)
        {
            StepIn(ref s, run, grid, world);
            Assert.True(MathF.Abs(s.Position.X) <= GameMap.HalfSize - MoveSettings.HalfWidth, $"tick {i}: {s.Position} ({s.Mode})");
        }
    }

    [Fact]
    public void WalkingOnALevelTwoFloor_AtTheOuterWall_StaysInside()
    {
        PieceGrid grid = Grid(Piece(BuildPieceType.Floor, 31, 2, 16));   // 6 m up, over the 4 m wall
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(77f, BuildGrid.LevelHeight * 2f + 0.5f, 2.5f), Yaw = 90f };
        Settle(ref s, grid, world);
        Assert.True(s.Position.Y > 5f, $"on the floor: {s.Position}");
        var walk = new InputCommand { MoveY = 1f, Yaw = 90f };
        for (int i = 0; i < 90; i++)
        {
            StepIn(ref s, walk, grid, world);
            Assert.True(MathF.Abs(s.Position.X) <= Bound, $"tick {i}: {s.Position} ({s.Mode})");
        }
    }

    // Review D round 1: on a level-two ramp in the edge cell that falls towards the wall (feet above the 4 m wall), pushing out
    // must not record the lower surface outside at the clamped X: the clamp comes right after the horizontal sweep, so the
    // floor is followed at the clamped position and the feet never sink into the ramp's plate.
    [Fact]
    public void PushingOutDownALevelTwoRampAtTheOuterWall_NeverSinksIntoIt()
    {
        BuildPieceShape ramp = Piece(BuildPieceType.Ramp, 31, 2, 16, 3);   // falls +X: 9 m at x 75, 6 m at the wall
        PieceGrid grid = Grid(ramp);
        Slope slope = BuildGrid.SlopeOf(ramp);
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(77f, 8.5f, 2.5f), Yaw = 90f };
        Settle(ref s, grid, world);
        var push = new InputCommand { MoveY = 1f, Yaw = 90f, Buttons = InputButtons.Sprint };
        for (int i = 0; i < 40; i++)
        {
            StepIn(ref s, push, grid, world);
            Vector3 p = s.Position;
            Assert.True(MathF.Abs(p.X) <= Bound, $"tick {i}: {p}");
            if (slope.Range(p.X - MoveSettings.HalfWidth, p.Z - MoveSettings.HalfWidth, p.X + MoveSettings.HalfWidth, p.Z + MoveSettings.HalfWidth,
                    out _, out float high, out _))
                Assert.True(p.Y >= high - 0.01f, $"tick {i}: feet {p.Y} under the ramp surface {high} at {p}");
        }
    }

    // On the ground the clamp also zeroes the velocity across the wall, as StepAir does, so the next tick does not push on.
    [Fact]
    public void ClampToMap_ZeroesHorizontalVelocity_LikeStepAir()
    {
        PieceGrid grid = Grid(Piece(BuildPieceType.Floor, 31, 2, 16));
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(Bound - 0.05f, BuildGrid.LevelHeight * 2f + 0.5f, 2.5f), Yaw = 90f };
        Settle(ref s, grid, world);
        var walk = new InputCommand { MoveY = 1f, Yaw = 90f };
        StepIn(ref s, walk, grid, world);
        Assert.Equal(Bound, s.Position.X, 4);
        Assert.Equal(0f, s.HorizontalVelocity.X);
        Assert.Equal(MovementMode.Ground, s.Mode);
    }

    // A level-one floor puts the feet 1 m under the top of the outer wall: a sprint jump there would hurdle or mantle onto
    // (or over) the wall. The vault does not start when its landing is outside the walls.
    [Fact]
    public void TryStartVault_RefusesALandingOutsideTheMap()
    {
        PieceGrid grid = Grid(Piece(BuildPieceType.Floor, 31, 1, 16), Piece(BuildPieceType.Floor, 30, 1, 16));
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(78.5f, BuildGrid.LevelHeight + 0.5f, 2.5f), Yaw = 90f };
        Settle(ref s, grid, world);
        Assert.True(s.Position.Y > 2.5f, $"on the floor: {s.Position}");
        StepIn(ref s, new InputCommand { MoveY = 1f, Yaw = 90f, Buttons = InputButtons.Sprint }, grid, world);
        StepIn(ref s, new InputCommand { MoveY = 1f, Yaw = 90f, Buttons = InputButtons.Sprint | InputButtons.Jump }, grid, world);
        Assert.NotEqual(MovementMode.Vault, s.Mode);
        for (int i = 0; i < 30; i++)
        {
            StepIn(ref s, new InputCommand { MoveY = 1f, Yaw = 90f, Buttons = InputButtons.Sprint }, grid, world);
            Assert.True(MathF.Abs(s.Position.X) <= Bound, $"tick {i}: {s.Position} ({s.Mode})");
        }
    }
}
