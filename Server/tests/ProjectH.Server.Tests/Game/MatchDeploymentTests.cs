using System;
using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Flow;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 12 D4-D6, D12, D16 (spec §2 공중 투입, 재접속) through Match.Tick: the match starts aboard the drop transport,
// riders follow the route, jump, fall, glide and land; actions wait for the ground; a dropped rider comes back to the
// same state. The zone is the production one (its first circle covers the whole map), so nobody is hurt on the way down.
public class MatchDeploymentTests
{
    public const string WideZones = """
        {
          "initialCenter": [0, 0],
          "initialRadius": 115,
          "arenaHalfSize": 60,
          "phases": [
            {"waitSeconds":45,"shrinkSeconds":40,"targetRadius":70,"damagePerSecond":1},
            {"waitSeconds":20,"shrinkSeconds":15,"targetRadius":0,"damagePerSecond":20}
          ]
        }
        """;

    private static RoyaleHarness Harness(string zones = WideZones) =>
        new(TestGameData.CombatLoadout, zonesJson: zones, airDrop: true);

    private static (RoyaleHarness h, PlayerEntity a, PlayerEntity b) Started(string zones = WideZones)
    {
        RoyaleHarness h = Harness(zones);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        return (h, a, b);
    }

    private static void TickUntilMode(RoyaleHarness h, PlayerEntity p, MovementMode mode, int max = 2000) =>
        h.TickUntil(() => p.State.Mode == mode, max);

    // Ticks with an empty input for every player until the window opens (so jump presses count).
    private static void TickToTheWindow(RoyaleHarness h)
    {
        h.Match.Route.JumpWindow(out uint first, out _);
        h.TickUntil(() => h.Match.ServerTick >= first, 1000);
    }

    [Fact]
    public void TheMatchStart_SendsTheRoute_ThenPutsEveryoneAboard()
    {
        var (h, a, b) = Started();
        Assert.True(h.Match.HasRoute);
        DropRoute route = h.Match.Route;
        // Rolled with SpawnSeed (1) + round (1), starting at the start tick.
        Assert.Equal(DropPlanner.Plan(2, route.StartTick, 30), route);
        Assert.Equal(h.Match.ServerTick, route.StartTick);

        foreach (PlayerEntity p in new[] { a, b })
        {
            Assert.Equal(MovementMode.Transport, p.State.Mode);
            Assert.Equal(route.PositionAt(h.Match.ServerTick), p.State.Position);
            var sent = h.Packets.Where(s => s.PeerId == p.PeerId).ToList();
            int routeAt = sent.FindIndex(s => s.Id == PacketId.TransportRoute);
            int respawnAt = sent.FindIndex(s => s.Id == PacketId.PlayerRespawned);
            Assert.True(routeAt >= 0 && routeAt < respawnAt, "the route before the respawns");
            var r = RoyaleHarness.Reader(sent[routeAt]);
            Assert.True(TransportRoutePacket.TryRead(ref r, out DropRoute received));
            Assert.Equal(route, received);
            Assert.All(sent.Where(s => s.Id == PacketId.PlayerRespawned), s => Assert.Equal(MovementMode.Transport, RoyaleHarness.ReadRespawned(s).Mode));
        }
    }

    [Fact]
    public void Riders_FollowTheRoute_EveryTick_AndTheZoneLeavesThemAlone()
    {
        // The small test zone (radius 30): the route starts far outside it.
        var (h, a, _) = Started(TestGameData.ZonesJson);
        for (int i = 0; i < 60; i++)
        {
            h.Ticks(1);
            Assert.Equal(h.Match.Route.PositionAt(h.Match.ServerTick), a.State.Position);
        }
        Assert.Equal(CombatRules.MaxHealth, a.Health);
    }

    [Fact]
    public void TheZoneClock_StartsWhenTheRouteEnds()
    {
        var (h, _, _) = Started();
        Assert.Equal(1, h.Match.Zone.Phase);
        Assert.Equal(h.Match.Route.EndTick + 45u * 30u, h.Match.Zone.ShrinkStartTick);
    }

