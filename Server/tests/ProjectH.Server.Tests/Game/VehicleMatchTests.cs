using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Client.Game;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Build;
using ProjectH.Server.Game.Flow;
using ProjectH.Server.Game.Vehicles;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 19 D3-D10, D13, D15: vehicles in a match: entering, driving, exiting, the seated checks, damage and wrecks,
// replication. The dev sandbox (damage always allowed, Solo teams) unless the test needs the match flow (RoyaleHarness).
// The plaza around the origin is flat at y 0 and free of boxes.
public class VehicleMatchTests
{
    private static readonly Vector3 Chest = new(0f, 1.2f, 0f);

    // 기능: 차량을 만든다(실패하면 테스트 실패).
    // 입력: m - 경기, x·z - 중심, heading - 방향.
    // 출력: 차량.
    private static Vehicle Spawn(Match m, float x, float z, float heading = 0f)
    {
        Assert.True(m.SpawnVehicle(x, z, heading, out Vehicle? v));
        return v!;
    }

    // 기능: 운전 입력 하나를 보내고 한 Tick 돌린다.
    // 입력: h - 경기, p - 운전자, throttle·steer - 축, buttons - 버튼.
    // 출력: 반환값 없음.
    private static void Drive(SandboxHarness h, PlayerEntity p, float throttle, float steer = 0f, InputButtons buttons = InputButtons.None)
    {
        h.Send(p, new InputCommand { MoveY = throttle, MoveX = steer, Buttons = buttons, Yaw = p.State.Yaw });
        h.Match.Tick();
    }

    // 기능: 받은 마지막 VehicleStates를 읽는다.
    // 입력: h - 경기, peer - 받는 사람.
    // 출력: 기록 목록과 ack.
    private static (VehicleRecord[] Records, uint Ack) LastStates(SandboxHarness h, int peer)
    {
        SandboxHarness.Sent sent = h.To(peer, PacketId.VehicleStates).Last();
        Assert.Equal(DeliveryMethod.Unreliable, sent.Method);
        PacketReader r = SandboxHarness.Body(sent);
        var records = new VehicleRecord[VehicleSettings.MaxVehicles];
        Assert.True(VehicleStatesPacket.TryRead(ref r, records, out _, out uint ack, out int count));
        return (records.Take(count).ToArray(), ack);
    }

    // 기능: 보낸 DamageTaken 본문을 읽는다(읽기 실패면 테스트 실패).
    // 입력: sent - 보낸 패킷.
    // 출력: DamageTaken.
    private static DamageTaken Taken(SandboxHarness.Sent sent) { PacketReader r = SandboxHarness.Body(sent); Assert.True(DamageTaken.TryRead(ref r, out var v)); return v; }

    // ---- entering and exiting ----

    [Fact]
    public void E_EntersTheDriverSeat_ThenThePassengerSeat_AThirdGetsNothing()
    {
        var h = new SandboxHarness();
        Vehicle v = Spawn(h.Match, 0f, 0f);
        PlayerEntity a = h.Join(1, new Vector3(-2f, 0f, 0f));
        PlayerEntity b = h.Join(2, new Vector3(2f, 0f, 0f));
        PlayerEntity c = h.Join(3, new Vector3(0f, 0f, 3.2f));
        h.Press(a, InputButtons.Interact);
        Assert.True(a.InVehicle);
        Assert.Same(a, v.Driver);
        Assert.Equal(VehicleSettings.DriverSeat, a.Seat);
        Assert.Equal(VehicleSimulation.SeatPosition(v.Move.Position, v.Move.Heading, 0), a.State.Position);
        Assert.Equal(MovementMode.Ground, a.State.Mode);

        h.Press(b, InputButtons.Interact);
        Assert.Same(b, v.Seats[VehicleSettings.PassengerSeat]);
        h.Clear();
        h.Press(c, InputButtons.Interact);
        Assert.False(c.InVehicle);
        Assert.Single(h.To(3, PacketId.PickupResult));   // the full car is no target: E falls through to the pickup
        Assert.Equal(2, h.Match.VehicleEnters);
    }

