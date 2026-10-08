using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// D7: a looted spawn point is refilled LootRespawnSeconds later (30 s = 900 ticks at 30 Hz); 0 turns it off.
public class LootRespawnTests
{
    private static readonly Vector3 PointPosition = new(0f, 0f, 8f);

    private readonly List<(int Peer, PacketId Id)> _sent = new();
    private Match _match = null!;
    private PlayerEntity _a = null!;
    private uint _seq;

    private void Start(int respawnSeconds, string lootJson = TestGameData.LootJson)
    {
        _match = new Match(new ServerOptions { MaxPlayers = 2, LootRespawnSeconds = respawnSeconds, DevRespawn = true },TestGameData.Create(lootJson: lootJson),
            (peer, data, _) => _sent.Add((peer, (PacketId)data[0])), TestGameData.CombatLoadout,
            new[] { new LootPoint(PointPosition, LootPoints.FloorTable) });
        _match.TryJoin(1, "a");
        _match.TryGetPlayer(1, out _a);
        _a.State.Position = PointPosition + new Vector3(0f, 0f, -1f);
        _sent.Clear();
    }

    private void Press(InputButtons buttons)
    {
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = ++_seq, Buttons = buttons, Yaw = 180f });
        _match.EnqueueInput(1, packet);
        _match.Tick();
    }

    private bool PointFilled() => Enumerable.Range(0, _match.WorldItems.Count).Any(i => _match.WorldItems[i].SpawnPoint == 0);

    private int PointItems() => Enumerable.Range(0, _match.WorldItems.Count).Count(i => _match.WorldItems[i].SpawnPoint == 0);

    [Fact]
    public void TakenPoint_IsRefilled_After30Seconds()
    {
        Start(respawnSeconds: 30, TestGameData.WeaponsOnlyLootJson);   // a weapon is always taken whole
        Press(InputButtons.Interact);
        Assert.False(PointFilled());
        uint takenAt = _match.ServerTick - 1;
        _sent.Clear();

        while (_match.ServerTick < takenAt + 900) _match.Tick();
        Assert.False(PointFilled());
        _match.Tick();

        Assert.True(PointFilled());
        int index = Enumerable.Range(0, _match.WorldItems.Count).Single(i => _match.WorldItems[i].SpawnPoint == 0);
        Assert.Equal(PointPosition, _match.WorldItems[index].Data.Position);
        Assert.Contains(_sent, s => s.Peer == 1 && s.Id == PacketId.ItemSpawned);
    }

    [Fact]
    public void ZeroSeconds_NeverRefills()
    {
        Start(respawnSeconds: 0, TestGameData.WeaponsOnlyLootJson);
        Press(InputButtons.Interact);
        for (int i = 0; i < 2000; i++) _match.Tick();
        Assert.False(PointFilled());
    }

    // A partly taken stack stays at its point, so the point is not looted and gets no second item.
    private const string AmmoOnlyLootJson = """
        {
          "rarityWeights": { "Common": 1, "Uncommon": 1, "Rare": 1, "Epic": 1, "Legendary": 1 },
          "tables": { "Floor": [ { "kind": "Ammo", "weight": 1 } ], "Building": [ { "kind": "Ammo", "weight": 1 } ], "Tower": [ { "kind": "Ammo", "weight": 1 } ] }
        }
        """;

    [Fact]
    public void PartialPickup_KeepsThePointFilled_WithoutAnExtraItem()
    {
        Start(respawnSeconds: 1, AmmoOnlyLootJson);
        var type = (AmmoType)_match.WorldItems[0].Data.DefId;
        int max = TestGameData.Items().Ammo(type).Max;
        _match.SetItemAmount(0, 1000);
        _a.Inventory.SetAmmo(type, max - 5);

        Press(InputButtons.Interact);
        for (int i = 0; i < 100; i++) _match.Tick();   // well past the 30-tick respawn delay

        Assert.Equal(max, _a.Inventory.GetAmmo(type));
        Assert.Equal(1, _match.WorldItems.Count);
        Assert.Equal(1, PointItems());
        Assert.Equal(995, _match.WorldItems[0].Data.Amount);
    }

    // The refill itself refuses a point that holds an item, whatever put it there (here: a timer that was set
    // and then the item came back, like the swap rollback in PickupWeapon). One live item per point.
    [Fact]
    public void DueTimer_DoesNotRefillAPointThatHoldsAnItem()
    {
        Start(respawnSeconds: 1, TestGameData.WeaponsOnlyLootJson);
        _match.RemoveItemAt(0);                                                        // timer set
        _match.SpawnItem(new LootRoll(ItemKind.Weapon, _a.Inventory.Current.Weapon!.Id, 0, 5), PointPosition, 0);
        for (int i = 0; i < 100; i++) _match.Tick();
        Assert.Equal(1, PointItems());

        // The timer was cleared, not just skipped: taking the item again arms a fresh one.
        _match.RemoveItemAt(Enumerable.Range(0, _match.WorldItems.Count).Single(i => _match.WorldItems[i].SpawnPoint == 0));
        for (int i = 0; i < 31; i++) _match.Tick();
        Assert.Equal(1, PointItems());
    }

    // D12: dropped items are not spawn points; taking one refills nothing.
    [Fact]
    public void TakingADroppedItem_RefillsNothing()
    {
        Start(respawnSeconds: 1, TestGameData.WeaponsOnlyLootJson);
        _match.RemoveItemAt(0);                            // the point is now waiting
        for (int i = 0; i < 31; i++) _match.Tick();        // ... and refilled
        Assert.True(PointFilled());
        int before = _match.WorldItems.Count;

        _a.State.Position = new Vector3(8f, 0f, 0f);
        Press(InputButtons.Drop);                          // G: a dropped weapon, not a spawn item
        Assert.Equal(before + 1, _match.WorldItems.Count);
        for (int i = 0; i < 8; i++) _match.Tick();         // review fix C6: past the item action interval
        Press(InputButtons.Interact);
        for (int i = 0; i < 100; i++) _match.Tick();
        Assert.Equal(before, _match.WorldItems.Count);
    }
}
