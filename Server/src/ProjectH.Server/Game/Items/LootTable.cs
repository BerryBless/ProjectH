using System;
using System.Collections.Generic;
using System.IO;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Items;

// What a loot table entry produces (D5). Ammo picks its type uniformly; Weapon picks the weapon uniformly
// and the rarity from rarityWeights.
public enum LootKind : byte
{
    Weapon,
    Ammo,
    Medkit,
    ShieldCell,
}

// One rolled item, before it has an id or a position.
public readonly struct LootRoll
{
    // 기능: 굴려 나온 아이템 하나의 값을 담는다.
    // 입력: kind - 아이템 종류, defId - 정의 ID, rarity - 등급 번호, amount - 수량.
    // 출력: 받은 값을 그대로 가진 LootRoll.
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
public sealed class LootTable
{
    public const int MaxWeight = 1_000_000;   // keeps every table total far below int.MaxValue

    private readonly int[] _rarityWeights;
    private readonly int _rarityTotal;
    private readonly string[] _names;
    private readonly LootKind[][] _kinds;
    private readonly int[][] _weights;
    private readonly int[] _totals;

    // 기능: 검증된 등급 가중치와 테이블 항목으로 루트 테이블을 만들고 가중치 합계를 미리 계산한다.
    // 입력: rarityWeights - 등급별 무기 가중치, names - 테이블 이름, kinds - 테이블별 항목 종류, weights - 테이블별 항목 가중치.
    // 출력: 등급 합계와 테이블별 합계가 계산된 불변 LootTable 객체.
    private LootTable(int[] rarityWeights, string[] names, LootKind[][] kinds, int[][] weights)
    {
        _rarityWeights = rarityWeights;
        _names = names;
        _kinds = kinds;
        _weights = weights;
        _totals = new int[names.Length];
        foreach (int w in rarityWeights) _rarityTotal += w;
        for (int t = 0; t < names.Length; t++)
        {
            foreach (int w in weights[t]) _totals[t] += w;
        }
    }

    public int TableCount => _names.Length;

    // 기능: 루트 테이블 이름으로 테이블 번호를 찾는다.
    // 입력: name - 테이블 이름.
    // 출력: 일치하는 테이블 번호, 없으면 -1.
    // Index of a table name, or -1. Resolved once when the spawner is built, not per roll.
    public int TableIndex(string name) => Array.IndexOf(_names, name);

    // 기능: 테이블 가중치로 항목 종류를 고르고 그 종류의 아이템 하나를 굴린다.
    // 입력: table - 테이블 번호, rng - 게임 루프 스레드가 소유한 시드 난수, weapons - 무기 카탈로그, items - 아이템 카탈로그.
    // 출력: 무기(무작위 무기, 등급 가중치로 고른 등급, 탄창 가득), 탄약(무작위 종류, 줍기 수량), Medkit 또는 Shield Cell 1개의 LootRoll.
    public LootRoll Roll(int table, Random rng, WeaponCatalog weapons, ItemCatalog items)
    {
        switch (Pick(_kinds[table], _weights[table], _totals[table], rng))
        {
            case LootKind.Weapon:
                WeaponDefinition weapon = weapons[rng.Next(weapons.Count)];
                byte rarity = (byte)PickIndex(_rarityWeights, _rarityTotal, rng);
                return new LootRoll(ItemKind.Weapon, weapon.Id, rarity, weapon.MagazineSize);
            case LootKind.Ammo:
                var type = (AmmoType)(1 + rng.Next(ItemConstants.AmmoTypeCount));
                return new LootRoll(ItemKind.Ammo, (byte)type, 0, items.Ammo(type).PickupAmount);
            case LootKind.Medkit:
                return new LootRoll(ItemKind.Consumable, (byte)ConsumableType.Medkit, 0, 1);
            default:
                return new LootRoll(ItemKind.Consumable, (byte)ConsumableType.ShieldCell, 0, 1);
        }
    }

    // 기능: loot.json 파일을 읽어 items.json 기준으로 검증하고 루트 테이블을 만든다.
    // 입력: path - loot.json 경로, items - 등급 이름을 확인할 아이템 카탈로그.
    // 출력: 로드된 LootTable. 파일이 없거나 내용이 잘못되면 InvalidOperationException을 던진다(서버 시작 중단).
    public static LootTable LoadFile(string path, ItemCatalog items)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"Loot data not found: {path}");
        if (!TryParse(File.ReadAllText(path), items, out var loot, out string? error))
            throw new InvalidOperationException($"Invalid loot data {path}: {error}");
        return loot!;
    }

    // 기능: loot.json 내용을 파싱하고 등급 가중치(모든 등급 필수)와 테이블 항목(종류 중복 금지, 가중치 범위)을 검증한다.
    // 입력: json - loot.json 내용, items - 등급 이름을 확인할 아이템 카탈로그, loot - 만든 루트 테이블, error - 실패 이유.
    // 출력: 성공하면 true와 loot, 실패하면 false와 첫 번째 오류 메시지.
    public static bool TryParse(string json, ItemCatalog items, out LootTable? loot, out string? error)
    {
        loot = null;
        if (!DataJson.TryDeserialize(json, out LootJson? root, out error)) return false;

        Dictionary<string, int>? rarityJson = root!.RarityWeights;
        if (rarityJson == null)
        {
            error = "\"rarityWeights\" is required.";
            return false;
        }
        var rarityWeights = new int[ItemConstants.RarityCount];
        foreach (var pair in rarityJson)
        {
            int index = items.RarityIndex(pair.Key);
            if (index < 0)
            {
                error = $"rarityWeights: unknown rarity \"{pair.Key}\" (items.json).";
                return false;
            }
            if (pair.Value < 1 || pair.Value > MaxWeight)
            {
                error = $"rarityWeights.{pair.Key}: weight must be 1-{MaxWeight}.";
                return false;
            }
            rarityWeights[index] = pair.Value;
        }
        for (int i = 0; i < rarityWeights.Length; i++)
        {
            if (rarityWeights[i] == 0)
            {
                error = $"rarityWeights: \"{items.RarityName(i)}\" is missing.";
                return false;
            }
        }

        Dictionary<string, List<EntryJson?>?>? tablesJson = root.Tables;
        if (tablesJson == null || tablesJson.Count == 0)
        {
            error = "\"tables\" must hold at least one table.";
            return false;
        }

        var names = new string[tablesJson.Count];
        var kinds = new LootKind[tablesJson.Count][];
        var weights = new int[tablesJson.Count][];
        int t = 0;
        foreach (var pair in tablesJson)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                error = "tables: a table name is empty.";
                return false;
            }
            List<EntryJson?>? entries = pair.Value;
            if (entries == null || entries.Count == 0)
            {
                error = $"tables.{pair.Key}: must hold at least one entry.";
                return false;
            }
            names[t] = pair.Key;
            kinds[t] = new LootKind[entries.Count];
            weights[t] = new int[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                EntryJson? e = entries[i];
                if (e == null || !TryParseKind(e.Kind, out LootKind kind))
                {
                    error = $"tables.{pair.Key}[{i}]: kind must be Weapon, Ammo, Medkit or ShieldCell.";
                    return false;
                }
                if (Array.IndexOf(kinds[t], kind, 0, i) >= 0)
                {
                    error = $"tables.{pair.Key}[{i}]: duplicate kind {kind}.";
                    return false;
                }
                if (e.Weight < 1 || e.Weight > MaxWeight)
                {
                    error = $"tables.{pair.Key}[{i}]: weight must be 1-{MaxWeight}.";
                    return false;
                }
                kinds[t][i] = kind;
                weights[t][i] = e.Weight;
            }
            t++;
        }

        loot = new LootTable(rarityWeights, names, kinds, weights);
        error = null;
        return true;
    }

    // 기능: 루트 항목 종류 문자열을 LootKind로 바꾼다.
    // 입력: text - "Weapon", "Ammo", "Medkit", "ShieldCell" 중 하나여야 하는 문자열, kind - 변환된 항목 종류.
    // 출력: 알려진 이름이면 true와 kind, 아니면 false.
    private static bool TryParseKind(string? text, out LootKind kind)
    {
        switch (text)
        {
            case "Weapon": kind = LootKind.Weapon; return true;
            case "Ammo": kind = LootKind.Ammo; return true;
            case "Medkit": kind = LootKind.Medkit; return true;
            case "ShieldCell": kind = LootKind.ShieldCell; return true;
            default: kind = LootKind.Weapon; return false;
        }
    }

    // 기능: 가중치 비율로 항목 종류 하나를 고른다.
    // 입력: kinds - 항목 종류 목록, weights - 종류별 가중치, total - 가중치 합계, rng - 난수.
    // 출력: 선택된 항목 종류.
    private static LootKind Pick(LootKind[] kinds, int[] weights, int total, Random rng) => kinds[PickIndex(weights, total, rng)];

    // 기능: 가중치 비율로 항목 번호 하나를 고른다.
    // 입력: weights - 항목별 가중치, total - 가중치 합계, rng - 난수.
    // 출력: 선택된 항목 번호.
    // Weighted choice: one Next(total), then walk the cumulative weights. No allocation.
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

    private sealed class LootJson
    {
        public Dictionary<string, int>? RarityWeights { get; set; }
        public Dictionary<string, List<EntryJson?>?>? Tables { get; set; }
    }

    private sealed class EntryJson
    {
        public string? Kind { get; set; }
        public int Weight { get; set; }
    }
}