    [Fact]
    public void Enter_IsRefused_WhileSliding_Falling_OrVaulting()
    {
        var h = new SandboxHarness();
        Spawn(h.Match, 0f, 0f);
        PlayerEntity a = h.Join(1, new Vector3(-2f, 0f, -0.5f));
        // Sliding (actions are allowed, entering is not): still a slide after this tick's step.
        a.State.Mode = MovementMode.Slide;
        a.State.HorizontalVelocity = new Vector2(0f, 9f);
        h.Send(a, new InputCommand { Buttons = InputButtons.Interact | InputButtons.Crouch, Yaw = 0f });
        h.Match.Tick();
        Assert.Equal(MovementMode.Slide, a.State.Mode);
        Assert.False(a.InVehicle);
        // Vaulting and falling allow no action at all.
        h.Place(a, new Vector3(-2f, 0f, 0f));
        a.State = new MoveState { Position = new Vector3(-2f, 0f, 0f), Mode = MovementMode.Vault, ModeTicks = 10 };
        h.Press(a, InputButtons.None);
        h.Press(a, InputButtons.Interact);
        Assert.False(a.InVehicle);
    }

    [Fact]
    public void Enter_FromCrouch_Works_TooFar_AndAWreck_AreRefused()
    {
        var h = new SandboxHarness();
        Vehicle v = Spawn(h.Match, 0f, 0f);
        PlayerEntity a = h.Join(1, new Vector3(-2.7f, 0f, 0f));   // 1.6 m from the body
        h.Press(a, InputButtons.Interact);
        Assert.False(a.InVehicle);
        h.Place(a, new Vector3(-2f, 0f, 0f));
        a.State.Mode = MovementMode.Crouch;
        h.Match.QaDamageVehicle(v, 1000);
        h.Press(a, InputButtons.None);   // release E: a press acts once
        h.Press(a, InputButtons.Interact);
        Assert.False(a.InVehicle);   // wrecked
        Vehicle w = Spawn(h.Match, 0f, 6f);
        h.Place(a, new Vector3(-2f, 0f, 6f));
        a.State.Mode = MovementMode.Crouch;
        h.Press(a, InputButtons.Crouch);   // release E, stay crouched
        h.Send(a, new InputCommand { Buttons = InputButtons.Interact | InputButtons.Crouch, Yaw = a.State.Yaw });
        h.Match.Tick();
        Assert.Same(a, w.Driver);
        Assert.Equal(MovementMode.Ground, a.State.Mode);
    }

    [Fact]
    public void Exit_PutsTheDriverOnItsSide_AlthoughActionsAreBlocked_AndResetsTheHistory()
    {
        var h = new SandboxHarness();
        Vehicle v = Spawn(h.Match, 0f, 0f);
        PlayerEntity a = h.Join(1, new Vector3(-2f, 0f, 0f));
        h.Press(a, InputButtons.Interact);
        Drive(h, a, 0f);
        h.Press(a, InputButtons.Interact);
        Assert.False(a.InVehicle);
        Assert.Null(v.Driver);
        Assert.Equal(-2f, a.State.Position.X, 3);   // heading 0: the driver's side is -X
        Assert.Equal(0f, a.State.Position.Z, 3);
        Assert.Equal(MovementMode.Ground, a.State.Mode);
        Assert.Equal(a.State.Position, a.History.Sample(h.Match.ServerTick - 3));   // no rewind into the seat
        Assert.Equal(1, h.Match.VehicleExits);
    }

    [Fact]
    public void Exit_TakesTheNextSpot_WhenTheDriversSideIsBlocked()
    {
        // The open-ground wall at x 26 (25.75..26.25, z -2.5..2.5, 3 m high) blocks the driver's side of a car at x 28.4.
        var h = new SandboxHarness();
        Vehicle v = Spawn(h.Match, 28.4f, 0f);
        PlayerEntity a = h.Join(1, SandboxHarness.Ground(30.4f, 0f));
        h.Press(a, InputButtons.Interact);
        Assert.Same(a, v.Driver);
        h.Press(a, InputButtons.None);
        h.Press(a, InputButtons.Interact);
        Assert.False(a.InVehicle);
        Assert.Equal(30.4f, a.State.Position.X, 3);
    }

    [Fact]
    public void Seated_TheInputDrives_BrakeStops_AndThePlayerFollowsTheSeat()
    {
        var h = new SandboxHarness();
        Vehicle v = Spawn(h.Match, 0f, -6f);
        PlayerEntity a = h.Join(1, new Vector3(-2f, 0f, -6f));
        h.Press(a, InputButtons.Interact);
        for (int i = 0; i < 30; i++) Drive(h, a, 1f);
        Assert.Equal(VehicleSettings.Acceleration, v.Move.Speed, 1);
        Assert.True(v.Move.Position.Z > -2.5f);
        Assert.Equal(VehicleSimulation.SeatPosition(v.Move.Position, v.Move.Heading, 0), a.State.Position);
        Assert.Equal(0, h.Match.MovementAnomalies);
        for (int i = 0; i < 15 && v.Move.Speed > 0f; i++) Drive(h, a, 0f, 0f, InputButtons.Jump);
        Assert.Equal(0f, v.Move.Speed);
    }

