using System;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class TestArenaTests
{
    private const float Dt = 1f / 30f;

    [Fact]
    public void HasAtMost30Boxes()
    {
        Assert.InRange(TestArena.Boxes.Length, 1, 30);
    }

    [Fact]
    public void EveryBox_HasMinBelowMax_AndRestsOnOrAboveTheFloor()
    {
        foreach (Box box in TestArena.Boxes)
        {
            Assert.True(box.Min.X < box.Max.X);
            Assert.True(box.Min.Y < box.Max.Y);
            Assert.True(box.Min.Z < box.Max.Z);
            Assert.True(box.Min.Y >= 0f);
        }
    }

    [Fact]
    public void CentralArea_IsClear()
    {
        foreach (Box box in TestArena.Boxes)
        {
            // Horizontal distance from the origin to the box footprint.
            float dx = MathF.Max(MathF.Max(box.Min.X, -box.Max.X), 0f);
            float dz = MathF.Max(MathF.Max(box.Min.Z, -box.Max.Z), 0f);
            Assert.True(MathF.Sqrt(dx * dx + dz * dz) >= TestArena.ClearRadius);
        }
    }

    [Fact]
    public void SpawnPositions_DoNotOverlapAnyBox()
    {
        for (ushort id = 1; id <= 50; id++)
        {
            Vector3 spawn = Match.SpawnPosition(id);
            Assert.False(MovementSimulation.OverlapsAny(spawn, TestArena.Boxes), $"entity {id} at {spawn}");
        }
    }

    // D4: depenetration in a gap narrower than the character can pick an odd direction, so the
    // arena has none. Touching or overlapping boxes (gap <= 0) are covered by the next test.
    [Fact]
    public void GapsBetweenBoxes_AreZeroOrWiderThanTheCharacter()
    {
        ReadOnlySpan<Box> boxes = TestArena.Boxes;
        const float minGap = 2f * MoveSettings.HalfWidth + 2f * MoveSettings.Skin;
        // Outer walls are the only boxes 38 m or longer; the rest of the arena is much smaller.
        static bool IsOuterWall(Box box) => box.Size.X >= 38f || box.Size.Z >= 38f;
        for (int i = 0; i < boxes.Length; i++)
        {
            for (int j = i + 1; j < boxes.Length; j++)
            {
                Box a = boxes[i];
                Box b = boxes[j];
                bool sameHeightBand = a.Min.Y < b.Max.Y && b.Min.Y < a.Max.Y;
                if (!sameHeightBand) continue;   // stacked boxes (the platform) are not a gap
                // The outer walls (40+ m long) leave 0.5 m corner slits on purpose: too narrow for the
                // character to enter (see Character_CannotLeaveThroughCorner), and they seal the arena.
                if (IsOuterWall(a) && IsOuterWall(b)) continue;

                float gapX = MathF.Max(b.Min.X - a.Max.X, a.Min.X - b.Max.X);
                float gapZ = MathF.Max(b.Min.Z - a.Max.Z, a.Min.Z - b.Max.Z);
                float gap = MathF.Max(gapX, gapZ);
                Assert.True(gap <= 0f || gap >= minGap, $"boxes {i} and {j}: gap {gap}");
            }
        }
    }

    // Controller ruling: depenetrating per box traps a character where two boxes side by side share
    // or overlap a vertical face. Stacking (Y ranges only meet) is allowed. Exact comparison: every
    // coordinate in the arena is a binary fraction, so no tolerance is needed.
    [Fact]
    public void NoTwoBoxesTouchSideBySide()
    {
        ReadOnlySpan<Box> boxes = TestArena.Boxes;
        for (int i = 0; i < boxes.Length; i++)
        {
            for (int j = i + 1; j < boxes.Length; j++)
            {
                Box a = boxes[i];
                Box b = boxes[j];
                bool yOverlap = a.Min.Y < b.Max.Y && b.Min.Y < a.Max.Y;
                bool footprintTouches = a.Min.X <= b.Max.X && b.Min.X <= a.Max.X
                                     && a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z;
                Assert.False(yOverlap && footprintTouches, $"boxes {i} and {j} touch or overlap side by side");
            }
        }
    }

    [Fact]
    public void Character_CannotLeaveThroughCorner()
    {
        // Inner faces of the outer walls are at +-19.5.
        const float inner = 19.5f;
        foreach (float sx in new[] { 1f, -1f })
        {
            foreach (float sz in new[] { 1f, -1f })
            {
                var state = new MoveState { Position = new Vector3(sx * 18.5f, 0f, sz * 18.5f) };
                // Yaw is in degrees and forward = (sin yaw, cos yaw): walk forward toward the corner.
                float yaw = MathF.Atan2(sx, sz) * (180f / MathF.PI);
                for (int i = 0; i < 120; i++)
                {
                    var input = new InputCommand { MoveY = 1f, Yaw = yaw };
                    MovementSimulation.Step(ref state, input, Dt, TestArena.Boxes);
                    Assert.True(MathF.Abs(state.Position.X) <= inner && MathF.Abs(state.Position.Z) <= inner,
                        $"corner ({sx},{sz}) step {i}: {state.Position}");
                }
            }
        }
    }

    [Fact]
    public void LongRandomWalk_InArena_IsDeterministic_AndNeverOverlaps()
    {
        var a = new MoveState { Position = new Vector3(0f, 0f, 5f) };
        var b = a;
        for (int i = 0; i < 600; i++)
        {
            var input = new InputCommand
            {
                MoveX = (i % 7) / 7f - 0.4f,
                MoveY = 1f,
                Yaw = i * 1.7f,
                Buttons = (i % 25 == 0 ? InputButtons.Jump : InputButtons.None) | (i % 3 == 0 ? InputButtons.Sprint : InputButtons.None),
            };
            MovementSimulation.Step(ref a, input, Dt, TestArena.Boxes);
            MovementSimulation.Step(ref b, input, Dt, TestArena.Boxes);
            Assert.False(MovementSimulation.OverlapsAny(a.Position, TestArena.Boxes));
        }
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.VelocityY, b.VelocityY);
        Assert.Equal(a.Yaw, b.Yaw);
    }
}
