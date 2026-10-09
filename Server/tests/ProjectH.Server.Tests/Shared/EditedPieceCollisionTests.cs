using System;
using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 13.5 D3: moving through and against edited pieces (PartsOf boxes), gathered before every step as Match and the
// client prediction do. Same plaza as PieceCollisionTests: cell 16 is x 0..5, z 0..5, flat terrain at 0.
public class EditedPieceCollisionTests
{
    private const float Dt = 1f / 30f;
    private const int C = 16;
    private const int Door = (1 << 1) | (1 << 4);
    private const int Window = 1 << 4;
    private const int HalfWall = 0b111_000_000;

    // 기능: 격자 좌표·회전을 정규화하고 편집 상태를 입힌 테스트용 조각 모양을 만든다. 정규화나 편집 상태가 유효하지 않으면 Assert 실패.
    // 입력: type - 조각 종류, x/y/z - 격자 좌표(셀·층), rotation - 회전(기본 0), edit - 편집 상태 비트(기본 0).
    // 출력: 정규화된 좌표·회전에 edit를 적용한 BuildPieceShape.
    private static BuildPieceShape Piece(BuildPieceType type, int x, int y, int z, int rotation = 0, int edit = 0)
    {
        Assert.True(BuildGrid.TryNormalize(type, x, y, z, rotation, out BuildPieceShape shape));
        Assert.True(BuildEdit.IsValid(type, edit));
        return shape.WithEdit(edit, shape.Rotation);
    }

    // 기능: 조각 모양들을 ID 1부터 순서대로 넣은 테스트용 PieceGrid를 만든다. 추가에 실패하면 Assert 실패.
    // 입력: shapes - 넣을 조각 모양 목록.
    // 출력: 용량 256에 shapes가 ID 1..N으로 등록된 PieceGrid.
    private static PieceGrid Grid(params BuildPieceShape[] shapes)
    {
        var grid = new PieceGrid(256);
        for (int i = 0; i < shapes.Length; i++) Assert.True(grid.TryAdd((uint)(i + 1), shapes[i], out _));
        return grid;
    }

    // 기능: Match.Move처럼 주변을 모은 뒤 한 걸음 움직인다.
    // 입력: s - 이동 상태(갱신된다), input - 입력, grid - 조각, world - 수집 버퍼.
    // 출력: 이번 걸음의 결과.
    private static StepResult StepOnce(ref MoveState s, InputCommand input, PieceGrid grid, CollisionWorld world)
    {
        world.Gather(s.Position, 0, 0UL, grid);
        MovementSimulation.Step(ref s, input, Dt, world, GameMap.Terrain, out StepResult result);
        return result;
    }

    // 기능: 지금 자리에서 몸이 어떤 충돌체에 박혀 있는지 본다.
    // 입력: s - 이동 상태, grid - 조각, world - 수집 버퍼.
    // 출력: 박혀 있으면 true.
    private static bool Penetrates(in MoveState s, PieceGrid grid, CollisionWorld world)
    {
        world.Gather(s.Position, 0, 0UL, grid);
        return MovementSimulation.Penetrates(s.Position, MovementSimulation.CollisionHeight(s.Mode), world);
    }

    // The character (0.7 x 1.8 m) walks through a door (one 5/3 m column, 2 m high) without touching it, both ways.
    [Fact]
    public void ADoor_LetsTheCharacterWalkThrough_BothWays()
    {
        PieceGrid grid = Grid(Piece(BuildPieceType.Wall, C, 0, C, 0, Door), Piece(BuildPieceType.Wall, C, 0, C, 1, Door));
        var world = new CollisionWorld();
        // South wall (z = 0, x 0..5): its door is x 5/3..10/3.
        foreach ((float startZ, float yaw, Func<float, bool> through) in new (float, float, Func<float, bool>)[]
        {
            (-2f, 0f, z => z > 1.5f), (2f, 180f, z => z < -1.5f),
        })
        {
            var s = new MoveState { Position = new Vector3(2.5f, 0f, startZ) };
            for (int i = 0; i < 90; i++)
            {
                StepOnce(ref s, new InputCommand { MoveY = 1f, Yaw = yaw }, grid, world);
                Assert.False(Penetrates(s, grid, world), $"tick {i}: {s.Position}");
            }
            Assert.True(through(s.Position.Z), $"stopped at {s.Position}");
            Assert.Equal(2.5f, s.Position.X, 2);
        }
        // West wall (x = 0, z 0..5), along +X / -X.
        var w = new MoveState { Position = new Vector3(-2f, 0f, 2.5f) };
        for (int i = 0; i < 90; i++) StepOnce(ref w, new InputCommand { MoveY = 1f, Yaw = 90f }, grid, world);
        Assert.True(w.Position.X > 1.5f, $"stopped at {w.Position}");
    }

