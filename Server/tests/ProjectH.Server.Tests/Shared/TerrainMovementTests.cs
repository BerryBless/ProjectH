using System;
using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 6 D4: the terrain never blocks a horizontal move; it sets the floor under the feet. Uphill lifts, downhill
// keeps a walking character on the ground, falls land on the slope.
public class TerrainMovementTests
{
    private const float Dt = 1f / 30f;
    private const float Yaw90 = 90f;   // facing +X

    // A 4 m hill centred on the origin, 16 m out (8 cells of 2 m), on an 80 x 80 m grid. Steepest about 0.39.
    private static readonly HeightField Hill4 = Hill.Build(-40f, -40f, 2f, 41, 41, new[] { new Hill(20, 20, 0, 8, 4 * Hill.UnitsPerMeter) });

    // A 1 m box standing on flat ground far from the hill: x 29..31, y 0..1, z -1..1.
    private static readonly Box[] LowBoxFar = { Box.FromCenterSize(new Vector3(30f, 0.5f, 0f), new Vector3(2f, 1f, 2f)) };

    private static void AssertOnTheSurface(in MoveState s, HeightField terrain, string where)
    {
        Assert.True(MathF.Abs(s.Position.Y - terrain.Height(s.Position.X, s.Position.Z)) <= 1e-4f,
            $"{where}: feet {s.Position.Y} vs terrain {terrain.Height(s.Position.X, s.Position.Z)}");
    }

    [Fact]
    public void WalkingOverAHill_FollowsTheSurface_AndStaysGroundedEveryStep()
    {
        var s = new MoveState { Position = new Vector3(-20f, 0f, 0.5f) };
        float top = 0f;
        for (int i = 0; i < 300; i++)
        {
            MovementSimulation.Step(ref s, new InputCommand { MoveY = 1f, Yaw = Yaw90 }, Dt, ReadOnlySpan<Box>.Empty, Hill4);
            Assert.True(MovementSimulation.IsGrounded(s, ReadOnlySpan<Box>.Empty, Hill4), $"airborne at step {i}: {s.Position}");
            AssertOnTheSurface(s, Hill4, $"step {i}");
            top = MathF.Max(top, s.Position.Y);
        }
        Assert.True(s.Position.X > 20f, $"did not cross the hill: {s.Position}");
        Assert.True(top > 3.9f, $"never reached the top: {top}");
        Assert.Equal(0f, s.Position.Y);
    }

    [Fact]
    public void SprintingDownAHill_StaysGroundedEveryStep()
    {
        foreach (float yaw in new[] { 0f, 45f, 90f, 135f, 200f, 300f })
        {
            var s = new MoveState { Position = new Vector3(0f, 4f, 0f) };
            for (int i = 0; i < 90; i++)
            {
                MovementSimulation.Step(ref s, new InputCommand { MoveY = 1f, Yaw = yaw, Buttons = InputButtons.Sprint }, Dt,
                    ReadOnlySpan<Box>.Empty, Hill4);
                Assert.True(MovementSimulation.IsGrounded(s, ReadOnlySpan<Box>.Empty, Hill4), $"yaw {yaw}: airborne at step {i}: {s.Position}");
                AssertOnTheSurface(s, Hill4, $"yaw {yaw} step {i}");
            }
        }
    }

    [Fact]
    public void JumpingOnASlope_LeavesTheGround_AndLandsOnTheSurface()
    {
        var s = new MoveState { Position = new Vector3(-8f, Hill4.Height(-8f, 0f), 0f) };
        MovementSimulation.Step(ref s, new InputCommand { MoveY = 1f, Yaw = Yaw90, Buttons = InputButtons.Jump }, Dt, ReadOnlySpan<Box>.Empty, Hill4);
        Assert.True(s.Position.Y > Hill4.Height(s.Position.X, s.Position.Z) + 0.1f, "did not leave the ground");
        bool landed = false;
        for (int i = 0; i < 60 && !landed; i++)
        {
            MovementSimulation.Step(ref s, new InputCommand { MoveY = 1f, Yaw = Yaw90 }, Dt, ReadOnlySpan<Box>.Empty, Hill4);
            landed = MovementSimulation.IsGrounded(s, ReadOnlySpan<Box>.Empty, Hill4);
        }
        Assert.True(landed, $"never landed: {s.Position}");
        AssertOnTheSurface(s, Hill4, "after landing");
        Assert.Equal(0f, s.VelocityY);
    }