    [Fact]
    public void AJumpBeforeTheWindow_IsIgnored_InsideItTheRiderFalls_AndNeverBoardsAgain()
    {
        var (h, a, _) = Started();
        h.Send(a, new InputCommand { Buttons = InputButtons.Jump });
        h.Ticks(1);
        Assert.Equal(MovementMode.Transport, a.State.Mode);

        TickToTheWindow(h);
        h.Send(a, new InputCommand { Buttons = InputButtons.Jump });
        h.Ticks(1);
        Assert.Equal(MovementMode.Freefall, a.State.Mode);
        Vector3 jumpedAt = a.State.Position;
        Assert.Equal(h.Match.Route.PositionAt(h.Match.ServerTick), jumpedAt);

        // Pressing jump again opens the glider; nothing ever puts it back on the transport.
        h.Ticks(2);
        h.Send(a, new InputCommand { Buttons = InputButtons.Jump });
        h.Ticks(1);
        Assert.Equal(MovementMode.Glide, a.State.Mode);
        for (int i = 0; i < 30; i++)
        {
            h.Send(a, new InputCommand { Buttons = InputButtons.Jump });
            h.Ticks(1);
            Assert.NotEqual(MovementMode.Transport, a.State.Mode);
        }
        Assert.True(a.State.Position.Y < jumpedAt.Y);
    }

    [Fact]
    public void RidersWhoSendNothing_AreDropped_GlideDown_AndLandUnhurt()
    {
        var (h, a, b) = Started();
        h.Match.Route.JumpWindow(out _, out uint last);
        h.TickUntil(() => b.State.Mode == MovementMode.Freefall, 2000);
        Assert.Equal(last, h.Match.ServerTick);

        TickUntilMode(h, b, MovementMode.Glide);
        TickUntilMode(h, b, MovementMode.Ground);
        Assert.True(b.Alive);
        Assert.Equal(CombatRules.MaxHealth, b.Health);
        Assert.Equal(Vector2.Zero, b.State.HorizontalVelocity);
        Assert.True(MathF.Abs(b.State.Position.X) < GameMap.HalfSize && MathF.Abs(b.State.Position.Z) < GameMap.HalfSize);
        Assert.Equal(0, h.Match.MovementAnomalies);
        Assert.Empty(h.SentTo(2, PacketId.DamageTaken));
    }

    [Fact]
    public void Riders_Fallers_AndGliders_CannotAct_UntilTheyLand()
    {
        var (h, a, _) = Started();
        // Aboard: fire, pick up and heal do nothing.
        for (int i = 0; i < 5; i++)
        {
            h.Send(a, new InputCommand { Buttons = InputButtons.Fire | InputButtons.Interact | InputButtons.UseMedkit, AimPitch = 10f });
            h.Ticks(1);
        }
        TickToTheWindow(h);
        h.Send(a, new InputCommand { Buttons = InputButtons.Jump });
        h.Ticks(1);
        while (a.State.Mode != MovementMode.Ground)
        {
            h.Send(a, new InputCommand { Buttons = InputButtons.Fire | InputButtons.Interact, AimPitch = 10f });
            h.Ticks(1);
            // The landing tick itself already acts: the gate looks at the mode after the move.
            if (a.State.Mode == MovementMode.Ground) break;
            Assert.Empty(h.SentTo(1, PacketId.ShotFired));
            Assert.Empty(h.SentTo(1, PacketId.PickupResult));
        }

        // On the ground the same buttons act again (the loadout's rifle fires).
        h.Send(a, new InputCommand { Buttons = InputButtons.Fire, AimPitch = 10f });
        h.Ticks(1);
        Assert.NotEmpty(h.SentTo(1, PacketId.ShotFired));
    }

