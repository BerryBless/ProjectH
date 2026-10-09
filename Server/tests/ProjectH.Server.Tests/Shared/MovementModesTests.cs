using System;
using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 12 spec §2: sprint and energy, crouch, slide, air momentum and the landing speed (D3, D7, D10), on the flat
// floor unless a test builds its own world. Every expectation follows the 30 Hz step rule.
public class MovementModesTests
{
    private const float Dt = 1f / 30f;

    private static readonly InputCommand Sprint = new() { MoveY = 1f, Buttons = InputButtons.Sprint };
    private static readonly InputCommand Walk = new() { MoveY = 1f };
    private static readonly InputCommand Idle = new();

    // 기능: 최소·최대 모서리 좌표 여섯 값으로 충돌 상자를 만든다.
    // 입력: minX/minY/minZ - 최소 모서리 좌표, maxX/maxY/maxZ - 최대 모서리 좌표.
    // 출력: 지정한 범위의 Box.
    private static Box B(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        => new(new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ));

    // 기능: 같은 입력으로 이동 Step을 steps번 반복해 상태를 진행시킨다.
    // 입력: s - 진행시킬 이동 상태, input - 매 Step에 줄 입력, steps - 반복 횟수, world - 충돌 상자들(기본 없음), terrain - 지형(null이면 평지).
    // 출력: 반환값 없음. s가 steps Step만큼 진행된다.
    private static void Run(ref MoveState s, InputCommand input, int steps, ReadOnlySpan<Box> world = default, HeightField? terrain = null)
    {
        for (int i = 0; i < steps; i++) MovementSimulation.Step(ref s, input, Dt, world, terrain ?? HeightField.Flat);
    }

    // 기능: 이동 Step을 한 번 돌리고 그 Step의 결과를 받는다.
    // 입력: s - 이동 상태, input - 입력, world - 충돌 상자들(기본 없음), terrain - 지형(null이면 평지).
    // 출력: 그 Step의 StepResult(Sprinting·Charging·착지 속도·막은 상자 등). s는 한 Step 진행된다.
    private static StepResult StepOnce(ref MoveState s, InputCommand input, ReadOnlySpan<Box> world = default, HeightField? terrain = null)
    {
        MovementSimulation.Step(ref s, input, Dt, world, terrain ?? HeightField.Flat, out StepResult result);
        return result;
    }

    // 기능: 입력 복사본에 버튼을 더한다.
    // 입력: input - 바탕 입력(값 복사), extra - 추가로 누를 버튼.
    // 출력: extra 버튼이 더해진 새 InputCommand. 원본은 바뀌지 않는다.
    private static InputCommand With(InputCommand input, InputButtons extra)
    {
        input.Buttons |= extra;
        return input;
    }

    // ---- Sprint and energy (D3) ----

    [Fact]
    public void ADefaultState_IsRested_InGroundMode()
    {
        var s = new MoveState();
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.Equal(MovementTuning.MaxEnergy, s.Energy);
        Assert.False(s.Exhausted);
    }

    [Fact]
    public void Sprinting_DrainsEnergy_AtTwentyPerSecond_AndMovesAtSprintSpeed()
    {
        var s = new MoveState();
        StepResult r = StepOnce(ref s, Sprint);
        Assert.True(r.Sprinting);
        Assert.Equal(MoveSettings.SprintSpeed, s.HorizontalVelocity.Length(), 4);
        Run(ref s, Sprint, 29);
        // 67 hundredths per tick (20 x 100 / 30, rounded) for 30 ticks.
        Assert.Equal(100f - 30 * 0.67f, s.Energy, 3);
        Assert.Equal(MoveSettings.SprintSpeed, s.Position.Z, 3);
    }

    [Fact]
    public void SprintSpeed_IsTheLimit_ForAnyInput()
    {
        var s = new MoveState();
        Run(ref s, new InputCommand { MoveY = 1000f, MoveX = 5f, Buttons = InputButtons.Sprint }, 30);
        Assert.Equal(MoveSettings.SprintSpeed, new Vector2(s.Position.X, s.Position.Z).Length(), 3);
    }

    [Fact]
    public void SprintWithoutMoving_CostsNothing()
    {
        var s = new MoveState();
        StepResult r = StepOnce(ref s, new InputCommand { Buttons = InputButtons.Sprint });
        Assert.False(r.Sprinting);
        Assert.Equal(MovementTuning.MaxEnergy, s.Energy);
    }

