using System;
using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class MovementSimulationTests
{
    private const float Dt = 1f / 30f;

    private static MoveState Run(InputCommand input, int steps, MoveState start = default)
    {
        MoveState state = start;
        for (int i = 0; i < steps; i++) MovementSimulation.Step(ref state, input, Dt, ReadOnlySpan<Box>.Empty);
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
        MovementSimulation.Step(ref s, new InputCommand { Buttons = InputButtons.Jump }, Dt, ReadOnlySpan<Box>.Empty);
        Assert.True(s.Position.Y > 0f);

        float peak = s.Position.Y;
        for (int i = 0; i < 60; i++)
        {
            MovementSimulation.Step(ref s, new InputCommand(), Dt, ReadOnlySpan<Box>.Empty);
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
        MovementSimulation.Step(ref s, jump, Dt, ReadOnlySpan<Box>.Empty);
        float vAfterFirst = s.VelocityY;
        MovementSimulation.Step(ref s, jump, Dt, ReadOnlySpan<Box>.Empty);
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
            MovementSimulation.Step(ref a, input, Dt, ReadOnlySpan<Box>.Empty);
            MovementSimulation.Step(ref b, input, Dt, ReadOnlySpan<Box>.Empty);
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
}