    // b's lag-compensation record at the route position is rewritten with the given mode (the newest record is what a
    // shot sees: the view tick is clamped to the present), a stands off to the side on the ground and fires at it.
    // Transport: not hit. Ground (the control): the very same ray hits, so the rider skip is what decides.
    [Theory]
    [InlineData(MovementMode.Transport, false)]
    [InlineData(MovementMode.Ground, true)]
    public void ARider_CannotBeShot_ButTheSameShotHitsAWalker(MovementMode recorded, bool hits)
    {
        var (h, a, b) = Started();
        a.State.Mode = MovementMode.Ground;
        Vector3 at = h.Match.Route.PositionAt(h.Match.ServerTick);
        b.History.Reset(h.Match.ServerTick, at, recorded);
        h.Place(a, new Vector3(at.X + 20f, 0f, at.Z));
        TestAim.YawPitch(a.State.Position, at + RoyaleHarness.Chest, out float yaw, out float pitch);
        Assert.True(CombatRules.TryAimDirection(yaw, pitch, out Vector3 dir));
        Assert.True(HitScan.TracePlayer(a.State.Position + new Vector3(0f, CombatRules.EyeHeight, 0f), dir, 100f, at, out _),
            "the aimed ray reaches the box");
        h.Send(a, new InputCommand { Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = h.Match.ServerTick });
        h.Ticks(1);
        Assert.Single(h.SentTo(1, PacketId.ShotFired));
        Assert.Equal(hits ? 1 : 0, h.SentTo(1, PacketId.HitConfirmed).Count);
        Assert.Equal(hits, b.Health + b.Shield < CombatRules.MaxHealth + TestGameData.LoadoutShield);
    }

    // The gate: no reload, slot switch or drop aboard or in freefall (the buttons arrive, the server ignores them).
    [Theory]
    [InlineData(MovementMode.Transport)]
    [InlineData(MovementMode.Freefall)]
    public void Reload_SlotSwitch_AndDrop_DoNothing_AboardOrFalling(MovementMode mode)
    {
        var (h, a, _) = Started();
        if (mode == MovementMode.Freefall)
        {
            TickToTheWindow(h);
            h.Send(a, new InputCommand { Buttons = InputButtons.Jump });
            h.Ticks(1);
        }
        Assert.Equal(mode, a.State.Mode);
        a.Inventory.Current.MagAmmo -= 1;
        int mag = a.Inventory.Current.MagAmmo;
        int spawned = h.SentTo(1, PacketId.ItemSpawned).Count;
        for (int i = 0; i < 3; i++)
        {
            h.Send(a, new InputCommand { Buttons = InputButtons.Reload | InputButtons.Slot2 | InputButtons.Drop });
            h.Ticks(1);
            Assert.Equal(mode, a.State.Mode);
        }
        Assert.False(a.Reloading);
        Assert.Equal(0, a.Inventory.CurrentSlot);
        Assert.False(a.Inventory.Current.IsEmpty);
        Assert.Equal(mag, a.Inventory.Current.MagAmmo);
        Assert.Equal(spawned, h.SentTo(1, PacketId.ItemSpawned).Count);   // no dropped weapon
    }

    // Final review C9: while gated the fire button's held state still follows the input, so a semi-automatic weapon held
    // through the landing does not fire until the button is pressed again.
    [Fact]
    public void FireHeld_ThroughTheLanding_DoesNotFireASemiAutomatic_UntilPressedAgain()
    {
        var (h, a, _) = Started();
        a.Inventory.CurrentSlot = 1;   // the loadout's semi-automatic weapon
        Assert.False(a.Inventory.Current.Weapon!.Automatic);
        a.State.Mode = MovementMode.Freefall;
        h.Place(a, new Vector3(0f, 80f, -3f));
        for (int i = 0; i < 3; i++)
        {
            h.Send(a, new InputCommand { Buttons = InputButtons.Fire, AimPitch = 10f });
            h.Ticks(1);
            Assert.Equal(MovementMode.Freefall, a.State.Mode);
        }
        Assert.True(a.FireHeld);

        a.State.Mode = MovementMode.Ground;
        a.State.VelocityY = 0f;
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Send(a, new InputCommand { Buttons = InputButtons.Fire, AimPitch = 10f });
        h.Ticks(1);
        Assert.Equal(MovementMode.Ground, a.State.Mode);
        Assert.Empty(h.SentTo(1, PacketId.ShotFired));

        h.Send(a, new InputCommand { AimPitch = 10f });
        h.Ticks(1);
        h.Send(a, new InputCommand { Buttons = InputButtons.Fire, AimPitch = 10f });
        h.Ticks(1);
        Assert.Single(h.SentTo(1, PacketId.ShotFired));
    }

