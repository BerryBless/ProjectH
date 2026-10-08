using System;
using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 19 D2: the car's motion, which the server and the driver's prediction both run.
public class VehicleSimulationTests
{
    private const float Dt = 1f / 30f;

    // 기능: 운전 입력을 만든다.
    // 입력: throttle - MoveY, steer - MoveX, buttons - 버튼.
    // 출력: InputCommand.
    private static InputCommand Drive(float throttle, float steer = 0f, InputButtons buttons = InputButtons.None) =>
        new() { MoveY = throttle, MoveX = steer, Buttons = buttons };

    // 기능: 평지에서 상자·경사면 목록으로 n Tick 운전한다.
    // 입력: state - 상태, input - 입력, ticks - Tick 수, boxes - 상자(null = 없음), hasDriver - 운전자 유무.
    // 출력: 마지막 Tick 결과.
    private static VehicleStepResult Run(ref VehicleMove state, InputCommand input, int ticks, Box[]? boxes = null, bool hasDriver = true)
    {
        VehicleStepResult result = default;
        for (int i = 0; i < ticks; i++)
            VehicleSimulation.Step(ref state, input, hasDriver, Dt, boxes ?? Array.Empty<Box>(), ReadOnlySpan<Slope>.Empty, HeightField.Flat, out result);
        return result;
    }

    [Fact]
    public void Throttle_Accelerates_ToTheTopSpeed_BoostHigher()
    {
        var s = new VehicleMove();
        Run(ref s, Drive(1f), 30);
        Assert.Equal(VehicleSettings.Acceleration, s.Speed, 2);   // 1 s at 8 m/s^2
        Assert.True(s.Position.Z > 3.5f && s.Position.Z < 4.5f, $"{s.Position}");   // heading 0 faces +Z
        s.Position = new Vector3(0f, 0f, -70f);   // room to reach the top speed before the map bound
        Run(ref s, Drive(1f), 120);
        Assert.Equal(VehicleSettings.MaxForwardSpeed, s.Speed, 3);
        s.Position = new Vector3(0f, 0f, -70f);
        Run(ref s, Drive(1f, 0f, InputButtons.Sprint), 120);
        Assert.Equal(VehicleSettings.BoostMaxSpeed, s.Speed, 3);
        // Boost released: rolls back down to the normal top speed (not a jump).
        Run(ref s, Drive(1f), 1);
        Assert.True(s.Speed < VehicleSettings.BoostMaxSpeed && s.Speed > VehicleSettings.MaxForwardSpeed);
        s.Position = new Vector3(0f, 0f, -70f);
        Run(ref s, Drive(1f), 120);
        Assert.Equal(VehicleSettings.MaxForwardSpeed, s.Speed, 3);
    }

    [Fact]
    public void Brake_Coast_Reverse_AndOppositeInputBrakes()
    {
        var s = new VehicleMove { Speed = 12f };
        Run(ref s, Drive(0f, 0f, InputButtons.Jump), 15);   // 0.5 s x 24 m/s^2 = 12
        Assert.Equal(0f, s.Speed, 3);

        s.Speed = 4f;
        Run(ref s, Drive(0f), 15);   // coasting 4 m/s^2 for 0.5 s
        Assert.Equal(2f, s.Speed, 2);

        s.Speed = 6f;
        Run(ref s, Drive(-1f), 1);   // against the motion = brake, never straight into reverse
        Assert.Equal(6f - VehicleSettings.BrakeDeceleration * Dt, s.Speed, 3);
        Run(ref s, Drive(-1f), 60);
        Assert.Equal(-VehicleSettings.MaxReverseSpeed, s.Speed, 3);   // reversing tops out at 6
        Run(ref s, Drive(-1f, 0f, InputButtons.Sprint), 60);
        Assert.Equal(-VehicleSettings.MaxReverseSpeed, s.Speed, 3);   // no boost backwards
    }

    [Fact]
    public void Steering_TurnsOnlyWhileMoving_ScaledBySpeed_AndReversedBackwards()
    {
        var s = new VehicleMove();
        Run(ref s, Drive(0f, 1f), 30);
        Assert.Equal(0f, s.Heading);
        Assert.Equal(1f, s.Steer);

        s = new VehicleMove { Speed = 10f };
        VehicleSimulation.Step(ref s, Drive(1f, 1f), true, Dt, ReadOnlySpan<Box>.Empty, ReadOnlySpan<Slope>.Empty, HeightField.Flat, out _);
        Assert.Equal(VehicleSettings.TurnRateDegrees * Dt, s.Heading, 3);   // full rate at >= 5 m/s

        s = new VehicleMove { Speed = 2.5f };
        VehicleSimulation.Step(ref s, Drive(0f, 1f), true, Dt, ReadOnlySpan<Box>.Empty, ReadOnlySpan<Slope>.Empty, HeightField.Flat, out _);
        float speed = 2.5f - VehicleSettings.CoastDeceleration * Dt;
        Assert.Equal(VehicleSettings.TurnRateDegrees * speed / VehicleSettings.FullTurnSpeed * Dt, s.Heading, 3);

        s = new VehicleMove { Speed = -6f };
        VehicleSimulation.Step(ref s, Drive(-1f, 1f), true, Dt, ReadOnlySpan<Box>.Empty, ReadOnlySpan<Slope>.Empty, HeightField.Flat, out _);
        Assert.True(s.Heading > 350f, $"{s.Heading}");   // reversing with right steer turns the other way
    }

