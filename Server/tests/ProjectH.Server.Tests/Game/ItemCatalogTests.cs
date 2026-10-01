using System;
using System.IO;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Game;

public class ItemCatalogTests
{
    private static string Parse(string json, int simHz = 30)
    {
        Assert.False(ItemCatalog.TryParse(json, simHz, out var catalog, out string? error));
        Assert.Null(catalog);
        Assert.False(string.IsNullOrEmpty(error));
        return error!;
    }

    // Replaces one exact piece of the valid test file, so each case breaks exactly one rule.
    private static string Broken(string from, string to)
    {
        Assert.Contains(from, TestGameData.ItemsJson);
        return TestGameData.ItemsJson.Replace(from, to);
    }

    [Fact]
    public void Valid_ConvertsSecondsToTicks_AndBuildsWireData()
    {
        Assert.True(ItemCatalog.TryParse(TestGameData.ItemsJson, 30, out var items, out string? error), error);
        Assert.Equal(30, items!.SimHz);
        Assert.Equal(1.2f, items.DamageMultiplier(4));
        Assert.Equal("Rare", items.RarityName(2));
        Assert.Equal(3, items.RarityIndex("Epic"));
        Assert.Equal(-1, items.RarityIndex("Mythic"));
        Assert.Equal(TestGameData.MediumPickup, items.Ammo(AmmoType.Medium).PickupAmount);
        Assert.Equal(TestGameData.HeavyMax, items.Ammo(AmmoType.Heavy).Max);
        Assert.Equal(TestGameData.MedkitUseTicks, items.Consumable(ConsumableType.Medkit).UseTicks);
        Assert.Equal(TestGameData.ShieldCellShield, items.Consumable(ConsumableType.ShieldCell).Shield);
        Assert.Equal(TestGameData.ShieldCellMaxStack, items.Consumable(ConsumableType.ShieldCell).MaxStack);

        Assert.Equal(5, items.Wire.Rarities.Length);
        Assert.Equal(AmmoType.Light, items.Wire.Ammo[0].Type);
        Assert.Equal(TestGameData.LightMax, items.Wire.Ammo[0].Max);
        Assert.Equal(ConsumableType.ShieldCell, items.Wire.Consumables[1].Type);
        Assert.Equal(TestGameData.ShieldCellUseTicks, items.Wire.Consumables[1].UseTicks);
    }