    [Fact]
    public void EmptyEnergy_EndsTheSprint_UntilTwentyIsBack()
    {
        var s = new MoveState();
        int ticks = 0;
        while (!s.Exhausted && ticks < 300)
        {
            StepOnce(ref s, Sprint);
            ticks++;
        }
        Assert.True(s.Exhausted);
        Assert.Equal(150, ticks);   // 10000 / 67 rounded up: about 5 s
        Assert.Equal(0f, s.Energy);

        // Shift still held: walking speed, no cost, and the energy comes back after the delay.
        StepResult r = StepOnce(ref s, Sprint);
        Assert.False(r.Sprinting);
        Assert.Equal(MoveSettings.WalkSpeed, s.HorizontalVelocity.Length(), 4);

        int more = 0;
        while (s.Exhausted && more < 300)
        {
            r = StepOnce(ref s, Sprint);
            Assert.False(r.Sprinting);
            more++;
        }
        Assert.True(s.Energy >= MovementTuning.SprintResumeEnergy);
        Assert.True(StepOnce(ref s, Sprint).Sprinting);
    }

    [Fact]
    public void Recovery_WaitsOneSecond_ThenRecoversTwentyFivePerSecond()
    {
        var s = new MoveState();
        Run(ref s, Sprint, 60);
        float afterSprint = s.Energy;
        Run(ref s, Walk, 30);   // the 1 s delay: nothing comes back
        Assert.Equal(afterSprint, s.Energy, 3);
        Run(ref s, Walk, 30);   // then 83 hundredths per tick
        Assert.Equal(afterSprint + 30 * 0.83f, s.Energy, 3);
    }

    [Fact]
    public void Energy_NeverExceedsTheMaximum()
    {
        var s = new MoveState();
        Run(ref s, Sprint, 3);
        Run(ref s, Idle, 200);
        Assert.Equal(MovementTuning.MaxEnergy, s.Energy);
        Assert.Equal(0, s.EnergySpent);
    }

    // ---- Crouch (D7) ----

    [Fact]
    public void Crouch_LowersTheBox_To1Point2_AndWalksAt2Point5()
    {
        var s = new MoveState();
        StepOnce(ref s, With(Walk, InputButtons.Crouch));
        Assert.Equal(MovementMode.Crouch, s.Mode);
        Assert.Equal(1.2f, MovementSimulation.CollisionHeight(s.Mode));
        Run(ref s, With(Walk, InputButtons.Crouch), 29);
        Assert.Equal(MovementTuning.CrouchSpeed, s.Position.Z, 1);
    }

    [Fact]
    public void Crouch_PassesUnderALowGap_ThatStandingCannot()
    {
        // A slab from 1.5 m up across the way: 1.2 m fits under it, 1.8 m does not.
        Box[] slab = { B(-3f, 1.5f, 2f, 3f, 2f, 3f) };
        var standing = new MoveState();
        Run(ref standing, Walk, 60, slab);
        Assert.True(standing.Position.Z < 2f);

        var crouched = new MoveState();
        Run(ref crouched, With(Walk, InputButtons.Crouch), 120, slab);
        Assert.True(crouched.Position.Z > 3f);
        Assert.False(MovementSimulation.OverlapsAny(crouched.Position, 1.2f, slab));
    }

    [Fact]
    public void Crouch_UnderACeiling_StaysDown_UntilThereIsRoom()
    {
        Box[] ceiling = { B(-2f, 1.5f, -2f, 2f, 2f, 2f) };
        var s = new MoveState { Position = new Vector3(0f, 0f, -4f) };
        Run(ref s, With(Walk, InputButtons.Crouch), 50, ceiling);   // crouch-walks in under it
        Assert.Equal(MovementMode.Crouch, s.Mode);
        Assert.False(MovementSimulation.CanStand(s.Position, ceiling));

        Run(ref s, Idle, 10, ceiling);   // released, but no room: still down, and a jump cannot stand it up either
        Assert.Equal(MovementMode.Crouch, s.Mode);
        StepOnce(ref s, new InputCommand { Buttons = InputButtons.Jump }, ceiling);
        Assert.Equal(MovementMode.Crouch, s.Mode);
        Assert.Equal(0f, s.VelocityY);

        Run(ref s, new InputCommand { MoveY = 1f }, 60, ceiling);   // walks out from under it and stands up
        Assert.Equal(MovementMode.Ground, s.Mode);
    }

