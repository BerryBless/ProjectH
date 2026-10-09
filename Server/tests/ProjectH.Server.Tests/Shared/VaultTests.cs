using System;
using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 12 D8 (spec §2 Mantle·Hurdle): the four checks, the two kinds and the constant-velocity move. The character
// walks or sprints along +X (yaw 90) towards a box whose near face is at x = 2.
public class VaultTests
{
    private const float Dt = 1f / 30f;
    private const float Hw = MoveSettings.HalfWidth;

    private static readonly InputCommand Walk = new() { MoveY = 1f, Yaw = 90f };
    private static readonly InputCommand Sprint = new() { MoveY = 1f, Yaw = 90f, Buttons = InputButtons.Sprint };

    // 기능: 최소·최대 모서리 좌표 여섯 값으로 충돌 상자를 만든다.
    // 입력: minX/minY/minZ - 최소 모서리 좌표, maxX/maxY/maxZ - 최대 모서리 좌표.
    // 출력: 지정한 범위의 Box.
    private static Box B(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        => new(new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ));

    // 기능: x 2..4, z -1..1에 선 높이 height의 상자와 추가 상자들로 충돌 세계를 만든다.
    // 입력: height - 첫 상자의 높이, more - 뒤에 덧붙일 상자들.
    // 출력: 첫 상자가 0번인 Box 배열.
    // A 2 x 2 m box (x 2..4) of the given height, and optionally more boxes.
    private static Box[] World(float height, params Box[] more)
    {
        var world = new Box[1 + more.Length];
        world[0] = B(2f, 0f, -1f, 4f, height, 1f);
        more.CopyTo(world, 1);
        return world;
    }

    // 기능: 입력 복사본에 Jump 버튼을 더한다.
    // 입력: input - 바탕 입력(값 복사).
    // 출력: Jump가 눌린 새 InputCommand. 원본은 바뀌지 않는다.
    private static InputCommand Jump(InputCommand input)
    {
        input.Buttons |= InputButtons.Jump;
        return input;
    }

    // 기능: startX에서 input으로 상자 앞면(x = 2)에 gap 이내로 다가간 뒤 Jump를 한 번 누른 Tick까지 돌린다.
    // 입력: world - 충돌 상자들, input - 접근 입력, gap - 앞면까지 허용 간격, startX - 시작 X 좌표.
    // 출력: Jump Tick 직후의 MoveState(Vault가 시작됐으면 Mode가 Vault).
    // Moves with input until the front face is within gap of x = 2 (at full speed from the first step: the ground sets
    // the velocity every tick), then presses jump once. Returns the state right after the jump tick.
    private static MoveState ApproachAndJump(Box[] world, InputCommand input, float gap = 0.5f, float startX = -3f)
    {
        var s = new MoveState { Position = new Vector3(startX, 0f, 0f), Yaw = 90f };
        for (int i = 0; i < 200 && 2f - (s.Position.X + Hw) > gap; i++) MovementSimulation.Step(ref s, input, Dt, world, HeightField.Flat);
        MovementSimulation.Step(ref s, Jump(input), Dt, world, HeightField.Flat);
        return s;
    }

    // 기능: Vault가 끝날 때까지(최대 30 Tick) 돌린 뒤 서 있는 Tick 하나를 더 돌려 발을 표면에 붙인다.
    // 입력: s - Vault 중인 이동 상태, world - 충돌 상자들, input - Vault 동안 줄 입력.
    // 출력: 반환값 없음. s가 Vault를 마치고 바닥에 선 상태가 된다.
    // The rest of the vault, then one standing tick: the vault ends MoveSettings.Skin above the surface and the ground
    // check of the next tick snaps the feet onto it.
    private static void RunVault(ref MoveState s, Box[] world, InputCommand input)
    {
        for (int i = 0; i < 30 && s.Mode == MovementMode.Vault; i++) MovementSimulation.Step(ref s, input, Dt, world, HeightField.Flat);
        MovementSimulation.Step(ref s, new InputCommand { Yaw = 90f }, Dt, world, HeightField.Flat);
    }

