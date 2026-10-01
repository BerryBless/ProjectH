using System;
using System.Numerics;
using ProjectH.Server.Game.Flow;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 12 D5, D6 (spec §2 공중 투입): the route, riding it, jumping, freefall, the glider and the landing, on the
// shared simulation alone. Match integration is MatchDeploymentTests.
public class DeploymentMovementTests
{
    private const float Dt = 1f / 30f;
    private const int SimHz = 30;

    private static readonly InputCommand Idle = new();
    private static readonly InputCommand JumpPress = new() { Buttons = InputButtons.Jump };

    // A route along +X at 90 m: x -100..100 over 300 ticks, starting at tick 1000.
    private static DropRoute AlongX() => new()
    {
        StartX = -100f, StartZ = 0f, EndX = 100f, EndZ = 0f, Altitude = 90f, StartTick = 1000, DurationTicks = 300,
    };

    private static MoveState Freefalling(Vector3 at) => new() { Position = at, Mode = MovementMode.Freefall };

    // ---- Route (D5) ----

    [Fact]
    public void ThePlannedRoute_GoesThroughTheCentre_FromOutsideToOutside_AtTwentyMetresPerSecond()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            DropRoute route = DropPlanner.Plan(seed, 500, SimHz);
            Vector3 middle = route.PositionAt(route.StartTick + route.DurationTicks / 2.0);
            Assert.InRange(MathF.Abs(middle.X), 0f, 0.5f);
            Assert.InRange(MathF.Abs(middle.Z), 0f, 0.5f);
            Assert.Equal(MovementTuning.TransportAltitude, middle.Y);
            // Each end is TransportOutsideMargin along the route beyond where it crosses the outer wall.
            float length = Vector2.Distance(new Vector2(route.StartX, route.StartZ), new Vector2(route.EndX, route.EndZ));
            float axis = MathF.Max(MathF.Abs(route.EndX), MathF.Abs(route.EndZ)) / (length * 0.5f);   // max(|dx|, |dz|)
            Assert.Equal(GameMap.HalfSize / axis + MovementTuning.TransportOutsideMargin, length * 0.5f, 2);
            Assert.True(MathF.Max(MathF.Abs(route.StartX), MathF.Abs(route.StartZ)) > GameMap.HalfSize + 10f);
            Assert.Equal(length / MovementTuning.TransportSpeed * SimHz, route.DurationTicks, 0.999);
        }
    }

    // Final review B3: measured over many seeds, a route is 200 m (an axis, 10 s) to 2 x (half-diagonal + 20) = about
    // 266 m (a diagonal, about 13.3 s).
    [Fact]
    public void TheRouteLength_IsTwoHundredToAbout266Metres()
    {
        float shortest = float.MaxValue;
        float longest = 0f;
        uint longestTicks = 0;
        for (int seed = 0; seed < 5000; seed++)
        {
            DropRoute route = DropPlanner.Plan(seed, 0, SimHz);
            float length = Vector2.Distance(new Vector2(route.StartX, route.StartZ), new Vector2(route.EndX, route.EndZ));
            shortest = MathF.Min(shortest, length);
            if (length > longest)
            {
                longest = length;
                longestTicks = route.DurationTicks;
            }
        }
        float diagonal = 2f * (GameMap.HalfSize * MathF.Sqrt(2f) + MovementTuning.TransportOutsideMargin);
        Assert.InRange(shortest, 200f, 200.5f);
        Assert.InRange(longest, diagonal - 0.5f, diagonal + 0.01f);
        Assert.InRange(diagonal, 266f, 266.5f);
        Assert.InRange(longestTicks, 13.2f * SimHz, 13.35f * SimHz);
    }

    [Fact]
    public void TheSameSeed_GivesTheSameRoute_AndSeedsDiffer()
    {
        Assert.Equal(DropPlanner.Plan(7, 10, SimHz), DropPlanner.Plan(7, 10, SimHz));
        Assert.NotEqual(DropPlanner.Plan(7, 10, SimHz).EndX, DropPlanner.Plan(8, 10, SimHz).EndX);
    }

    [Fact]
    public void AnAxisRoute_IsTwoHundredMetres_TenSeconds()
    {
        DropRoute route = AlongX();
        Assert.Equal(new Vector3(-100f, 90f, 0f), route.PositionAt(route.StartTick));
        Assert.Equal(new Vector3(0f, 90f, 0f), route.PositionAt(route.StartTick + 150));
        Assert.Equal(new Vector3(100f, 90f, 0f), route.PositionAt(route.EndTick + 50));   // held at the end
        Assert.Equal(new Vector3(-100f, 90f, 0f), route.PositionAt(0));                  // and before the start
    }

    [Fact]
    public void TheJumpWindow_IsWhereTheTransportIsTenMetresInsideTheWalls()
    {
        DropRoute route = AlongX();
        route.JumpWindow(out uint first, out uint last);
        // x = -70 at 30/200 of the route (tick 1045), x = +70 at 170/200 (tick 1255).
        Assert.Equal(1045u, first);
        Assert.Equal(1255u, last);
        Assert.True(route.PositionAt(first - 1).X < -70f);
        Assert.True(route.PositionAt(last + 1).X > 70f);

        // A diagonal route: the square's corner, about 99 m from the centre.
        DropRoute diagonal = DropPlanner.Plan(0, 0, SimHz);
        diagonal.JumpWindow(out first, out last);
        for (uint t = first; t <= last; t++)
        {
            Vector3 p = diagonal.PositionAt(t);
            Assert.True(MathF.Abs(p.X) <= 70.01f && MathF.Abs(p.Z) <= 70.01f);
        }
    }

    // ---- Riding and jumping (D5, D6) ----

    [Fact]
    public void Ride_PlacesTheRiderOnTheRoute_AndIgnoresOtherModes()
    {
        DropRoute route = AlongX();
        var s = new MoveState { Mode = MovementMode.Transport };
        Assert.True(DropTransport.Ride(ref s, new InputCommand { Yaw = 45f }, route, 1100));
        Assert.Equal(route.PositionAt(1100), s.Position);
        Assert.Equal(45f, s.Yaw);
        Assert.Equal(MovementMode.Transport, s.Mode);

        var walker = new MoveState { Position = new Vector3(1f, 0f, 2f) };
        Assert.False(DropTransport.Ride(ref walker, JumpPress, route, 1100));
        Assert.Equal(new Vector3(1f, 0f, 2f), walker.Position);

        // Final review C15: a jumper (freefall or glide) is not touched either: not placed, not turned, mode kept.
        foreach (MovementMode mode in new[] { MovementMode.Freefall, MovementMode.Glide })
        {
            var jumper = new MoveState { Position = new Vector3(5f, 60f, 6f), Mode = mode, VelocityY = -10f, Yaw = 30f };
            MoveState before = jumper;
            Assert.False(DropTransport.Ride(ref jumper, new InputCommand { Yaw = 45f, Buttons = InputButtons.Jump }, route, 1100));
            Assert.Equal(before, jumper);
        }
    }

    // Final review C15: the transport flies inside the snapshot's +-128 m position range (SnapshotEntity), with the
    // character's height above it to spare.
    [Fact]
    public void TheTransportAltitude_FitsTheSnapshotRange()
    {
        Assert.InRange(MovementTuning.TransportAltitude + MoveSettings.Height, 0f, 127f);
    }

    [Fact]
    public void AJump_BeforeTheWindow_IsIgnored_InsideItDropsIntoFreefall()
    {
        DropRoute route = AlongX();
        var s = new MoveState { Mode = MovementMode.Transport };
        DropTransport.Ride(ref s, JumpPress, route, 1040);
        Assert.Equal(MovementMode.Transport, s.Mode);
        DropTransport.Ride(ref s, JumpPress, route, 1100);
        Assert.Equal(MovementMode.Freefall, s.Mode);
        Assert.Equal(route.PositionAt(1100), s.Position);
        Assert.Equal(Vector2.Zero, s.HorizontalVelocity);
        Assert.False(DropTransport.Ride(ref s, JumpPress, route, 1101));   // no longer riding: a second jump does nothing here
    }

    [Fact]
    public void TheJumpPress_DoesNotAlsoOpenTheGlider()
    {
        DropRoute route = AlongX();
        var s = new MoveState { Mode = MovementMode.Transport };
        if (!DropTransport.Ride(ref s, JumpPress, route, 1100)) MovementSimulation.Step(ref s, JumpPress, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        MovementSimulation.Step(ref s, Idle, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.Equal(MovementMode.Freefall, s.Mode);
    }

    [Fact]
    public void AtTheWindowsEnd_TheRiderIsDropped()
    {
        DropRoute route = AlongX();
        var s = new MoveState { Mode = MovementMode.Transport };
        for (uint tick = 1000; tick < 1255; tick++)
        {
            DropTransport.Ride(ref s, Idle, route, tick);
            Assert.Equal(MovementMode.Transport, s.Mode);
        }
        DropTransport.Ride(ref s, Idle, route, 1255);
        Assert.Equal(MovementMode.Freefall, s.Mode);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(30.0)]
    [InlineData(45.0)]
    [InlineData(200.0)]
    public void ARiderWhoSendsNothing_IsDropped_AndLandsInsideTheWalls(double degrees)
    {
        // D16: a graced rider coasts on empty input: dropped at the window's end, falls straight, glides down.
        float dx = (float)Math.Sin(degrees * Math.PI / 180.0);
        float dz = (float)Math.Cos(degrees * Math.PI / 180.0);
        float half = GameMap.HalfSize / MathF.Max(MathF.Abs(dx), MathF.Abs(dz)) + MovementTuning.TransportOutsideMargin;
        var route = new DropRoute
        {
            StartX = -dx * half, StartZ = -dz * half, EndX = dx * half, EndZ = dz * half, Altitude = 90f,
            StartTick = 0, DurationTicks = (uint)MathF.Ceiling(2f * half / MovementTuning.TransportSpeed * SimHz),
        };
        var s = new MoveState { Mode = MovementMode.Transport };
        uint tick = 0;
        for (; tick < 2000 && s.Mode != MovementMode.Ground; tick++)
        {
            if (!DropTransport.Ride(ref s, Idle, route, tick)) MovementSimulation.Step(ref s, Idle, Dt, GameMap.Boxes, GameMap.Terrain);
        }
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.True(MathF.Abs(s.Position.X) < GameMap.HalfSize && MathF.Abs(s.Position.Z) < GameMap.HalfSize, $"landed at {s.Position}");
        Assert.False(MovementSimulation.OverlapsAny(s.Position, GameMap.Boxes));
    }

    // ---- Freefall (D6) ----

    [Fact]
    public void Freefall_ReachesThirtyMetresPerSecond_AndNoMore()
    {
        MoveState s = Freefalling(new Vector3(0f, 2000f, 0f));
        for (int i = 0; i < 90; i++) MovementSimulation.Step(ref s, Idle, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.Equal(-MovementTuning.FreefallTerminalSpeed, s.VelocityY);
        Assert.Equal(MovementMode.Freefall, s.Mode);
    }

    [Theory]
    [InlineData(0f, 1f, 15f)]    // forward
    [InlineData(1f, 0f, 10f)]    // side
    [InlineData(0f, -1f, 6f)]    // back
    public void Freefall_Steering_HasForwardSideAndBackLimits(float moveX, float moveY, float limit)
    {
        MoveState s = Freefalling(new Vector3(0f, 2000f, 0f));
        var input = new InputCommand { MoveX = moveX, MoveY = moveY, Yaw = 30f };
        MovementSimulation.Step(ref s, input, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.Equal(MovementTuning.FreefallAcceleration * Dt, s.HorizontalVelocity.Length(), 4);
        for (int i = 0; i < 60; i++) MovementSimulation.Step(ref s, input, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.Equal(limit, s.HorizontalVelocity.Length(), 3);
    }

    [Fact]
    public void Freefall_AndGlide_IgnoreCrouch_AndNeverSlide()
    {
        MoveState s = Freefalling(new Vector3(0f, 2000f, 0f));
        var crouch = new InputCommand { MoveY = 1f, Buttons = InputButtons.Crouch | InputButtons.Sprint };
        for (int i = 0; i < 30; i++) MovementSimulation.Step(ref s, crouch, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.Equal(MovementMode.Freefall, s.Mode);
        MovementSimulation.Step(ref s, JumpPress, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        for (int i = 0; i < 30; i++) MovementSimulation.Step(ref s, crouch, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.Equal(MovementMode.Glide, s.Mode);
    }

    // ---- Glide (D6) ----

    [Fact]
    public void AJumpPress_InFreefall_OpensTheGlider_ThatNeverCloses()
    {
        MoveState s = Freefalling(new Vector3(0f, 500f, 0f));
        MovementSimulation.Step(ref s, JumpPress, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.Equal(MovementMode.Glide, s.Mode);
        Assert.Equal(-MovementTuning.GlideFallSpeed, s.VelocityY);
        for (int i = 0; i < 30; i++) MovementSimulation.Step(ref s, JumpPress, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.Equal(MovementMode.Glide, s.Mode);
    }

    [Theory]
    [InlineData(0f, 1f, 14f)]
    [InlineData(-1f, 0f, 10f)]
    [InlineData(0f, -1f, 4f)]
    public void Glide_Steering_HasForwardSideAndBackLimits(float moveX, float moveY, float limit)
    {
        var s = new MoveState { Position = new Vector3(0f, 500f, 0f), Mode = MovementMode.Glide };
        var input = new InputCommand { MoveX = moveX, MoveY = moveY, Yaw = 300f };
        for (int i = 0; i < 90; i++) MovementSimulation.Step(ref s, input, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.Equal(limit, s.HorizontalVelocity.Length(), 3);
        Assert.Equal(-MovementTuning.GlideFallSpeed, s.VelocityY);
    }

    [Fact]
    public void TheGlider_OpensItself_ThirtyMetresAboveTheTerrain()
    {
        MoveState s = Freefalling(new Vector3(44f, 90f, -44f));   // over the Lookout plateau (6 m)
        float terrain = GameMap.Terrain.Height(44f, -44f);
        while (s.Mode == MovementMode.Freefall) MovementSimulation.Step(ref s, Idle, Dt, GameMap.Boxes, GameMap.Terrain);
        Assert.Equal(MovementMode.Glide, s.Mode);
        float height = s.Position.Y - terrain;
        Assert.InRange(height, MovementTuning.GlideAutoDeployHeight - 1.1f, MovementTuning.GlideAutoDeployHeight);
    }

    [Fact]
    public void TheGlider_OpensItself_ThirtyMetresAboveABoxTop()
    {
        Box[] tower = { new(new Vector3(-5f, 0f, -5f), new Vector3(5f, 40f, 5f)) };
        MoveState s = Freefalling(new Vector3(0f, 100f, 0f));
        while (s.Mode == MovementMode.Freefall) MovementSimulation.Step(ref s, Idle, Dt, tower, HeightField.Flat);
        Assert.InRange(s.Position.Y - 40f, MovementTuning.GlideAutoDeployHeight - 1.1f, MovementTuning.GlideAutoDeployHeight);
    }

    [Fact]
    public void GlideLanding_IsGroundMode_WithNoHorizontalVelocity_AndNoLandingSpeed()
    {
        var s = new MoveState { Position = new Vector3(0f, 3f, 0f), Mode = MovementMode.Glide, HorizontalVelocity = new Vector2(10f, 0f) };
        StepResult result = default;
        for (int i = 0; i < 40 && s.Mode == MovementMode.Glide; i++)
            MovementSimulation.Step(ref s, new InputCommand { MoveY = 1f, Yaw = 90f }, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat, out result);
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.Equal(Vector2.Zero, s.HorizontalVelocity);
        Assert.Equal(0f, s.Position.Y);
        Assert.Equal(0f, result.LandingSpeed);
        Assert.True(MovementSimulation.IsGrounded(s, ReadOnlySpan<Box>.Empty, HeightField.Flat));
    }

    [Fact]
    public void Gliding_IntoTheOuterWall_StaysInside()
    {
        // The air is clamped at HalfSize - HalfWidth - Skin (the outer walls are only 4 m high).
        const float bound = GameMap.HalfSize - MoveSettings.HalfWidth - MoveSettings.Skin;
        var s = new MoveState { Position = new Vector3(75f, 20f, 0f), Mode = MovementMode.Glide };
        var east = new InputCommand { MoveY = 1f, Yaw = 90f };
        bool reached = false;
        for (int i = 0; i < 200 && s.Mode == MovementMode.Glide; i++)
        {
            MovementSimulation.Step(ref s, east, Dt, GameMap.Boxes, GameMap.Terrain);
            Assert.True(s.Position.X <= bound);
            if (s.Mode == MovementMode.Glide && s.Position.X == bound)
            {
                reached = true;
                Assert.Equal(0f, s.HorizontalVelocity.X);
            }
        }
        Assert.True(reached, "the glider was held at the clamp");
        Assert.Equal(MovementMode.Ground, s.Mode);
    }

    [Fact]
    public void ATransportRider_StepsOnlyItsYaw()
    {
        var s = new MoveState { Position = new Vector3(1f, 90f, 2f), Mode = MovementMode.Transport };
        MovementSimulation.Step(ref s, new InputCommand { MoveY = 1f, Yaw = 10f, Buttons = InputButtons.Jump }, Dt, GameMap.Boxes, GameMap.Terrain);
        Assert.Equal(new Vector3(1f, 90f, 2f), s.Position);
        Assert.Equal(10f, s.Yaw);
        Assert.Equal(MovementMode.Transport, s.Mode);
    }
}
