using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Combat;

// Weapon numbers live in data, not code (D4, request §16). Loaded and validated once at startup; a
// bad file stops the server the same way a bad ServerOptions value does. Immutable afterwards, so the
// game loop reads it without locks.
public sealed class WeaponCatalog
{
    private readonly WeaponDefinition[] _weapons;
    // Weapon id -> index in _weapons, -1 when unknown. Ids are bytes, so 256 entries cover every id.
    private readonly int[] _indexById = new int[256];

    private WeaponCatalog(WeaponDefinition[] weapons, int simHz)
    {
        _weapons = weapons;
        SimHz = simHz;
        Array.Fill(_indexById, -1);
        WireInfos = new WeaponInfo[weapons.Length];
        for (int i = 0; i < weapons.Length; i++)
        {
            _indexById[weapons[i].Id] = i;
            WireInfos[i] = weapons[i].ToWire();
        }
    }

    public int Count => _weapons.Length;
    // Tick values were converted with this rate; Match refuses a catalog built for another SimHz.
    public int SimHz { get; }
    // Built once for the WeaponCatalog packet sent at every join.
    public WeaponInfo[] WireInfos { get; }

    public WeaponDefinition this[int index] => _weapons[index];

    // Weapon items and inventory slots store the weapon id (Phase 4).
    public bool TryGetById(byte id, out WeaponDefinition weapon)
    {
        int index = _indexById[id];
        weapon = index >= 0 ? _weapons[index] : null!;
        return index >= 0;
    }

    public static WeaponCatalog LoadFile(string path, int simHz)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"Weapon data not found: {path}");
        string json = File.ReadAllText(path);
        if (!TryParse(json, simHz, out var catalog, out string? error))
            throw new InvalidOperationException($"Invalid weapon data {path}: {error}");
        return catalog!;
    }

    public static bool TryParse(string json, int simHz, out WeaponCatalog? catalog, out string? error)
    {
        catalog = null;
        if (simHz < 1)
        {
            error = "SimHz must be positive.";
            return false;
        }

        if (!DataJson.TryDeserialize(json, out CatalogJson? root, out error)) return false;

        List<WeaponJson?>? list = root!.Weapons;
        if (list == null || list.Count < 1 || list.Count > WeaponCatalogPacket.MaxWeapons)
        {
            error = $"\"weapons\" must hold 1-{WeaponCatalogPacket.MaxWeapons} entries.";
            return false;
        }

        var weapons = new WeaponDefinition[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            string? problem = Validate(list[i], simHz, out WeaponDefinition? weapon);
            if (problem != null)
            {
                error = $"weapons[{i}]: {problem}";
                return false;
            }
            weapons[i] = weapon!;
            for (int j = 0; j < i; j++)
            {
                if (weapons[j].Id == weapons[i].Id)
                {
                    error = $"weapons[{i}]: duplicate id {weapons[i].Id}.";
                    return false;
                }
                if (weapons[j].Name == weapons[i].Name)
                {
                    error = $"weapons[{i}]: duplicate name \"{weapons[i].Name}\".";
                    return false;
                }
            }
        }

        catalog = new WeaponCatalog(weapons, simHz);
        error = null;
        return true;
    }

    private static string? Validate(WeaponJson? w, int simHz, out WeaponDefinition? weapon)
    {
        weapon = null;
        if (w == null) return "entry is null.";
        if (w.Id < 1 || w.Id > byte.MaxValue) return "id must be 1-255.";
        if (string.IsNullOrWhiteSpace(w.Name)) return "name is required.";
        if (Encoding.UTF8.GetByteCount(w.Name) > WeaponCatalogPacket.MaxNameBytes)
            return $"name must be at most {WeaponCatalogPacket.MaxNameBytes} UTF-8 bytes.";
        if (w.Damage < 1 || w.Damage > ushort.MaxValue) return "damage must be 1-65535.";
        if (w.MagazineSize < 1 || w.MagazineSize > byte.MaxValue) return "magazineSize must be 1-255.";
        if (!DataJson.TryTicks(w.FireIntervalSeconds, simHz, out ushort fireTicks)) return "fireIntervalSeconds must be positive and finite (at most 65535 ticks).";
        if (!DataJson.TryTicks(w.ReloadSeconds, simHz, out ushort reloadTicks)) return "reloadSeconds must be positive and finite (at most 65535 ticks).";
        float range = (float)w.Range;
        if (!float.IsFinite(range) || range <= 0f) return "range must be positive and finite.";
        float spread = (float)w.Spread;
        float recoil = (float)w.Recoil;
        if (!float.IsFinite(spread) || spread < 0f) return "spread must be 0 or more.";
        if (!float.IsFinite(recoil) || recoil < 0f) return "recoil must be 0 or more.";
        if (!ItemCatalog.TryParseAmmoType(w.AmmoType, out AmmoType ammoType)) return "ammoType must be Light, Medium or Heavy.";

        weapon = new WeaponDefinition((byte)w.Id, w.Name, (ushort)w.Damage, fireTicks, (byte)w.MagazineSize,
            reloadTicks, range, w.Automatic, spread, recoil, ammoType);
        return null;
    }

    private sealed class CatalogJson
    {
        public List<WeaponJson?>? Weapons { get; set; }
    }

    // Missing numbers stay 0 and fail validation, so every required field must be written.
    private sealed class WeaponJson
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public int Damage { get; set; }
        public double FireIntervalSeconds { get; set; }
        public int MagazineSize { get; set; }
        public double ReloadSeconds { get; set; }
        public double Range { get; set; }
        public bool Automatic { get; set; }
        public double Spread { get; set; }
        public double Recoil { get; set; }
        public string? AmmoType { get; set; }
    }
}
