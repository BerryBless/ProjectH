using System;
using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Build;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;
using static ProjectH.Server.Tests.Game.WeaponsPhase17Tests;

namespace ProjectH.Server.Tests.Game;

// Phase 17 D6-D10: projectiles (the grenade and the rocket), their replication, explosions and their lifetime, on the dev
// sandbox (damage always allowed) and in a battle royale match (RoyaleHarness, for Starting, the finish and the reset).
// The wall stands on the south edge of plaza cell 16 (z 0, x 0..5); the plaza is flat at y 0.
public class ProjectilePhase17Tests
{
    private static readonly BuildPieceShape WallShape = new(BuildPieceType.Wall, 16, 0, 16, 0);
    private static readonly Vector3 WallCentre = new(2.5f, 1.5f, 0f);
    private static readonly Vector3 Chest = new(0f, 1.2f, 0f);

    // 기능: 운영 무기 데이터, AR·산탄총·로켓 장비로 개발 모드 경기를 만든다.
    // 입력: 없음.
    // 출력: SandboxHarness.
    private static SandboxHarness Harness() => new(Loadout(Ar, Shotgun, Rocket), data: Data());

    // 기능: 다 지어진 나무 벽(150 체력)을 플라자 칸 16의 남쪽 가장자리에 놓는다.
    // 입력: h - 경기.
    // 출력: 벽 id.
    private static uint Wall(SandboxHarness h)
    {
        h.Ticks(160);   // past the longest construction
        return h.Match.Build.Add(WallShape, BuildMaterialType.Wood, 99, 0, grounded: true);
    }

    // 기능: 조건이 될 때까지 Tick을 돌린다(최대 max).
    // 입력: h - 경기, condition - 조건, max - 최대 Tick.
    // 출력: 반환값 없음. 조건이 안 되면 실패.
    private static void TickUntil(SandboxHarness h, Func<bool> condition, int max)
    {
        for (int i = 0; i < max && !condition(); i++) h.Match.Tick();
        Assert.True(condition(), "condition not reached");
    }

    // 기능: 보낸 투사체·전투 패킷의 본문을 읽는다(읽기 실패면 테스트 실패).
    // 입력: sent - 보낸 패킷.
    // 출력: 읽은 값.
    private static ProjectileSpawned Spawned(SandboxHarness.Sent sent) { PacketReader r = SandboxHarness.Body(sent); Assert.True(ProjectileSpawned.TryRead(ref r, out var v)); return v; }
    private static ProjectileState State(SandboxHarness.Sent sent) { PacketReader r = SandboxHarness.Body(sent); Assert.True(ProjectileState.TryRead(ref r, out var v)); return v; }
    private static ProjectileExploded Exploded(SandboxHarness.Sent sent) { PacketReader r = SandboxHarness.Body(sent); Assert.True(ProjectileExploded.TryRead(ref r, out var v)); return v; }
    private static PlayerDied Died(SandboxHarness.Sent sent) { PacketReader r = SandboxHarness.Body(sent); Assert.True(PlayerDied.TryRead(ref r, out var v)); return v; }
    private static HitConfirmed Hit(SandboxHarness.Sent sent) { PacketReader r = SandboxHarness.Body(sent); Assert.True(HitConfirmed.TryRead(ref r, out var v)); return v; }
    private static DamageTaken Taken(SandboxHarness.Sent sent) { PacketReader r = SandboxHarness.Body(sent); Assert.True(DamageTaken.TryRead(ref r, out var v)); return v; }

    // ---- pure motion and surfaces ----