    [Theory]
    [InlineData(1.5f)]
    [InlineData(2.0f)]
    public void AMiddleBox_IsMantled_OntoItsTop_FortyCentimetresIn(float height)
    {
        Box[] world = World(height);
        MoveState s = ApproachAndJump(world, Walk);
        Assert.Equal(MovementMode.Vault, s.Mode);
        Assert.Equal(11, s.ModeTicks);   // 12 ticks (0.4 s), the first one already moved

        RunVault(ref s, world, Walk);
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.Equal(height, s.Position.Y, 3);
        Assert.Equal(2f + MovementTuning.MantleInset, s.Position.X, 3);
        Assert.True(MovementSimulation.IsGrounded(s, world, HeightField.Flat));
        Assert.False(MovementSimulation.OverlapsAny(s.Position, world));
    }

    [Fact]
    public void ALowBox_AtSprintSpeed_IsHurdled_ToTheGroundBehindIt()
    {
        Box[] world = World(1.0f);
        MoveState s = ApproachAndJump(world, Sprint);
        Assert.Equal(MovementMode.Vault, s.Mode);
        Assert.Equal(5, s.ModeTicks);   // 6 ticks (0.2 s)

        RunVault(ref s, world, Sprint);
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.Equal(0f, s.Position.Y, 3);
        Assert.Equal(4f + Hw + MovementTuning.HurdleLandingGap, s.Position.X, 3);
        Assert.False(MovementSimulation.OverlapsAny(s.Position, world));
    }

    [Fact]
    public void ALowBox_AtWalkingSpeed_IsANormalJump()
    {
        Box[] world = World(1.0f);
        MoveState s = ApproachAndJump(world, Walk);
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.True(s.VelocityY > 0f);
    }

    [Fact]
    public void AThreeMetreWall_CannotBeVaulted()
    {
        Box[] world = World(3.0f);
        MoveState s = ApproachAndJump(world, Sprint);
        Assert.Equal(MovementMode.Ground, s.Mode);
        for (int i = 0; i < 60; i++)
        {
            MovementSimulation.Step(ref s, Jump(Sprint), Dt, world, HeightField.Flat);
            Assert.NotEqual(MovementMode.Vault, s.Mode);
            Assert.True(s.Position.X + Hw <= 2f);
        }
    }

    [Fact]
    public void TooFarAway_IsANormalJump()
    {
        Box[] world = World(1.5f);
        MoveState s = ApproachAndJump(world, Walk, gap: 1.2f);
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.True(s.VelocityY > 0f);
    }

    [Fact]
    public void NoRoomOnTheTop_IsNoMantle()
    {
        // A slab 2.5 m up over the box: standing on the 1.5 m top would need 3.3 m.
        Box[] world = World(1.5f, B(2f, 2.5f, -1f, 4f, 3f, 1f));
        MoveState s = ApproachAndJump(world, Walk);
        Assert.Equal(MovementMode.Ground, s.Mode);
    }

    [Fact]
    public void NoRoomBehindAHurdle_StandsOnTheTop()
    {
        // A wall right behind the low box.
        Box[] world = World(1.0f, B(4.2f, 0f, -3f, 5f, 3f, 3f));
        MoveState s = ApproachAndJump(world, Sprint);
        Assert.Equal(MovementMode.Vault, s.Mode);
        RunVault(ref s, world, Sprint);
        Assert.Equal(1f, s.Position.Y, 3);
        Assert.Equal(2f + MovementTuning.MantleInset, s.Position.X, 3);
    }

    [Fact]
    public void ADeepLowBox_IsNotHurdled_ButStoodOn()
    {
        Box[] world = { B(2f, 0f, -1f, 6f, 1f, 1f) };   // 4 m deep
        MoveState s = ApproachAndJump(world, Sprint);
        RunVault(ref s, world, Sprint);
        Assert.Equal(1f, s.Position.Y, 3);
        Assert.InRange(s.Position.X, 2f, 6f);
    }