    [Fact]
    public void EntriesInAnyOrder_AreStoredByType()
    {
        string swapped = TestGameData.ItemsJson
            .Replace("\"type\": \"Light\"", "\"type\": \"TMP\"")
            .Replace("\"type\": \"Heavy\"", "\"type\": \"Light\"")
            .Replace("\"type\": \"TMP\"", "\"type\": \"Heavy\"");
        Assert.True(ItemCatalog.TryParse(swapped, 30, out var items, out string? error), error);
        Assert.Equal("Heavy Rounds", items!.Ammo(AmmoType.Light).Name);
        Assert.Equal("Light Rounds", items.Ammo(AmmoType.Heavy).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    public void EmptyOrMalformed_IsRejected(string json)
    {
        Parse(json);
    }

    [Theory]
    // rarities
    [InlineData("{ \"name\": \"Legendary\", \"damageMultiplier\": 1.20 }", "{ \"name\": \"Legendary\", \"damageMultiplier\": 0 }")]
    [InlineData("{ \"name\": \"Legendary\", \"damageMultiplier\": 1.20 }", "{ \"name\": \"Epic\", \"damageMultiplier\": 1.20 }")]
    [InlineData("{ \"name\": \"Legendary\", \"damageMultiplier\": 1.20 }", "{ \"name\": \"\", \"damageMultiplier\": 1.20 }")]
    [InlineData("{ \"name\": \"Legendary\", \"damageMultiplier\": 1.20 }", "{ \"name\": \"12345678901234567\", \"damageMultiplier\": 1.20 }")]
    [InlineData("{ \"name\": \"Legendary\", \"damageMultiplier\": 1.20 }", "{ \"name\": \"Legendary\", \"damageMultiplier\": 1.20 }, { \"name\": \"Mythic\", \"damageMultiplier\": 1.25 }")]
    // ammo
    [InlineData("\"type\": \"Heavy\"", "\"type\": \"Medium\"")]
    [InlineData("\"type\": \"Heavy\"", "\"type\": \"Rockets\"")]
    [InlineData("\"pickupAmount\": 10, \"max\": 30", "\"pickupAmount\": 31, \"max\": 30")]
    [InlineData("\"pickupAmount\": 10, \"max\": 30", "\"pickupAmount\": 0, \"max\": 30")]
    [InlineData("\"pickupAmount\": 10, \"max\": 30", "\"pickupAmount\": 10, \"max\": 65536")]
    [InlineData("\"name\": \"Heavy Rounds\"", "\"name\": \"Light Rounds\"")]
    // consumables
    [InlineData("\"id\": \"ShieldCell\"", "\"id\": \"Medkit\"")]
    [InlineData("\"id\": \"ShieldCell\"", "\"id\": \"Grenade\"")]
    [InlineData("\"useSeconds\": 2.0", "\"useSeconds\": 0")]
    [InlineData("\"heal\": 0, \"shield\": 25", "\"heal\": 0, \"shield\": 0")]
    [InlineData("\"heal\": 50", "\"heal\": -1")]
    [InlineData("\"maxStack\": 6", "\"maxStack\": 0")]
    [InlineData("\"maxStack\": 6", "\"maxStack\": 256")]
    public void BadEntry_IsRejected(string from, string to)
    {
        Parse(Broken(from, to));
    }

    [Fact]
    public void LoadFile_MissingFile_Throws()
    {
        string path = Path.Combine(Path.GetTempPath(), "projecth-no-such-items-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.Throws<InvalidOperationException>(() => ItemCatalog.LoadFile(path, 30));
    }

    // The file shipped next to the server must load and match spec D3, D4 and D11.
    [Fact]
    public void ShippedItemsJson_MatchesSpec()
    {
        var items = ItemCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, "items.json"), 30);

        string[] names = { "Common", "Uncommon", "Rare", "Epic", "Legendary" };
        float[] multipliers = { 1.00f, 1.05f, 1.10f, 1.15f, 1.20f };
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(names[i], items.RarityName(i));
            Assert.Equal(multipliers[i], items.DamageMultiplier(i));
        }
        Assert.Equal(180, items.Ammo(AmmoType.Light).Max);
        Assert.Equal(150, items.Ammo(AmmoType.Medium).Max);
        Assert.Equal(30, items.Ammo(AmmoType.Heavy).Max);
        Assert.Equal(60, items.Ammo(AmmoType.Light).PickupAmount);

        var medkit = items.Consumable(ConsumableType.Medkit);
        Assert.Equal(90, medkit.UseTicks);   // 3 s
        Assert.Equal(50, medkit.Heal);
        Assert.Equal(0, medkit.Shield);
        Assert.Equal(3, medkit.MaxStack);
        var cell = items.Consumable(ConsumableType.ShieldCell);
        Assert.Equal(60, cell.UseTicks);     // 2 s
        Assert.Equal(0, cell.Heal);
        Assert.Equal(25, cell.Shield);
        Assert.Equal(6, cell.MaxStack);
    }

    [Fact]
    public void GameData_LoadDirectory_LoadsTheShippedFiles()
    {
        var data = GameData.LoadDirectory(AppContext.BaseDirectory, 30);
        Assert.Equal(30, data.SimHz);
        Assert.Equal(3, data.Weapons.Count);
        Assert.Equal("Legendary", data.Items.RarityName(4));
        Assert.True(data.Loot.TableIndex("Floor") >= 0);
        Assert.True(data.Loot.TableIndex("Tower") >= 0);
    }

    [Fact]
    public void GameData_RejectsCatalogsBuiltForDifferentSimHz()
    {
        var items = TestGameData.Items(simHz: 60);
        Assert.Throws<ArgumentException>(() => new GameData(TestWeapons.Create(simHz: 30), items, TestGameData.Loot(items), TestGameData.Zones()));
    }

    // D5 "all references exist": a table named by a LootPoint must be in loot.json.
    [Fact]
    public void GameData_RejectsLootWithoutATableTheSpawnPointsUse()
    {
        var items = TestGameData.Items();
        var noTower = TestGameData.Loot(items, """
            { "rarityWeights": { "Common": 1, "Uncommon": 1, "Rare": 1, "Epic": 1, "Legendary": 1 },
              "tables": { "Floor": [ { "kind": "Ammo", "weight": 1 } ] } }
            """);
        var ex = Assert.Throws<ArgumentException>(() => new GameData(TestWeapons.Create(), items, noTower, TestGameData.Zones()));
        Assert.Contains("Tower", ex.Message);
    }
}
