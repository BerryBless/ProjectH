using System;
using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Character-vs-box collision (Phase 1 spec §1, §5). Expected numbers follow the discrete 30 Hz step
// rule, not the continuous formulas: with Dt = 1/30 the jump velocity on step k is 7 - (k-1)·(2/3)
// and the feet height after n steps is (7n - n(n-1)/3) / 30.
public class CollisionTests
{
    private const float Dt = 1f / 30f;
    private const float Hw = MoveSettings.HalfWidth;
    private const float Skin = MoveSettings.Skin;

    private static readonly InputCommand Idle = new InputCommand();
    private static readonly InputCommand WalkPlusX = new InputCommand { MoveY = 1f, Yaw = 90f };

    private static Box B(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        => new Box(new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ));

    // Wall 1 m thick, x 2..3, long along Z.
    private static readonly Box[] Wall = { B(2f, 0f, -50f, 3f, 3f, 50f) };
    // Box 2 x 2 m footprint, x 2..4, 1 m high (reachable) or 1.5 m high (not reachable).
    private static readonly Box[] LowBox = { B(2f, 0f, -2f, 4f, 1f, 2f) };
    private static readonly Box[] HighBox = { B(2f, 0f, -2f, 4f, 1.5f, 2f) };

    [Fact]
    public void WalkIntoWall_StopsAtFace_WithoutOverlap()
    {
        var s = new MoveState();
        for (int i = 0; i < 60; i++)
        {
            MovementSimulation.Step(ref s, WalkPlusX, Dt, Wall);
            Assert.False(MovementSimulation.OverlapsAny(s.Position, Wall));
        }
        // Front face stops Skin before the wall: x = 2 - 0.35 - 0.001.
        Assert.Equal(2f - Hw - Skin, s.Position.X, 3);
        Assert.True(s.Position.X + Hw <= 2f);
    }

    [Fact]
    public void DiagonalIntoWall_SlidesAlongIt()
    {
        var s = new MoveState();
        var diagonal = new InputCommand { MoveY = 1f, Yaw = 45f };
        for (int i = 0; i < 60; i++) MovementSimulation.Step(ref s, diagonal, Dt, Wall);

        Assert.Equal(2f - Hw - Skin, s.Position.X, 3);
        // Z is never blocked: 60 steps at 4.5 · cos 45° m/s = 2 s · 3.182 = 6.364 m.
        Assert.Equal(MoveSettings.WalkSpeed * MathF.Cos(MathF.PI / 4f) * 2f, s.Position.Z, 2);
    }

    [Fact]
    public void JumpApex_UnderDiscreteSteps_ClearsOneMetre_ButNotOnePointFive()
    {
        var s = new MoveState();
        MovementSimulation.Step(ref s, new InputCommand { Buttons = InputButtons.Jump }, Dt, ReadOnlySpan<Box>.Empty);
        float peak = s.Position.Y;
        for (int i = 0; i < 40; i++)
        {
            MovementSimulation.Step(ref s, Idle, Dt, ReadOnlySpan<Box>.Empty);
            if (s.Position.Y > peak) peak = s.Position.Y;
        }
        // Rises while 7 - (k-1)·2/3 > 0, i.e. 11 steps: (77 - 110/3) / 30 = 1.344 m
        // (the continuous 7² / (2·20) = 1.225 m is lower because each step uses the pre-gravity speed).
        Assert.InRange(peak, 1.30f, 1.40f);
    }

    [Fact]
    public void JumpOntoLowBox_StandsOnTop_Grounded()
    {
        var s = new MoveState();
        MovementSimulation.Step(ref s, new InputCommand { MoveY = 1f, Yaw = 90f, Buttons = InputButtons.Jump }, Dt, LowBox);
        for (int i = 0; i < 19; i++) MovementSimulation.Step(ref s, WalkPlusX, Dt, LowBox);

        // Feet pass y = 1 on the way up at step 6 (1.067 m) and fall back below it only at step 17,
        // by which time x = 2.55 is over the box: it lands on the top, then the ground snap sets y = 1.
        Assert.Equal(1f, s.Position.Y);
        Assert.Equal(0f, s.VelocityY);
        Assert.True(MovementSimulation.IsGrounded(s, LowBox));
        Assert.Equal(3f, s.Position.X, 3);   // 20 steps · 0.15 m, never blocked
        Assert.False(MovementSimulation.OverlapsAny(s.Position, LowBox));
    }

    [Fact]
    public void HighBox_CannotBeClimbedByJumping()
    {
        var s = new MoveState();
        var jumpForward = new InputCommand { MoveY = 1f, Yaw = 90f, Buttons = InputButtons.Jump };
        for (int i = 0; i < 90; i++)
        {
            MovementSimulation.Step(ref s, jumpForward, Dt, HighBox);
            Assert.True(s.Position.X + Hw <= 2f);   // never gets over the 1.5 m edge (apex 1.344 m)
            Assert.False(MovementSimulation.OverlapsAny(s.Position, HighBox));
        }
    }

    // Review Focus: standing on a box must keep an exact height (no grounded/airborne flicker).
    [Fact]
    public void StandingOnBox_HeightNeverChanges()
    {
        var s = new MoveState { Position = new Vector3(3f, 1f, 0f) };
        for (int i = 0; i < 90; i++)
        {
            MovementSimulation.Step(ref s, Idle, Dt, LowBox);
            Assert.Equal(1f, s.Position.Y);
            Assert.Equal(0f, s.VelocityY);
        }
    }

