using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Items;

public sealed class AmmoDefinition
{
    // 기능: 탄약 종류 하나의 정의를 만든다.
    // 입력: type - 탄약 종류, name - 이름, pickupAmount - 루트 탄약 하나의 탄 수, max - 예비 탄약 최대치.
    // 출력: 받은 값을 그대로 가진 변경 불가 AmmoDefinition.
    public AmmoDefinition(AmmoType type, string name, ushort pickupAmount, ushort max)
    {
        Type = type;
        Name = name;
        PickupAmount = pickupAmount;
        Max = max;
    }

    public AmmoType Type { get; }
    public string Name { get; }
    public ushort PickupAmount { get; }   // rounds in one loot ammo item
    public ushort Max { get; }            // reserve limit of this type (D3)
}

public sealed class ConsumableDefinition
{
    // 기능: 회복 아이템 종류 하나의 정의를 만든다.
    // 입력: type - 회복 아이템 종류, name - 이름, useTicks - 사용 시간(Tick), heal - 체력 회복량, shield - 실드 회복량, maxStack - 최대 보유 개수.
    // 출력: 받은 값을 그대로 가진 변경 불가 ConsumableDefinition.
    public ConsumableDefinition(ConsumableType type, string name, ushort useTicks, ushort heal, ushort shield, byte maxStack)
    {
        Type = type;
        Name = name;
        UseTicks = useTicks;
        Heal = heal;
        Shield = shield;
        MaxStack = maxStack;
    }

    public ConsumableType Type { get; }
    public string Name { get; }
    public ushort UseTicks { get; }   // channel time (D11), already in simulation ticks
    public ushort Heal { get; }
    public ushort Shield { get; }
    public byte MaxStack { get; }
}

// items.json (D2): rarities, ammo types and consumables. Loaded and validated once at startup; a bad
// file stops the server. Immutable afterwards, so the game loop reads it without locks.
public sealed class ItemCatalog
{
    private readonly float[] _damageMultipliers;
    private readonly string[] _rarityNames;
    private readonly AmmoDefinition[] _ammo;              // index = AmmoType - 1
    private readonly ConsumableDefinition[] _consumables; // index = ConsumableType - 1

    // 기능: 검증된 등급·탄약·회복 아이템 정의로 카탈로그를 만들고, 입장 시 보낼 ItemCatalog Packet 데이터를 한 번 만들어 둔다.
    // 입력: rarityNames - 등급 이름, damageMultipliers - 등급별 피해 배율, ammo - 탄약 정의(AmmoType 순), consumables - 회복 아이템 정의(ConsumableType 순), simHz - Tick 변환에 쓴 시뮬레이션 주파수.
    // 출력: 정의와 Wire 데이터가 채워진 불변 ItemCatalog 객체.
    private ItemCatalog(string[] rarityNames, float[] damageMultipliers, AmmoDefinition[] ammo,
        ConsumableDefinition[] consumables, int simHz)
    {
        _rarityNames = rarityNames;
        _damageMultipliers = damageMultipliers;
        _ammo = ammo;
        _consumables = consumables;
        SimHz = simHz;

        var wire = new ItemCatalogData
        {
            Rarities = new RarityInfo[rarityNames.Length],
            Ammo = new AmmoInfo[ammo.Length],
            Consumables = new ConsumableInfo[consumables.Length],
        };
        for (int i = 0; i < rarityNames.Length; i++)
            wire.Rarities[i] = new RarityInfo { Name = rarityNames[i], DamageMultiplier = damageMultipliers[i] };
        for (int i = 0; i < ammo.Length; i++)
            wire.Ammo[i] = new AmmoInfo { Type = ammo[i].Type, Name = ammo[i].Name, Max = ammo[i].Max };
        for (int i = 0; i < consumables.Length; i++)
        {
            ConsumableDefinition c = consumables[i];
            wire.Consumables[i] = new ConsumableInfo
            {
                Type = c.Type, Name = c.Name, UseTicks = c.UseTicks, Heal = c.Heal, Shield = c.Shield, MaxStack = c.MaxStack,
            };
        }
        Wire = wire;
    }