    [Fact]
    public void Crouch_DisablesSprint()
    {
        var s = new MoveState();
        StepResult r = StepOnce(ref s, With(Walk, InputButtons.Crouch));   // walking: a crouch, not a slide
        r = StepOnce(ref s, With(Sprint, InputButtons.Crouch));
        Assert.False(r.Sprinting);
        Assert.Equal(MovementTuning.CrouchSpeed, s.HorizontalVelocity.Length(), 4);
    }

    // ---- Slide (D3, D7) ----

    // 기능: 평지에서 5 Tick 질주해 Slide를 시작할 수 있는 상태를 만든다.
    // 입력: 없음.
    // 출력: 질주 속도로 달리는 중인 MoveState.
    private static MoveState Sprinting()
    {
        var s = new MoveState();
        Run(ref s, Sprint, 5);
        return s;
    }

    [Fact]
    public void Slide_StartsFromASprint_AtNineMetresPerSecond()
    {
        MoveState s = Sprinting();
        StepResult r = StepOnce(ref s, With(Sprint, InputButtons.Crouch));
        Assert.Equal(MovementMode.Slide, s.Mode);
        Assert.False(r.Sprinting);
        Assert.True(r.Charging);
        Assert.Equal(MovementTuning.SlideStartSpeed - MovementTuning.SlideFriction * Dt, s.HorizontalVelocity.Length(), 3);
    }

    [Fact]
    public void CrouchWhileWalking_IsACrouch_NotASlide()
    {
        var s = new MoveState();
        Run(ref s, Walk, 5);
        StepOnce(ref s, With(Walk, InputButtons.Crouch));
        Assert.Equal(MovementMode.Crouch, s.Mode);
    }

    // Final review C7: a slide starts only while sprinting (Sprint held, not exhausted, Ground mode, at least
    // SlideMinStartSpeed). Sprint-fast without Sprint, or exhausted, Crouch is a crouch.
    [Fact]
    public void CrouchAtSprintSpeed_WithoutSprintOrExhausted_IsACrouch_NotASlide()
    {
        MoveState s = Sprinting();
        Assert.True(s.HorizontalVelocity.Length() >= MovementTuning.SlideMinStartSpeed);
        StepOnce(ref s, With(Walk, InputButtons.Crouch));
        Assert.Equal(MovementMode.Crouch, s.Mode);

        MoveState tired = Sprinting();
        tired.Exhausted = true;
        StepOnce(ref tired, With(Sprint, InputButtons.Crouch));
        Assert.Equal(MovementMode.Crouch, tired.Mode);
    }

    // C7: a crouch held through the landing of a sprint jump, without Sprint, is a crouch (no slide).
    [Fact]
    public void AHeldCrouchLanding_WithoutSprint_IsACrouch()
    {
        MoveState s = Sprinting();
        StepOnce(ref s, With(Sprint, InputButtons.Jump));
        Assert.True(s.VelocityY > 0f);
        var air = new InputCommand { MoveY = 1f, Buttons = InputButtons.Crouch };
        for (int i = 0; i < 60 && (s.VelocityY != 0f || s.Position.Y > 0f); i++) StepOnce(ref s, air);
        StepOnce(ref s, air);
        Assert.Equal(MovementMode.Crouch, s.Mode);
    }

    // Slide-hop ruling: starting a slide costs SlideStartEnergyCost (15) and restarts the recovery delay, like a sprint tick.
    [Fact]
    public void StartingASlide_CostsExactlyFifteenEnergy_AndRestartsTheRecoveryDelay()
    {
        var s = new MoveState { HorizontalVelocity = new Vector2(0f, MoveSettings.SprintSpeed) };
        StepOnce(ref s, With(Sprint, InputButtons.Crouch));
        Assert.Equal(MovementMode.Slide, s.Mode);
        Assert.Equal(1500, s.EnergySpent);
        Assert.Equal(MovementTuning.MaxEnergy - MovementTuning.SlideStartEnergyCost, s.Energy, 3);
        Assert.Equal(30, s.EnergyDelayTicks);
        Assert.False(s.Exhausted);
    }