    // A window (the middle tile, 1-2 m up) does not let anyone walk through, and an unedited wall's line next to it is
    // walked along without sticking at the parts' joints.
    [Fact]
    public void AWindowAndAHalfWall_StopAWalker_WithoutTrapping()
    {
        foreach (int edit in new[] { Window, HalfWall })
        {
            PieceGrid grid = Grid(Piece(BuildPieceType.Wall, C, 0, C, 0, edit));
            var world = new CollisionWorld();
            var s = new MoveState { Position = new Vector3(2.5f, 0f, -2f) };
            for (int i = 0; i < 60; i++)
            {
                StepOnce(ref s, new InputCommand { MoveY = 1f }, grid, world);
                Assert.False(Penetrates(s, grid, world), $"edit {edit}, tick {i}: {s.Position}");
            }
            Assert.True(s.Position.Z < 0f, $"edit {edit}: passed to {s.Position}");
            // Back away.
            for (int i = 0; i < 30; i++) StepOnce(ref s, new InputCommand { MoveY = 1f, Yaw = 180f }, grid, world);
            Assert.True(s.Position.Z < -0.9f, $"edit {edit}: trapped at {s.Position}");
        }
    }

    [Fact]
    public void AFloorQuadrantHole_DropsTheCharacterThrough_AndTheRestHoldsIt()
    {
        // A level-1 floor (top at 3 m) with its (+X, +Z) quadrant open.
        PieceGrid grid = Grid(Piece(BuildPieceType.Floor, C, 1, C, 0, 0b1000));
        var world = new CollisionWorld();
        var held = new MoveState { Position = new Vector3(1.25f, 3f, 1.25f) };
        for (int i = 0; i < 30; i++) StepOnce(ref held, new InputCommand(), grid, world);
        Assert.Equal(3f, held.Position.Y, 3);
        var falls = new MoveState { Position = new Vector3(3.75f, 3f, 3.75f) };
        for (int i = 0; i < 60; i++)
        {
            StepOnce(ref falls, new InputCommand(), grid, world);
            Assert.False(Penetrates(falls, grid, world), $"tick {i}: {falls.Position}");
        }
        Assert.True(falls.Position.Y < 0.2f, $"held at {falls.Position}");
    }

    [Fact]
    public void ARoofPassage_DropsTheCharacterThrough_AndAFlatRoofHoldsIt()
    {
        var world = new CollisionWorld();
        PieceGrid passage = Grid(Piece(BuildPieceType.Roof, C, 0, C, 0, BuildEdit.RoofPassage));
        var s = new MoveState { Position = new Vector3(2.5f, 3f, 2.5f) };
        for (int i = 0; i < 60; i++)
        {
            StepOnce(ref s, new InputCommand(), passage, world);
            Assert.False(Penetrates(s, passage, world), $"tick {i}: {s.Position}");
        }
        Assert.True(s.Position.Y < 0.2f, $"held at {s.Position}");

        PieceGrid flat = Grid(Piece(BuildPieceType.Roof, C, 0, C, 0, BuildEdit.RoofFlat));
        var f = new MoveState { Position = new Vector3(2.5f, 3f, 2.5f) };
        for (int i = 0; i < 30; i++) StepOnce(ref f, new InputCommand(), flat, world);
        Assert.Equal(3f, f.Position.Y, 3);

        // Walking off the passage's rim into the hole from the slab.
        var rim = new MoveState { Position = new Vector3(0.6f, 3f, 2.5f) };
        for (int i = 0; i < 30; i++) StepOnce(ref rim, new InputCommand { MoveY = 1f, Yaw = 90f }, passage, world);
        for (int i = 0; i < 60; i++) StepOnce(ref rim, new InputCommand(), passage, world);
        Assert.True(rim.Position.Y < 0.2f || rim.Position.X > 3.75f, $"at {rim.Position}");
    }

