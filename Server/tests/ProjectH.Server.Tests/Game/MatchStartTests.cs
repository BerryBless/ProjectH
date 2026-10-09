using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 5 D2 (before the match), D3 (the start) and D13 (the round reset), through Match.Tick.
public class MatchStartTests
{
    // Review Focus: damage before the match. Shots fly and stop at the target, but nobody is hurt and the
    // shooter gets no HitConfirmed; nobody can die, so nothing is dropped.
    [Fact]
    public void BeforeTheMatch_ShotsHurtNobody()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout, minPlayers: 3);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));

        for (int i = 0; i < 60; i++) h.ShootOnce(a, b);   // 20 shots: 600 damage if any of it counted

        Assert.Equal(MatchFlowState.WaitingForPlayers, h.Match.Flow.State);
        Assert.Equal(CombatRules.MaxHealth, b.Health);
        Assert.Equal(TestGameData.LoadoutShield, b.Shield);
        Assert.True(b.Alive);
        Assert.Empty(h.SentTo(1, PacketId.HitConfirmed));
        Assert.Empty(h.SentTo(2, PacketId.DamageTaken));
        Assert.DoesNotContain(h.Packets, s => s.Id == PacketId.PlayerDied);
        // The tracer still ends at B (a shot into the open would fly 100 m).
        ShotFired shot = RoyaleHarness.ReadShot(h.SentTo(2, PacketId.ShotFired)[0]);
        Assert.True(Vector3.Distance(shot.Start, shot.End) < 6.5f);
    }

    [Fact]
    public void DuringTheCountdown_ShotsHurtNobody_AndThereIsNoLoot()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.Match.Tick();
        Assert.Equal(MatchFlowState.Starting, h.Match.Flow.State);
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));

        for (int i = 0; i < 20; i++) h.ShootOnce(a, b);

        Assert.Equal(MatchFlowState.Starting, h.Match.Flow.State);
        Assert.Equal(CombatRules.MaxHealth, b.Health);
        Assert.Equal(TestGameData.LoadoutShield, b.Shield);
        Assert.Empty(h.SentTo(1, PacketId.HitConfirmed));
        Assert.Equal(0, h.Match.WorldItems.Count);
    }

    [Fact]
    public void MatchStart_ResetsEveryone_RollsLoot_AndStartsTheZone_InOneTick()
    {
        var h = new RoyaleHarness();   // production start: empty-handed, Shield 0
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.Match.Tick();
        // Before the start: moved away, carrying things, hurt, with kills from nowhere.
        h.Place(a, new Vector3(10f, 0f, 10f));
        a.Inventory.Medkits = 2;
        a.Inventory.SetAmmo(AmmoType.Light, 40);
        a.Health = 30;
        a.Shield = 20;
        a.Kills = 3;
        h.Packets.Clear();

        h.RunToMatch();

        Assert.Equal(MatchFlowState.Playing, h.Match.Flow.State);
        // Phase 6 D9: each on a different drop point (the harness's lobby ring spots).
        Assert.NotEqual(a.State.Position, b.State.Position);
        foreach (PlayerEntity p in new[] { a, b })
        {
            Assert.Contains(p.State.Position, RoyaleHarness.LobbyRingDrops);
            Assert.Equal(CombatRules.MaxHealth, p.Health);
            Assert.Equal(0, p.Shield);
            Assert.True(p.Alive);
            Assert.True(p.Participant);
            Assert.Equal(0, p.Kills);
            Assert.Equal(0, p.Inventory.Medkits);
            Assert.Equal(0, p.Inventory.GetAmmo(AmmoType.Light));
            Assert.True(p.Inventory.Slots.All(s => s.IsEmpty));
            // Everyone hears of every teleport (the client re-syncs its prediction as after a respawn).
            Assert.Contains(h.SentTo(1, PacketId.PlayerRespawned), s => RoyaleHarness.ReadRespawned(s).EntityId == p.EntityId);
        }
        Assert.Equal(LootPoints.All.Length, h.Match.WorldItems.Count);
        Assert.Equal(2, h.Match.Flow.Participants);
        Assert.Equal(2, h.Match.Flow.Alive);
        Assert.Equal(1, h.Match.Zone.Phase);
        Assert.Equal(LootPoints.All.Length, h.SentTo(2, PacketId.ItemSpawned).Count);
    }

    // D3: loot seed = LootSeed + round. The same round on two servers rolls the same loot; the next round differs.
    [Fact]
    public void MatchLoot_IsSeededByTheRound()
    {
        // 기능: 월드 아이템을 위치 순으로 정렬해 ItemId를 0으로 지운 목록으로 만든다(두 서버 비교용).
        // 입력: h - 경기 하네스.
        // 출력: 비교할 아이템 데이터 배열.
        static WorldItemData[] LootOf(RoyaleHarness h) =>
            Enumerable.Range(0, h.Match.WorldItems.Count).Select(i => h.Match.WorldItems[i].Data)
                .OrderBy(d => d.Position.X).ThenBy(d => d.Position.Z).ThenBy(d => d.Position.Y)
                .Select(d => d with { ItemId = 0 }).ToArray();

        var first = new RoyaleHarness();
        var second = new RoyaleHarness();
        foreach (var h in new[] { first, second })
        {
            h.Join(1);
            h.Join(2);
            h.RunToMatch();
        }
        WorldItemData[] round1 = LootOf(first);
        Assert.Equal(round1, LootOf(second));

        // Round 2 on the first server.
        first.Match.Flow.Eliminate();
        first.Match.Flow.Finish(first.Match.ServerTick);
        first.TickUntil(() => first.Match.Flow.Round == 2, RoyaleHarness.ResultTicks + 5);
        first.RunToMatch();
        Assert.NotEqual(round1, LootOf(first));
    }

    // Respawn keeps Seq: the first input after the start continues the client's sequence and moves the player.
    [Fact]
    public void MatchStart_KeepsTheInputSequence()
    {
        var h = new RoyaleHarness();
        PlayerEntity a = h.Join(1);
        h.Join(2);
        for (int i = 0; i < 5; i++)
        {
            h.Send(a, new InputCommand { MoveY = 1f });
            h.Match.Tick();
        }
        uint before = a.LastProcessedSeq;
        Assert.Equal(5u, before);

        h.RunToMatch();
        Assert.Equal(before, a.LastProcessedSeq);
        Vector3 spawn = a.State.Position;
        for (int i = 0; i < 5; i++)
        {
            h.Send(a, new InputCommand { MoveY = 1f });
            h.Match.Tick();
        }
        Assert.Equal(before + 5, a.LastProcessedSeq);
        Assert.NotEqual(spawn, a.State.Position);
    }

    // Review Focus: a round reset leaking items or state. After Finished -> Closing every item is gone (and every
    // client told), everyone is alive on the spawn ring with nothing, the zone is off, and the next round counts down.
    [Fact]
    public void RoundReset_ClearsItemsAndState_AndCountsDownTheNextRound()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        h.Match.SpawnItem(new LootRoll(ItemKind.Consumable, (byte)ConsumableType.Medkit, 0, 1), new Vector3(3f, 0f, 3f), -1);
        b.Alive = false;
        b.Placement = h.Match.Flow.Eliminate();
        b.Health = 0;
        a.Kills = 1;
        h.Match.Flow.Finish(h.Match.ServerTick);
        int itemsBefore = h.Match.WorldItems.Count;
        Assert.Equal(LootPoints.All.Length + 1, itemsBefore);
        h.Packets.Clear();

        h.TickUntil(() => h.Match.Flow.Round == 2, RoyaleHarness.ResultTicks + 5);

        Assert.Equal(MatchFlowState.Starting, h.Match.Flow.State);
        Assert.Equal(0, h.Match.WorldItems.Count);
        Assert.Equal(itemsBefore, h.SentTo(1, PacketId.ItemRemoved).Count);
        Assert.Equal(itemsBefore, h.SentTo(2, PacketId.ItemRemoved).Count);
        Assert.Equal(0, h.Match.Zone.Phase);
        Assert.Equal(0, h.Match.Flow.Participants);
        foreach (PlayerEntity p in new[] { a, b })
        {
            Assert.True(p.Alive);
            Assert.Equal(CombatRules.MaxHealth, p.Health);
            Assert.Equal(Match.SpawnPosition(p.EntityId), p.State.Position);
            Assert.False(p.Participant);
            Assert.Equal(0, p.Placement);
        }
        Assert.Equal(2, h.SentTo(1, PacketId.PlayerRespawned).Count);

        // Before the next match starts, shots hurt nobody again.
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        for (int i = 0; i < 10; i++) h.ShootOnce(a, b);
        Assert.Equal(CombatRules.MaxHealth, b.Health);
        Assert.Equal(TestGameData.LoadoutShield, b.Shield);
    }

    // D13: Closing is entered and left in the same tick (Match calls Reopen right after the reset), so no tick
    // ever ends in Closing. Finished -> Closing -> round 2 Starting -> round 2 Playing, checked after every tick.
    [Fact]
    public void Closing_NeverOutlivesItsTick_AndRound2Plays()
    {
        var h = new RoyaleHarness();
        h.Join(1);
        h.Join(2);
        h.RunToMatch();
        h.Match.Flow.Eliminate();
        h.Match.Flow.Finish(h.Match.ServerTick);
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        h.Packets.Clear();

        int ticks = 0;
        while (!(h.Match.Flow.Round == 2 && h.Match.Flow.InMatch))
        {
            Assert.True(++ticks <= RoyaleHarness.ResultTicks + 2 * RoyaleHarness.CountdownTicks + 5, $"stuck in {h.Match.Flow.State}");
            h.Match.Tick();
            Assert.NotEqual(MatchFlowState.Closing, h.Match.Flow.State);
        }
        Assert.Equal(MatchFlowState.Playing, h.Match.Flow.State);
        Assert.Equal(2, h.Match.Flow.Participants);
        Assert.Equal(LootPoints.All.Length, h.Match.WorldItems.Count);
        Assert.Equal(1, h.Match.Zone.Phase);
        // The reset inventories reach their owners (no round-1 items left on a client's HUD).
        Assert.NotEmpty(h.SentTo(1, PacketId.InventoryState));
        Assert.NotEmpty(h.SentTo(2, PacketId.InventoryState));
    }

    // D13: with too few players left at Closing, the next round waits instead of counting down.
    [Fact]
    public void RoundReset_WithTooFewPlayers_WaitsForPlayers()
    {
        var h = new RoyaleHarness();
        PlayerEntity a = h.Join(1);
        h.Join(2);
        h.RunToMatch();
        h.Match.Flow.Eliminate();
        h.Match.Flow.Finish(h.Match.ServerTick);
        h.Match.Leave(2);

        h.TickUntil(() => h.Match.Flow.Round == 2, RoyaleHarness.ResultTicks + 5);

        Assert.Equal(MatchFlowState.WaitingForPlayers, h.Match.Flow.State);
        Assert.Equal(0, h.Match.WorldItems.Count);
        Assert.Equal(0, h.Match.Zone.Phase);
        Assert.False(a.Participant);
        h.Ticks(RoyaleHarness.CountdownTicks * 2);
        Assert.Equal(MatchFlowState.WaitingForPlayers, h.Match.Flow.State);
    }
}