    // A Sprint+Crouch hop chain (slide, slide jump, land in a new slide, ...) is not free: each start costs 15 energy, the
    // air recovers nothing within the delay, and once the energy runs out (Exhausted) the next landing is a crouch.
    [Fact]
    public void ASprintCrouchHopChain_RunsOutOfEnergy_AndStops()
    {
        MoveState s = Sprinting();
        var hold = new InputCommand { MoveY = 1f, Buttons = InputButtons.Sprint | InputButtons.Crouch };
        int starts = 0;
        for (int i = 0; i < 2000 && s.Mode != MovementMode.Crouch; i++)
        {
            MovementMode before = s.Mode;
            bool jump = s.Mode == MovementMode.Slide;
            StepOnce(ref s, jump ? With(hold, InputButtons.Jump) : hold);
            if (before != MovementMode.Slide && s.Mode == MovementMode.Slide) starts++;
        }
        Assert.Equal(MovementMode.Crouch, s.Mode);
        Assert.True(s.Exhausted);
        Assert.InRange(starts, 6, 7);   // 100 / 15, less the sprint before it
    }

    [Fact]
    public void Slide_Slows_AndEndsInACrouch_BelowThreeMetresPerSecond()
    {
        MoveState s = Sprinting();
        var slide = new InputCommand { Buttons = InputButtons.Crouch };   // no steering needed: the slide keeps its direction
        StepOnce(ref s, With(Sprint, InputButtons.Crouch));
        float previous = s.HorizontalVelocity.Length();
        int ticks = 0;
        while (s.Mode == MovementMode.Slide && ticks < 100)
        {
            StepOnce(ref s, slide);
            if (s.Mode == MovementMode.Slide)
            {
                Assert.True(s.HorizontalVelocity.Length() < previous);
                previous = s.HorizontalVelocity.Length();
            }
            ticks++;
        }
        Assert.Equal(MovementMode.Crouch, s.Mode);
        // From 9 m/s at 5 m/s per second down to 3 m/s: 1.2 s.
        Assert.InRange(ticks, 35, 37);
    }

    [Fact]
    public void Slide_Downhill_SlowsLess_OrSpeedsUp()
    {
        // A 0.6 slope (the steepest of the map) falling along +X: 6 m high at x = 0, 0 at x = 10.
        var downhill = new HeightField(-10f, -10f, 10f, 3, 3, new[] { 6f, 6f, 0f, 6f, 6f, 0f, 6f, 6f, 0f });
        Assert.Equal(new Vector2(-0.6f, 0f), downhill.Gradient(5f, 0f));

        var flat = new MoveState { Yaw = 90f };
        var slope = new MoveState { Position = new Vector3(0.5f, downhill.Height(0.5f, 0f), 0f), Yaw = 90f };
        var sprintX = new InputCommand { MoveY = 1f, Yaw = 90f, Buttons = InputButtons.Sprint };
        Run(ref flat, sprintX, 3);
        Run(ref slope, sprintX, 3, default, downhill);
        var slideX = new InputCommand { Yaw = 90f, Buttons = InputButtons.Crouch };
        // The slide starts while sprinting (Sprint held, final review C7), then keeps going with Crouch alone.
        StepOnce(ref flat, With(sprintX, InputButtons.Crouch));
        StepOnce(ref slope, With(sprintX, InputButtons.Crouch), default, downhill);
        Run(ref flat, slideX, 9);
        Run(ref slope, slideX, 9, default, downhill);

        Assert.Equal(MovementMode.Slide, slope.Mode);
        // Flat: -5 m/s per second. Downhill: +0.6 x 20 x 0.6 - 5 = +2.2 m/s per second.
        Assert.True(slope.HorizontalVelocity.Length() > MovementTuning.SlideStartSpeed);
        Assert.True(flat.HorizontalVelocity.Length() < MovementTuning.SlideStartSpeed);
    }

