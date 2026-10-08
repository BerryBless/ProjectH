using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Items;

// What a loot table entry produces (D5). Ammo picks its type uniformly; Weapon picks the weapon uniformly
// and the rarity from rarityWeights (the table's own, Phase 16 D5, or the global ones). Material (Phase 16 D5) picks
// the building material uniformly and gives the entry's amount. Phase 17 D13: a table may name the weapons and ammo types
// it picks from ("weapons", "ammo"); without them it picks from the whole catalog / every type. Grenade (Phase 17 D9) is
// one grenade.
public enum LootKind : byte
{
    Weapon,
    Ammo,
    Medkit,
    ShieldCell,
    Material,
    Grenade,
}

// One rolled item, before it has an id or a position.
public readonly struct LootRoll
{
    public LootRoll(ItemKind kind, byte defId, byte rarity, ushort amount)
    {
        Kind = kind;
        DefId = defId;
        Rarity = rarity;
        Amount = amount;
    }

    public ItemKind Kind { get; }
    public byte DefId { get; }
    public byte Rarity { get; }
    public ushort Amount { get; }
}

// loot.json (D5): weighted tables per spawn point and the weapon rarity weights. Loaded and validated
// once at startup against items.json; immutable afterwards. Roll takes the caller's Random, which the
// game loop thread owns (seeded from ServerOptions.LootSeed), so results repeat for the same seed.
// Phase 16 D5: a table is either the old array of entries (one roll, the global rarity weights) or an object with its
// own rarityWeights, rolls, guaranteed kinds and entries (the container tables Chest, AmmoBox and SupplyDrop). Also the
// containers' spawn chances and the supply drop schedule. A floor table rolls exactly as before (same Random calls).
public sealed class LootTable
{
    public const int MaxWeight = 1_000_000;   // keeps every table total far below int.MaxValue
    // Phase 16 D2: a container holds at most this many items (a fixed slot array per container).
    public const int MaxRolls = 4;
    public const string ChestTable = "Chest";
    public const string AmmoBoxTable = "AmmoBox";
    public const string SupplyDropTable = "SupplyDrop";
    public const int MaxMaterialAmount = 1000;
    public const double MaxSupplyDropSeconds = 3600;
    public const float MinFallSpeed = 0.5f;
    public const float MaxFallSpeed = 60f;

    private readonly int[] _rarityWeights;
    private readonly int _rarityTotal;
    private readonly string[] _names;
    private readonly LootKind[][] _kinds;
    private readonly int[][] _weights;
    private readonly int[] _totals;
    private readonly ushort[][] _amounts;          // Material entries' amounts (0 for other kinds)
    private readonly int[]?[] _tableRarity;        // null = the global weights
    private readonly int[] _tableRarityTotal;
    private readonly int[] _rolls;
    private readonly LootKind[][] _guaranteed;
    private readonly double[] _supplyDropSeconds;
    // Phase 17 D13: the table's weapon ids and ammo types to pick from (null = the whole catalog / every type).
    private readonly byte[]?[] _weaponIds;
    private readonly AmmoType[]?[] _ammoTypes;

    // 기능: 검증이 끝난 표들로 LootTable을 만든다(표마다 합계를 한 번 계산해 둔다).
    // 입력: rarityWeights - 전역 등급 가중치, tables - 표, chestChance·ammoBoxChance - 생성 확률, supplyDropSeconds - 일정, fallSpeed - 낙하 속도.
    // 출력: 바뀌지 않는 LootTable.
    private LootTable(int[] rarityWeights, TableData[] tables, double chestChance, double ammoBoxChance, double[] supplyDropSeconds,
        float fallSpeed)
    {
        _rarityWeights = rarityWeights;
        foreach (int w in rarityWeights) _rarityTotal += w;
        int n = tables.Length;
        _names = new string[n];
        _kinds = new LootKind[n][];
        _weights = new int[n][];
        _totals = new int[n];
        _amounts = new ushort[n][];
        _tableRarity = new int[]?[n];
        _tableRarityTotal = new int[n];
        _rolls = new int[n];
        _guaranteed = new LootKind[n][];
        _weaponIds = new byte[]?[n];
        _ammoTypes = new AmmoType[]?[n];
        for (int t = 0; t < n; t++)
        {
            TableData d = tables[t];
            _names[t] = d.Name;
            _kinds[t] = d.Kinds;
            _weights[t] = d.Weights;
            _amounts[t] = d.Amounts;
            foreach (int w in d.Weights) _totals[t] += w;
            _tableRarity[t] = d.Rarity;
            if (d.Rarity != null) foreach (int w in d.Rarity) _tableRarityTotal[t] += w;
            _rolls[t] = d.Rolls;
            _guaranteed[t] = d.Guaranteed;
            _weaponIds[t] = d.WeaponIds;
            _ammoTypes[t] = d.AmmoTypes;
        }
        ChestSpawnChance = chestChance;
        AmmoBoxSpawnChance = ammoBoxChance;
        _supplyDropSeconds = supplyDropSeconds;
        SupplyDropFallSpeed = fallSpeed;
    }

