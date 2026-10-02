using System;
using System.IO;
using ProjectH.Server.Game.Build;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Server.Game.Zone;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// All data files of the server (D2), loaded and cross-checked once at startup. Immutable afterwards.
public sealed class GameData
{
    public const string WeaponsFile = "weapons.json";
    public const string ItemsFile = "items.json";
    public const string LootFile = "loot.json";
    public const string ZonesFile = "zones.json";
    public const string BuildingFile = BuildingCatalog.FileName;

    // Phase 13: building null = the shipped numbers (BuildingCatalog.Default), so tests need no file.
    public GameData(WeaponCatalog weapons, ItemCatalog items, LootTable loot, ZoneData zones, BuildingCatalog? building = null)
    {
        Weapons = weapons ?? throw new ArgumentNullException(nameof(weapons));
        Items = items ?? throw new ArgumentNullException(nameof(items));
        Loot = loot ?? throw new ArgumentNullException(nameof(loot));
        Zones = zones ?? throw new ArgumentNullException(nameof(zones));
        Building = building ?? BuildingCatalog.Default(weapons.SimHz);
        if (Building.SimHz != weapons.SimHz)
            throw new ArgumentException($"Weapons were built for SimHz {weapons.SimHz}, building data for {Building.SimHz}.", nameof(building));
        if (weapons.SimHz != items.SimHz)
            throw new ArgumentException($"Weapons were built for SimHz {weapons.SimHz}, items for {items.SimHz}.", nameof(items));
        if (weapons.SimHz != zones.SimHz)
            throw new ArgumentException($"Weapons were built for SimHz {weapons.SimHz}, zones for {zones.SimHz}.", nameof(zones));
        // Every table the map's spawn points name must exist (D5, "all references exist").
        foreach (LootPoint point in LootPoints.All)
        {
            if (loot.TableIndex(point.Table) < 0)
                throw new ArgumentException($"loot.json has no table \"{point.Table}\" used by LootPoints.", nameof(loot));
        }
    }

    public WeaponCatalog Weapons { get; }
    public ItemCatalog Items { get; }
    public LootTable Loot { get; }
    // Phase 5 (D6): the safe zone phases.
    public ZoneData Zones { get; }
    // Phase 13 D4: materials, resources, the harvest tool, harvestables, placement limits, the interest window.
    public BuildingCatalog Building { get; }
    public int SimHz => Weapons.SimHz;

    // The files are copied next to the server executable. A missing or invalid file throws, so the host
    // refuses to start (same as an invalid ServerOptions value).
    public static GameData LoadDirectory(string directory, int simHz)
    {
        var weapons = WeaponCatalog.LoadFile(Path.Combine(directory, WeaponsFile), simHz);
        var items = ItemCatalog.LoadFile(Path.Combine(directory, ItemsFile), simHz);
        var loot = LootTable.LoadFile(Path.Combine(directory, LootFile), items);
        var zones = ZoneData.LoadFile(Path.Combine(directory, ZonesFile), simHz);
        var building = BuildingCatalog.LoadFile(Path.Combine(directory, BuildingFile), simHz);
        try
        {
            return new GameData(weapons, items, loot, zones, building);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("Invalid game data: " + ex.Message, ex);
        }
    }
}