    [Fact]
    public void InTheAir_OrCrouched_OrWithoutMovingForward_ThereIsNoVault()
    {
        Box[] world = World(1.5f);
        // Crouched: crouch then jump next to the box. The jump stands it up (there is room) and is a normal jump.
        var crouch = new InputCommand { MoveY = 1f, Yaw = 90f, Buttons = InputButtons.Crouch };
        MoveState s = ApproachAndJump(world, crouch, gap: 0.3f);
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.True(s.VelocityY > 0f);

        // In the air: a jump far from the box, then the jump button held while it reaches the box.
        s = new MoveState { Position = new Vector3(0.6f, 0f, 0f), Yaw = 90f };
        MovementSimulation.Step(ref s, Jump(Walk), Dt, world, HeightField.Flat);
        for (int i = 0; i < 10; i++)
        {
            MovementSimulation.Step(ref s, Jump(Walk), Dt, world, HeightField.Flat);
            Assert.NotEqual(MovementMode.Vault, s.Mode);
        }

        // Strafing (no forward input) next to the box.
        s = new MoveState { Position = new Vector3(2f - Hw - 0.3f, 0f, 0f), Yaw = 0f };
        MovementSimulation.Step(ref s, new InputCommand { MoveX = 1f, Yaw = 0f, Buttons = InputButtons.Jump }, Dt, world, HeightField.Flat);
        Assert.Equal(MovementMode.Ground, s.Mode);
    }

    [Fact]
    public void DuringAVault_ThePositionMovesTheSameAmountEveryTick_AndInputIsIgnored()
    {
        Box[] world = World(1.5f);
        MoveState s = ApproachAndJump(world, Walk);
        Vector3 previous = s.Position;
        MovementSimulation.Step(ref s, Walk, Dt, world, HeightField.Flat);
        Vector3 delta = s.Position - previous;
        while (s.Mode == MovementMode.Vault)
        {
            previous = s.Position;
            var odd = new InputCommand { MoveX = -1f, MoveY = -1f, Yaw = 200f, Buttons = InputButtons.Jump | InputButtons.Crouch | InputButtons.Sprint };
            MovementSimulation.Step(ref s, odd, Dt, world, HeightField.Flat);
            if (s.Mode == MovementMode.Vault || s.ModeTicks == 0)
            {
                Vector3 step = s.Position - previous;
                Assert.Equal(delta.X, step.X, 4);
                Assert.Equal(delta.Y, step.Y, 4);
                Assert.Equal(delta.Z, step.Z, 4);
            }
        }
        Assert.Equal(1.5f + MoveSettings.Skin, s.Position.Y, 4);
    }

    [Fact]
    public void TheVaultState_ReplaysFromVelocitiesAndModeTicks()
    {
        // What a client gets in a snapshot (position, both velocities, mode, ModeTicks) continues the vault exactly.
        Box[] world = World(1.5f);
        MoveState s = ApproachAndJump(world, Walk);
        MovementSimulation.Step(ref s, Walk, Dt, world, HeightField.Flat);
        var copy = new MoveState
        {
            Position = s.Position, VelocityY = s.VelocityY, Yaw = s.Yaw, Mode = s.Mode,
            HorizontalVelocity = s.HorizontalVelocity, ModeTicks = s.ModeTicks,
        };
        RunVault(ref s, world, Walk);
        RunVault(ref copy, world, Walk);
        Assert.Equal(s.Position, copy.Position);
        Assert.Equal(s.Mode, copy.Mode);
    }

    [Fact]
    public void AThinWall_InFrontOfALowBox_BlocksTheHurdle()
    {
        // A 3 m tall 0.2 m thick wall at x 1.9..2.1 and the 1 m box right behind it (x 2.1..4).
        Box[] world = { B(1.9f, 0f, -1f, 2.1f, 3f, 1f), B(2.1f, 0f, -1f, 4f, 1f, 1f) };
        MoveState s = ApproachAndJump(world, Sprint, gap: 0.3f, startX: -3f);
        Assert.NotEqual(MovementMode.Vault, s.Mode);
        for (int i = 0; i < 40; i++)
        {
            MovementSimulation.Step(ref s, Jump(Sprint), Dt, world, HeightField.Flat);
            Assert.NotEqual(MovementMode.Vault, s.Mode);
        }
    }

    [Fact]
    public void TheLowBoxAlone_IsStillHurdled()
    {
        Box[] world = { B(2.1f, 0f, -1f, 4f, 1f, 1f) };
        MoveState s = ApproachAndJump(world, Sprint, gap: 0.3f);
        Assert.Equal(MovementMode.Vault, s.Mode);
    }

