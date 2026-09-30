using System;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;

namespace ProjectH.Server.Tests;

// In-memory game data for tests (TestWeapons + the item numbers of spec D3/D11), so no test depends on
// the shipped data files.
internal static class TestGameData
{
    public const int LightMax = 180;
    public const int LightPickup = 60;
    public const int MediumMax = 150;
    public const int MediumPickup = 45;
    public const int HeavyMax = 30;
    public const int HeavyPickup = 10;
    public const int MedkitUseTicks = 90;       // 3 s at 30 Hz
    public const int MedkitHeal = 50;
    public const int MedkitMaxStack = 3;
    public const int ShieldCellUseTicks = 60;   // 2 s at 30 Hz
    public const int ShieldCellShield = 25;
    public const int ShieldCellMaxStack = 6;

    public const string ItemsJson = """
        {
          "rarities": [
            { "name": "Common", "damageMultiplier": 1.00 },
            { "name": "Uncommon", "damageMultiplier": 1.05 },
            { "name": "Rare", "damageMultiplier": 1.10 },
            { "name": "Epic", "damageMultiplier": 1.15 },
            { "name": "Legendary", "damageMultiplier": 1.20 }
          ],
          "ammo": [
            { "type": "Light", "name": "Light Rounds", "pickupAmount": 60, "max": 180 },
            { "type": "Medium", "name": "Medium Rounds", "pickupAmount": 45, "max": 150 },
            { "type": "Heavy", "name": "Heavy Rounds", "pickupAmount": 10, "max": 30 }
          ],
          "consumables": [
            { "id": "Medkit", "name": "Medkit", "useSeconds": 3.0, "heal": 50, "shield": 0, "maxStack": 3 },
            { "id": "ShieldCell", "name": "Shield Cell", "useSeconds": 2.0, "heal": 0, "shield": 25, "maxStack": 6 }
          ]
        }
        """;

    // Spec §1 loot.json.
    public const string LootJson = """
        {
          "rarityWeights": { "Common": 50, "Uncommon": 25, "Rare": 15, "Epic": 7, "Legendary": 3 },
          "tables": {
            "Floor": [ { "kind": "Weapon", "weight": 35 }, { "kind": "Ammo", "weight": 35 },
                       { "kind": "Medkit", "weight": 15 }, { "kind": "ShieldCell", "weight": 15 } ],
            "Tower": [ { "kind": "Weapon", "weight": 60 }, { "kind": "Ammo", "weight": 10 },
                       { "kind": "Medkit", "weight": 15 }, { "kind": "ShieldCell", "weight": 15 } ]
          }
        }
        """;

    // Every spawn point rolls a weapon: tests that must find a weapon on the floor use this.
    public const string WeaponsOnlyLootJson = """
        {
          "rarityWeights": { "Common": 50, "Uncommon": 25, "Rare": 15, "Epic": 7, "Legendary": 3 },
          "tables": { "Floor": [ { "kind": "Weapon", "weight": 1 } ], "Tower": [ { "kind": "Weapon", "weight": 1 } ] }
        }
        """;

    public static ItemCatalog Items(int simHz = 30)
    {
        if (!ItemCatalog.TryParse(ItemsJson, simHz, out var items, out string? error))
            throw new InvalidOperationException("Test items are invalid: " + error);
        return items!;
    }

    // D1: combat tests start armed instead of picking weapons up. Slot 0 = Test Auto, slot 1 = Test Semi
    // (both Common, so damage is unscaled), shield 50: five Test Auto hits (30) take 50 + 100 to 0.
    // Reserves are large enough that no Phase 3 combat test runs out of rounds.
    public const int LoadoutShield = 50;
    public const int LoadoutMediumAmmo = 60;
    public const int LoadoutHeavyAmmo = 30;

    public static StartingLoadout CombatLoadout => new()
    {
        Shield = LoadoutShield,
        Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 0), new LoadoutWeapon(TestWeapons.SemiId, 0) },
        MediumAmmo = LoadoutMediumAmmo,
        HeavyAmmo = LoadoutHeavyAmmo,
    };

    public static LootTable Loot(ItemCatalog items, string json = LootJson)
    {
        if (!LootTable.TryParse(json, items, out var loot, out string? error))
            throw new InvalidOperationException("Test loot is invalid: " + error);
        return loot!;
    }

    public static GameData Create(int simHz = 30, float autoRange = 100f, string lootJson = LootJson)
    {
        ItemCatalog items = Items(simHz);
        return new GameData(TestWeapons.Create(simHz, autoRange), items, Loot(items, lootJson));
    }
}