    [Fact]
    public void Seated_NoActions_NoShot_NoSlotSwitch_NoBuild()
    {
        var h = new SandboxHarness();
        Spawn(h.Match, 0f, 0f);
        PlayerEntity a = h.Join(1, new Vector3(-2f, 0f, 0f));
        h.Press(a, InputButtons.Interact);
        h.Clear();
        h.Act(a, InputButtons.Fire, new Vector3(0f, 1f, 10f));
        h.Press(a, InputButtons.Slot2);
        h.Press(a, InputButtons.ToolBuild);
        Assert.DoesNotContain(h.Packets, s => s.Id == PacketId.ShotFired);
        Assert.Equal(0, a.Inventory.CurrentSlot);
        Assert.Equal(ToolKind.Weapon, a.Inventory.Tool);
        Assert.True(a.InVehicle);
    }

    // ---- damage ----

    [Fact]
    public void AShot_HitsTheCar_NotTheSeatedPlayer_NoHitConfirmed()
    {
        var h = new SandboxHarness();
        Vehicle v = Spawn(h.Match, 0f, 0f);
        PlayerEntity driver = h.Join(1, new Vector3(-2f, 0f, 0f));
        PlayerEntity shooter = h.Join(2, new Vector3(-0.5f, 0f, -12f));
        h.Press(driver, InputButtons.Interact);
        int health = driver.Health + driver.Shield;
        h.Clear();
        h.Act(shooter, InputButtons.Fire, driver.State.Position + Chest);
        Assert.Equal(health, driver.Health + driver.Shield);
        Assert.True(v.Health < h.Match.VehicleData.MaxHealth, $"{v.Health}");
        Assert.Empty(h.To(2, PacketId.HitConfirmed));
    }

    [Fact]
    public void AShot_IsRewound_ToWhereTheShooterSawTheCar()
    {
        var h = new SandboxHarness();
        Vehicle v = Spawn(h.Match, -40f, -30f, 90f);
        PlayerEntity driver = h.Join(1, SandboxHarness.Ground(-40f, -32f));
        h.Press(driver, InputButtons.Interact);
        Assert.True(driver.InVehicle);
        for (int i = 0; i < 40; i++) Drive(h, driver, 1f, 0f, InputButtons.Sprint);
        uint seen = h.Match.ServerTick;
        Vector3 old = v.Move.Position;
        for (int i = 0; i < 8; i++) Drive(h, driver, 1f, 0f, InputButtons.Sprint);
        Assert.True(v.Move.Position.X - old.X > 4f);   // the car moved on by more than its width

        PlayerEntity control = h.Join(2, SandboxHarness.Ground(old.X + 3f, -22f));
        PlayerEntity shooter = h.Join(3, SandboxHarness.Ground(old.X - 3f, -22f));
        Vector3 target = old + new Vector3(0f, 1.15f, 0f);
        TestAim.YawPitch(control.State.Position, target, out float yaw, out float pitch);
        int before = v.Health;
        h.Send(driver, new InputCommand { MoveY = 1f, Buttons = InputButtons.Sprint });
        h.Send(control, new InputCommand { Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = h.Match.ServerTick });
        h.Match.Tick();
        Assert.Equal(before, v.Health);   // where the car is now, the shot misses

        TestAim.YawPitch(shooter.State.Position, target, out yaw, out pitch);
        h.Send(driver, new InputCommand { MoveY = 1f, Buttons = InputButtons.Sprint });
        h.Send(shooter, new InputCommand { Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = seen });
        h.Match.Tick();
        Assert.True(v.Health < before, "the rewound car was not hit");
    }

    [Fact]
    public void ARocket_ExplodesOnTheCar_TheBlastHurtsTheCar_NotItsOccupant()
    {
        var h = new SandboxHarness(WeaponsPhase17Tests.Loadout(WeaponsPhase17Tests.Ar, WeaponsPhase17Tests.Shotgun, WeaponsPhase17Tests.Rocket),
            data: WeaponsPhase17Tests.Data());
        Vehicle v = Spawn(h.Match, 0f, 0f);
        PlayerEntity driver = h.Join(1, new Vector3(-2f, 0f, 0f));
        PlayerEntity shooter = h.Join(2, new Vector3(0f, 0f, -12f));
        h.Press(driver, InputButtons.Interact);
        int health = driver.Health + driver.Shield;
        h.Act(shooter, InputButtons.Fire | InputButtons.Slot3, new Vector3(0f, 1f, 0f));
        for (int i = 0; i < 20 && h.Match.Explosions == 0; i++) h.Match.Tick();
        Assert.Equal(1, h.Match.Explosions);
        Assert.Equal(health, driver.Health + driver.Shield);
        Assert.True(v.Health < h.Match.VehicleData.MaxHealth, $"{v.Health}");
        // It went off at the rear square's face, not past the car.
        Assert.True(h.Match.ExplosionLog[0].Position.Z < -2f && h.Match.ExplosionLog[0].Position.Z > -2.5f, $"{h.Match.ExplosionLog[0].Position}");
    }