    [Fact]
    public void AOneWayRoof_IsWalkedUpFromItsLowEaves()
    {
        // Rising toward +X (Edit 2); a ramp from the ground reaches the roof's low eaves at x = 0 from cell 15.
        PieceGrid grid = Grid(Piece(BuildPieceType.Ramp, 15, 0, C, 1), Piece(BuildPieceType.Roof, C, 0, C, 0, 2));
        var world = new CollisionWorld();
        var s = new MoveState { Position = new Vector3(X0(15) + 0.5f, 0f, 2.5f) };
        float maxY = 0f;
        for (int i = 0; i < 150; i++)
        {
            StepOnce(ref s, new InputCommand { MoveY = 1f, Yaw = 90f }, grid, world);
            Assert.False(Penetrates(s, grid, world), $"tick {i}: {s.Position}");
            maxY = MathF.Max(maxY, s.Position.Y);
        }
        Assert.True(maxY > 3.5f, $"highest {maxY}");
    }

    // 기능: 건설 격자 셀의 최소 X 좌표(월드)를 구한다.
    // 입력: x - 격자 셀 X 번호.
    // 출력: 해당 셀의 서쪽 경계 X 좌표.
    private static float X0(int x) => BuildGrid.CellMinX(x);

    // Random inputs in an edited one-cell box (a door, a window, a half wall, a plain wall, a floor with a quadrant open and
    // a roof passage over it): never inside a piece, never stuck (some input always moves the character on).
    [Fact]
    public void InAnEditedBox_RandomInputs_NeverTrapOrSwallowTheCharacter()
    {
        PieceGrid grid = Grid(
            Piece(BuildPieceType.Wall, C, 0, C, 0, Door),
            Piece(BuildPieceType.Wall, C, 0, C, 1, Window),
            Piece(BuildPieceType.Wall, C, 0, C + 1, 0, HalfWall),
            Piece(BuildPieceType.Wall, C + 1, 0, C, 1),
            Piece(BuildPieceType.Floor, C, 1, C + 1, 0, 0b0011),
            Piece(BuildPieceType.Floor, C + 1, 1, C, 0, 0b1001),
            Piece(BuildPieceType.Roof, C, 0, C, 0, BuildEdit.RoofPassage),
            Piece(BuildPieceType.Wall, C, 1, C + 1, 0, TallOpening()),
            Piece(BuildPieceType.Ramp, C - 1, 0, C, 1));
        var world = new CollisionWorld();
        foreach (int seed in new[] { 1, 2, 3 })
        {
            var rng = new Random(seed);
            var s = new MoveState { Position = new Vector3(2.5f, 0f, 2.5f) };
            int still = 0;
            for (int i = 0; i < 1500; i++)
            {
                var input = new InputCommand
                {
                    MoveX = (float)rng.NextDouble() * 2f - 1f, MoveY = (float)rng.NextDouble() * 2f - 1f, Yaw = (float)rng.NextDouble() * 360f,
                    Buttons = (rng.Next(4) == 0 ? InputButtons.Jump : 0) | (rng.Next(2) == 0 ? InputButtons.Sprint : 0) |
                              (rng.Next(8) == 0 ? InputButtons.Crouch : 0),
                };
                Vector3 before = s.Position;
                StepOnce(ref s, input, grid, world);
                Assert.False(Penetrates(s, grid, world), $"seed {seed}, tick {i}: {s.Position}");
                still = Vector3.Distance(before, s.Position) < 1e-4f ? still + 1 : 0;
                Assert.True(still < 60, $"seed {seed}: stuck at {s.Position}");
                // Keep it near the box.
                if (MathF.Abs(s.Position.X - 2.5f) > 9f || MathF.Abs(s.Position.Z - 2.5f) > 9f) s.Position = new Vector3(2.5f, 0f, 2.5f);
            }
        }
    }

    // 기능: 벽의 가운데 세로 열(타일 1, 4, 7)을 모두 뚫은 편집 상태 비트를 만든다.
    // 입력: 없음.
    // 출력: 가운데 열 세 타일이 뚫린 벽 편집 비트.
    private static int TallOpening() => (1 << 1) | (1 << 4) | (1 << 7);
}