    [Fact]
    public void AReloadAlreadyRunning_GoesOn_AndCompletes_WhenThePlayerIsAirborne()
    {
        var (h, a, _) = Started();
        a.State.Mode = MovementMode.Ground;
        h.Place(a, new Vector3(0f, 0f, -3f));
        a.Inventory.Current.MagAmmo -= 1;
        h.Send(a, new InputCommand { Buttons = InputButtons.Reload });
        h.Ticks(1);
        Assert.True(a.Reloading);

        a.State.Mode = MovementMode.Freefall;
        h.Place(a, new Vector3(0f, 80f, -3f));
        h.Ticks(1);
        Assert.Equal(MovementMode.Freefall, a.State.Mode);
        Assert.True(a.Reloading);
        h.TickUntil(() => !a.Reloading, 300);
        Assert.Equal(a.Inventory.Current.Weapon!.MagazineSize, a.Inventory.Current.MagAmmo);
    }

    // A hurdle (a 1 m crate) and a mantle (a 1.5 m crate) of the real map through Match.Tick: the vault is a legal move
    // for the self-check, and a vault is no fall.
    [Theory]
    [InlineData(0f, 22f, 1.0f)]      // the crate at (0, 22): hurdle, lands beyond it
    [InlineData(18f, -18f, 1.5f)]    // the crate at (18, -18): mantle, ends on top of it
    public void AHurdleAndAMantle_AreNotAnomalies_AndHurtNobody(float boxX, float boxZ, float top)
    {
        var (h, a, _) = Started();
        a.State.Mode = MovementMode.Ground;
        float front = boxZ - 1f;   // the crate is 2 m deep
        h.Place(a, new Vector3(boxX, 0f, front - 8f));
        var forward = new InputCommand { MoveY = 1f, Buttons = InputButtons.Sprint };
        h.TickUntil(() =>
        {
            h.Send(a, forward);
            return a.State.Position.Z >= front - 0.5f;
        }, 120);
        bool vaulted = false;
        for (int i = 0; i < 60 && !(vaulted && a.State.Mode == MovementMode.Ground); i++)
        {
            h.Send(a, new InputCommand { MoveY = 1f, Buttons = InputButtons.Sprint | InputButtons.Jump });
            h.Ticks(1);
            if (a.State.Mode == MovementMode.Vault) vaulted = true;
        }
        Assert.True(vaulted, "the move was a vault");
        Assert.Equal(MovementMode.Ground, a.State.Mode);
        if (top > 1.2f) Assert.True(MathF.Abs(a.State.Position.Y - top) < 0.2f, "standing on the crate");
        else Assert.True(a.State.Position.Z > boxZ + 1f, "past the crate");
        Assert.Equal(0, h.Match.MovementAnomalies);
        Assert.Empty(h.SentTo(1, PacketId.DamageTaken));
        Assert.True(a.Alive);
    }

    [Fact]
    public void ANewcomerDuringTheMatch_GetsTheRoute()
    {
        var (h, _, _) = Started();
        h.Join(3);
        Assert.Single(h.SentTo(3, PacketId.TransportRoute));
    }