    [Fact]
    public void ABlockFasterThan8_DamagesTheCar_ThePieceIsUnharmed()
    {
        var h = new SandboxHarness();
        uint wall = h.AddPiece(new BuildPieceShape(BuildPieceType.Wall, 16, 0, 16, 0));   // z 0, x 0..5
        Vehicle v = Spawn(h.Match, 2.5f, -14f);
        PlayerEntity a = h.Join(1, new Vector3(0.5f, 0f, -14f));
        h.Press(a, InputButtons.Interact);
        for (int i = 0; i < 90 && h.Match.VehicleImpacts == 0; i++) Drive(h, a, 1f, 0f, InputButtons.Sprint);
        Assert.Equal(1, h.Match.VehicleImpacts);
        Assert.Equal(0f, v.Move.Speed);
        Assert.InRange(h.Match.VehicleData.MaxHealth - v.Health, 30, 80);   // (about 16.6 - 8) x 6
        h.Match.Build.TryGetSlot(wall, out int slot);
        Assert.Equal(0, h.Match.Build.At(slot).Damage);
        Assert.True(VehicleSimulation.FootprintBox(v.Move.Position, v.Move.Heading, 0).Max.Z < 0f);
    }

    [Fact]
    public void RunningOverAnEnemy_HurtsIt_OncePerCooldown_CreditToTheDriver()
    {
        var h = new SandboxHarness();
        Vehicle v = Spawn(h.Match, 0f, -11f);
        PlayerEntity driver = h.Join(1, new Vector3(-2f, 0f, -11f));
        PlayerEntity enemy = h.Join(2, new Vector3(0f, 0f, 1f));
        h.Press(driver, InputButtons.Interact);
        h.Clear();
        for (int i = 0; i < 60; i++) Drive(h, driver, 1f, 0f, InputButtons.Sprint);
        Assert.Equal(1, h.Match.VehicleRunOvers);
        DamageTaken hit = Taken(h.To(2, PacketId.DamageTaken).Single());
        Assert.Equal(driver.EntityId, hit.AttackerId);
        Assert.True(hit.Damage > 2 * VehicleSettings.Acceleration);   // speed (> 6 m/s) x 2
        Assert.True(v.Move.Position.Z > 3f);   // the player does not block the car
    }

    [Fact]
    public void AWreck_EjectsAndHurtsTheOccupants_CreditsTheLastEnemy_AndIsGoneAfterFiveSeconds()
    {
        var h = new SandboxHarness();
        Vehicle v = Spawn(h.Match, 0f, 0f);
        PlayerEntity driver = h.Join(1, new Vector3(-2f, 0f, 0f));
        PlayerEntity passenger = h.Join(2, new Vector3(2f, 0f, 0f));
        PlayerEntity shooter = h.Join(3, new Vector3(-0.5f, 0f, -12f));
        h.Press(driver, InputButtons.Interact);
        h.Press(passenger, InputButtons.Interact);
        h.Act(shooter, InputButtons.Fire, v.Move.Position + new Vector3(0f, 1f, 0f));
        Assert.True(v.Health < 400);
        h.Clear();
        Assert.True(h.Match.QaDamageVehicle(v, 1000));
        Assert.Equal(VehicleState.Wrecked, v.State);
        Assert.False(driver.InVehicle);
        Assert.False(passenger.InVehicle);
        DamageTaken d = Taken(h.To(1, PacketId.DamageTaken).Single());
        Assert.Equal(25, d.Damage);
        Assert.Equal(shooter.EntityId, d.AttackerId);
        Assert.Equal(shooter.EntityId, Taken(h.To(2, PacketId.DamageTaken).Single()).AttackerId);
        byte id = v.Id;
        h.Ticks(149);
        Assert.Equal(1, h.Match.VehicleCount);
        h.Ticks(2);
        Assert.Equal(0, h.Match.VehicleCount);
        // A new car in the same second never reuses the id.
        Assert.NotEqual(id, Spawn(h.Match, 0f, 10f).Id);
    }