    [Fact]
    public void NoDriver_Brakes()
    {
        var s = new VehicleMove { Speed = 20f, Steer = 1f };
        Run(ref s, Drive(1f, 1f, InputButtons.Sprint), 30, hasDriver: false);
        Assert.Equal(0f, s.Speed);
        Assert.Equal(0f, s.Steer);
    }

    [Fact]
    public void Step_IsDeterministic()
    {
        var a = new VehicleMove { Position = new Vector3(3f, 0f, -4f), Heading = 33f };
        var b = a;
        var rng = new Random(19);
        for (int i = 0; i < 600; i++)
        {
            var input = Drive((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), rng.Next(4) == 0 ? InputButtons.Sprint : InputButtons.None);
            CollisionWorld world = new();
            world.Gather(a.Position, 0, 0, null);
            VehicleSimulation.Step(ref a, input, true, Dt, world, GameMap.Terrain, out VehicleStepResult ra);
            world.Gather(b.Position, 0, 0, null);
            VehicleSimulation.Step(ref b, input, true, Dt, world, GameMap.Terrain, out VehicleStepResult rb);
            Assert.Equal(a.Position, b.Position);
            Assert.Equal(a.Heading, b.Heading);
            Assert.Equal(a.Speed, b.Speed);
            Assert.Equal(ra.Blocked, rb.Blocked);
        }
    }

    [Fact]
    public void ABox_StopsTheCar_RestoresPositionAndHeading_AndReportsTheImpactSpeed()
    {
        // A wall across +Z, 5 m ahead of the centre (the front square reaches 2.2 m ahead).
        Box[] wall = { new(new Vector3(-5f, 0f, 5f), new Vector3(5f, 3f, 6f)) };
        var s = new VehicleMove { Speed = 15f, Heading = 0f };
        VehicleStepResult r = default;
        Vector3 before = s.Position;
        float heading = 0f;
        for (int i = 0; i < 60 && !r.Blocked; i++)
        {
            before = s.Position;
            heading = s.Heading;
            r = Run(ref s, Drive(1f, 0.3f), 1, wall);
        }
        Assert.True(r.Blocked);
        Assert.Equal(before, s.Position);
        Assert.Equal(heading, s.Heading);
        Assert.Equal(0f, s.Speed);
        Assert.True(r.ImpactSpeed > 15f);
        Assert.Equal(new ColliderId(ColliderKind.Static, 0), r.BlockedBy);
        Assert.False(VehicleSimulation.Touches(VehicleSimulation.FootprintBox(s.Position, s.Heading, 0), wall[0]));

        // Pressing on keeps it out of the wall: it never ends a tick inside it.
        for (int i = 0; i < 120; i++)
        {
            Run(ref s, Drive(1f, 1f), 1, wall);
            Assert.False(VehicleSimulation.Touches(VehicleSimulation.FootprintBox(s.Position, s.Heading, 0), wall[0]));
            Assert.False(VehicleSimulation.Touches(VehicleSimulation.FootprintBox(s.Position, s.Heading, 1), wall[0]));
        }
    }