    [Fact]
    public void Advance_FollowsTheClientsParabola()
    {
        Vector3 p = new(1f, 2f, 3f), v = new(4f, 10f, -2f);
        Vector3 p0 = p, v0 = v;
        const float dt = 1f / 30f;
        for (int i = 0; i < 45; i++) ProjectileRules.Advance(ref p, ref v, 9.81f, dt);
        float t = 45 * dt;
        Vector3 expected = p0 + v0 * t + new Vector3(0f, -9.81f, 0f) * (0.5f * t * t);
        Assert.True(Vector3.Distance(expected, p) < 1e-3f, $"{p} vs {expected}");
        Assert.True(Vector3.Distance(v0 + new Vector3(0f, -9.81f * t, 0f), v) < 1e-3f);
    }

    [Fact]
    public void Surfaces_GiveTheirNormals()
    {
        var box = new Box(new Vector3(-1f, 0f, -1f), new Vector3(1f, 2f, 1f));
        Box[] boxes = { box };
        Assert.Equal(4f, ProjectileRules.TraceBoxes(new Vector3(-5f, 1f, 0f), Vector3.UnitX, 10f, boxes, out Vector3 n), 4);
        Assert.Equal(-Vector3.UnitX, n);
        ProjectileRules.TraceBoxes(new Vector3(0f, 5f, 0f), -Vector3.UnitY, 10f, boxes, out n);
        Assert.Equal(Vector3.UnitY, n);
        ProjectileRules.TraceBoxes(new Vector3(0f, 1f, 4f), -Vector3.UnitZ, 10f, boxes, out n);
        Assert.Equal(Vector3.UnitZ, n);
        // The flat floor plane is up; a ramp's plane leans against its rise.
        ProjectileRules.TraceWorld(new Vector3(30f, 5f, 30f), -Vector3.UnitY, 50f, ReadOnlySpan<Box>.Empty, HeightField.Flat, out n);
        Assert.Equal(Vector3.UnitY, n);
        var ramp = new BuildPieceShape(BuildPieceType.Ramp, 16, 0, 16, 0);   // rising towards +Z
        Vector3 rampNormal = ProjectileRules.PieceNormal(ramp, new Vector3(2.5f, 10f, 2.5f), -Vector3.UnitY, 8.5f);
        Assert.True(rampNormal.Y > 0.8f && rampNormal.Z < -0.3f, rampNormal.ToString());
        // A bounce reflects the normal component and damps the whole velocity.
        Assert.Equal(new Vector3(4f, 4f, 0f), ProjectileRules.Reflect(new Vector3(10f, -10f, 0f), Vector3.UnitY, 0.4f));
    }

    [Fact]
    public void TheTerrainNormal_IsTheGradients()
    {
        Vector3 origin = new(20f, 40f, -60f);
        float d = ProjectileRules.TraceWorld(origin, -Vector3.UnitY, 100f, ReadOnlySpan<Box>.Empty, GameMap.Terrain, out Vector3 n);
        Vector3 hit = origin - Vector3.UnitY * d;
        Vector2 g = GameMap.Terrain.Gradient(hit.X, hit.Z);
        Assert.True(Vector3.Distance(Vector3.Normalize(new Vector3(-g.X, 1f, -g.Y)), n) < 1e-4f);
    }

    // ---- rockets ----

    [Fact]
    public void Rocket_SpendsItsRound_FliesAndBreaksAWoodWall_ThePlayerBehindIsShielded()
    {
        SandboxHarness h = Harness();
        uint wall = Wall(h);
        PlayerEntity shooter = h.Join(1, new Vector3(2.5f, 0f, -10f));
        PlayerEntity behind = h.Join(2, new Vector3(2.5f, 0f, 2f));
        h.Clear();
        h.Act(shooter, InputButtons.Fire | InputButtons.Slot3, WallCentre);
        Assert.Equal(0, shooter.Inventory.Slots[2].MagAmmo);
        Assert.DoesNotContain(h.Packets, s => s.Id == PacketId.ShotFired);
        ProjectileSpawned spawned = Spawned(h.To(2, PacketId.ProjectileSpawned).Single());
        Assert.Equal(ProjectileKind.Rocket, spawned.Kind);
        Assert.Equal(shooter.EntityId, spawned.OwnerId);
        Assert.Equal(h.Match.ServerTick, spawned.StartTick);
        Assert.Equal(40f, spawned.Velocity.Length(), 3);
        Assert.Equal(1, h.Match.Projectiles.Count);

        TickUntil(h, () => h.Match.Explosions == 1, 20);
        ProjectileExploded boom = Exploded(h.To(2, PacketId.ProjectileExploded).Single());
        Assert.Equal(spawned.Id, boom.Id);
        Assert.InRange(boom.Position.Z, -0.3f, -0.1f);   // at the wall's south face, just outside
        Assert.False(h.Match.Build.Contains(wall));       // 300 structure damage at the face > 150
        Assert.Equal(100, behind.Health);                 // the wall stood when the blast was tested against players
        Assert.Equal(0, h.Match.Projectiles.Count);
    }