    [Fact]
    public void ATeammatesShots_AreNoCredit_ForTheWreck()
    {
        var h = new SandboxHarness(options: new ServerOptions { MaxPlayers = 8, DevRespawn = true, TeamSize = 2 });
        Vehicle v = Spawn(h.Match, 0f, 0f);
        PlayerEntity driver = h.Join(1, new Vector3(-2f, 0f, 0f));
        PlayerEntity mate = h.Join(2, new Vector3(-0.5f, 0f, -12f));
        Assert.Equal(driver.TeamId, mate.TeamId);
        h.Press(driver, InputButtons.Interact);
        h.Act(mate, InputButtons.Fire, v.Move.Position + new Vector3(0f, 1f, 0f));
        Assert.True(v.Health < 400);   // anyone damages a car
        h.Clear();
        h.Match.QaDamageVehicle(v, 1000);
        Assert.Equal(0, Taken(h.To(1, PacketId.DamageTaken).Single()).AttackerId);
    }

    [Fact]
    public void AKnockDown_PutsTheSeatedPlayerOutFirst()
    {
        var h = new SandboxHarness(options: new ServerOptions { MaxPlayers = 8, DevRespawn = true, TeamSize = 2 });
        Vehicle v = Spawn(h.Match, 0f, 0f);
        PlayerEntity driver = h.Join(1, new Vector3(-2f, 0f, 0f));
        h.Join(2, new Vector3(5f, 0f, 5f));
        h.Press(driver, InputButtons.Interact);
        Assert.True(h.Match.DownPlayer(driver));
        Assert.False(driver.InVehicle);
        Assert.Null(v.Driver);
        Assert.True(driver.IsDowned);
        Assert.Equal(-2f, driver.State.Position.X, 3);
    }

    [Fact]
    public void E_NextToADownedTeammate_RevivesInstead_ItDoesNotEnterTheCar()
    {
        var h = new SandboxHarness(options: new ServerOptions { MaxPlayers = 8, DevRespawn = true, TeamSize = 2 });
        Vehicle v = Spawn(h.Match, 0f, 0f);
        PlayerEntity a = h.Join(1, new Vector3(-2f, 0f, 0f));
        PlayerEntity mate = h.Join(2, new Vector3(-3.5f, 0f, 0.5f));
        Assert.True(h.Match.DownPlayer(mate));
        h.Press(a, InputButtons.Interact);
        Assert.False(a.InVehicle);
        Assert.Null(v.Driver);
        // Holding E starts the revive; entering the car is never the answer while a channel target is in reach.
        h.Send(a, new InputCommand { Buttons = InputButtons.Interact | InputButtons.InteractHeld });
        h.Match.Tick();
        Assert.True(a.ChannelActive);
        Assert.False(a.InVehicle);
    }

    [Fact]
    public void Leaving_EmptiesTheSeat_TheDriverlessCarBrakes()
    {
        var h = new SandboxHarness();
        Vehicle v = Spawn(h.Match, 0f, -10f);
        PlayerEntity a = h.Join(1, new Vector3(-2f, 0f, -10f));
        h.Press(a, InputButtons.Interact);
        for (int i = 0; i < 20; i++) Drive(h, a, 1f);
        h.Match.Leave(1);
        Assert.Null(v.Driver);
        h.Ticks(30);
        Assert.Equal(0f, v.Move.Speed);
    }

    // ---- building, replication, limits ----

    [Fact]
    public void APieceAcrossACar_IsRefused()
    {
        var h = new SandboxHarness();
        Spawn(h.Match, 2.5f, 0f, 90f);   // straddles the wall slot of cell 16's south edge (z 0)
        PlayerEntity b = h.Join(1, new Vector3(2.5f, 0f, -3f));
        h.Press(b, InputButtons.ToolBuild);
        b.Inventory.SetResource(BuildMaterialType.Wood, 100);
        var aim = new Vector3(2.5f, 1.5f, 0f);
        h.Act(b, InputButtons.None, aim);
        h.Match.EnqueueBuild(1, new BuildRequest { Sequence = 1, Piece = (byte)BuildPieceType.Wall, Material = 0, X = 16, Y = 0, Z = 16, Rotation = 0 });
        h.Act(b, InputButtons.None, aim);
        PacketReader r = SandboxHarness.Body(h.To(1, PacketId.BuildResult).Last());
        Assert.True(BuildResult.TryRead(ref r, out BuildResult result));
        Assert.Equal(BuildResultCode.Blocked, result.Code);
    }

