using System;
using System.IO;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Game;

public class LootTableTests
{
    private readonly GameData _data = TestGameData.Create();

    private string Parse(string json)
    {
        Assert.False(LootTable.TryParse(json, _data.Items, out var loot, out string? error));
        Assert.Null(loot);
        Assert.False(string.IsNullOrEmpty(error));
        return error!;
    }

    private static string Broken(string from, string to)
    {
        Assert.Contains(from, TestGameData.LootJson);
        return TestGameData.LootJson.Replace(from, to);
    }

    private LootRoll Roll(string table, Random rng) => _data.Loot.Roll(_data.Loot.TableIndex(table), rng, _data.Weapons, _data.Items);

    [Fact]
    public void Valid_HasTheThreeTables()
    {
        Assert.Equal(3, _data.Loot.TableCount);
        Assert.True(_data.Loot.TableIndex("Floor") >= 0);
        Assert.True(_data.Loot.TableIndex("Building") >= 0);
        Assert.True(_data.Loot.TableIndex("Tower") >= 0);
        Assert.Equal(-1, _data.Loot.TableIndex("Basement"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{ \"rarityWeights\": { \"Common\": 1, \"Uncommon\": 1, \"Rare\": 1, \"Epic\": 1, \"Legendary\": 1 }, \"tables\": {} }")]
    public void EmptyOrMalformed_IsRejected(string json)
    {
        Parse(json);
    }

    [Theory]
    [InlineData("\"Legendary\": 3", "\"Legendary\": 0")]              // zero weight
    [InlineData("\"Legendary\": 3", "\"Mythic\": 3")]                 // unknown rarity, Legendary missing
    [InlineData(", \"Legendary\": 3", "")]                            // missing rarity
    [InlineData("{ \"kind\": \"Ammo\", \"weight\": 10 }", "{ \"kind\": \"Ammo\", \"weight\": 0 }")]
    [InlineData("{ \"kind\": \"Ammo\", \"weight\": 10 }", "{ \"kind\": \"Ammo\", \"weight\": -1 }")]
    [InlineData("{ \"kind\": \"Ammo\", \"weight\": 10 }", "{ \"kind\": \"Grenade\", \"weight\": 10 }")]
    [InlineData("{ \"kind\": \"Ammo\", \"weight\": 10 }", "{ \"kind\": \"Weapon\", \"weight\": 10 }")]   // duplicate kind
    [InlineData("{ \"kind\": \"Ammo\", \"weight\": 10 }", "{ \"kind\": \"Ammo\", \"weight\": 1000001 }")]
    [InlineData("{ \"kind\": \"Ammo\", \"weight\": 10 }", "null")]
    public void BadEntry_IsRejected(string from, string to)
    {
        Parse(Broken(from, to));
    }

    [Fact]
    public void EmptyTable_IsRejected()
    {
        Parse("{ \"rarityWeights\": { \"Common\": 1, \"Uncommon\": 1, \"Rare\": 1, \"Epic\": 1, \"Legendary\": 1 }, \"tables\": { \"Floor\": [] } }");
    }

    [Fact]
    public void LoadFile_MissingFile_Throws()
    {
        string path = Path.Combine(Path.GetTempPath(), "projecth-no-such-loot-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.Throws<InvalidOperationException>(() => LootTable.LoadFile(path, _data.Items));
    }

    // Shipped loot.json = spec §1 (same rolls as the test copy for the same seed).
    [Fact]
    public void ShippedLootJson_RollsLikeTheSpecTable()
    {
        var shipped = LootTable.LoadFile(Path.Combine(AppContext.BaseDirectory, "loot.json"), _data.Items);
        var a = new Random(7);
        var b = new Random(7);
        foreach (string table in new[] { "Floor", "Building", "Tower" })
        {
            for (int i = 0; i < 200; i++)
            {
                LootRoll x = shipped.Roll(shipped.TableIndex(table), a, _data.Weapons, _data.Items);
                LootRoll y = Roll(table, b);
                Assert.Equal((x.Kind, x.DefId, x.Rarity, x.Amount), (y.Kind, y.DefId, y.Rarity, y.Amount));
            }
        }
    }

    // D5: the same seed gives the same loot (two Random instances, never hard-coded rolls).
    [Fact]
    public void SameSeed_SameRolls_OtherSeed_OtherRolls()
    {
        var a = new Random(12345);
        var b = new Random(12345);
        var c = new Random(54321);
        int differences = 0;
        for (int i = 0; i < 100; i++)
        {
            LootRoll x = Roll("Floor", a);
            LootRoll y = Roll("Floor", b);
            LootRoll z = Roll("Floor", c);
            Assert.Equal((x.Kind, x.DefId, x.Rarity, x.Amount), (y.Kind, y.DefId, y.Rarity, y.Amount));
            if ((x.Kind, x.DefId, x.Rarity, x.Amount) != (z.Kind, z.DefId, z.Rarity, z.Amount)) differences++;
        }
        Assert.True(differences > 0);
    }

    // Spec §6: 10 000 Floor rolls roughly follow 35 / 35 / 15 / 15 and weapon rarities 50/25/15/7/3.
    [Fact]
    public void Distribution_FollowsTheWeights()
    {
        var rng = new Random(2026);
        int weapons = 0, ammo = 0, medkits = 0, cells = 0;
        var rarities = new int[5];
        var ammoTypes = new int[4];
        const int rolls = 10_000;
        for (int i = 0; i < rolls; i++)
        {
            LootRoll r = Roll("Floor", rng);
            switch (r.Kind)
            {
                case ItemKind.Weapon:
                    weapons++;
                    rarities[r.Rarity]++;
                    Assert.True(_data.Weapons.TryGetById(r.DefId, out var weapon));
                    Assert.Equal(weapon.MagazineSize, r.Amount);   // full magazine
                    break;
                case ItemKind.Ammo:
                    ammo++;
                    ammoTypes[r.DefId]++;
                    Assert.Equal(_data.Items.Ammo((AmmoType)r.DefId).PickupAmount, r.Amount);
                    break;
                default:
                    Assert.Equal(ItemKind.Consumable, r.Kind);
                    Assert.Equal(1, r.Amount);
                    if (r.DefId == (byte)ConsumableType.Medkit) medkits++; else cells++;
                    break;
            }
        }

        Assert.InRange(weapons, 3200, 3800);
        Assert.InRange(ammo, 3200, 3800);
        Assert.InRange(medkits, 1250, 1750);
        Assert.InRange(cells, 1250, 1750);
        Assert.InRange(rarities[0] / (double)weapons, 0.45, 0.55);
        Assert.InRange(rarities[4] / (double)weapons, 0.01, 0.05);
        Assert.All(rarities, count => Assert.True(count > 0));   // every rarity appears
        for (int t = 1; t <= 3; t++) Assert.InRange(ammoTypes[t] / (double)ammo, 0.28, 0.39);
    }

    [Fact]
    public void Tower_RollsMoreWeapons()
    {
        var rng = new Random(9);
        int weapons = 0;
        for (int i = 0; i < 2000; i++)
        {
            if (Roll("Tower", rng).Kind == ItemKind.Weapon) weapons++;
        }
        Assert.InRange(weapons, 1080, 1320);   // 60 %
    }

    [Fact]
    public void Roll_AllocatesNothing()
    {
        var rng = new Random(1);
        int floor = _data.Loot.TableIndex("Floor");
        _data.Loot.Roll(floor, rng, _data.Weapons, _data.Items);
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) _data.Loot.Roll(floor, rng, _data.Weapons, _data.Items);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }
}