    [Fact]
    public void Snapshots_CarryTheMode_AndTheOwnersMovementState()
    {
        var (h, a, _) = Started();
        h.Ticks(2);
        var last = h.SentTo(1, PacketId.WorldSnapshot).Last();
        var r = RoyaleHarness.Reader(last);
        Assert.True(WorldSnapshotHeader.TryRead(ref r, out WorldSnapshotHeader header));
        Assert.Equal(MoveState.MaxEnergyHundredths, header.Self.Energy);
        for (int i = 0; i < header.Count; i++)
        {
            Assert.True(SnapshotEntity.TryRead(ref r, out SnapshotEntity e));
            Assert.Equal(MovementMode.Transport, e.Mode);
            Assert.True(e.IsAlive);
        }
    }

    [Fact]
    public void AMoveFasterThanItsMode_IsCounted()
    {
        var (h, a, _) = Started();
        Assert.Equal(0, h.Match.MovementAnomalies);
        // A vault state no Step would make: 1 km/s for one tick.
        a.State.Mode = MovementMode.Vault;
        a.State.ModeTicks = 1;
        a.State.HorizontalVelocity = new Vector2(1000f, 0f);
        h.Ticks(1);
        Assert.Equal(1, h.Match.MovementAnomalies);
    }

    [Fact]
    public void TheNextRound_ClearsTheRoute_AndStartsOnTheGround()
    {
        var (h, a, b) = Started();
        h.Match.Flow.Finish(h.Match.ServerTick);
        h.TickUntil(() => h.Match.Flow.State == MatchFlowState.Starting || h.Match.Flow.State == MatchFlowState.WaitingForPlayers, 200);
        Assert.False(h.Match.HasRoute);
        Assert.Equal(MovementMode.Ground, a.State.Mode);
        Assert.Equal(MovementMode.Ground, b.State.Mode);
    }

    // ---- Reconnect (D16) ----

    [Theory]
    [InlineData(MovementMode.Transport)]
    [InlineData(MovementMode.Freefall)]
    [InlineData(MovementMode.Glide)]
    [InlineData(MovementMode.Ground)]
    public void ADropAndAResume_InAnyDeploymentMode_KeepsTheCharactersState_AndResendsTheRoute(MovementMode at)
    {
        var (h, a, _) = Started();
        if (at != MovementMode.Transport) TickUntilMode(h, a, at);
        Assert.True(h.Match.Disconnect(1, allowGrace: true));
        h.Ticks(3);   // graced: the server keeps stepping it with empty input
        MoveState kept = a.State;

        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(11, "p1"));
        Assert.Equal(kept.Position, a.State.Position);
        Assert.Equal(kept.Mode, a.State.Mode);
        Assert.Single(h.SentTo(11, PacketId.TransportRoute));

        // Its next snapshot shows the mode it is in, and it still lands.
        h.Ticks(2);
        var r = RoyaleHarness.Reader(h.SentTo(11, PacketId.WorldSnapshot).Last());
        Assert.True(WorldSnapshotHeader.TryRead(ref r, out WorldSnapshotHeader header));
        bool found = false;
        for (int i = 0; i < header.Count; i++)
        {
            Assert.True(SnapshotEntity.TryRead(ref r, out SnapshotEntity e));
            if (e.EntityId != a.EntityId) continue;
            Assert.Equal(a.State.Mode, e.Mode);
            found = true;
        }
        Assert.True(found);
        TickUntilMode(h, a, MovementMode.Ground);
        Assert.True(a.Alive);
    }

    [Fact]
    public void TheDevSandbox_SpawnsOnTheGround_WithoutARoute()
    {
        var sent = new System.Collections.Generic.List<PacketId>();
        var match = new Match(new ServerOptions { MaxPlayers = 4, DevRespawn = true }, TestGameData.Create(),
            (_, data, _) => sent.Add((PacketId)data[0]));
        Assert.Equal(JoinResult.Ok, match.TryJoin(1, "a"));
        Assert.Equal(JoinResult.Ok, match.TryJoin(2, "b"));
        for (int i = 0; i < 120; i++) match.Tick();
        match.TryGetPlayer(1, out PlayerEntity a);
        Assert.Equal(MovementMode.Ground, a.State.Mode);
        Assert.False(match.HasRoute);
        Assert.DoesNotContain(PacketId.TransportRoute, sent);
    }
}