    [Fact]
    public void VehicleStates_InterestRange_OwnVehicle_AndTheDeadSeeAll()
    {
        var h = new SandboxHarness();
        Vehicle far = Spawn(h.Match, -62f, 62f);
        Vehicle near = Spawn(h.Match, 50f, -60f);
        PlayerEntity a = h.Join(1, SandboxHarness.Ground(52f, -62f));
        PlayerEntity dead = h.Join(2, SandboxHarness.Ground(52f, -66f));
        dead.Alive = false;
        dead.RespawnAtTick = uint.MaxValue;
        h.Ticks(2);
        (VehicleRecord[] seen, uint _) = LastStates(h, 1);
        Assert.Equal(new[] { near.Id }, seen.Select(r => r.Id).ToArray());
        Assert.Equal(2, LastStates(h, 2).Records.Length);
        Assert.Equal(VehicleState.Active, seen[0].State);
        Assert.Equal(400, seen[0].Health);
    }

    [Fact]
    public void VehicleStates_NameTheDriver_AckTheDriversInput_AndSendNothingWithoutVehicles()
    {
        var h = new SandboxHarness();
        PlayerEntity a = h.Join(1, new Vector3(-2f, 0f, 0f));
        h.Ticks(4);
        Assert.Empty(h.To(1, PacketId.VehicleStates));
        Vehicle v = Spawn(h.Match, 0f, 0f);
        h.Press(a, InputButtons.Interact);
        Drive(h, a, 1f);
        h.Ticks(1);
        (VehicleRecord[] records, uint ack) = LastStates(h, 1);
        Assert.Equal(a.EntityId, records.Single().Driver);
        Assert.Equal(a.LastProcessedSeq, ack);
        Assert.Equal(v.Id, records.Single().Id);
    }

    [Fact]
    public void AtMostEightVehicles_AndIdsAreUnique()
    {
        var h = new SandboxHarness();
        var ids = new HashSet<byte>();
        for (int i = 0; i < VehicleSettings.MaxVehicles; i++) ids.Add(Spawn(h.Match, -60f + i * 8f, 70f).Id);
        Assert.Equal(VehicleSettings.MaxVehicles, ids.Count);
        Assert.DoesNotContain((byte)0, ids);
        Assert.False(h.Match.SpawnVehicle(0f, 0f, 0f, out _));
    }

    [Fact]
    public void TheMatchStart_SpawnsTheVehicles_TheFinishClearsThem_TheNextMatchSpawnsThemAgain()
    {
        var h = new RoyaleHarness();
        h.Join(1);
        h.Join(2);
        Assert.Equal(0, h.Match.VehicleCount);
        h.RunToMatch();
        Assert.Equal(VehicleSpawns.Count, h.Match.VehicleCount);
        foreach (Vehicle v in h.Match.VehicleSlots.ToArray().Where(x => x.InUse))
        {
            Assert.Equal(VehicleState.Active, v.State);
            Assert.Equal(400, v.Health);
        }
        h.Match.ForceFinish();
        Assert.Equal(0, h.Match.VehicleCount);   // none on the result screen
        h.TickUntil(() => h.Match.Flow.State == MatchFlowState.WaitingForPlayers || h.Match.Flow.State == MatchFlowState.Starting, 200);
        Assert.Equal(0, h.Match.VehicleCount);   // none in the lobby after the round reset
        h.RunToMatch();
        Assert.Equal(VehicleSpawns.Count, h.Match.VehicleCount);
        Assert.Equal(2 * VehicleSpawns.Count, h.Match.VehiclesSpawned);
    }