    // Tick values were converted with this rate; Match refuses a catalog built for another SimHz.
    public int SimHz { get; }
    // Built once for the ItemCatalog packet sent at every join.
    public ItemCatalogData Wire { get; }

    // 기능: 등급의 피해 배율을 돌려준다.
    // 입력: rarity - 등급 번호.
    // 출력: 그 등급의 피해 배율.
    public float DamageMultiplier(int rarity) => _damageMultipliers[rarity];
    // 기능: 등급 번호의 이름을 돌려준다.
    // 입력: rarity - 등급 번호.
    // 출력: 그 등급의 이름.
    public string RarityName(int rarity) => _rarityNames[rarity];
    // 기능: 탄약 종류의 정의를 돌려준다.
    // 입력: type - 탄약 종류(None이 아닌 값).
    // 출력: 그 종류의 AmmoDefinition.
    public AmmoDefinition Ammo(AmmoType type) => _ammo[(int)type - 1];
    // 기능: 회복 아이템 종류의 정의를 돌려준다.
    // 입력: type - 회복 아이템 종류(None이 아닌 값).
    // 출력: 그 종류의 ConsumableDefinition.
    public ConsumableDefinition Consumable(ConsumableType type) => _consumables[(int)type - 1];

    // 기능: 등급 이름으로 등급 번호를 찾는다.
    // 입력: name - 찾을 등급 이름.
    // 출력: 일치하는 등급 번호, 없으면 -1.
    // Index of a rarity name, or -1 (loot.json refers to rarities by name).
    public int RarityIndex(string name)
    {
        for (int i = 0; i < _rarityNames.Length; i++)
        {
            if (_rarityNames[i] == name) return i;
        }
        return -1;
    }

