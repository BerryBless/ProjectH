using System;
using System.IO;
using ProjectH.Server.Game.Build;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Server.Game.Map;
using ProjectH.Server.Game.Squad;
using ProjectH.Server.Game.Vehicles;
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
    public const string SquadFile = SquadCatalog.FileName;
    public const string MapFile = MapCatalog.FileName;
    public const string VehiclesFile = VehicleCatalog.FileName;

    // Phase 13: building null = the shipped numbers (BuildingCatalog.Default), so tests need no file.
    // 기능: 데이터 파일들을 묶고 서로 맞는지 검사한다(SimHz, Loot Table 참조와 바닥 Loot Point 표의 항목 1개 이상(Phase 16 리뷰), Phase 17 표의 무기 id,
    //   Phase 14 재투입 장비, Phase 15 지도 수치, Phase 19 차량 수치).
    // 입력: weapons·items·loot·zones - 필수 데이터, building - 건설 수치(null = 기본값), squad - 분대 수치(null = 기본값, Phase 14 D11),
    //   map - Ping·Waypoint 수치(null = 기본값, Phase 15 D9), vehicles - 차량 수치(null = 기본값, Phase 19 D12).
    // 출력: 검증된 GameData. 맞지 않으면 ArgumentException.
    public GameData(WeaponCatalog weapons, ItemCatalog items, LootTable loot, ZoneData zones, BuildingCatalog? building = null,
        SquadCatalog? squad = null, MapCatalog? map = null, VehicleCatalog? vehicles = null)
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
            int table = loot.TableIndex(point.Table);
            if (table < 0)
                throw new ArgumentException($"loot.json has no table \"{point.Table}\" used by LootPoints.", nameof(loot));
            // Phase 16 review (Low): a floor point rolls one weighted entry (LootTable.Roll); a table of guaranteed kinds only
            // would index an empty entry list at every match start.
            if (loot.EntryCount(table) == 0)
                throw new ArgumentException($"loot.json table \"{point.Table}\" is used by LootPoints and needs at least one entry.", nameof(loot));
        }
        // Phase 17 D13: every weapon a loot table names exists.
        string? lootWeaponError = loot.ValidateWeapons(weapons);
        if (lootWeaponError != null) throw new ArgumentException(lootWeaponError, nameof(loot));
        Squad = squad ?? SquadCatalog.Default(weapons.SimHz);
        if (Squad.SimHz != weapons.SimHz)
            throw new ArgumentException($"Weapons were built for SimHz {weapons.SimHz}, squad data for {Squad.SimHz}.", nameof(squad));
        // Phase 14 D10: the reboot loadout names weapons and ammo of these catalogs.
        string? loadoutError = Squad.RebootLoadout.Validate(this);
        if (loadoutError != null) throw new ArgumentException("squad.json rebootLoadout: " + loadoutError, nameof(squad));
        Map = map ?? MapCatalog.Default(weapons.SimHz);
        if (Map.SimHz != weapons.SimHz)
            throw new ArgumentException($"Weapons were built for SimHz {weapons.SimHz}, map data for {Map.SimHz}.", nameof(map));
        Vehicles = vehicles ?? VehicleCatalog.Default(weapons.SimHz);
        if (Vehicles.SimHz != weapons.SimHz)
            throw new ArgumentException($"Weapons were built for SimHz {weapons.SimHz}, vehicle data for {Vehicles.SimHz}.", nameof(vehicles));
    }

    public WeaponCatalog Weapons { get; }
    public ItemCatalog Items { get; }
    public LootTable Loot { get; }
    // Phase 5 (D6): the safe zone phases.
    public ZoneData Zones { get; }
    // Phase 13 D4: materials, resources, the harvest tool, harvestables, placement limits, the interest window.
    public BuildingCatalog Building { get; }
    // Phase 14 D11: knock-down, revive and reboot numbers.
    public SquadCatalog Squad { get; }
    // Phase 15 D9: ping lifetimes and limits, the ping checks' ranges and the MapMarker receive rate.
    public MapCatalog Map { get; }
    // Phase 19 D12: vehicle health, impact and run-over damage, the wreck time and the interest range.
    public VehicleCatalog Vehicles { get; }
    public int SimHz => Weapons.SimHz;

    // 기능: 서버 실행 파일 옆의 데이터 파일을 모두 읽고 GameData로 묶는다(Phase 15: map.json 포함, Phase 16: loot.json에 Container 표가 모두 있어야 한다,
    //   Phase 17: weapons.json에 수류탄 정의가 있어야 한다, Phase 19: vehicles.json 포함).
    // 입력: directory - 파일이 있는 폴더, simHz - 서버 Tick 속도.
    // 출력: 검증된 GameData. 파일이 없거나 틀리면 InvalidOperationException(서버가 시작하지 않는다).
    // The files are copied next to the server executable. A missing or invalid file throws, so the host
    // refuses to start (same as an invalid ServerOptions value).
    public static GameData LoadDirectory(string directory, int simHz)
    {
        var weapons = WeaponCatalog.LoadFile(Path.Combine(directory, WeaponsFile), simHz);
        var items = ItemCatalog.LoadFile(Path.Combine(directory, ItemsFile), simHz);
        var loot = LootTable.LoadFile(Path.Combine(directory, LootFile), items);
        var zones = ZoneData.LoadFile(Path.Combine(directory, ZonesFile), simHz);
        var building = BuildingCatalog.LoadFile(Path.Combine(directory, BuildingFile), simHz);
        var squad = SquadCatalog.LoadFile(Path.Combine(directory, SquadFile), simHz);
        var map = MapCatalog.LoadFile(Path.Combine(directory, MapFile), simHz);
        var vehicles = VehicleCatalog.LoadFile(Path.Combine(directory, VehiclesFile), simHz);
        // Phase 16 D5: the shipped data must fill the containers and supply drops (tests may leave them out: no containers then).
        string? containerError = loot.ValidateContainerTables();
        if (containerError != null) throw new InvalidOperationException("Invalid game data: " + containerError);
        // Phase 17 D9: the shipped weapons must define the grenade (tests may leave it out: no grenade can be thrown then).
        string? grenadeError = weapons.RequireGrenade();
        if (grenadeError != null) throw new InvalidOperationException("Invalid game data: " + grenadeError);
        try
        {
            return new GameData(weapons, items, loot, zones, building, squad, map, vehicles);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("Invalid game data: " + ex.Message, ex);
        }
    }
}