    public int TableCount => _names.Length;

    // Phase 16 D2: how likely a chest and an ammo box spawn at a match start (loot.json spawnChance, default 1).
    public double ChestSpawnChance { get; }
    public double AmmoBoxSpawnChance { get; }
    // Phase 16 D6: the supply drops of a match, in seconds of the zone clock (ascending, at most SupplyDropsPacket.MaxSupplyDrops;
    // empty = none), and their fall speed (m/s; the start height is SupplyDropFall.StartHeight).
    public ReadOnlySpan<double> SupplyDropSeconds => _supplyDropSeconds;
    public float SupplyDropFallSpeed { get; }

    // Index of a table name, or -1. Resolved once when the spawner is built, not per roll.
    public int TableIndex(string name) => Array.IndexOf(_names, name);

    // 기능: 표들이 이름으로 고른 무기 id가 모두 무기 카탈로그에 있는지 본다(Phase 17 D13, GameData가 시작 때 부른다).
    // 입력: weapons - 무기 카탈로그.
    // 출력: 모두 있으면 null, 아니면 이유.
    public string? ValidateWeapons(WeaponCatalog weapons)
    {
        for (int t = 0; t < _names.Length; t++)
        {
            byte[]? ids = _weaponIds[t];
            if (ids == null) continue;
            foreach (byte id in ids)
            {
                if (!weapons.TryGetById(id, out _)) return $"loot.json table \"{_names[t]}\" names unknown weapon id {id}.";
            }
        }
        return null;
    }

    // 기능: 표가 고르는 무기 id 목록을 돌려준다(테스트·QA 확인용).
    // 입력: table - 표 index.
    // 출력: id 목록. 목록이 없는 표(카탈로그 전체에서 고름)는 빈 Span.
    public ReadOnlySpan<byte> WeaponIds(int table) => _weaponIds[table];

    // 기능: 표가 고르는 탄 종류 목록을 돌려준다(테스트·QA 확인용).
    // 입력: table - 표 index.
    // 출력: 종류 목록, 없으면(모든 종류) 빈 Span.
    public ReadOnlySpan<AmmoType> AmmoTypes(int table) => _ammoTypes[table];

    // 기능: 표 하나에서 아이템 하나를 가중치로 굴린다(바닥 Loot Point와 Container의 가중치 굴림).
    // 입력: table - 표 index, rng - Game Loop가 가진 Random, weapons·items - 카탈로그.
    // 출력: 굴린 아이템. 할당 없음. Phase 16 이전 표는 같은 Random 호출 순서라 결과가 같다.
    public LootRoll Roll(int table, Random rng, WeaponCatalog weapons, ItemCatalog items)
    {
        int entry = PickIndex(_weights[table], _totals[table], rng);
        return RollKind(table, _kinds[table][entry], _amounts[table][entry], rng, weapons, items);
    }