    [Fact]
    public void Rocket_DirectHit_ExplodesOnTheTarget_KillCreditAndExplosionCause()
    {
        SandboxHarness h = Harness();
        PlayerEntity shooter = h.Join(1, new Vector3(2.5f, 0f, -12f));
        PlayerEntity target = h.Join(2, new Vector3(2.5f, 0f, -2f));
        target.Health = 50;
        h.Act(shooter, InputButtons.Fire | InputButtons.Slot3, target.State.Position + Chest);
        TickUntil(h, () => h.Match.Explosions == 1, 20);
        Assert.False(target.Alive);
        PlayerDied died = Died(h.To(1, PacketId.PlayerDied).Single());
        Assert.Equal(target.EntityId, died.VictimId);
        Assert.Equal(shooter.EntityId, died.KillerId);
        Assert.Equal(DeathCause.Explosion, died.Cause);
        HitConfirmed hit = Hit(h.To(1, PacketId.HitConfirmed).Single());
        Assert.InRange(hit.Damage, 70, 75);   // the blast is ~0.05 m from the body
        DamageTaken taken = Taken(h.To(2, PacketId.DamageTaken).Single());
        Assert.Equal(shooter.EntityId, taken.AttackerId);
        Assert.True(taken.FromDirection.Z < -0.5f);   // towards the blast, which came from the south
    }

    [Fact]
    public void Rocket_PassesItsOwnerAndTeammates_NoSelfOrTeamDamage()
    {
        SandboxHarness h = Harness();
        uint wall = Wall(h);
        PlayerEntity shooter = h.Join(1, new Vector3(2.5f, 0f, -1.5f));
        PlayerEntity mate = h.Join(2, new Vector3(1.0f, 0f, -1.0f));
        PlayerEntity enemy = h.Join(3, new Vector3(4.2f, 0f, -1.2f));
        shooter.TeamId = mate.TeamId = 200;
        enemy.TeamId = 201;
        h.Act(shooter, InputButtons.Fire | InputButtons.Slot3, WallCentre);
        TickUntil(h, () => h.Match.Explosions == 1, 10);
        Assert.Equal(100, shooter.Health);
        Assert.Equal(100, mate.Health);
        Assert.True(enemy.Health < 100);
        Assert.False(h.Match.Build.Contains(wall));
    }

    [Fact]
    public void FullProjectileSlots_NoLaunch_NoRoundSpent()
    {
        SandboxHarness h = Harness();
        PlayerEntity shooter = h.Join(1, new Vector3(2.5f, 0f, -10f));
        ProjectileDefinition grenade = h.Match.Projectiles.Count == 0 ? Data().Weapons.Projectile(ProjectileKind.Grenade)! : null!;
        for (int i = 0; i < ProjectileSet.Capacity; i++)
            Assert.True(h.Match.Projectiles.TryAdd(new Projectile { Definition = grenade, Resting = true, ExplodeTick = uint.MaxValue }, out _));
        Assert.False(h.Match.Projectiles.HasFree);
        h.Act(shooter, InputButtons.Fire | InputButtons.Slot3, WallCentre);
        Assert.Equal(1, shooter.Inventory.Slots[2].MagAmmo);
        h.Act(shooter, InputButtons.ThrowGrenade, WallCentre);
        Assert.Equal(6, shooter.Inventory.Grenades);
        Assert.DoesNotContain(h.Packets, s => s.Id == PacketId.ProjectileSpawned);
        Assert.Equal(ProjectileSet.Capacity, h.Match.Projectiles.Count);
    }