    [Fact]
    public void Slide_NeverExceedsThirteenMetresPerSecond()
    {
        // A long 0.6 slope.
        var hill = new HeightField(-100f, -10f, 100f, 3, 2, new[] { 120f, 60f, 0f, 120f, 60f, 0f });
        // Review fix D1: starts inside the map (the ground step now clamps to the outer walls at +-80 m).
        var s = new MoveState { Position = new Vector3(-75f, hill.Height(-75f, 0f), 0f), Yaw = 90f };
        Run(ref s, new InputCommand { MoveY = 1f, Yaw = 90f, Buttons = InputButtons.Sprint }, 3, default, hill);
        StepOnce(ref s, new InputCommand { MoveY = 1f, Yaw = 90f, Buttons = InputButtons.Sprint | InputButtons.Crouch }, default, hill);
        Run(ref s, new InputCommand { Yaw = 90f, Buttons = InputButtons.Crouch }, 150, default, hill);
        Assert.Equal(MovementMode.Slide, s.Mode);
        Assert.Equal(MovementTuning.SlideMaxSpeed, s.HorizontalVelocity.Length(), 3);
    }

    [Fact]
    public void Slide_EndsWithAJump_ThatKeepsTheSlideSpeed()
    {
        MoveState s = Sprinting();
        StepOnce(ref s, With(Sprint, InputButtons.Crouch));
        float speed = s.HorizontalVelocity.Length();
        StepOnce(ref s, new InputCommand { Buttons = InputButtons.Crouch | InputButtons.Jump });
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.True(s.VelocityY > 0f);
        Assert.True(s.HorizontalVelocity.Length() > speed - 0.5f);
    }

    [Fact]
    public void Slide_ReleasingCrouch_StandsUp()
    {
        MoveState s = Sprinting();
        StepOnce(ref s, With(Sprint, InputButtons.Crouch));
        StepOnce(ref s, Sprint);
        Assert.Equal(MovementMode.Ground, s.Mode);
    }

    [Fact]
    public void Slide_IntoAWall_EndsInACrouch_AndReportsTheBox()
    {
        Box[] wall = { B(-3f, 0f, 3f, 3f, 3f, 4f) };
        var s = new MoveState();
        Run(ref s, Sprint, 5, wall);
        StepOnce(ref s, With(Sprint, InputButtons.Crouch), wall);
        Assert.Equal(MovementMode.Slide, s.Mode);
        StepResult r = default;
        for (int i = 0; i < 30 && s.Mode == MovementMode.Slide; i++) r = StepOnce(ref s, new InputCommand { Buttons = InputButtons.Crouch }, wall);
        Assert.Equal(MovementMode.Crouch, s.Mode);
        Assert.Equal(new ColliderId(ColliderKind.Static, 0), r.BlockedBy);
        Assert.True(r.Charging);
    }

    // ---- Air momentum (D3) ----

    [Fact]
    public void SprintJump_TakesOffFaster_AtTheSameHeight()
    {
        var s = new MoveState();
        Run(ref s, Sprint, 3);
        StepOnce(ref s, With(Sprint, InputButtons.Jump));
        Assert.Equal(MoveSettings.SprintSpeed * MovementTuning.SprintJumpSpeedScale, s.HorizontalVelocity.Length(), 3);
        Assert.Equal(MoveSettings.JumpSpeed, s.VelocityY);
    }

    // Final review C8: in the air Sprint is ignored: no energy cost, not sprinting, not charging. The sprint jump keeps
    // the speed it took off with.
    [Fact]
    public void InTheAir_SprintCostsNothing_AndChargesNothing()
    {
        var s = new MoveState();
        Run(ref s, Sprint, 3);
        StepResult takeoff = StepOnce(ref s, With(Sprint, InputButtons.Jump));
        Assert.True(takeoff.Sprinting);
        ushort spent = s.EnergySpent;
        float speed = s.HorizontalVelocity.Length();
        for (int i = 0; i < 10; i++)
        {
            StepResult r = StepOnce(ref s, Sprint);
            Assert.True(s.VelocityY != 0f, "still in the air");
            Assert.False(r.Sprinting);
            Assert.False(r.Charging);
        }
        Assert.Equal(spent, s.EnergySpent);
        Assert.Equal(speed, s.HorizontalVelocity.Length(), 3);
    }

    [Fact]
    public void InTheAir_TheVelocityCarriesOver_WithoutInput()
    {
        var s = new MoveState();
        Run(ref s, Walk, 3);
        StepOnce(ref s, With(Walk, InputButtons.Jump));
        float zAtTakeoff = s.Position.Z;
        Run(ref s, Idle, 10);
        Assert.Equal(zAtTakeoff + 10 * MoveSettings.WalkSpeed * Dt, s.Position.Z, 3);
    }

