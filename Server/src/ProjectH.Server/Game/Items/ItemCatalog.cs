using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Items;

public sealed class AmmoDefinition
{
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

    public float DamageMultiplier(int rarity) => _damageMultipliers[rarity];
    public string RarityName(int rarity) => _rarityNames[rarity];
    public AmmoDefinition Ammo(AmmoType type) => _ammo[(int)type - 1];
    public ConsumableDefinition Consumable(ConsumableType type) => _consumables[(int)type - 1];

    // Index of a rarity name, or -1 (loot.json refers to rarities by name).
    public int RarityIndex(string name)
    {
        for (int i = 0; i < _rarityNames.Length; i++)
        {
            if (_rarityNames[i] == name) return i;
        }
        return -1;
    }

    public static ItemCatalog LoadFile(string path, int simHz)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"Item data not found: {path}");
        if (!TryParse(File.ReadAllText(path), simHz, out var catalog, out string? error))
            throw new InvalidOperationException($"Invalid item data {path}: {error}");
        return catalog!;
    }

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

    // 기능: 탄 종류 이름을 AmmoType으로 바꾼다(enum 이름만: "1"이나 "None"은 탄 종류가 아니다. Phase 17: Shells, Rockets).
    // 입력: text - JSON의 이름.
    // 출력: 아는 이름이면 true와 종류.
    public static bool TryParseAmmoType(string? text, out AmmoType type)
    {
        type = AmmoType.None;
        switch (text)
        {
            case "Light": type = AmmoType.Light; return true;
            case "Medium": type = AmmoType.Medium; return true;
            case "Heavy": type = AmmoType.Heavy; return true;
            case "Shells": type = AmmoType.Shells; return true;
            case "Rockets": type = AmmoType.Rockets; return true;
            default: return false;
        }
    }

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

    // 기능: 탄약 목록을 읽는다(Phase 17: Light·Medium·Heavy·Shells·Rockets 각 1개).
    // 입력: list - JSON 항목들, ammo - 결과(색인 = 종류 - 1).
    // 출력: 맞으면 null, 틀리면 이유.
    private static string? ParseAmmo(List<AmmoJson?>? list, out AmmoDefinition[]? ammo)
    {
        ammo = null;
        if (list == null || list.Count != ItemConstants.AmmoTypeCount)
            return $"\"ammo\" must hold exactly {ItemConstants.AmmoTypeCount} entries (Light, Medium, Heavy, Shells, Rockets).";

        var result = new AmmoDefinition[ItemConstants.AmmoTypeCount];
        for (int i = 0; i < list.Count; i++)
        {
            AmmoJson? a = list[i];
            if (a == null) return $"ammo[{i}]: entry is null.";
            if (!TryParseAmmoType(a.Type, out AmmoType type)) return $"ammo[{i}]: type must be Light, Medium, Heavy, Shells or Rockets.";
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

    // 기능: 소모품 목록을 읽는다(Phase 17 D9: Grenade는 채널·회복 없이 maxStack만 갖는다).
    // 입력: list - JSON 항목들, simHz - Tick 속도, consumables - 결과(색인 = 종류 - 1).
    // 출력: 맞으면 null, 틀리면 이유.
    private static string? ParseConsumables(List<ConsumableJson?>? list, int simHz, out ConsumableDefinition[]? consumables)
    {
        consumables = null;
        if (list == null || list.Count != ItemConstants.ConsumableTypeCount)
            return $"\"consumables\" must hold exactly {ItemConstants.ConsumableTypeCount} entries (Medkit, ShieldCell, Grenade).";

        var result = new ConsumableDefinition[ItemConstants.ConsumableTypeCount];
        for (int i = 0; i < list.Count; i++)
        {
            ConsumableJson? c = list[i];
            if (c == null) return $"consumables[{i}]: entry is null.";
            ConsumableType type = c.Id switch
            {
                "Medkit" => ConsumableType.Medkit,
                "ShieldCell" => ConsumableType.ShieldCell,
                "Grenade" => ConsumableType.Grenade,
                _ => ConsumableType.None,
            };
            if (type == ConsumableType.None) return $"consumables[{i}]: id must be Medkit, ShieldCell or Grenade.";
            if (result[(int)type - 1] != null) return $"consumables[{i}]: duplicate id {c.Id}.";
            string? nameProblem = CheckName(c.Name);
            if (nameProblem != null) return $"consumables[{i}]: {nameProblem}";
            ushort useTicks = 0;
            if (type == ConsumableType.Grenade)
            {
                // Phase 17 D9: thrown (6), never used over a channel: no use time, nothing healed (the wire says 0).
                if (c.UseSeconds != 0 || c.Heal != 0 || c.Shield != 0) return $"consumables[{i}]: a Grenade has no useSeconds, heal or shield.";
            }
            else
            {
                if (!DataJson.TryTicks(c.UseSeconds, simHz, out useTicks))
                    return $"consumables[{i}]: useSeconds must be positive and finite (at most 65535 ticks).";
                if (c.Heal < 0 || c.Heal > ushort.MaxValue || c.Shield < 0 || c.Shield > ushort.MaxValue)
                    return $"consumables[{i}]: heal and shield must be 0-65535.";
                if (c.Heal == 0 && c.Shield == 0) return $"consumables[{i}]: heal or shield must be above 0.";
            }
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