    // ---- grenades ----

    [Fact]
    public void Grenade_FuseIsThreeSeconds_BouncesAndRests()
    {
        SandboxHarness h = Harness();
        PlayerEntity thrower = h.Join(1, new Vector3(2.5f, 0f, -12f));
        h.Clear();
        h.Act(thrower, InputButtons.ThrowGrenade, new Vector3(2.5f, 1.6f, -2f));
        Assert.Equal(5, thrower.Inventory.Grenades);
        uint spawnTick = h.Match.ServerTick;
        ProjectileSpawned spawned = Spawned(h.To(1, PacketId.ProjectileSpawned).Single());
        Assert.Equal(ProjectileKind.Grenade, spawned.Kind);
        Assert.Equal(spawnTick, spawned.StartTick);
        Assert.True(spawned.Velocity.Y > 0f);   // thrown slightly up
        Assert.Equal(18f, spawned.Velocity.Length(), 3);

        h.Ticks(89);
        Assert.Equal(0, h.Match.Explosions);
        Assert.True(h.Match.Projectiles[0].Resting || h.Match.Projectiles.Count == 1);
        Assert.NotEmpty(h.To(1, PacketId.ProjectileState));   // it bounced
        ProjectileState last = State(h.To(1, PacketId.ProjectileState).Last());
        Assert.Equal(Vector3.Zero, last.Velocity);             // and came to rest
        h.Ticks(1);
        Assert.Equal(1, h.Match.Explosions);                    // 90 ticks = 3 s after the launch tick
        Assert.Single(h.To(1, PacketId.ProjectileExploded));
    }

    // Review fix: a slow grenade rests only on a surface facing up; off a wall it keeps bouncing and falls.
    [Fact]
    public void CanRest_OnlyOnASurfaceFacingUp()
    {
        Vector3 slow = new(0f, 0f, -0.5f);
        Assert.True(ProjectileRules.CanRest(Vector3.UnitY, slow));
        Assert.True(ProjectileRules.CanRest(Vector3.Normalize(new Vector3(0f, 1f, -0.6f)), slow));   // a ramp
        Assert.False(ProjectileRules.CanRest(-Vector3.UnitZ, slow));                                  // a wall
        Assert.False(ProjectileRules.CanRest(-Vector3.UnitY, slow));                                  // a ceiling
        Assert.False(ProjectileRules.CanRest(Vector3.UnitY, new Vector3(0f, 2f, 0f)));                // still fast
    }

    [Fact]
    public void Grenade_SlowlyIntoAWall_DoesNotStick_ItFallsAndRestsOnTheGround()
    {
        SandboxHarness h = Harness();
        Wall(h);
        PlayerEntity thrower = h.Join(1, new Vector3(2.5f, 0f, -12f));
        ProjectileDefinition grenade = Data().Weapons.Projectile(ProjectileKind.Grenade)!;
        Assert.True(h.Match.Projectiles.TryAdd(new Projectile
        {
            Definition = grenade, Position = new Vector3(2.5f, 1.5f, -0.4f), Velocity = new Vector3(0f, 0f, 2f), ExplodeTick = h.Match.ServerTick + 85,
            OwnerEntityId = thrower.EntityId, OwnerJoinOrder = thrower.JoinOrder, OwnerTeam = thrower.TeamId, DamageMultiplier = 1f,
        }, out int slot));
        h.Clear();
        h.Ticks(6);
        Assert.NotEmpty(h.To(1, PacketId.ProjectileState));   // it met the wall at 2 m/s: 0.8 m/s after the bounce
        Assert.False(h.Match.Projectiles[slot].Resting);       // a wall does not hold it
        h.Ticks(60);
        Assert.True(h.Match.Projectiles[slot].Resting);        // on the plaza floor
        Assert.InRange(h.Match.Projectiles[slot].Position.Y, 0f, 0.2f);
        Assert.True(h.Match.Projectiles[slot].Position.Z < -0.1f);   // still south of the wall
    }