    [Fact]
    public void AGracedDriver_LeavesTheSeat_AtTheDisconnect()
    {
        var h = new RoyaleHarness();
        PlayerEntity a = h.Join(1);
        h.Join(2);
        h.RunToMatch();
        Vehicle v = h.Match.VehicleSlots.ToArray().First(x => x.InUse);
        Vector3 side = VehicleSimulation.SeatPosition(v.Move.Position, v.Move.Heading, 0);
        var spot = new Vector3(side.X, GameMap.Terrain.Height(side.X, side.Z), side.Z);
        h.Place(a, spot);
        a.State = new MoveState { Position = spot, Mode = MovementMode.Ground };
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = 1, Buttons = InputButtons.Interact });
        h.Match.EnqueueInput(1, packet);
        h.Match.Tick();
        Assert.Same(a, v.Driver);
        for (uint seq = 2; seq < 12; seq++)
        {
            packet.Set(0, new InputCommand { Seq = seq, MoveY = 1f, Buttons = InputButtons.Sprint });
            h.Match.EnqueueInput(1, packet);
            h.Match.Tick();
        }
        Assert.True(h.Match.Disconnect(1, allowGrace: true));
        Assert.Null(v.Driver);
        Assert.False(a.InVehicle);
        Assert.True(a.IsGraced);
        // The grace coasts on the last input: the throttle it held must not walk it on.
        Vector3 exit = a.State.Position;
        h.Ticks(10);
        Assert.Equal(exit.X, a.State.Position.X, 3);
        Assert.Equal(exit.Z, a.State.Position.Z, 3);
    }

    [Fact]
    public void DrivingTicks_WithVehicleStates_AllocateNothing()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 8, DevRespawn = true }, TestGameData.Create(), static (_, _, _) => { },
            lootPoints: Array.Empty<LootPoint>());
        Assert.True(match.SpawnVehicle(0f, -20f, 0f, out Vehicle? v));
        Assert.True(match.SpawnVehicle(10f, -20f, 0f, out _));
        match.TryJoin(1, "p1");
        match.TryJoin(2, "p2");
        match.TryGetPlayer(1, out PlayerEntity driver);
        match.TryGetPlayer(2, out PlayerEntity enemy);
        driver.State.Position = new Vector3(-2f, 0f, -20f);
        enemy.State.Position = new Vector3(0f, 0f, 0f);
        uint seq = 0;
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = ++seq, Buttons = InputButtons.Interact });
        match.EnqueueInput(1, packet);
        match.Tick();
        Assert.Same(driver, v!.Driver);
        for (int i = 0; i < 70; i++)
        {
            packet.Set(0, new InputCommand { Seq = ++seq, MoveY = 1f, MoveX = i < 35 ? 0f : 0.3f });
            match.EnqueueInput(1, packet);
            match.Tick();
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 60; i++)
        {
            packet.Set(0, new InputCommand { Seq = ++seq, MoveY = 1f, MoveX = 0.5f });
            match.EnqueueInput(1, packet);
            match.Tick();
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.True(match.VehicleStatesSent > 0);
    }

    // ---- review fixes ----

    [Fact]
    public void E_WithAHeldMedkit_Enters_ButStartsNoHeal()
    {
        var h = new SandboxHarness();
        Vehicle v = Spawn(h.Match, 0f, 0f);
        PlayerEntity a = h.Join(1, new Vector3(-2f, 0f, 0f));
        a.Health = 50;
        a.Inventory.Medkits = 2;
        h.Press(a, InputButtons.Interact | InputButtons.UseMedkit);
        Assert.Same(a, v.Driver);
        Assert.Equal(ConsumableType.None, a.Inventory.Using);
        h.Ticks(200);   // longer than any heal: nothing completes from the seat
        Assert.Equal(50, a.Health);
        Assert.Equal(2, a.Inventory.Medkits);
    }

    [Fact]
    public void E_WithAHeldFire_Enters_ButFiresNoShot()
    {
        var h = new SandboxHarness();
        Vehicle v = Spawn(h.Match, 0f, 0f);
        PlayerEntity a = h.Join(1, new Vector3(-2f, 0f, 0f));
        int mag = a.Inventory.Current.MagAmmo;
        h.Clear();
        h.Act(a, InputButtons.Interact | InputButtons.Fire, new Vector3(0f, 1f, 10f));
        Assert.Same(a, v.Driver);
        Assert.DoesNotContain(h.Packets, s => s.Id == PacketId.ShotFired);
        Assert.Equal(mag, a.Inventory.Current.MagAmmo);
        Assert.True(a.FireHeld);
    }

    [Fact]
    public void AnEditThatRefillsAWallAroundACar_IsBlocked()
    {
        const int door = (1 << 1) | (1 << 4);
        var h = new SandboxHarness();
        PlayerEntity p = h.Join(1, new Vector3(2.5f, 0f, -3f));
        uint wall = h.AddPiece(new BuildPieceShape(BuildPieceType.Wall, 16, 0, 16, 0).WithEdit(door, 0), owner: p.EntityId);
        Spawn(h.Match, 2.5f, 0f);   // parked across the doorway
        h.Ticks(h.Match.Building.MinBuildIntervalTicks);
        var aim = new Vector3(2.5f, 1.5f, 0f);
        h.Act(p, InputButtons.None, aim);
        h.Match.EnqueueEdit(1, new BuildEditRequest { Sequence = 1, PieceId = wall, State = BuildEdit.PackState(0, 0) });
        h.Act(p, InputButtons.None, aim);
        PacketReader r = SandboxHarness.Body(h.To(1, PacketId.BuildResult).Last());
        Assert.True(BuildResult.TryRead(ref r, out BuildResult result));
        Assert.Equal(BuildResultCode.Blocked, result.Code);
        h.Match.Build.TryGetSlot(wall, out int slot);
        Assert.Equal(door, h.Match.Build.At(slot).Shape.Edit);
    }

    [Fact]
    public void APlayerInsideAParkedCarsFootprint_CanBeShot_OneBehindIt_IsCovered()
    {
        var h = new SandboxHarness();
        Vehicle v = Spawn(h.Match, 0f, 0f);
        PlayerEntity shooter = h.Join(1, new Vector3(0f, 0f, -12f));
        PlayerEntity inside = h.Join(2, new Vector3(0f, 0f, 0f));
        int before = inside.Health + inside.Shield;
        h.Act(shooter, InputButtons.Fire, inside.State.Position + Chest);
        Assert.True(inside.Health + inside.Shield < before, "the player in the footprint was covered by the car");
        Assert.Equal(400, v.Health);

        h.Place(inside, new Vector3(0f, 0f, 3f));   // just behind the front square, clear of it
        before = inside.Health + inside.Shield;
        h.Ticks(5);
        h.Act(shooter, InputButtons.Fire, inside.State.Position + new Vector3(0f, 1f, 0f));
        Assert.Equal(before, inside.Health + inside.Shield);
        Assert.True(v.Health < 400);
    }

    [Fact]
    public void TheLastCarGone_SendsOneEmptyVehicleStates()
    {
        var h = new SandboxHarness();
        Vehicle v = Spawn(h.Match, 0f, 0f);
        PlayerEntity a = h.Join(1, new Vector3(-2f, 0f, 0f));
        h.Press(a, InputButtons.Interact);
        h.Match.QaDamageVehicle(v, 1000);
        h.Ticks(152);
        Assert.Equal(0, h.Match.VehicleCount);
        h.Ticks(2);
        Assert.Empty(LastStates(h, 1).Records);
        int sent = h.To(1, PacketId.VehicleStates).Count();
        h.Ticks(10);
        Assert.Equal(sent, h.To(1, PacketId.VehicleStates).Count());   // only the one
    }

    [Fact]
    public void ForcedExit_WithEveryExitSpotBlocked_UsesTheSpotAboveTheCar_ETriesStaySeated()
    {
        // A car in the outer corner, past the bound every exit spot must be inside: no spot is valid.
        var h = new SandboxHarness();
        Vehicle v = Spawn(h.Match, 80f, 80f);
        PlayerEntity a = h.Join(1, new Vector3(78.2f, v.Move.Position.Y, 80f));
        h.Press(a, InputButtons.Interact);
        Assert.Same(a, v.Driver);
        h.Press(a, InputButtons.None);
        h.Press(a, InputButtons.Interact);
        Assert.True(a.InVehicle);   // E out with no spot: stays seated
        h.Match.QaDamageVehicle(v, 1000);
        Assert.False(a.InVehicle);
        Assert.Null(v.Driver);
        Assert.Equal(v.Move.Position + new Vector3(0f, 2f, 0f), a.State.Position);
    }

    // ---- the client's copy ----

    [Fact]
    public void TheClientsEnterRule_PicksTheSameVehicle()
    {
        var rng = new Random(19);
        var records = new VehicleRecord[VehicleSettings.MaxVehicles];
        var candidates = new VehicleCandidate[VehicleSettings.MaxVehicles];
        for (int round = 0; round < 2000; round++)
        {
            int n = rng.Next(VehicleSettings.MaxVehicles + 1);
            // Ids are unique in a packet (VehicleStatesPacket refuses repeats); their order is shuffled.
            byte[] ids = Enumerable.Range(1, 255).OrderBy(_ => rng.Next()).Take(n).Select(x => (byte)x).ToArray();
            for (int i = 0; i < n; i++)
            {
                var r = new VehicleRecord
                {
                    Id = ids[i],
                    State = rng.Next(5) == 0 ? VehicleState.Wrecked : VehicleState.Active,
                    Driver = (ushort)(rng.Next(2) == 0 ? 0 : 5),
                    Passenger = (ushort)(rng.Next(2) == 0 ? 0 : 6),
                    Position = new Vector3((float)(rng.NextDouble() * 8 - 4), 0f, (float)(rng.NextDouble() * 8 - 4)),
                    Heading = (float)(rng.NextDouble() * 360),
                };
                // Two exact ties now and then (the tie goes to the lower id on both sides).
                if (i > 0 && rng.Next(6) == 0) r = r with { Position = records[i - 1].Position, Heading = records[i - 1].Heading };
                records[i] = r;
                candidates[i] = new VehicleCandidate(r.Id, r.Position, r.Heading, r.State == VehicleState.Active && (r.Driver == 0 || r.Passenger == 0));
            }
            var feet = new Vector3((float)(rng.NextDouble() * 10 - 5), 0f, (float)(rng.NextDouble() * 10 - 5));
            Assert.Equal(Match.FindEnterTarget(feet, candidates.AsSpan(0, n)), VehiclePrompt.FindEnterTarget(feet, records, n));
        }
    }
}