    [Fact]
    public void AHurdleOnARaisedPlatform_NeverLandsThroughTheFloor()
    {
        // A 3 m platform (x -10..3.05) with a 1 m box on it (x 2..3) and nothing but the 3 m drop behind it.
        Box[] world = { B(-10f, 0f, -3f, 3.05f, 3f, 3f), B(2f, 3f, -1f, 3f, 4f, 1f) };
        var s = new MoveState { Position = new Vector3(-3f, 3f, 0f), Yaw = 90f };
        for (int i = 0; i < 200 && 2f - (s.Position.X + Hw) > 0.5f; i++) MovementSimulation.Step(ref s, Sprint, Dt, world, HeightField.Flat);
        MovementSimulation.Step(ref s, Jump(Sprint), Dt, world, HeightField.Flat);
        Assert.Equal(MovementMode.Vault, s.Mode);   // final review C10: the vault really starts
        RunVault(ref s, world, Sprint);
        Assert.Equal(4f, s.Position.Y, 3);   // mantled onto the box top, not dropped to the ground
    }

    // Final review C10: the path check's bottom is the start's feet, so the slab a hurdle starts on does not block a
    // hurdle that lands a little lower (within VaultBaseTolerance) behind the box.
    [Fact]
    public void AHurdle_ThatStepsDown_IsNotBlockedByTheFloorItStartsOn()
    {
        // A 0.25 m slab up to the box's face (x 2), a 1.25 m box (1 m above the slab) and the ground behind it.
        Box[] world = { B(-10f, 0f, -3f, 2f, 0.25f, 3f), B(2f, 0f, -1f, 3f, 1.25f, 1f) };
        var s = new MoveState { Position = new Vector3(-3f, 0.25f, 0f), Yaw = 90f };
        for (int i = 0; i < 200 && 2f - (s.Position.X + Hw) > 0.5f; i++) MovementSimulation.Step(ref s, Sprint, Dt, world, HeightField.Flat);
        MovementSimulation.Step(ref s, Jump(Sprint), Dt, world, HeightField.Flat);
        Assert.Equal(MovementMode.Vault, s.Mode);
        RunVault(ref s, world, Sprint);
        Assert.Equal(0f, s.Position.Y, 3);
        Assert.True(s.Position.X > 3f + Hw);
    }

    // The map's own boxes (spec §2: "1.5 m·2.0 m 상자 → Mantle, 1.0 m 상자 → Hurdle").
    [Fact]
    public void OnTheMap_TheGearworksCrates_AreHurdledAndMantled()
    {
        // Low crate (36, 0.5, 40): x 35..37. High crate (34, 0.75, 60): x 33..35. Approach both along +X.
        Assert.True(VaultsOnMap(new Vector3(31f, 0f, 40f), 35f, Sprint, out MoveState low));
        Assert.Equal(MoveSettings.Skin, low.Position.Y - GameMap.Terrain.Height(low.Position.X, low.Position.Z), 4);
        Assert.True(low.Position.X > 37f);

        Assert.True(VaultsOnMap(new Vector3(29f, 0f, 60f), 33f, Walk, out MoveState high));
        Assert.Equal(1.5f + MoveSettings.Skin, high.Position.Y, 4);
        Assert.InRange(high.Position.X, 33f, 35f);
    }

    // 기능: 실제 맵에서 start부터 +X로 달려 faceX의 상자 앞면 0.5 m 안에서 Jump를 눌러 Vault가 일어나는지 본다.
    // 입력: start - 시작 위치(Y는 지형 높이로 맞춘다), faceX - 상자 앞면의 X 좌표, input - 접근 입력, s - Vault가 끝난 직후(또는 120 Tick 뒤)의 상태.
    // 출력: Vault 모드에 들어갔으면 true, 아니면 false.
    private static bool VaultsOnMap(Vector3 start, float faceX, InputCommand input, out MoveState s)
    {
        s = new MoveState { Position = new Vector3(start.X, GameMap.Terrain.Height(start.X, start.Z), start.Z), Yaw = 90f };
        bool vaulted = false;
        for (int i = 0; i < 120; i++)
        {
            // Jump every tick within half a metre of the crate's face: the first press starts the vault.
            InputCommand step = faceX - (s.Position.X + Hw) < 0.5f ? Jump(input) : input;
            MovementSimulation.Step(ref s, step, Dt, GameMap.Boxes, GameMap.Terrain);
            if (s.Mode == MovementMode.Vault) vaulted = true;
            if (vaulted && s.Mode != MovementMode.Vault) break;
        }
        return vaulted;
    }
}