    [Fact]
    public void ALowBox_IsDrivenOver_ABoxAlreadyOverlapped_IsIgnored()
    {
        Box[] low = { new(new Vector3(-5f, 0f, 3f), new Vector3(5f, 0.4f, 4f)) };   // under the body bottom (0.5)
        var s = new VehicleMove();
        Run(ref s, Drive(1f), 45, low);
        Assert.True(s.Position.Z > 5f);

        // A piece built onto the car: it can drive out of it.
        Box[] onTop = { new(new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 3f, 0.5f)) };
        s = new VehicleMove();
        VehicleStepResult r = Run(ref s, Drive(1f), 45, onTop);
        Assert.False(r.Blocked);
        Assert.True(s.Position.Z > 3f);
    }

    [Fact]
    public void ASlopeBoundary_BlocksLikeAWall()
    {
        Slope[] ramp = { new(-2.5f, 4f, 2.5f, 9f, 0f, SlopeKind.Ramp, 0) };
        var s = new VehicleMove { Speed = 10f };
        VehicleStepResult r = default;
        for (int i = 0; i < 30 && !r.Blocked; i++)
            VehicleSimulation.Step(ref s, Drive(1f), true, Dt, ReadOnlySpan<Box>.Empty, ramp, HeightField.Flat, out r);
        Assert.True(r.Blocked);
        Assert.True(VehicleSimulation.FootprintBox(s.Position, s.Heading, 0).Max.Z <= 4f);
    }

    [Fact]
    public void TheMapBound_Blocks()
    {
        var s = new VehicleMove { Position = new Vector3(0f, 0f, VehicleSettings.MapBound - 0.1f), Speed = 10f };
        VehicleStepResult r = Run(ref s, Drive(1f), 1);
        Assert.True(r.Blocked);
        Assert.Equal(VehicleSettings.MapBound - 0.1f, s.Position.Z);
    }

    [Fact]
    public void ASteepRise_Blocks_TheHeightIsTheHighestOfCentreAndAxles()
    {
        // A ramp of rise 1 per metre along +Z from z = 2 (steeper than MaxClimbSlope).
        var heights = new float[11 * 11];
        for (int j = 0; j < 11; j++)
            for (int i = 0; i < 11; i++)
                heights[i + j * 11] = Math.Max(0f, (j - 2) * 1f);
        var terrain = new HeightField(-5f, -5f, 1f, 11, 11, heights);
        var s = new VehicleMove { Position = new Vector3(0f, 0f, -3f), Speed = 6f };
        VehicleStepResult r = default;
        for (int i = 0; i < 60 && !r.Blocked; i++)
            VehicleSimulation.Step(ref s, Drive(1f), true, Dt, ReadOnlySpan<Box>.Empty, ReadOnlySpan<Slope>.Empty, terrain, out r);
        Assert.True(r.Blocked);
        Assert.True(r.BlockedBy.IsNone);   // the terrain, not a collider

        float y = VehicleSimulation.GroundHeight(0f, -4.5f, 0f, terrain);   // front axle at -3.4: flat
        Assert.Equal(0f, y);
        Assert.Equal(terrain.Height(0f, -2.9f + 1.1f), VehicleSimulation.GroundHeight(0f, -2.9f, 0f, terrain), 4);
    }

    [Fact]
    public void BadInput_IsClampedAndSanitized()
    {
        var s = new VehicleMove();
        Run(ref s, new InputCommand { MoveY = float.NaN, MoveX = float.PositiveInfinity }, 10);
        Assert.Equal(0f, s.Speed);
        s = new VehicleMove();
        Run(ref s, new InputCommand { MoveY = 50f, MoveX = -50f }, 30);
        Assert.Equal(VehicleSettings.Acceleration, s.Speed, 2);
        Assert.Equal(-1f, s.Steer);
    }

    [Fact]
    public void Seats_Footprint_AndEnterDistance()
    {
        var pos = new Vector3(10f, 2f, 20f);
        // Heading 90 faces +X: the driver (left) is on the +Z side... left of +X is +Z.
        Vector3 driver = VehicleSimulation.SeatPosition(pos, 90f, VehicleSettings.DriverSeat);
        Vector3 passenger = VehicleSimulation.SeatPosition(pos, 90f, VehicleSettings.PassengerSeat);
        Assert.Equal(10.2f, driver.X, 3);
        Assert.Equal(20.5f, driver.Z, 3);
        Assert.Equal(19.5f, passenger.Z, 3);
        Assert.Equal(2.6f, driver.Y, 3);

        Box front = VehicleSimulation.FootprintBox(pos, 90f, 0);
        Assert.Equal(new Vector3(11.1f - 1.1f, 2.5f, 18.9f), front.Min);
        Assert.Equal(0f, VehicleSimulation.DistanceToBody(new Vector3(11f, 3f, 20f), pos, 90f));
        // 1 m beside the body at ground level: sqrt(1^2 + 0.5^2) (the body starts 0.5 m up).
        Assert.Equal(MathF.Sqrt(1.25f), VehicleSimulation.DistanceToBody(new Vector3(10f, 2f, 22.1f), pos, 90f), 4);
        Assert.True(VehicleSimulation.DistanceToBody(new Vector3(10f, 2f, 23f), pos, 90f) > VehicleSettings.EnterRange);
    }

    [Fact]
    public void NormalizeHeading_Wraps()
    {
        Assert.Equal(350f, VehicleSimulation.NormalizeHeading(-10f), 3);
        Assert.Equal(10f, VehicleSimulation.NormalizeHeading(370f), 3);
        Assert.Equal(0f, VehicleSimulation.NormalizeHeading(float.NaN));
    }
}