    [Fact]
    public void FallingOntoASlope_StopsOnTheSurface()
    {
        var s = new MoveState { Position = new Vector3(6f, 20f, 3f) };
        for (int i = 0; i < 120; i++) MovementSimulation.Step(ref s, new InputCommand(), Dt, ReadOnlySpan<Box>.Empty, Hill4);
        Assert.True(MovementSimulation.IsGrounded(s, ReadOnlySpan<Box>.Empty, Hill4));
        AssertOnTheSurface(s, Hill4, "landed");
        Assert.Equal(6f, s.Position.X);
        Assert.Equal(3f, s.Position.Z);
    }

    [Fact]
    public void StartingBelowTheSurface_IsLiftedOntoIt()
    {
        var s = new MoveState { Position = new Vector3(0f, 0f, 0f) };
        MovementSimulation.Step(ref s, new InputCommand(), Dt, ReadOnlySpan<Box>.Empty, Hill4);
        Assert.Equal(4f, s.Position.Y);
    }

    // The downhill follow must not pull a character off a box edge onto the ground 1 m below in one tick.
    [Fact]
    public void WalkingOffABoxTop_StillFalls()
    {
        var s = new MoveState { Position = new Vector3(30.5f, 1f, 0f) };
        bool sawMidAir = false;
        for (int i = 0; i < 40; i++)
        {
            MovementSimulation.Step(ref s, new InputCommand { MoveY = 1f, Yaw = Yaw90 }, Dt, LowBoxFar, Hill4);
            if (s.Position.Y > 0.05f && s.Position.Y < 0.95f) sawMidAir = true;
        }
        Assert.True(sawMidAir, "dropped 1 m in a single tick");
        Assert.Equal(0f, s.Position.Y);
    }

    // Where the terrain is 0, a hilly field gives exactly the results of the flat one (the old y = 0 floor).
    [Fact]
    public void WhereTheTerrainIsZero_ItMatchesTheFlatFloor()
    {
        var a = new MoveState { Position = new Vector3(27f, 0f, 0f) };
        var b = a;
        for (int i = 0; i < 300; i++)
        {
            var input = new InputCommand
            {
                MoveX = (i % 7) / 7f - 0.4f, MoveY = 1f, Yaw = 60f + i * 2.3f,
                Buttons = (i % 25 == 0 ? InputButtons.Jump : InputButtons.None) | (i % 3 == 0 ? InputButtons.Sprint : InputButtons.None),
            };
            MovementSimulation.Step(ref a, input, Dt, LowBoxFar, Hill4);
            MovementSimulation.Step(ref b, input, Dt, LowBoxFar, HeightField.Flat);
            if (Hill4.Height(a.Position.X, a.Position.Z) != 0f) break;   // wandered onto the hill: stop comparing
            Assert.Equal(b.Position, a.Position);
            Assert.Equal(b.VelocityY, a.VelocityY);
        }
    }

    [Fact]
    public void LongRandomWalk_OverTheHill_IsDeterministic_AndNeverBelowTheSurface()
    {
        var a = new MoveState { Position = new Vector3(-10f, 0f, -10f) };
        var b = a;
        for (int i = 0; i < 900; i++)
        {
            var input = new InputCommand
            {
                MoveX = (i % 5) / 5f - 0.4f, MoveY = 1f, Yaw = i * 3.1f,
                Buttons = (i % 31 == 0 ? InputButtons.Jump : InputButtons.None) | (i % 2 == 0 ? InputButtons.Sprint : InputButtons.None),
            };
            MovementSimulation.Step(ref a, input, Dt, LowBoxFar, Hill4);
            MovementSimulation.Step(ref b, input, Dt, LowBoxFar, Hill4);
            Assert.True(a.Position.Y >= Hill4.Height(a.Position.X, a.Position.Z) - 1e-4f, $"below the surface at step {i}: {a.Position}");
            Assert.False(MovementSimulation.OverlapsAny(a.Position, LowBoxFar));
        }
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.VelocityY, b.VelocityY);
    }

    [Fact]
    public void Step_OnTerrain_AllocatesNothing()
    {
        var s = new MoveState { Position = new Vector3(-10f, 0f, 0f) };
        var input = new InputCommand { MoveY = 1f, Yaw = Yaw90, Buttons = InputButtons.Sprint };
        MovementSimulation.Step(ref s, input, Dt, LowBoxFar, Hill4);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) MovementSimulation.Step(ref s, input, Dt, LowBoxFar, Hill4);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
