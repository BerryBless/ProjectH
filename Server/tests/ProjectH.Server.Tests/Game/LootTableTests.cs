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

    // 기능: 잘못된 loot.json 문자열의 파싱이 거절되는지 확인하고 오류 메시지를 돌려준다.
    // 입력: json - 파싱할 문자열.
    // 출력: 거절 오류 메시지. 파싱이 성공하거나 메시지가 비면 테스트가 실패한다.
    private string Parse(string json)
    {
        Assert.False(LootTable.TryParse(json, _data.Items, out var loot, out string? error));
        Assert.Null(loot);
        Assert.False(string.IsNullOrEmpty(error));
        return error!;
    }

    // 기능: 유효한 테스트 loot.json에서 한 조각만 바꿔 규칙 하나만 깨진 문자열을 만든다.
    // 입력: from - 바꿀 원문 조각(반드시 있어야 한다), to - 대신 넣을 조각.
    // 출력: 바뀐 JSON 문자열. 원문 조각이 없으면 테스트가 실패한다.
    private static string Broken(string from, string to)
    {
        Assert.Contains(from, TestGameData.LootJson);
        return TestGameData.LootJson.Replace(from, to);
    }

    // 기능: 테스트 Loot 데이터의 이름 붙은 테이블에서 한 번 굴린다.
    // 입력: table - 테이블 이름, rng - 굴림 난수.
    // 출력: 굴린 LootRoll.
    private LootRoll Roll(string table, Random rng) => _data.Loot.Roll(_data.Loot.TableIndex(table), rng, _data.Weapons, _data.Items);

    [Fact]
    public void Valid_HasTheThreeTables()
    {
        Assert.Equal(6, _data.Loot.TableCount);   // Phase 16: + Chest, AmmoBox, SupplyDrop
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
    [InlineData("{ \"kind\": \"Ammo\", \"weight\": 10 }", "{ \"kind\": \"Smoke\", \"weight\": 10 }")]   // Phase 17: Grenade is a kind now
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

    // 기능: 운영 데이터(weapons.json·items.json·loot.json)를 묶은 GameData를 읽는다.
    // 입력: 없음.
    // 출력: 운영 GameData.
    private static GameData Shipped() => GameData.LoadDirectory(AppContext.BaseDirectory, 30);

    // Phase 17 D13: the shipped floor tables (Floor, Building, Tower) and the ammo box never roll the rocket launcher (6) or
    // Rockets; they roll the other five weapons and four ammo types and grenades. The test copy (without weapon lists) rolls
    // the same kinds, ammo and consumables for the same seed (only the weapon ids differ: the test catalog has three).
    [Fact]
    public void ShippedLootJson_FloorTablesLeaveOutTheRocket()
    {
        GameData shipped = Shipped();
        var a = new Random(7);
        var b = new Random(7);
        var weapons = new bool[7];
        var ammo = new bool[6];
        foreach (string table in new[] { "Floor", "Building", "Tower" })
        {
            for (int i = 0; i < 2000; i++)
            {
                LootRoll x = shipped.Loot.Roll(shipped.Loot.TableIndex(table), a, shipped.Weapons, shipped.Items);
                LootRoll y = Roll(table, b);
                Assert.Equal(x.Kind, y.Kind);
                if (x.Kind == ItemKind.Weapon) weapons[x.DefId] = true;
                else Assert.Equal((x.DefId, x.Amount), (y.DefId, y.Amount));
                if (x.Kind == ItemKind.Ammo) ammo[x.DefId] = true;
            }
        }
        Assert.Equal(new[] { false, true, true, true, true, true, false }, weapons);
        Assert.Equal(new[] { false, true, true, true, true, false }, ammo);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, shipped.Loot.WeaponIds(shipped.Loot.TableIndex("AmmoBox")).ToArray());
        Assert.DoesNotContain(AmmoType.Rockets, shipped.Loot.AmmoTypes(shipped.Loot.TableIndex("AmmoBox")).ToArray());
    }

    // Phase 17 D13: chests and supply drops can hold the rocket launcher and Rockets.
    [Fact]
    public void ShippedLootJson_ChestsAndSupplyDropsIncludeTheRocket()
    {
        GameData shipped = Shipped();
        foreach (string table in new[] { "Chest", "SupplyDrop" })
        {
            int t = shipped.Loot.TableIndex(table);
            Assert.Contains((byte)6, shipped.Loot.WeaponIds(t).ToArray());
            Assert.Contains(AmmoType.Rockets, shipped.Loot.AmmoTypes(t).ToArray());
        }
        var rng = new Random(11);
        var output = new LootRoll[LootTable.MaxRolls];
        bool rocket = false;
        for (int i = 0; i < 500 && !rocket; i++)
        {
            int n = shipped.Loot.RollAll(shipped.Loot.TableIndex("SupplyDrop"), rng, shipped.Weapons, shipped.Items, output);
            for (int k = 0; k < n; k++) rocket |= output[k].Kind == ItemKind.Weapon && output[k].DefId == 6;
        }
        Assert.True(rocket);
    }

    [Fact]
    public void UnknownWeaponIdInATable_IsRejectedByGameData()
    {
        string json = TestGameData.LootJson.Replace("\"Floor\": { ", "\"Floor\": { \"weapons\": [ 1, 9 ], ");
        var loot = TestGameData.Loot(_data.Items, json);
        Assert.Contains("9", loot.ValidateWeapons(_data.Weapons));
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

    // Spec §6: 10 000 Floor rolls roughly follow the weights (Phase 17: 35 / 30 / 13 / 13 / 9 with grenades) and weapon rarities
    // 50/25/15/7/3; the ammo is one of the table's four types.
    [Fact]
    public void Distribution_FollowsTheWeights()
    {
        var rng = new Random(2026);
        int weapons = 0, ammo = 0, medkits = 0, cells = 0, grenades = 0;
        var rarities = new int[5];
        var ammoTypes = new int[6];
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
                    if (r.DefId == (byte)ConsumableType.Medkit) medkits++;
                    else if (r.DefId == (byte)ConsumableType.Grenade) grenades++;
                    else cells++;
                    break;
            }
        }

        Assert.InRange(weapons, 3200, 3800);
        Assert.InRange(ammo, 2700, 3300);
        Assert.InRange(medkits, 1050, 1550);
        Assert.InRange(cells, 1050, 1550);
        Assert.InRange(grenades, 650, 1150);
        Assert.InRange(rarities[0] / (double)weapons, 0.45, 0.55);
        Assert.InRange(rarities[4] / (double)weapons, 0.01, 0.05);
        Assert.All(rarities, count => Assert.True(count > 0));   // every rarity appears
        for (int t = 1; t <= 4; t++) Assert.InRange(ammoTypes[t] / (double)ammo, 0.20, 0.30);
        Assert.Equal(0, ammoTypes[(int)AmmoType.Rockets]);
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

    // ---- Phase 16 D5, D6: container tables, spawn chances and the supply drop schedule ----

    private const string Rarity = "\"rarityWeights\": { \"Common\": 1, \"Uncommon\": 1, \"Rare\": 1, \"Epic\": 1, \"Legendary\": 1 }";

    // 기능: 균등 희귀도 가중치와 Floor 테이블에 주어진 Chest 테이블(과 추가 항목)을 붙인 loot.json 문자열을 만든다.
    // 입력: table - Chest 테이블의 JSON, extra - tables 뒤에 붙일 추가 JSON 조각(기본 없음).
    // 출력: loot.json 문자열.
    private static string WithTable(string table, string extra = "") =>
        "{ " + Rarity + ", \"tables\": { \"Floor\": [ { \"kind\": \"Ammo\", \"weight\": 1 } ], \"Chest\": " + table + " }" + extra + " }";

    [Fact]
    public void ShippedLootJson_HasTheContainerTables_SpawnChancesAndSchedule()
    {
        GameData shippedData = Shipped();
        LootTable shipped = shippedData.Loot;
        Assert.Null(shipped.ValidateContainerTables());
        Assert.Equal(0.7, shipped.ChestSpawnChance);
        Assert.Equal(0.8, shipped.AmmoBoxSpawnChance);
        Assert.Equal(new[] { 60.0, 150.0 }, shipped.SupplyDropSeconds.ToArray());
        Assert.Equal(4f, shipped.SupplyDropFallSpeed);
        Assert.Equal(3, shipped.Rolls(shipped.TableIndex("Chest")));
        Assert.Equal(2, shipped.Rolls(shipped.TableIndex("AmmoBox")));
        Assert.Equal(4, shipped.Rolls(shipped.TableIndex("SupplyDrop")));
        Assert.Equal(1, shipped.Rolls(shipped.TableIndex("Floor")));
        // The test copy rolls the same container loot as the shipped file.
        var a = new Random(3);
        var b = new Random(3);
        var x = new LootRoll[LootTable.MaxRolls];
        var y = new LootRoll[LootTable.MaxRolls];
        foreach (string table in new[] { "Chest", "AmmoBox", "SupplyDrop" })
        {
            for (int i = 0; i < 100; i++)
            {
                int n = shipped.RollAll(shipped.TableIndex(table), a, shippedData.Weapons, shippedData.Items, x);
                Assert.Equal(n, _data.Loot.RollAll(_data.Loot.TableIndex(table), b, _data.Weapons, _data.Items, y));
                // Phase 17: the shipped tables name weapons 1-6 (the test catalog has 1-3), so a weapon matches by kind and rarity.
                for (int k = 0; k < n; k++)
                {
                    Assert.Equal((x[k].Kind, x[k].Rarity), (y[k].Kind, y[k].Rarity));
                    if (x[k].Kind != ItemKind.Weapon) Assert.Equal((x[k].DefId, x[k].Amount), (y[k].DefId, y[k].Amount));
                }
            }
        }
    }

    [Fact]
    public void WithoutContainerTables_ValidateNamesTheMissingOne()
    {
        var loot = TestGameData.Loot(_data.Items, TestGameData.WeaponsOnlyLootJson);
        Assert.Contains("Chest", loot.ValidateContainerTables());
        Assert.Empty(loot.SupplyDropSeconds.ToArray());
        Assert.Equal(1.0, loot.ChestSpawnChance);
    }

    [Fact]
    public void ObjectTable_Parses()
    {
        Assert.True(LootTable.TryParse(WithTable("{ \"rolls\": 2, \"guaranteed\": [ \"Weapon\", \"Ammo\" ], \"rarityWeights\": { \"Legendary\": 1 } }"),
            _data.Items, out var loot, out string? error), error);
        var output = new LootRoll[LootTable.MaxRolls];
        int n = loot!.RollAll(loot.TableIndex("Chest"), new Random(1), _data.Weapons, _data.Items, output);
        Assert.Equal(2, n);
        Assert.Equal(ItemKind.Weapon, output[0].Kind);
        Assert.Equal(4, output[0].Rarity);
        Assert.Equal(ItemKind.Ammo, output[1].Kind);
    }

    [Theory]
    [InlineData("{ \"rolls\": 0, \"entries\": [ { \"kind\": \"Ammo\", \"weight\": 1 } ] }")]                  // rolls below 1
    [InlineData("{ \"rolls\": 5, \"entries\": [ { \"kind\": \"Ammo\", \"weight\": 1 } ] }")]                  // above MaxRolls
    [InlineData("{ \"rolls\": 1, \"guaranteed\": [ \"Weapon\", \"Ammo\" ] }")]                                  // more guaranteed than rolls
    [InlineData("{ \"rolls\": 2, \"guaranteed\": [ \"Material\" ], \"entries\": [ { \"kind\": \"Ammo\", \"weight\": 1 } ] }")]   // Material needs an amount
    [InlineData("{ \"rolls\": 2, \"guaranteed\": [ \"Smoke\" ], \"entries\": [ { \"kind\": \"Ammo\", \"weight\": 1 } ] }")]
    [InlineData("{ \"rolls\": 2, \"guaranteed\": [ \"Weapon\" ] }")]                                              // a weighted roll without entries
    [InlineData("{ \"entries\": [ { \"kind\": \"Material\", \"weight\": 1 } ] }")]                              // no amount
    [InlineData("{ \"entries\": [ { \"kind\": \"Material\", \"weight\": 1, \"amount\": 1001 } ] }")]
    [InlineData("{ \"entries\": [ { \"kind\": \"Ammo\", \"weight\": 1, \"amount\": 5 } ] }")]                 // amount on another kind
    [InlineData("{ \"rarityWeights\": { \"Epic\": 0 }, \"entries\": [ { \"kind\": \"Weapon\", \"weight\": 1 } ] }")]   // no rarity weight at all
    [InlineData("{ \"rarityWeights\": { \"Mythic\": 1 }, \"entries\": [ { \"kind\": \"Weapon\", \"weight\": 1 } ] }")]
    [InlineData("42")]
    public void BadObjectTable_IsRejected(string table)
    {
        Parse(WithTable(table));
    }

    [Theory]
    [InlineData(", \"spawnChance\": { \"Chest\": 1.5 }")]
    [InlineData(", \"spawnChance\": { \"Chest\": -0.1 }")]
    [InlineData(", \"spawnChance\": { \"Barrel\": 0.5 }")]
    [InlineData(", \"supplyDrops\": { \"times\": [ 150, 60 ] }")]
    [InlineData(", \"supplyDrops\": { \"times\": [ 0 ] }")]
    [InlineData(", \"supplyDrops\": { \"times\": [ 10, 20, 30, 40, 50 ] }")]
    [InlineData(", \"supplyDrops\": { \"times\": [ 60 ], \"fallSpeed\": 0 }")]
    [InlineData(", \"supplyDrops\": { \"times\": [ 60 ], \"fallSpeed\": 100 }")]
    [InlineData(", \"supplyDrops\": { }")]
    public void BadSpawnChanceOrSchedule_IsRejected(string extra)
    {
        Parse(WithTable("[ { \"kind\": \"Ammo\", \"weight\": 1 } ]", extra));
    }

    // Phase 16 review (Low): a table of guaranteed kinds only parses (a container may use one), but a floor loot point rolls
    // one weighted entry, so naming such a table from a loot point fails at startup with a clear error instead of an
    // IndexOutOfRangeException at every match start.
    private const string GuaranteedOnlyFloorJson = """
        { "rarityWeights": { "Common": 1, "Uncommon": 1, "Rare": 1, "Epic": 1, "Legendary": 1 },
          "tables": { "Floor": { "rolls": 1, "guaranteed": [ "Weapon" ] },
                      "Building": [ { "kind": "Ammo", "weight": 1 } ], "Tower": [ { "kind": "Ammo", "weight": 1 } ] } }
        """;

    [Fact]
    public void GuaranteedOnlyTable_Parses_ButAFloorPointCannotUseIt()
    {
        var items = TestGameData.Items();
        LootTable loot = TestGameData.Loot(items, GuaranteedOnlyFloorJson);
        Assert.Equal(0, loot.EntryCount(loot.TableIndex("Floor")));
        var error = Assert.Throws<ArgumentException>(() => new GameData(TestWeapons.Create(), items, loot, TestGameData.Zones()));
        Assert.Contains("Floor", error.Message);
        Assert.Contains("entry", error.Message);
    }

    [Fact]
    public void LootSpawner_RefusesAPointWhoseTableHasNoEntries()
    {
        var items = TestGameData.Items();
        // Phase 17: the ammo box keeps its ammo list; only its rolls and entries change.
        string json = TestGameData.LootJson.Replace("\"AmmoBox\": { \"rolls\": 2,", "\"AmmoBox\": { \"rolls\": 1,")
            .Replace("\"entries\": [ { \"kind\": \"Material\", \"weight\": 1, \"amount\": 10 } ] }", "\"entries\": [ ] }");
        Assert.NotEqual(TestGameData.LootJson, json);
        var data = new GameData(TestWeapons.Create(), items, TestGameData.Loot(items, json), TestGameData.Zones());
        Assert.Equal(0, data.Loot.EntryCount(data.Loot.TableIndex("AmmoBox")));   // a container table may hold guaranteed kinds only
        var point = new[] { new ProjectH.Shared.Simulation.LootPoint(new System.Numerics.Vector3(0f, 0f, 8f), "AmmoBox") };
        Assert.Throws<ArgumentException>(() => new LootSpawner(point, data, 1, 0));
        // The container itself still rolls from it.
        var output = new LootRoll[LootTable.MaxRolls];
        Assert.Equal(1, data.Loot.RollAll(data.Loot.TableIndex("AmmoBox"), new Random(1), data.Weapons, data.Items, output));
        Assert.Equal(ItemKind.Ammo, output[0].Kind);
    }
}