    [Fact]
    public void SlightlyAboveBoxTop_WithinProbe_SnapsOntoIt()
    {
        var s = new MoveState { Position = new Vector3(3f, 1.015f, 0f) };
        MovementSimulation.Step(ref s, Idle, Dt, LowBox);
        Assert.Equal(1f, s.Position.Y);
        Assert.Equal(0f, s.VelocityY);
    }

    [Fact]
    public void HeadHitsCeiling_StopsRising()
    {
        // Ceiling slab from y 2.5 to 3 over the start position.
        Box[] ceiling = { B(-2f, 2.5f, -2f, 2f, 3f, 2f) };
        var s = new MoveState();
        MovementSimulation.Step(ref s, new InputCommand { Buttons = InputButtons.Jump }, Dt, ceiling);
        for (int i = 0; i < 3; i++) MovementSimulation.Step(ref s, Idle, Dt, ceiling);

        // Steps 1-3 rise to 0.633 m (head 2.433 m); step 4 wants +0.167 m but only 0.066 m is free.
        Assert.Equal(0f, s.VelocityY);
        Assert.InRange(s.Position.Y, 0.69f, 0.70f);
        Assert.True(s.Position.Y + MoveSettings.Height <= 2.5f);

        MovementSimulation.Step(ref s, Idle, Dt, ceiling);
        Assert.True(s.VelocityY < 0f);   // falls again, does not stick to the ceiling
    }

    // Review Focus: a long fall moves several metres per tick and must not pass through a thin plate.
    [Fact]
    public void FastFall_DoesNotTunnelThroughThinPlate()
    {
        // 0.2 m plate; falling at 200 m/s moves about 6.7 m per tick.
        Box[] plate = { B(-5f, 5f, -5f, 5f, 5.2f, 5f) };
        var s = new MoveState { Position = new Vector3(0f, 20f, 0f), VelocityY = -200f };
        for (int i = 0; i < 10; i++)
        {
            MovementSimulation.Step(ref s, Idle, Dt, plate);
            Assert.True(s.Position.Y >= 5.2f);
        }
        Assert.Equal(5.2f, s.Position.Y);
        Assert.Equal(0f, s.VelocityY);
    }

    // Review Focus: a reconcile snap can put the player inside a box; the next step must push it out.
    [Fact]
    public void StartingInsideBox_IsPushedOutAlongLeastPenetration()
    {
        // Box x -1..1, y 0..1, z -1..1. Feet at x 0.9, y 0.5: +X needs 0.45 m, +Y 0.5 m, Z 1.35 m.
        Box[] box = { B(-1f, 0f, -1f, 1f, 1f, 1f) };
        var s = new MoveState { Position = new Vector3(0.9f, 0.5f, 0f) };
        MovementSimulation.Step(ref s, Idle, Dt, box);

        Assert.Equal(0.9f + 0.45f + Skin, s.Position.X, 3);
        Assert.False(MovementSimulation.OverlapsAny(s.Position, box));
        Assert.True(s.Position.Y < 0.5f);   // no support under it any more: it falls
    }

    [Fact]
    public void SameInputs_WithBoxes_ProduceIdenticalState()
    {
        Box[] world = { B(2f, 0f, -2f, 4f, 1f, 2f), B(-3f, 0f, 3f, 3f, 3f, 4f), B(-6f, 2.5f, -6f, 6f, 3f, 6f) };
        var a = new MoveState();
        var b = new MoveState();
        for (int i = 0; i < 300; i++)
        {
            var input = new InputCommand { MoveX = (i % 7) / 7f - 0.4f, MoveY = 1f, Yaw = i * 3.3f, Buttons = i % 20 == 0 ? InputButtons.Jump : InputButtons.None };
            MovementSimulation.Step(ref a, input, Dt, world);
            MovementSimulation.Step(ref b, input, Dt, world);
        }
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.VelocityY, b.VelocityY);
        Assert.Equal(a.Yaw, b.Yaw);
    }

    [Fact]
    public void NonFiniteInput_NextToWall_StaysFiniteAndOutside()
    {
        var s = new MoveState { Position = new Vector3(2f - Hw - Skin, 0f, 0f) };
        var bad = new InputCommand { MoveX = float.NaN, MoveY = float.PositiveInfinity, Yaw = float.NaN, Buttons = InputButtons.Jump };
        for (int i = 0; i < 30; i++)
        {
            MovementSimulation.Step(ref s, bad, Dt, Wall);
            Assert.True(float.IsFinite(s.Position.X) && float.IsFinite(s.Position.Y) && float.IsFinite(s.Position.Z));
            Assert.True(float.IsFinite(s.VelocityY));
            Assert.False(MovementSimulation.OverlapsAny(s.Position, Wall));
        }
    }

    [Fact]
    public void EmptyWorld_MatchesFloorOnlyBehaviour()
    {
        var s = new MoveState();
        MovementSimulation.Step(ref s, new InputCommand { Buttons = InputButtons.Jump }, Dt, ReadOnlySpan<Box>.Empty);
        for (int i = 0; i < 60; i++) MovementSimulation.Step(ref s, Idle, Dt, ReadOnlySpan<Box>.Empty);
        Assert.Equal(0f, s.Position.Y);
        Assert.Equal(0f, s.VelocityY);
        Assert.True(MovementSimulation.IsGrounded(s, ReadOnlySpan<Box>.Empty));
    }
}