    // 기능: items.json 파일을 읽어 검증하고 카탈로그를 만든다.
    // 입력: path - items.json 경로, simHz - 사용 시간을 Tick으로 바꿀 시뮬레이션 주파수.
    // 출력: 로드된 ItemCatalog. 파일이 없거나 내용이 잘못되면 InvalidOperationException을 던진다(서버 시작 중단).
    public static ItemCatalog LoadFile(string path, int simHz)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"Item data not found: {path}");
        if (!TryParse(File.ReadAllText(path), simHz, out var catalog, out string? error))
            throw new InvalidOperationException($"Invalid item data {path}: {error}");
        return catalog!;
    }

    // 기능: items.json 내용을 파싱하고 등급·탄약·회복 아이템을 검증해 카탈로그를 만든다.
    // 입력: json - items.json 내용, simHz - 시뮬레이션 주파수, catalog - 만든 카탈로그, error - 실패 이유.
    // 출력: 성공하면 true와 catalog, 실패하면 false와 첫 번째 오류 메시지.
    public static bool TryParse(string json, int simHz, out ItemCatalog? catalog, out string? error)
    {
        catalog = null;
        if (simHz < 1)
        {
            error = "SimHz must be positive.";
            return false;
        }
        if (!DataJson.TryDeserialize(json, out ItemsJson? root, out error)) return false;

        error = ParseRarities(root!.Rarities, out string[]? names, out float[]? multipliers);
        if (error != null) return false;
        error = ParseAmmo(root.Ammo, out AmmoDefinition[]? ammo);
        if (error != null) return false;
        error = ParseConsumables(root.Consumables, simHz, out ConsumableDefinition[]? consumables);
        if (error != null) return false;

        catalog = new ItemCatalog(names!, multipliers!, ammo!, consumables!, simHz);
        return true;
    }

    // 기능: 탄약 종류 이름 문자열을 AmmoType으로 바꾼다.
    // 입력: text - "Light", "Medium", "Heavy" 중 하나여야 하는 문자열, type - 변환된 탄약 종류.
    // 출력: 알려진 이름이면 true와 type, 아니면 false(type은 None).
    // Enum names only: "1" or "None" are not ammo types.
    public static bool TryParseAmmoType(string? text, out AmmoType type)
    {
        type = AmmoType.None;
        switch (text)
        {
            case "Light": type = AmmoType.Light; return true;
            case "Medium": type = AmmoType.Medium; return true;
            case "Heavy": type = AmmoType.Heavy; return true;
            default: return false;
        }
    }

    // 기능: rarities 목록의 개수·이름·피해 배율·중복을 검증하고 배열로 바꾼다.
    // 입력: list - JSON의 rarities 항목, names - 등급 이름 배열, multipliers - 등급별 피해 배율 배열.
    // 출력: 성공하면 null과 names·multipliers, 실패하면 오류 메시지(out 값은 null).
    private static string? ParseRarities(List<RarityJson?>? list, out string[]? names, out float[]? multipliers)
    {
        names = null;
        multipliers = null;
        if (list == null || list.Count != ItemConstants.RarityCount)
            return $"\"rarities\" must hold exactly {ItemConstants.RarityCount} entries.";

        var n = new string[list.Count];
        var m = new float[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            RarityJson? r = list[i];
            if (r == null) return $"rarities[{i}]: entry is null.";
            string? nameProblem = CheckName(r.Name);
            if (nameProblem != null) return $"rarities[{i}]: {nameProblem}";
            float multiplier = (float)r.DamageMultiplier;
            if (!float.IsFinite(multiplier) || multiplier <= 0f || multiplier > 10f)
                return $"rarities[{i}]: damageMultiplier must be above 0 and at most 10.";
            if (Array.IndexOf(n, r.Name, 0, i) >= 0) return $"rarities[{i}]: duplicate name \"{r.Name}\".";
            n[i] = r.Name!;
            m[i] = multiplier;
        }
        names = n;
        multipliers = m;
        return null;
    }

    // 기능: ammo 목록의 종류·이름·최대 보유량·줍기 수량·중복을 검증하고 AmmoType 순 배열로 만든다.
    // 입력: list - JSON의 ammo 항목, ammo - 탄약 정의 배열.
    // 출력: 성공하면 null과 ammo, 실패하면 오류 메시지(ammo는 null).
    private static string? ParseAmmo(List<AmmoJson?>? list, out AmmoDefinition[]? ammo)
    {
        ammo = null;
        if (list == null || list.Count != ItemConstants.AmmoTypeCount)
            return $"\"ammo\" must hold exactly {ItemConstants.AmmoTypeCount} entries (Light, Medium, Heavy).";

        var result = new AmmoDefinition[ItemConstants.AmmoTypeCount];
        for (int i = 0; i < list.Count; i++)
        {
            AmmoJson? a = list[i];
            if (a == null) return $"ammo[{i}]: entry is null.";
            if (!TryParseAmmoType(a.Type, out AmmoType type)) return $"ammo[{i}]: type must be Light, Medium or Heavy.";
            if (result[(int)type - 1] != null) return $"ammo[{i}]: duplicate type {type}.";
            string? nameProblem = CheckName(a.Name);
            if (nameProblem != null) return $"ammo[{i}]: {nameProblem}";
            if (a.Max < 1 || a.Max > ushort.MaxValue) return $"ammo[{i}]: max must be 1-65535.";
            if (a.PickupAmount < 1 || a.PickupAmount > a.Max) return $"ammo[{i}]: pickupAmount must be 1-max.";
            foreach (AmmoDefinition? other in result)
            {
                if (other != null && other.Name == a.Name) return $"ammo[{i}]: duplicate name \"{a.Name}\".";
            }
            result[(int)type - 1] = new AmmoDefinition(type, a.Name!, (ushort)a.PickupAmount, (ushort)a.Max);
        }
        ammo = result;
        return null;
    }

    // 기능: consumables 목록의 id·이름·사용 시간·회복량·최대 스택·중복을 검증하고 ConsumableType 순 배열로 만든다.
    // 입력: list - JSON의 consumables 항목, simHz - 사용 시간(초)을 Tick으로 바꿀 주파수, consumables - 회복 아이템 정의 배열.
    // 출력: 성공하면 null과 consumables, 실패하면 오류 메시지(consumables는 null).
    private static string? ParseConsumables(List<ConsumableJson?>? list, int simHz, out ConsumableDefinition[]? consumables)
    {
        consumables = null;
        if (list == null || list.Count != ItemConstants.ConsumableTypeCount)
            return $"\"consumables\" must hold exactly {ItemConstants.ConsumableTypeCount} entries (Medkit, ShieldCell).";

        var result = new ConsumableDefinition[ItemConstants.ConsumableTypeCount];
        for (int i = 0; i < list.Count; i++)
        {
            ConsumableJson? c = list[i];
            if (c == null) return $"consumables[{i}]: entry is null.";
            ConsumableType type = c.Id switch
            {
                "Medkit" => ConsumableType.Medkit,
                "ShieldCell" => ConsumableType.ShieldCell,
                _ => ConsumableType.None,
            };
            if (type == ConsumableType.None) return $"consumables[{i}]: id must be Medkit or ShieldCell.";
            if (result[(int)type - 1] != null) return $"consumables[{i}]: duplicate id {c.Id}.";
            string? nameProblem = CheckName(c.Name);
            if (nameProblem != null) return $"consumables[{i}]: {nameProblem}";
            if (!DataJson.TryTicks(c.UseSeconds, simHz, out ushort useTicks))
                return $"consumables[{i}]: useSeconds must be positive and finite (at most 65535 ticks).";
            if (c.Heal < 0 || c.Heal > ushort.MaxValue || c.Shield < 0 || c.Shield > ushort.MaxValue)
                return $"consumables[{i}]: heal and shield must be 0-65535.";
            if (c.Heal == 0 && c.Shield == 0) return $"consumables[{i}]: heal or shield must be above 0.";
            if (c.MaxStack < 1 || c.MaxStack > byte.MaxValue) return $"consumables[{i}]: maxStack must be 1-255.";
            foreach (ConsumableDefinition? other in result)
            {
                if (other != null && other.Name == c.Name) return $"consumables[{i}]: duplicate name \"{c.Name}\".";
            }
            result[(int)type - 1] = new ConsumableDefinition(type, c.Name!, useTicks, (ushort)c.Heal, (ushort)c.Shield, (byte)c.MaxStack);
        }
        consumables = result;
        return null;
    }

    // 기능: 데이터 항목 이름이 비어 있지 않고 UTF-8 바이트 제한 안인지 확인한다.
    // 입력: name - 확인할 이름.
    // 출력: 문제가 없으면 null, 있으면 오류 메시지.
    private static string? CheckName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "name is required.";
        if (Encoding.UTF8.GetByteCount(name) > ItemConstants.MaxNameBytes)
            return $"name must be at most {ItemConstants.MaxNameBytes} UTF-8 bytes.";
        return null;
    }

    private sealed class ItemsJson
    {
        public List<RarityJson?>? Rarities { get; set; }
        public List<AmmoJson?>? Ammo { get; set; }
        public List<ConsumableJson?>? Consumables { get; set; }
    }

    // Missing numbers stay 0 and fail validation, so every required field must be written.
    private sealed class RarityJson
    {
        public string? Name { get; set; }
        public double DamageMultiplier { get; set; }
    }

    private sealed class AmmoJson
    {
        public string? Type { get; set; }
        public string? Name { get; set; }
        public int PickupAmount { get; set; }
        public int Max { get; set; }
    }

    private sealed class ConsumableJson
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public double UseSeconds { get; set; }
        public int Heal { get; set; }
        public int Shield { get; set; }
        public int MaxStack { get; set; }
    }
}