    [Fact]
    public void AirControl_SteersAtTwelve_ButNeverAboveTheTakeoffSpeed()
    {
        var s = new MoveState();
        Run(ref s, Sprint, 3);
        StepOnce(ref s, With(Sprint, InputButtons.Jump));
        float takeoff = s.HorizontalVelocity.Length();
        // Turn to the side in the air: the velocity turns, its length never grows.
        for (int i = 0; i < 15; i++)
        {
            StepOnce(ref s, new InputCommand { MoveX = 1f });
            Assert.True(s.HorizontalVelocity.Length() <= takeoff + 1e-4f);
        }
        Assert.True(s.HorizontalVelocity.X > 0f);
    }

    [Fact]
    public void AStandingJump_DriftsAtMostAtWalkingSpeed()
    {
        var s = new MoveState();
        StepOnce(ref s, new InputCommand { Buttons = InputButtons.Jump });
        StepOnce(ref s, Walk);
        Assert.Equal(MovementTuning.AirAcceleration * Dt, s.HorizontalVelocity.Length(), 4);
        Run(ref s, Walk, 15);
        Assert.Equal(MoveSettings.WalkSpeed, s.HorizontalVelocity.Length(), 4);
    }

    [Fact]
    public void AWallInTheAir_StopsThatPartOfTheVelocity()
    {
        Box[] wall = { B(-3f, 0f, 1f, 3f, 3f, 2f) };
        var s = new MoveState();
        StepOnce(ref s, With(Sprint, InputButtons.Jump), wall);
        for (int i = 0; i < 10; i++) StepOnce(ref s, Idle, wall);
        Assert.Equal(0f, s.HorizontalVelocity.Y);
        Assert.False(MovementSimulation.OverlapsAny(s.Position, wall));
    }

    // ---- Landing speed (D10) ----

    [Theory]
    [InlineData(1f)]
    [InlineData(4f)]
    [InlineData(10f)]
    public void AFall_ReportsItsLandingSpeed_Once(float height)
    {
        var s = new MoveState { Position = new Vector3(0f, height, 0f) };
        float landing = 0f;
        int landings = 0;
        for (int i = 0; i < 120; i++)
        {
            StepResult r = StepOnce(ref s, Idle);
            if (r.LandingSpeed > 0f)
            {
                landing = r.LandingSpeed;
                landings++;
            }
        }
        Assert.Equal(1, landings);
        // v = sqrt(2 g h), within one tick of gravity (the step rule).
        Assert.InRange(landing, MathF.Sqrt(40f * height) - 0.7f, MathF.Sqrt(40f * height) + 0.7f);
    }

    [Fact]
    public void Walking_AndStanding_ReportNoLanding()
    {
        var s = new MoveState();
        for (int i = 0; i < 30; i++) Assert.Equal(0f, StepOnce(ref s, Sprint).LandingSpeed);
    }

    // ---- Terrain slope (D7) ----

    [Fact]
    public void Gradient_MatchesTheHeights_OnBothTriangles_AndIsZeroOutside()
    {
        HeightField t = GameMap.Terrain;
        const float e = 0.01f;   // a small step that stays inside the triangle
        for (int i = 0; i < t.VertsX - 1; i += 3)
        {
            for (int j = 0; j < t.VertsZ - 1; j += 3)
            {
                // One point inside each triangle of the cell: (u, v) = (0.75, 0.25) and (0.25, 0.75).
                for (int k = 0; k < 2; k++)
                {
                    float x = t.OriginX + (i + (k == 0 ? 0.75f : 0.25f)) * t.CellSize;
                    float z = t.OriginZ + (j + (k == 0 ? 0.25f : 0.75f)) * t.CellSize;
                    Vector2 g = t.Gradient(x, z);
                    Assert.Equal((t.Height(x + e, z) - t.Height(x, z)) / e, g.X, 0.01);
                    Assert.Equal((t.Height(x, z + e) - t.Height(x, z)) / e, g.Y, 0.01);
                }
            }
        }
        Assert.Equal(Vector2.Zero, t.Gradient(-200f, 0f));
        Assert.Equal(Vector2.Zero, t.Gradient(float.NaN, 0f));
    }
}
