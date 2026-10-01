using System;
using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 5 D4, D8-D10, D12 through Match.Tick: zone damage, permanent death, placements, kills, the winner,
// leaving and joining during a match.
public class MatchEliminationTests
{
    // A small first circle (radius 5 around the origin) so a player can stand outside it during phase 1:
    // phase 1 = 2 s wait + 1 s shrink at 3 damage per second, then phase 2 at 7 per second down to radius 0.
    private const string DamageZonesJson = """
        {
          "initialCenter": [0, 0],
          "initialRadius": 5,
          "arenaHalfSize": 19.5,
          "phases": [
            {"waitSeconds":2,"shrinkSeconds":1,"targetRadius":4,"damagePerSecond":3},
            {"waitSeconds":2,"shrinkSeconds":1,"targetRadius":0,"damagePerSecond":7}
          ]
        }
        """;

    private static readonly Vector3 Outside = new(10f, 0f, 0f);   // outside every circle of DamageZonesJson
    private static readonly Vector3 Center = Vector3.Zero;         // inside until phase 2 shrinks

    private static void TickTo(RoyaleHarness h, uint serverTick)
    {
        while (h.Match.ServerTick < serverTick) h.Match.Tick();
    }

    // Spec §6: outside -> Health drops once per second, Shield stays; inside -> nothing; phase 2 hurts more.
    [Fact]
    public void ZoneDamage_OutsideOncePerSecond_HealthOnly_AndPerPhase()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout, DamageZonesJson);   // shield 50
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        uint start = h.Match.MatchStartTick;
        h.Place(a, Outside);
        h.Place(b, Center);

        TickTo(h, start + 30);   // the tick start + 30 is not processed yet
        Assert.Equal(100, a.Health);
        h.Match.Tick();          // processes start + 30: the first whole second
        Assert.Equal(97, a.Health);
        Assert.Equal(TestGameData.LoadoutShield, a.Shield);   // D8: the shield does not stop the zone
        Assert.Equal(100, b.Health);

        TickTo(h, start + 60);
        Assert.Equal(97, a.Health);   // nothing between the seconds
        h.Match.Tick();
        Assert.Equal(94, a.Health);

        TickTo(h, start + 91);        // start + 90: phase 1's shrink ends, phase 2 (7 per second) begins
        Assert.Equal(2, h.Match.Zone.Phase);
        Assert.Equal(87, a.Health);
        Assert.Equal(100, b.Health);
        Assert.Equal(TestGameData.LoadoutShield, a.Shield);
        Assert.Equal(MatchFlowState.FinalPhase, h.Match.Flow.State);   // phase 2 is the last one
    }

    // D4, D8, D9, D12: a zone death is permanent, has no killer and gives no kill; loot never refills.
    [Fact]
    public void ZoneDeath_IsPermanent_WithoutKiller_AndWithAPlacement()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout, DamageZonesJson);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        PlayerEntity c = h.Join(3);
        h.RunToMatch();
        h.Place(a, Outside);
        h.Place(b, Center);
        h.Place(c, Center + new Vector3(0.5f, 0f, 0f));
        a.Health = 3;
        int lootBefore = h.Match.WorldItems.Count;
        int respawnsBefore = h.SentTo(2, PacketId.PlayerRespawned).Count;

        h.TickUntil(() => !a.Alive, 40);
        PlayerDied died = RoyaleHarness.ReadDied(h.SentTo(2, PacketId.PlayerDied).Single());
        Assert.Equal(a.EntityId, died.VictimId);
        Assert.Equal(0, died.KillerId);
        Assert.Equal(3, died.Placement);
        Assert.Equal(3, a.Placement);
        Assert.Equal(0, b.Kills + c.Kills);
        Assert.Equal(2, h.Match.Flow.Alive);

        h.Ticks(200);   // far past the dev respawn delay (3 s) and nothing comes back
        Assert.False(a.Alive);
        Assert.True(h.Match.Flow.InMatch);
        Assert.Equal(respawnsBefore, h.SentTo(2, PacketId.PlayerRespawned).Count);
        Assert.Equal(lootBefore + 4, h.Match.WorldItems.Count);   // A's death drop; no spawn point refilled
    }

    // Spec §6 / Review Focus: placements are the reverse death order, kills are counted, exactly one winner who is
    // the last one alive, and the match finishes in the tick of the last kill.
    [Fact]
    public void Kills_Placements_AndTheLastOneAliveWins()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        PlayerEntity c = h.Join(3);
        h.RunToMatch();
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.Place(c, new Vector3(3f, 0f, 0f));

        h.ShootUntilDead(a, b);
        Assert.Equal(3, b.Placement);
        Assert.Equal(1, a.Kills);
        Assert.Equal(MatchFlowState.Playing, h.Match.Flow.State);

        h.ShootUntilDead(a, c);
        Assert.Equal(2, c.Placement);
        Assert.Equal(2, a.Kills);
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        Assert.Equal(1, a.Placement);
        Assert.True(a.Alive);
        Assert.Equal(a.EntityId, h.Match.WinnerId);
        Assert.Single(new[] { a, b, c }, p => p.Placement == 1);

        PlayerDied lastDeath = RoyaleHarness.ReadDied(h.SentTo(3, PacketId.PlayerDied).Last());
        Assert.Equal(c.EntityId, lastDeath.VictimId);
        Assert.Equal(a.EntityId, lastDeath.KillerId);
        Assert.Equal(2, lastDeath.Placement);

        // Finished: no more damage, and the dead stay dead until the round reset.
        h.Ticks(RoyaleHarness.ResultTicks - 2);
        Assert.False(b.Alive);
        Assert.False(c.Alive);
    }

    // D9: the last two die in the same tick (the zone, list order): the one processed last is first, the only winner.
    [Fact]
    public void LastTwoDieInTheSameTick_TheOneProcessedLastWins()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout, DamageZonesJson);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        h.Place(a, Outside);
        h.Place(b, -Outside);
        a.Health = 3;
        b.Health = 3;

        h.TickUntil(() => h.Match.Flow.State == MatchFlowState.Finished, 40);
        Assert.False(a.Alive);
        Assert.False(b.Alive);
        Assert.Equal(2, a.Placement);   // processed first
        Assert.Equal(1, b.Placement);
        Assert.Equal(b.EntityId, h.Match.WinnerId);
        Assert.Equal(0, h.Match.Flow.Alive);
    }

    // Review Focus: a match that never ends. Nobody fights; the zone closes to radius 0 and kills everyone, so the
    // spec zones (about 2 min) end with a finished match and exactly one winner.
    [Fact]
    public void NobodyFights_TheZoneStillEndsTheMatch()
    {
        var h = new RoyaleHarness();
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        uint start = h.Match.MatchStartTick;
        const uint zoneTicks = 3540;   // every wait and shrink of spec §1 at 30 Hz

        h.TickUntil(() => h.Match.Flow.State == MatchFlowState.Finished, (int)zoneTicks + 5 * 30 + 30);

        Assert.True(h.Match.ServerTick <= start + zoneTicks + 5 * 30 + 31);
        Assert.Single(new[] { a, b }, p => p.Placement == 1);
        Assert.Contains(h.Match.WinnerId, new[] { a.EntityId, b.EntityId });
    }

    // D10 / Review Focus: a leaver does not avoid elimination. Its items drop for the others, it no longer counts
    // as alive, and the remaining player wins on the next tick.
    [Fact]
    public void Leaving_MidMatch_IsAnElimination_AndDropsTheInventory()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        int itemsBefore = h.Match.WorldItems.Count;
        h.Packets.Clear();

        h.Match.Leave(2);

        Assert.Equal(1, h.Match.Flow.Alive);
        Assert.Equal(itemsBefore + 4, h.Match.WorldItems.Count);   // Test Auto, Test Semi, Medium and Heavy ammo
        Assert.Equal(4, h.SentTo(1, PacketId.ItemSpawned).Count);
        Assert.Empty(h.SentTo(2, PacketId.ItemSpawned));             // the leaver is gone already

        h.Match.Tick();
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        Assert.Equal(a.EntityId, h.Match.WinnerId);
        Assert.Equal(1, a.Placement);
    }

    // D10: someone who joins during the match spectates: dead, not a participant, told so reliably, never hit,
    // and it cannot keep the match going. The next round it plays.
    [Fact]
    public void JoiningMidMatch_Spectates_UntilTheNextRound()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();

        PlayerEntity late = h.Join(3);
        Assert.False(late.Alive);
        Assert.False(late.Participant);
        PlayerDied told = RoyaleHarness.ReadDied(h.SentTo(3, PacketId.PlayerDied).Single());
        Assert.Equal(late.EntityId, told.VictimId);
        Assert.Equal(0, told.KillerId);
        Assert.Equal(0, told.Placement);
        Assert.Empty(h.SentTo(1, PacketId.PlayerDied));   // only the newcomer is told
        Assert.Equal(2, h.Match.Flow.Alive);

        // Standing between the shooter and the target, the spectator is not hit.
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(late, new Vector3(0f, 0f, 0f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.ShootUntilDead(a, b);
        Assert.Equal(CombatRules.MaxHealth, late.Health);
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        Assert.Equal(a.EntityId, h.Match.WinnerId);

        h.TickUntil(() => h.Match.Flow.Round == 2, RoyaleHarness.ResultTicks + 5);
        Assert.True(late.Alive);
        h.RunToMatch();
        Assert.True(late.Participant);
        Assert.Equal(3, h.Match.Flow.Participants);
    }

    // Allocation-free ticks: a running match with zone damage applied every second (no deaths in the window).
    [Fact]
    public void MatchTicks_WithZoneDamage_DoNotAllocate()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout, DamageZonesJson, record: false);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        h.Place(a, Outside);
        h.Place(b, Center);
        h.Match.Tick();   // warm up

        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 80; i++) h.Match.Tick();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Equal(0, allocated);
        Assert.Equal(94, a.Health);   // two zone hits landed inside the window
    }

    // D9, D10: the last one alive leaves before the match finishes. Leaving is an elimination (placement 1), so
    // nobody is left to win: WinnerId is 0, and the participant still connected gets its own correct result.
    [Fact]
    public void PlacementOnePlayerLeftBeforeFinished_NoWinner_RemainingGetTheirResults()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        PlayerEntity c = h.Join(3);
        h.RunToMatch();
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.Place(c, new Vector3(3f, 0f, 0f));
        h.ShootUntilDead(a, b);
        Assert.Equal(3, b.Placement);
        Assert.Equal(MatchFlowState.Playing, h.Match.Flow.State);

        h.Match.Leave(3);   // 2nd place
        h.Match.Leave(1);   // the last one alive: placement 1, gone before Finished
        Assert.Equal(2, c.Placement);
        Assert.Equal(1, a.Placement);
        Assert.Equal(0, h.Match.Flow.Alive);
        h.Packets.Clear();

        h.Match.Tick();

        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        Assert.Equal(0, h.Match.WinnerId);
        Assert.Empty(h.SentTo(1, PacketId.MatchResult));   // leavers get none
        Assert.Empty(h.SentTo(3, PacketId.MatchResult));
        var reader = RoyaleHarness.Reader(h.SentTo(2, PacketId.MatchResult).Single());
        Assert.True(MatchResult.TryRead(ref reader, out var result));
        Assert.Equal(0, result.WinnerId);
        Assert.Equal(3, result.Placement);
        Assert.Equal(0, result.Kills);
        Assert.Equal(3, result.Participants);
    }

    // D13: three rounds in a row. Each ends by the zone; after each Closing the world holds no items and every
    // connected player is alive at its spawn point with an empty inventory. Bounded loops only.
    [Fact]
    public void ThreeRounds_EachClosingLeavesAnEmptyWorldAndFreshPlayers()
    {
        var h = new RoyaleHarness(zonesJson: DamageZonesJson);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);

        for (int round = 1; round <= 3; round++)
        {
            Assert.Equal(round, h.Match.Flow.Round);
            h.RunToMatch();
            Assert.True(h.Match.WorldItems.Count > 0, "the match fills the world with loot");
            h.Place(a, Outside);
            h.Place(b, Center);
            a.Health = 3;
            // Something to lose at the round reset.
            b.Inventory.Medkits = 2;
            b.Inventory.ShieldCells = 1;
            b.Inventory.SetAmmo(AmmoType.Light, 30);
            b.Shield = 40;

            h.TickUntil(() => h.Match.Flow.Round == round + 1, 40 + 30 + RoyaleHarness.ResultTicks + 5);

            Assert.Equal(0, h.Match.WorldItems.Count);
            foreach (var player in new[] { a, b })
            {
                Assert.True(player.Alive);
                Assert.Equal(Match.SpawnPosition(player.EntityId), player.State.Position);
                Assert.Equal(CombatRules.MaxHealth, player.Health);
                Assert.Equal(0, player.Shield);
                Assert.Equal(0, player.Inventory.Medkits);
                Assert.Equal(0, player.Inventory.ShieldCells);
                Assert.Equal(0, player.Inventory.GetAmmo(AmmoType.Light));
                Assert.Equal(0, player.Inventory.GetAmmo(AmmoType.Medium));
                Assert.Equal(0, player.Inventory.GetAmmo(AmmoType.Heavy));
                foreach (var slot in player.Inventory.Slots) Assert.True(slot.IsEmpty);
            }
        }
    }
}
