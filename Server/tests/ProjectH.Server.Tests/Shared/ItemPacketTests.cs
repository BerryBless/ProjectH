using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class ItemPacketTests
{
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];

    private PacketReader ReaderAfterId(int length, PacketId expected)
    {
        var reader = new PacketReader(_buffer.AsSpan(0, length));
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(expected, id);
        return reader;
    }

    private static ItemCatalogData Catalog(string name = "Name") => new ItemCatalogData
    {
        Rarities = new[]
        {
            new RarityInfo { Name = name, DamageMultiplier = 1f },
            new RarityInfo { Name = name, DamageMultiplier = 1.05f },
            new RarityInfo { Name = name, DamageMultiplier = 1.1f },
            new RarityInfo { Name = name, DamageMultiplier = 1.15f },
            new RarityInfo { Name = name, DamageMultiplier = 1.2f },
        },
        Ammo = new[]
        {
            new AmmoInfo { Type = AmmoType.Light, Name = name, Max = 180 },
            new AmmoInfo { Type = AmmoType.Medium, Name = name, Max = 150 },
            new AmmoInfo { Type = AmmoType.Heavy, Name = name, Max = 30 },
        },
        Consumables = new[]
        {
            new ConsumableInfo { Type = ConsumableType.Medkit, Name = name, UseTicks = 90, Heal = 50, Shield = 0, MaxStack = 3 },
            new ConsumableInfo { Type = ConsumableType.ShieldCell, Name = name, UseTicks = 60, Heal = 0, Shield = 25, MaxStack = 6 },
        },
    };

    private static WorldItemData Weapon(ushort id) => new WorldItemData
    {
        ItemId = id,
        Kind = ItemKind.Weapon,
        DefId = 2,
        Rarity = 4,
        Amount = 0,   // an empty magazine is a valid weapon item
        Position = new Vector3(1.5f, 1f, -14.5f),
    };

    [Fact]
    public void ItemCatalog_RoundTrip()
    {
        var writer = new PacketWriter(_buffer);
        ItemCatalogPacket.Write(ref writer, Catalog());
        Assert.False(writer.Overflowed);
        var reader = ReaderAfterId(writer.Length, PacketId.ItemCatalog);
        Assert.True(ItemCatalogPacket.TryRead(ref reader, out var read));

        Assert.Equal(5, read.Rarities.Length);
        Assert.Equal(1.2f, read.Rarities[4].DamageMultiplier);
        Assert.Equal(AmmoType.Heavy, read.Ammo[2].Type);
        Assert.Equal(30, read.Ammo[2].Max);
        Assert.Equal(ConsumableType.ShieldCell, read.Consumables[1].Type);
        Assert.Equal(60, read.Consumables[1].UseTicks);
        Assert.Equal(25, read.Consumables[1].Shield);
        Assert.Equal(6, read.Consumables[1].MaxStack);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void ItemCatalog_WithMaximumLengthNames_Is219Bytes()
    {
        var writer = new PacketWriter(_buffer);
        ItemCatalogPacket.Write(ref writer, Catalog(new string('n', ItemConstants.MaxNameBytes)));
        Assert.False(writer.Overflowed);
        Assert.Equal(ItemCatalogPacket.MaxSize, writer.Length);
        Assert.True(writer.Length <= ProtocolConstants.MaxPacketSize);
    }

    [Fact]
    public void ItemCatalog_Truncated_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        ItemCatalogPacket.Write(ref writer, Catalog());
        for (int cut = 1; cut < writer.Length; cut++)
        {
            var reader = ReaderAfterId(cut, PacketId.ItemCatalog);
            Assert.False(ItemCatalogPacket.TryRead(ref reader, out _), $"cut at {cut}");
        }
    }

    [Fact]
    public void ItemCatalog_BadValues_AreRejected()
    {
        var wrongOrder = Catalog();
        (wrongOrder.Ammo[0], wrongOrder.Ammo[1]) = (wrongOrder.Ammo[1], wrongOrder.Ammo[0]);
        var noEffect = Catalog();
        noEffect.Consumables[0].Heal = 0;
        var zeroMultiplier = Catalog();
        zeroMultiplier.Rarities[2].DamageMultiplier = 0f;
        var nanMultiplier = Catalog();
        nanMultiplier.Rarities[2].DamageMultiplier = float.NaN;
        var fourRarities = Catalog();
        fourRarities.Rarities = fourRarities.Rarities[..4];

        foreach (var bad in new[] { wrongOrder, noEffect, zeroMultiplier, nanMultiplier, fourRarities })
        {
            var writer = new PacketWriter(_buffer);
            ItemCatalogPacket.Write(ref writer, bad);
            var reader = ReaderAfterId(writer.Length, PacketId.ItemCatalog);
            Assert.False(ItemCatalogPacket.TryRead(ref reader, out _));
        }
    }

    [Fact]
    public void ItemSpawned_RoundTrip_Is20Bytes()
    {
        var writer = new PacketWriter(_buffer);
        ItemSpawnedPacket.Write(ref writer, Weapon(513));
        Assert.Equal(ItemSpawnedPacket.Size, writer.Length);
        Assert.Equal(20, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.ItemSpawned);
        Assert.True(ItemSpawnedPacket.TryRead(ref reader, out var item));
        Assert.Equal(513, item.ItemId);
        Assert.Equal(ItemKind.Weapon, item.Kind);
        Assert.Equal(2, item.DefId);
        Assert.Equal(4, item.Rarity);
        Assert.Equal(0, item.Amount);
        Assert.Equal(new Vector3(1.5f, 1f, -14.5f), item.Position);
    }

    [Theory]
    [InlineData(0, ItemKind.Weapon, 1, 0, 5)]        // id 0 is "none"
    [InlineData(1, ItemKind.None, 1, 0, 5)]
    [InlineData(1, (ItemKind)5, 1, 0, 5)]            // Phase 13: 4 is Material
    [InlineData(1, ItemKind.Material, 0, 0, 5)]      // no such material (DefId = material + 1)
    [InlineData(1, ItemKind.Material, 4, 0, 5)]
    [InlineData(1, ItemKind.Material, 1, 1, 5)]      // no rarity
    [InlineData(1, ItemKind.Material, 1, 0, 0)]      // empty
    [InlineData(1, ItemKind.Weapon, 0, 0, 5)]        // weapon id 0
    [InlineData(1, ItemKind.Weapon, 1, 5, 5)]        // rarity out of range
    [InlineData(1, ItemKind.Ammo, 4, 0, 60)]         // no such ammo type
    [InlineData(1, ItemKind.Ammo, 1, 1, 60)]         // ammo has no rarity
    [InlineData(1, ItemKind.Ammo, 1, 0, 0)]          // empty stack
    [InlineData(1, ItemKind.Consumable, 3, 0, 1)]
    [InlineData(1, ItemKind.Consumable, 1, 0, 0)]
    public void WorldItem_InvalidValues_AreRejected(ushort id, ItemKind kind, byte defId, byte rarity, ushort amount)
    {
        var writer = new PacketWriter(_buffer);
        ItemSpawnedPacket.Write(ref writer, new WorldItemData { ItemId = id, Kind = kind, DefId = defId, Rarity = rarity, Amount = amount });
        var reader = ReaderAfterId(writer.Length, PacketId.ItemSpawned);
        Assert.False(ItemSpawnedPacket.TryRead(ref reader, out _));
    }

    [Fact]
    public void WorldItem_NonFinitePosition_OrTruncated_IsRejected()
    {
        var item = Weapon(1);
        item.Position = new Vector3(0f, float.PositiveInfinity, 0f);
        var writer = new PacketWriter(_buffer);
        ItemSpawnedPacket.Write(ref writer, item);
        var reader = ReaderAfterId(writer.Length, PacketId.ItemSpawned);
        Assert.False(ItemSpawnedPacket.TryRead(ref reader, out _));

        writer = new PacketWriter(_buffer);
        ItemSpawnedPacket.Write(ref writer, Weapon(1));
        reader = ReaderAfterId(writer.Length - 1, PacketId.ItemSpawned);
        Assert.False(ItemSpawnedPacket.TryRead(ref reader, out _));
    }

    // Review Focus (D13, D14): a full chunk of 50 items is 952 bytes, well under one datagram.
    [Fact]
    public void WorldItems_FullChunk_Is952Bytes_AndRoundTrips()
    {
        var writer = new PacketWriter(_buffer);
        WorldItemsPacket.WriteHeader(ref writer, WorldItemsPacket.MaxItems);
        for (int i = 0; i < WorldItemsPacket.MaxItems; i++) WorldItemData.Write(ref writer, Weapon((ushort)(i + 1)));

        Assert.False(writer.Overflowed);
        Assert.Equal(WorldItemsPacket.MaxSize, writer.Length);
        Assert.Equal(952, writer.Length);
        Assert.True(writer.Length <= ProtocolConstants.MaxPacketSize);

        var reader = ReaderAfterId(writer.Length, PacketId.WorldItems);
        Assert.True(WorldItemsPacket.TryReadHeader(ref reader, out int count));
        Assert.Equal(50, count);
        for (int i = 0; i < count; i++)
        {
            Assert.True(WorldItemData.TryRead(ref reader, out var item));
            Assert.Equal(i + 1, item.ItemId);
        }
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(51, 51)]
    [InlineData(3, 2)]   // count says 3, only 2 items follow
    public void WorldItems_BadCountOrShortPayload_IsRejected(int count, int items)
    {
        var writer = new PacketWriter(_buffer);
        WorldItemsPacket.WriteHeader(ref writer, count);
        for (int i = 0; i < items; i++) WorldItemData.Write(ref writer, Weapon((ushort)(i + 1)));
        var reader = ReaderAfterId(writer.Length, PacketId.WorldItems);
        Assert.False(WorldItemsPacket.TryReadHeader(ref reader, out _));
    }

    [Fact]
    public void ItemRemoved_RoundTrip_AndRejectsIdZero()
    {
        var writer = new PacketWriter(_buffer);
        ItemRemoved.Write(ref writer, new ItemRemoved { ItemId = 65535 });
        Assert.Equal(3, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.ItemRemoved);
        Assert.True(ItemRemoved.TryRead(ref reader, out var removed));
        Assert.Equal(65535, removed.ItemId);

        writer = new PacketWriter(_buffer);
        ItemRemoved.Write(ref writer, new ItemRemoved { ItemId = 0 });
        reader = ReaderAfterId(writer.Length, PacketId.ItemRemoved);
        Assert.False(ItemRemoved.TryRead(ref reader, out _));
    }

    [Fact]
    public void InventoryState_RoundTrip_Is23Bytes()
    {
        var state = new InventoryState
        {
            Slot0 = new InventorySlotState { WeaponId = 1, Rarity = 2, MagAmmo = 30 },
            Slot2 = new InventorySlotState { WeaponId = 3, Rarity = 0, MagAmmo = 0 },
            CurrentSlot = 2,
            LightAmmo = 180,
            MediumAmmo = 7,
            HeavyAmmo = 30,
            Medkits = 3,
            ShieldCells = 6,
            Using = ConsumableType.ShieldCell,
            UseRemainingTicks = 59,
            RebootCards = 2,
        };
        var writer = new PacketWriter(_buffer);
        InventoryState.Write(ref writer, state);
        Assert.Equal(1 + InventoryState.PayloadSize, writer.Length);
        Assert.Equal(23, writer.Length);   // Phase 14: the card count byte

        var reader = ReaderAfterId(writer.Length, PacketId.InventoryState);
        Assert.True(InventoryState.TryRead(ref reader, out var read));
        Assert.Equal(30, read.Slot0.MagAmmo);
        Assert.Equal(2, read.Slot0.Rarity);
        Assert.True(read.Slot1.IsEmpty);
        Assert.Equal(3, read.GetSlot(2).WeaponId);
        Assert.Equal(2, read.CurrentSlot);
        Assert.Equal(7, read.GetAmmo(AmmoType.Medium));
        Assert.Equal(180, read.GetAmmo(AmmoType.Light));
        Assert.Equal(6, read.ShieldCells);
        Assert.Equal(ConsumableType.ShieldCell, read.Using);
        Assert.Equal(59, read.UseRemainingTicks);
        Assert.Equal(2, read.RebootCards);

        // More cards than anyone can hold is refused.
        _buffer[writer.Length - 1] = SquadConstants.MaxCardsHeld + 1;
        reader = ReaderAfterId(writer.Length, PacketId.InventoryState);
        Assert.False(InventoryState.TryRead(ref reader, out _));
    }

    [Fact]
    public void InventoryState_BadValues_OrTruncated_AreRejected()
    {
        var badSlot = new InventoryState { CurrentSlot = 3 };
        var badRarity = new InventoryState { Slot1 = new InventorySlotState { WeaponId = 1, Rarity = 5 } };
        var ammoInEmptySlot = new InventoryState { Slot1 = new InventorySlotState { WeaponId = 0, MagAmmo = 4 } };
        var badUse = new InventoryState { Using = (ConsumableType)3 };
        foreach (var bad in new[] { badSlot, badRarity, ammoInEmptySlot, badUse })
        {
            var writer = new PacketWriter(_buffer);
            InventoryState.Write(ref writer, bad);
            var reader = ReaderAfterId(writer.Length, PacketId.InventoryState);
            Assert.False(InventoryState.TryRead(ref reader, out _));
        }

        var w = new PacketWriter(_buffer);
        InventoryState.Write(ref w, new InventoryState());
        var shortReader = ReaderAfterId(w.Length - 1, PacketId.InventoryState);
        Assert.False(InventoryState.TryRead(ref shortReader, out _));
    }

    [Fact]
    public void PickupResult_RoundTrip_AndRejectsUnknownResult()
    {
        var writer = new PacketWriter(_buffer);
        PickupResult.Write(ref writer, new PickupResult { Result = PickupResultCode.Full, ItemId = 9 });
        Assert.Equal(4, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.PickupResult);
        Assert.True(PickupResult.TryRead(ref reader, out var result));
        Assert.Equal(PickupResultCode.Full, result.Result);
        Assert.Equal(9, result.ItemId);

        writer = new PacketWriter(_buffer);
        PickupResult.Write(ref writer, new PickupResult { Result = (PickupResultCode)3 });
        reader = ReaderAfterId(writer.Length, PacketId.PickupResult);
        Assert.False(PickupResult.TryRead(ref reader, out _));
    }
}