    // 기능: Container 하나의 Loot를 굴린다(Phase 16 D5): 표의 guaranteed 종류를 하나씩, 남은 칸은 가중치로, 모두 rolls개.
    // 입력: table - 표 index, rng - 그 Container 흐름의 Random, weapons·items - 카탈로그, output - MaxRolls칸 이상.
    // 출력: 채운 개수(= 표의 rolls). 할당 없음.
    public int RollAll(int table, Random rng, WeaponCatalog weapons, ItemCatalog items, Span<LootRoll> output)
    {
        LootKind[] guaranteed = _guaranteed[table];
        int count = 0;
        for (int i = 0; i < guaranteed.Length; i++) output[count++] = RollKind(table, guaranteed[i], 0, rng, weapons, items);
        while (count < _rolls[table]) output[count++] = Roll(table, rng, weapons, items);
        return count;
    }

    // 기능: 표의 굴림 수를 돌려준다.
    // 입력: table - 표 index.
    // 출력: rolls(배열 형식 표는 1).
    public int Rolls(int table) => _rolls[table];

    // 기능: 표의 가중치 항목 수를 돌려준다(guaranteed만 있는 Container 표는 0일 수 있다. 바닥 Loot Point는 Roll이 항목을 고르므로 1개 이상이어야 한다).
    // 입력: table - 표 index.
    // 출력: 항목 수.
    public int EntryCount(int table) => _kinds[table].Length;

    // 기능: 종류가 정해진 아이템 하나를 만든다(무기·탄종·재료는 Random으로 고른다. Phase 17: 표의 무기·탄 목록이 있으면 그 안에서).
    // 입력: table - 등급 가중치·목록을 줄 표, kind - 종류, amount - Material 양, rng·weapons·items - 굴림 재료.
    // 출력: 아이템.
    private LootRoll RollKind(int table, LootKind kind, ushort amount, Random rng, WeaponCatalog weapons, ItemCatalog items)
    {
        switch (kind)
        {
            case LootKind.Weapon:
                byte[]? ids = _weaponIds[table];
                WeaponDefinition weapon;
                if (ids == null) weapon = weapons[rng.Next(weapons.Count)];
                else weapons.TryGetById(ids[rng.Next(ids.Length)], out weapon);   // ids checked against the catalog at startup
                int[]? own = _tableRarity[table];
                byte rarity = own == null ? (byte)PickIndex(_rarityWeights, _rarityTotal, rng) : (byte)PickIndex(own, _tableRarityTotal[table], rng);
                return new LootRoll(ItemKind.Weapon, weapon.Id, rarity, weapon.MagazineSize);
            case LootKind.Ammo:
                AmmoType[]? types = _ammoTypes[table];
                var type = types == null ? (AmmoType)(1 + rng.Next(ItemConstants.AmmoTypeCount)) : types[rng.Next(types.Length)];
                return new LootRoll(ItemKind.Ammo, (byte)type, 0, items.Ammo(type).PickupAmount);
            case LootKind.Medkit:
                return new LootRoll(ItemKind.Consumable, (byte)ConsumableType.Medkit, 0, 1);
            case LootKind.Grenade:
                return new LootRoll(ItemKind.Consumable, (byte)ConsumableType.Grenade, 0, 1);
            case LootKind.Material:
                // Phase 13 D15: a Material item's DefId is the material + 1.
                return new LootRoll(ItemKind.Material, (byte)(1 + rng.Next(BuildMaterials.Count)), 0, amount);
            default:
                return new LootRoll(ItemKind.Consumable, (byte)ConsumableType.ShieldCell, 0, 1);
        }
    }

