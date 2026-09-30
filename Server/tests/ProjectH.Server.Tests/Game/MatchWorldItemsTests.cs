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

// World items through Match: initial loot, the join list, and the events every change sends.
public class MatchWorldItemsTests
{
    private sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method)
    {
        public PacketId Id => (PacketId)Data[0];
    }

    private readonly List<Sent> _sent = new();

    private Match NewMatch(int seed = 1, LootPoint[]? points = null) =>
        new(new ServerOptions { MaxPlayers = 4, LootSeed = seed }, TestGameData.Create(),
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)), lootPoints: points);

    private static PacketReader Reader(Sent s)
    {
        var reader = new PacketReader(s.Data);
        reader.TryReadPacketId(out _);
        return reader;
    }

    // Every item the WorldItems packets to one peer carried, in order.
    private List<WorldItemData> ListSentTo(int peer)
    {
        var items = new List<WorldItemData>();
        foreach (Sent s in _sent.Where(s => s.PeerId == peer && s.Id == PacketId.WorldItems))
        {
            Assert.Equal(DeliveryMethod.ReliableOrdered, s.Method);
            Assert.True(s.Data.Length <= ProtocolConstants.MaxPacketSize, $"chunk of {s.Data.Length} bytes");
            var reader = Reader(s);
            Assert.True(WorldItemsPacket.TryReadHeader(ref reader, out int count));
            for (int i = 0; i < count; i++)
            {
                Assert.True(WorldItemData.TryRead(ref reader, out var item));
                items.Add(item);
            }
            Assert.Equal(0, reader.Remaining);
        }
        return items;
    }

    private static LootRoll Ammo(ushort amount = 10) => new(ItemKind.Ammo, (byte)AmmoType.Light, 0, amount);

    [Fact]
    public void NewMatch_FillsEveryLootPoint()
    {
        var match = NewMatch();
        Assert.Equal(LootPoints.All.Length, match.WorldItems.Count);
        for (int point = 0; point < LootPoints.All.Length; point++)
        {
            int index = Enumerable.Range(0, match.WorldItems.Count).Single(i => match.WorldItems[i].SpawnPoint == point);
            Assert.Equal(LootPoints.All[point].Position, match.WorldItems[index].Data.Position);
        }
    }

    // D5: the seed decides the loot, so a restart with the same seed lays out the same items.
    [Fact]
    public void SameSeed_SameLoot_OtherSeed_OtherLoot()
    {
        static string Describe(Match m) => string.Join(";", Enumerable.Range(0, m.WorldItems.Count)
            .Select(i => m.WorldItems[i].Data).Select(d => $"{d.Kind}/{d.DefId}/{d.Rarity}/{d.Amount}@{d.Position}"));

        Assert.Equal(Describe(NewMatch(seed: 42)), Describe(NewMatch(seed: 42)));
        Assert.NotEqual(Describe(NewMatch(seed: 42)), Describe(NewMatch(seed: 43)));
    }

    [Fact]
    public void Constructor_RejectsAPointWithAnUnknownTable()
    {
        var points = new[] { new LootPoint(new Vector3(0f, 0f, 8f), "Basement") };
        Assert.Throws<ArgumentException>(() => NewMatch(points: points));
    }

    // Success criterion: both clients see the same items at the same places.
    [Fact]
    public void Join_SendsTheWholeList_SameForEveryPlayer_BeforeSpawns()
    {
        var match = NewMatch();
        match.TryJoin(1, "a");
        match.TryJoin(2, "b");

        var toA = ListSentTo(1);
        var toB = ListSentTo(2);
        Assert.Equal(LootPoints.All.Length, toA.Count);
        Assert.Equal(toA, toB);

        var order = _sent.Where(s => s.PeerId == 1).Select(s => s.Id).ToList();
        Assert.True(order.IndexOf(PacketId.WorldItems) < order.IndexOf(PacketId.PlayerSpawned));
    }

    // Review Focus: a full world (256 items) goes out in 6 chunks of at most 50, each under 1200 bytes.
    [Fact]
    public void Join_WithAFullWorld_SendsSixChunks_AndEveryItemOnce()
    {
        var match = NewMatch();
        for (int i = match.WorldItems.Count; i < WorldItems.Capacity; i++) match.SpawnItem(Ammo(), new Vector3(i % 20, 0f, i / 20), -1);
        Assert.Equal(WorldItems.Capacity, match.WorldItems.Count);

        match.TryJoin(1, "a");

        Assert.Equal(6, _sent.Count(s => s.PeerId == 1 && s.Id == PacketId.WorldItems));
        var items = ListSentTo(1);
        Assert.Equal(WorldItems.Capacity, items.Count);
        Assert.Equal(WorldItems.Capacity, items.Select(item => item.ItemId).Distinct().Count());
    }

    [Fact]
    public void Spawn_Amount_Remove_AreSentToEveryone()
    {
        var match = NewMatch();
        match.TryJoin(1, "a");
        match.TryJoin(2, "b");
        _sent.Clear();

        ushort id = match.SpawnItem(Ammo(30), new Vector3(3f, 0f, 3f), -1);
        match.SetItemAmount(match.WorldItems.IndexOf(id), 12);
        match.RemoveItemAt(match.WorldItems.IndexOf(id));

        foreach (int peer in new[] { 1, 2 })
        {
            var events = _sent.Where(s => s.PeerId == peer).ToList();
            Assert.Equal(new[] { PacketId.ItemSpawned, PacketId.ItemSpawned, PacketId.ItemRemoved }, events.Select(s => s.Id));
            Assert.All(events, s => Assert.Equal(DeliveryMethod.ReliableOrdered, s.Method));

            var r = Reader(events[0]);
            Assert.True(ItemSpawnedPacket.TryRead(ref r, out var spawned));
            Assert.Equal(id, spawned.ItemId);
            Assert.Equal(30, spawned.Amount);
            r = Reader(events[1]);
            Assert.True(ItemSpawnedPacket.TryRead(ref r, out var updated));
            Assert.Equal(id, updated.ItemId);
            Assert.Equal(12, updated.Amount);
            r = Reader(events[2]);
            Assert.True(ItemRemoved.TryRead(ref r, out var removed));
            Assert.Equal(id, removed.ItemId);
        }
        Assert.Equal(-1, match.WorldItems.IndexOf(id));
    }

    // D13: the eviction is announced before the new item, so a client's list never holds 257.
    [Fact]
    public void SpawnIntoAFullWorld_RemovesTheOldestDropFirst()
    {
        var match = NewMatch();
        ushort oldest = match.SpawnItem(Ammo(), Vector3.Zero, -1);
        while (match.WorldItems.Count < WorldItems.Capacity) match.SpawnItem(Ammo(), Vector3.Zero, -1);
        match.TryJoin(1, "a");
        _sent.Clear();

        ushort newest = match.SpawnItem(Ammo(), Vector3.One, -1);

        Assert.Equal(new[] { PacketId.ItemRemoved, PacketId.ItemSpawned }, _sent.Select(s => s.Id));
        var r = Reader(_sent[0]);
        Assert.True(ItemRemoved.TryRead(ref r, out var removed));
        Assert.Equal(oldest, removed.ItemId);
        r = Reader(_sent[1]);
        Assert.True(ItemSpawnedPacket.TryRead(ref r, out var spawned));
        Assert.Equal(newest, spawned.ItemId);
        Assert.Equal(WorldItems.Capacity, match.WorldItems.Count);
    }

    [Fact]
    public void SpawnAndRemove_AllocateNothing()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 2 }, TestGameData.Create(), static (_, _, _) => { });
        match.TryJoin(1, "a");
        LootRoll roll = Ammo();
        ushort warm = match.SpawnItem(roll, Vector3.Zero, -1);
        match.RemoveItemAt(match.WorldItems.IndexOf(warm));

        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            ushort id = match.SpawnItem(roll, Vector3.Zero, -1);
            int index = match.WorldItems.IndexOf(id);
            match.SetItemAmount(index, 3);
            match.RemoveItemAt(index);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }
}