    // Phase 17–19 review: a grenade resting on a floor falls again when the floor is destroyed (it does not hang in the air),
    // and the clients are told with a ProjectileState that moves (velocity 0 would read as resting).
    [Fact]
    public void ARestingGrenade_FallsAgain_WhenItsFloorIsDestroyed()
    {
        SandboxHarness h = Harness();
        var floorShape = new BuildPieceShape(BuildPieceType.Floor, 16, 1, 16, 0);   // x 0..5, z 0..5, top at 3 m
        uint floor = h.AddPiece(floorShape);
        float top = BuildGrid.BoundsOf(floorShape).Max.Y;
        PlayerEntity thrower = h.Join(1, new Vector3(2.5f, 0f, -14f));
        ProjectileDefinition grenade = Data().Weapons.Projectile(ProjectileKind.Grenade)!;
        Assert.True(h.Match.Projectiles.TryAdd(new Projectile
        {
            Definition = grenade, Position = new Vector3(2.5f, top + ProjectileRules.SurfaceOffset, 2.5f), Resting = true,
            ExplodeTick = h.Match.ServerTick + 300, OwnerEntityId = thrower.EntityId, OwnerJoinOrder = thrower.JoinOrder, DamageMultiplier = 1f,
        }, out int slot));
        h.Ticks(3);
        Assert.True(h.Match.Projectiles[slot].Resting);   // the floor holds it

        // Review round 2: damage and a placement cannot take a support away: no support trace for them.
        long checks = h.Match.RestSupportChecks;
        Assert.True(h.Match.DamagePieceById(floor, 1f, out bool broke));
        Assert.False(broke);
        h.Ticks(2);
        Assert.Equal(BuildResultCode.Ok, h.Match.PlacePiece(new BuildPieceShape(BuildPieceType.Wall, 16, 0, 16, 0), BuildMaterialType.Wood, out _));
        h.Ticks(2);
        Assert.Equal(checks, h.Match.RestSupportChecks);
        Assert.True(h.Match.Projectiles[slot].Resting);

        h.Clear();
        h.Match.DestroyPieces(new[] { floor });
        h.Ticks(1);
        Assert.False(h.Match.Projectiles[slot].Resting);
        ProjectileState fall = State(h.To(1, PacketId.ProjectileState).Single());
        Assert.True(fall.Velocity.Y < 0f, fall.Velocity.ToString());
        Assert.True(fall.Position.Y < top + ProjectileRules.SurfaceOffset);

        h.Ticks(150);
        Assert.True(h.Match.Projectiles[slot].Resting);
        Assert.InRange(h.Match.Projectiles[slot].Position.Y, 0f, 0.2f);   // on the plaza floor
        Assert.Equal(0, h.Match.Explosions);
    }