    public static LootTable LoadFile(string path, ItemCatalog items)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"Loot data not found: {path}");
        if (!TryParse(File.ReadAllText(path), items, out var loot, out string? error))
            throw new InvalidOperationException($"Invalid loot data {path}: {error}");
        return loot!;
    }

    // 기능: loot.json을 읽고 검증한다(Phase 16: 객체 형식 표, Material, 표별 등급, rolls, guaranteed, spawnChance, supplyDrops).
    // 입력: json - 파일 내용, items - 등급 이름을 가진 아이템 카탈로그.
    // 출력: 성공하면 true와 LootTable, 실패하면 false와 이유.
    public static bool TryParse(string json, ItemCatalog items, out LootTable? loot, out string? error)
    {
        loot = null;
        if (!DataJson.TryDeserialize(json, out LootJson? root, out error)) return false;

        if (root!.RarityWeights == null)
        {
            error = "\"rarityWeights\" is required.";
            return false;
        }
        if (!TryParseRarity(root.RarityWeights, items, allowZero: false, "rarityWeights", out int[]? rarityWeights, out error)) return false;

        Dictionary<string, JsonElement>? tablesJson = root.Tables;
        if (tablesJson == null || tablesJson.Count == 0)
        {
            error = "\"tables\" must hold at least one table.";
            return false;
        }

        var tables = new TableData[tablesJson.Count];
        int t = 0;
        foreach (var pair in tablesJson)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                error = "tables: a table name is empty.";
                return false;
            }
            if (!TryParseTable(pair.Key, pair.Value, items, out tables[t], out error)) return false;
            t++;
        }

        double chest = 1, ammoBox = 1;
        if (root.SpawnChance != null)
        {
            foreach (var pair in root.SpawnChance)
            {
                if (!double.IsFinite(pair.Value) || pair.Value < 0 || pair.Value > 1)
                {
                    error = $"spawnChance.{pair.Key}: must be 0-1.";
                    return false;
                }
                if (pair.Key == ChestTable) chest = pair.Value;
                else if (pair.Key == AmmoBoxTable) ammoBox = pair.Value;
                else
                {
                    error = $"spawnChance: unknown container \"{pair.Key}\" (Chest or AmmoBox).";
                    return false;
                }
            }
        }

        double[] dropSeconds = Array.Empty<double>();
        float fallSpeed = 4f;
        if (root.SupplyDrops != null)
        {
            List<double>? times = root.SupplyDrops.Times;
            if (times == null || times.Count > SupplyDropsPacket.MaxSupplyDrops)
            {
                error = $"supplyDrops.times: must hold 0-{SupplyDropsPacket.MaxSupplyDrops} times.";
                return false;
            }
            for (int i = 0; i < times.Count; i++)
            {
                if (!double.IsFinite(times[i]) || times[i] <= 0 || times[i] > MaxSupplyDropSeconds || (i > 0 && times[i] <= times[i - 1]))
                {
                    error = $"supplyDrops.times[{i}]: must be ascending seconds in (0, {MaxSupplyDropSeconds}].";
                    return false;
                }
            }
            if (!float.IsFinite(root.SupplyDrops.FallSpeed) || root.SupplyDrops.FallSpeed < MinFallSpeed || root.SupplyDrops.FallSpeed > MaxFallSpeed)
            {
                error = $"supplyDrops.fallSpeed: must be {MinFallSpeed}-{MaxFallSpeed} m/s.";
                return false;
            }
            dropSeconds = times.ToArray();
            fallSpeed = root.SupplyDrops.FallSpeed;
        }

        loot = new LootTable(rarityWeights!, tables, chest, ammoBox, dropSeconds, fallSpeed);
        error = null;
        return true;
    }

    // 기능: 운영 시작 검증(Phase 16 D5): Container 표 Chest·AmmoBox·SupplyDrop이 모두 있는지 본다.
    // 입력: 없음.
    // 출력: 모두 있으면 null, 없으면 이유.
    public string? ValidateContainerTables()
    {
        foreach (string name in new[] { ChestTable, AmmoBoxTable, SupplyDropTable })
        {
            if (TableIndex(name) < 0) return $"loot.json has no table \"{name}\" (Phase 16 containers).";
        }
        return null;
    }

    // 기능: 등급 가중치 객체를 배열로 바꾼다.
    // 입력: json - 등급 이름 → 가중치, items - 카탈로그, allowZero - 표별 등급이면 true(빠진 등급 = 0, 합 > 0), path - 오류 위치.
    // 출력: 성공하면 true와 RarityCount 칸 배열.
    private static bool TryParseRarity(Dictionary<string, int> json, ItemCatalog items, bool allowZero, string path, out int[]? weights,
        out string? error)
    {
        weights = new int[ItemConstants.RarityCount];
        error = null;
        foreach (var pair in json)
        {
            int index = items.RarityIndex(pair.Key);
            if (index < 0)
            {
                error = $"{path}: unknown rarity \"{pair.Key}\" (items.json).";
                return false;
            }
            if (pair.Value < (allowZero ? 0 : 1) || pair.Value > MaxWeight)
            {
                error = $"{path}.{pair.Key}: weight must be {(allowZero ? 0 : 1)}-{MaxWeight}.";
                return false;
            }
            weights[index] = pair.Value;
        }
        int total = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            if (weights[i] == 0 && !allowZero)
            {
                error = $"{path}: \"{items.RarityName(i)}\" is missing.";
                return false;
            }
            total += weights[i];
        }
        if (total == 0)
        {
            error = $"{path}: at least one rarity needs a weight.";
            return false;
        }
        return true;
    }

    // 기능: 표 하나를 읽는다. 배열이면 항목 목록(rolls 1, 전역 등급), 객체면 rarityWeights·rolls·guaranteed·entries
    //   (Phase 17 D13: 그리고 weapons·ammo 목록).
    // 입력: name - 표 이름, element - JSON 값, items - 카탈로그, table - 결과.
    // 출력: 성공하면 true.
    private static bool TryParseTable(string name, JsonElement element, ItemCatalog items, out TableData table, out string? error)
    {
        table = default;
        error = null;
        List<EntryJson?>? entries;
        TableJson? obj = null;
        try
        {
            if (element.ValueKind == JsonValueKind.Array) entries = element.Deserialize<List<EntryJson?>>(DataJson.Options);
            else if (element.ValueKind == JsonValueKind.Object)
            {
                obj = element.Deserialize<TableJson>(DataJson.Options);
                entries = obj?.Entries;
            }
            else
            {
                error = $"tables.{name}: must be an array of entries or an object.";
                return false;
            }
        }
        catch (JsonException ex)
        {
            error = $"tables.{name}: invalid JSON: {ex.Message}";
            return false;
        }

        int rolls = obj?.Rolls ?? 1;
        if (rolls < 1 || rolls > MaxRolls)
        {
            error = $"tables.{name}.rolls: must be 1-{MaxRolls}.";
            return false;
        }
        var guaranteed = new List<LootKind>();
        if (obj?.Guaranteed != null)
        {
            foreach (string? text in obj.Guaranteed)
            {
                if (!TryParseKind(text, out LootKind kind) || kind == LootKind.Material)
                {
                    error = $"tables.{name}.guaranteed: kind must be Weapon, Ammo, Medkit, ShieldCell or Grenade.";
                    return false;
                }
                guaranteed.Add(kind);
            }
        }
        if (guaranteed.Count > rolls)
        {
            error = $"tables.{name}.guaranteed: more kinds than rolls.";
            return false;
        }
        byte[]? weaponIds = null;
        if (obj?.Weapons != null)
        {
            if (obj.Weapons.Count == 0)
            {
                error = $"tables.{name}.weapons: must name at least one weapon id (or be left out for every weapon).";
                return false;
            }
            weaponIds = new byte[obj.Weapons.Count];
            for (int i = 0; i < weaponIds.Length; i++)
            {
                int id = obj.Weapons[i];
                if (id < 1 || id > byte.MaxValue || Array.IndexOf(weaponIds, (byte)id, 0, i) >= 0)
                {
                    error = $"tables.{name}.weapons[{i}]: must be a weapon id 1-255, each once.";
                    return false;
                }
                weaponIds[i] = (byte)id;
            }
        }
        AmmoType[]? ammoTypes = null;
        if (obj?.Ammo != null)
        {
            if (obj.Ammo.Count == 0)
            {
                error = $"tables.{name}.ammo: must name at least one ammo type (or be left out for every type).";
                return false;
            }
            ammoTypes = new AmmoType[obj.Ammo.Count];
            for (int i = 0; i < ammoTypes.Length; i++)
            {
                if (!ItemCatalog.TryParseAmmoType(obj.Ammo[i], out ammoTypes[i]) || Array.IndexOf(ammoTypes, ammoTypes[i], 0, i) >= 0)
                {
                    error = $"tables.{name}.ammo[{i}]: must be an ammo type (Light, Medium, Heavy, Shells, Rockets), each once.";
                    return false;
                }
            }
        }
        int[]? rarity = null;
        if (obj?.RarityWeights != null &&
            !TryParseRarity(obj.RarityWeights, items, allowZero: true, $"tables.{name}.rarityWeights", out rarity, out error))
            return false;

        int count = entries?.Count ?? 0;
        if (count == 0 && guaranteed.Count < rolls)
        {
            error = $"tables.{name}: must hold at least one entry.";
            return false;
        }
        var kinds = new LootKind[count];
        var weights = new int[count];
        var amounts = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            EntryJson? e = entries![i];
            if (e == null || !TryParseKind(e.Kind, out LootKind kind))
            {
                error = $"tables.{name}[{i}]: kind must be Weapon, Ammo, Medkit, ShieldCell, Material or Grenade.";
                return false;
            }
            if (Array.IndexOf(kinds, kind, 0, i) >= 0)
            {
                error = $"tables.{name}[{i}]: duplicate kind {kind}.";
                return false;
            }
            if (e.Weight < 1 || e.Weight > MaxWeight)
            {
                error = $"tables.{name}[{i}]: weight must be 1-{MaxWeight}.";
                return false;
            }
            if (kind == LootKind.Material && (e.Amount < 1 || e.Amount > MaxMaterialAmount))
            {
                error = $"tables.{name}[{i}]: a Material entry needs an amount of 1-{MaxMaterialAmount}.";
                return false;
            }
            if (kind != LootKind.Material && e.Amount != 0)
            {
                error = $"tables.{name}[{i}]: only a Material entry has an amount.";
                return false;
            }
            kinds[i] = kind;
            weights[i] = e.Weight;
            amounts[i] = (ushort)e.Amount;
        }
        table = new TableData(name, kinds, weights, amounts, rarity, rolls, guaranteed.ToArray(), weaponIds, ammoTypes);
        return true;
    }

    // 기능: 종류 이름을 LootKind로 바꾼다(Phase 16: Material, Phase 17: Grenade).
    // 입력: text - JSON의 kind.
    // 출력: 아는 이름이면 true와 종류.
    private static bool TryParseKind(string? text, out LootKind kind)
    {
        switch (text)
        {
            case "Weapon": kind = LootKind.Weapon; return true;
            case "Ammo": kind = LootKind.Ammo; return true;
            case "Medkit": kind = LootKind.Medkit; return true;
            case "ShieldCell": kind = LootKind.ShieldCell; return true;
            case "Material": kind = LootKind.Material; return true;
            case "Grenade": kind = LootKind.Grenade; return true;
            default: kind = LootKind.Weapon; return false;
        }
    }

    // Weighted choice: one Next(total), then walk the cumulative weights. No allocation. A zero weight is never picked.
    private static int PickIndex(int[] weights, int total, Random rng)
    {
        int roll = rng.Next(total);
        for (int i = 0; i < weights.Length; i++)
        {
            roll -= weights[i];
            if (roll < 0) return i;
        }
        return weights.Length - 1;   // not reached: roll < total
    }

    private readonly record struct TableData(string Name, LootKind[] Kinds, int[] Weights, ushort[] Amounts, int[]? Rarity, int Rolls,
        LootKind[] Guaranteed, byte[]? WeaponIds, AmmoType[]? AmmoTypes);

    private sealed class LootJson
    {
        public Dictionary<string, int>? RarityWeights { get; set; }
        public Dictionary<string, JsonElement>? Tables { get; set; }
        public Dictionary<string, double>? SpawnChance { get; set; }
        public SupplyDropsJson? SupplyDrops { get; set; }
    }

    private sealed class TableJson
    {
        public Dictionary<string, int>? RarityWeights { get; set; }
        public int? Rolls { get; set; }
        public List<string?>? Guaranteed { get; set; }
        public List<EntryJson?>? Entries { get; set; }
        // Phase 17 D13: the weapon ids and ammo type names this table picks from (null = all).
        public List<int>? Weapons { get; set; }
        public List<string?>? Ammo { get; set; }
    }

    private sealed class EntryJson
    {
        public string? Kind { get; set; }
        public int Weight { get; set; }
        public int Amount { get; set; }
    }

    private sealed class SupplyDropsJson
    {
        public List<double>? Times { get; set; }
        public float FallSpeed { get; set; } = 4f;
    }
}