    // Review fix: a resend names no owner who left (its entity id may belong to someone else by then).
    [Fact]
    public void AResend_AfterTheOwnerLeft_HasOwnerZero()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        a.Inventory.Grenades = 1;
        ThrowAhead(h, a);                                      // in the lobby (one player: no countdown yet)
        Assert.Equal(1, h.Match.Projectiles.Count);
        h.Match.Leave(1);
        h.Packets.Clear();
        h.Join(2);
        PacketReader r = RoyaleHarness.Reader(Assert.Single(h.SentTo(2, PacketId.ProjectileSpawned)));
        Assert.True(ProjectileSpawned.TryRead(ref r, out ProjectileSpawned p));
        Assert.Equal(0, p.OwnerId);
    }

    [Fact]
    public void Grenade_Interval_Count_EdgePress_AndHealCancel()
    {
        SandboxHarness h = Harness();
        PlayerEntity p = h.Join(1, new Vector3(2.5f, 0f, -12f));
        Vector3 ahead = new(2.5f, 1.6f, -2f);
        // Held over many inputs, it throws once (an edge button).
        for (int i = 0; i < 40; i++) h.Act(p, InputButtons.ThrowGrenade, ahead);
        Assert.Equal(1, h.Match.GrenadesThrown);
        // A new press within the 1 s interval does nothing; after it, one more.
        h.Ticks(100);
        h.Act(p, InputButtons.ThrowGrenade, ahead);
        h.Act(p, InputButtons.None, ahead);
        h.Act(p, InputButtons.ThrowGrenade, ahead);
        Assert.Equal(2, h.Match.GrenadesThrown);
        // A heal in progress is cancelled by a throw.
        h.Ticks(31);
        p.Health = 50;
        p.Inventory.Medkits = 1;
        h.Act(p, InputButtons.None, ahead);
        h.Act(p, InputButtons.UseMedkit, ahead);
        Assert.Equal(ConsumableType.Medkit, p.Inventory.Using);
        h.Act(p, InputButtons.ThrowGrenade, ahead);
        Assert.Equal(ConsumableType.None, p.Inventory.Using);
        Assert.Equal(3, h.Match.GrenadesThrown);
        // None left: nothing.
        p.Inventory.Grenades = 0;
        h.Ticks(31);
        h.Act(p, InputButtons.None, ahead);
        h.Act(p, InputButtons.ThrowGrenade, ahead);
        Assert.Equal(3, h.Match.GrenadesThrown);
    }

    [Fact]
    public void Grenade_Explosion_FallsOff_AndAWallBlocksIt()
    {
        SandboxHarness h = Harness();
        uint wall = Wall(h);
        PlayerEntity thrower = h.Join(1, new Vector3(2.5f, 0f, -14f));
        PlayerEntity near = h.Join(2, new Vector3(3.5f, 0f, -2.5f));
        PlayerEntity behindWall = h.Join(3, new Vector3(2.5f, 0f, 1.5f));
        // A grenade lying 1 m south of the wall (put in directly: the bounce path is not under test here).
        ProjectileDefinition grenade = Data().Weapons.Projectile(ProjectileKind.Grenade)!;
        Assert.True(h.Match.Projectiles.TryAdd(new Projectile
        {
            Definition = grenade, Position = new Vector3(2.5f, 0.05f, -1f), Resting = true, ExplodeTick = h.Match.ServerTick + 1,
            OwnerEntityId = thrower.EntityId, OwnerJoinOrder = thrower.JoinOrder, OwnerTeam = thrower.TeamId, DamageMultiplier = 1f,
        }, out _));
        h.Ticks(1);
        Assert.Equal(1, h.Match.Explosions);
        // near: its box is 1.15 m away (z -2.15 .. x 3.15): 80 x (1 - d / 5).
        float d = CombatRules.DistanceToBox(new Vector3(2.5f, 0.05f, -1f), new Vector3(3.15f, 0f, -2.85f), new Vector3(3.85f, 1.8f, -2.15f));
        Assert.Equal(100 - CombatRules.ExplosionDamage(80, d, 5f, 1f), near.Health);
        Assert.Equal(100, behindWall.Health);   // 2.15 m away but behind the wall
        Assert.Equal(100, thrower.Health);      // out of reach
        Assert.True(h.Match.Build.TryGetSlot(wall, out int slot));
        Assert.True(h.Match.Build.At(slot).Damage > 0);   // pieces take damage without a line of sight
    }

    [Fact]
    public void Explosion_OwnerGone_StillExplodes_NoKillCredit()
    {
        SandboxHarness h = Harness();
        PlayerEntity thrower = h.Join(1, new Vector3(2.5f, 0f, -14f));
        PlayerEntity victim = h.Join(2, new Vector3(2.5f, 0f, -1f));
        victim.Health = 10;
        ProjectileDefinition grenade = Data().Weapons.Projectile(ProjectileKind.Grenade)!;
        h.Match.Projectiles.TryAdd(new Projectile
        {
            Definition = grenade, Position = new Vector3(2.5f, 0.05f, -1.5f), Resting = true, ExplodeTick = h.Match.ServerTick + 1,
            OwnerEntityId = thrower.EntityId, OwnerJoinOrder = thrower.JoinOrder, OwnerTeam = thrower.TeamId, DamageMultiplier = 1f,
        }, out _);
        h.Match.Leave(1);
        h.Clear();
        h.Ticks(1);
        Assert.False(victim.Alive);
        PlayerDied died = Died(h.To(2, PacketId.PlayerDied).Single());
        Assert.Equal(0, died.KillerId);
        Assert.Equal(DeathCause.Explosion, died.Cause);
    }

    [Fact]
    public void ProjectileTicks_AllocateNothing()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 8, DevRespawn = true }, Data(), static (_, _, _) => { }, Loadout(Ar, Shotgun, Rocket));
        Assert.Equal(JoinResult.Ok, match.TryJoin(1, "p1"));
        Assert.Equal(JoinResult.Ok, match.TryJoin(2, "p2"));
        match.TryGetPlayer(1, out PlayerEntity a);
        match.TryGetPlayer(2, out PlayerEntity b);
        a.State.Position = new Vector3(2.5f, 0f, -12f);
        b.State.Position = new Vector3(2.5f, 0f, -4f);
        a.History.Reset(match.ServerTick, a.State.Position);
        b.History.Reset(match.ServerTick, b.State.Position);
        for (int i = 0; i < 160; i++) match.Tick();
        for (int x = 14; x < 19; x++) SandboxHarness.AddPiece(match, new BuildPieceShape(BuildPieceType.Wall, x, 0, 16, 0));
        uint seq = 0;
        void Input(InputButtons buttons, Vector3 at)
        {
            TestAim.YawPitch(a.State.Position, at, out float yaw, out float pitch);
            var packet = new PlayerInputPacket { Count = 1 };
            packet.Set(0, new InputCommand { Seq = ++seq, Buttons = buttons, AimYaw = yaw, AimPitch = pitch, ViewTick = match.ServerTick });
            match.EnqueueInput(1, packet);
            match.Tick();
        }
        // Warm up every path once (JIT, first sends).
        Input(InputButtons.ThrowGrenade, new Vector3(2.5f, 1.6f, 0f));
        Input(InputButtons.Fire | InputButtons.Slot3, WallCentre);
        for (int i = 0; i < 100; i++) Input(InputButtons.None, WallCentre);
        a.Inventory.Grenades = 6;
        a.Inventory.Slots[2].MagAmmo = 1;
        b.Health = 100;
        long start = GC.GetAllocatedBytesForCurrentThread();
        Input(InputButtons.ThrowGrenade, new Vector3(2.5f, 1.6f, 0f));      // a grenade bouncing, resting and exploding
        Input(InputButtons.Fire | InputButtons.Slot3, WallCentre);          // a rocket into the walls
        Input(InputButtons.Slot2, WallCentre);
        Input(InputButtons.Fire, b.State.Position + Chest);                  // a shotgun shot (eight pellets summed)
        for (int i = 0; i < 100; i++) Input(InputButtons.None, WallCentre);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
        Assert.True(match.Explosions >= 4);
    }

    // ---- lifetime in a match ----

    [Fact]
    public void Starting_NoNewProjectile_StartMatchClearsTheLobbys_WithoutAnExplosion()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        a.Inventory.Grenades = 2;
        ThrowAhead(h, a);
        Assert.Equal(1, h.Match.Projectiles.Count);            // the lobby allows a throw
        h.Join(2);
        h.TickUntil(() => h.Match.Flow.State == MatchFlowState.Starting, 5);
        a.NextGrenadeTick = 0;                                   // the throw interval is not under test here
        Assert.Equal(MatchFlowState.Starting, h.Match.Flow.State);
        a.Inventory.Grenades = 2;
        int before = h.Match.Projectiles.Count;
        ThrowAhead(h, a);
        Assert.Equal(2, a.Inventory.Grenades);                  // D6: not during the countdown
        Assert.True(h.Match.Projectiles.Count <= before);
        h.Packets.Clear();
        h.RunToMatch();
        Assert.Equal(0, h.Match.Projectiles.Count);
        Assert.DoesNotContain(h.Packets, s => s.Id == PacketId.ProjectileExploded);
    }

    // Phase 17–19 review: the server clears the lobby's projectiles in the tick the countdown starts, like the clients (their
    // MatchState turns Starting then): nothing the clients dropped explodes during the countdown.
    [Fact]
    public void EnteringStarting_ClearsTheLobbysProjectiles_InThatTick()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        a.Inventory.Grenades = 2;
        ThrowAhead(h, a);
        Assert.Equal(1, h.Match.Projectiles.Count);
        h.Join(2);
        h.Packets.Clear();
        h.Match.Tick();
        Assert.Equal(MatchFlowState.Starting, h.Match.Flow.State);
        Assert.Equal(0, h.Match.Projectiles.Count);
        h.Ticks(RoyaleHarness.CountdownTicks / 2);
        Assert.DoesNotContain(h.Packets, s => s.Id == PacketId.ProjectileExploded || s.Id == PacketId.ProjectileState);
    }

    [Fact]
    public void Finish_ClearsWithoutExplosion_AndNothingIsThrownOnTheResultScreen()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        h.Join(2);
        h.RunToMatch();
        a.Inventory.Grenades = 3;
        ThrowAhead(h, a);
        Assert.Equal(1, h.Match.Projectiles.Count);
        h.Packets.Clear();
        Assert.True(h.Match.ForceFinish());
        Assert.Equal(0, h.Match.Projectiles.Count);
        h.Ticks(35);
        ThrowAhead(h, a);
        Assert.Equal(0, h.Match.Projectiles.Count);
        Assert.DoesNotContain(h.Packets, s => s.Id == PacketId.ProjectileExploded || s.Id == PacketId.ProjectileSpawned);
    }

    [Fact]
    public void ANewcomer_GetsTheFlyingProjectiles()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        a.Inventory.Grenades = 1;
        ThrowAhead(h, a);
        h.Ticks(5);
        h.Packets.Clear();
        h.Join(2);
        RoyaleHarness.Sent sent = Assert.Single(h.SentTo(2, PacketId.ProjectileSpawned));
        PacketReader r = RoyaleHarness.Reader(sent);
        Assert.True(ProjectileSpawned.TryRead(ref r, out ProjectileSpawned p));
        Assert.Equal(h.Match.ServerTick, p.StartTick);
        Assert.Equal(h.Match.Projectiles[0].Position, p.Position);
        Assert.Equal(a.EntityId, p.OwnerId);
    }

    // 기능: 앞(+Z)으로 수류탄을 던지는 입력 하나를 보내고 한 Tick 돌린다.
    // 입력: h - 경기, p - 던지는 사람.
    // 출력: 반환값 없음.
    private static void ThrowAhead(RoyaleHarness h, PlayerEntity p)
    {
        h.Send(p, new InputCommand { Buttons = InputButtons.None, AimYaw = 0f, AimPitch = 0f });
        h.Match.Tick();
        h.Send(p, new InputCommand { Buttons = InputButtons.ThrowGrenade, AimYaw = 0f, AimPitch = 0f });
        h.Match.Tick();
    }
}
