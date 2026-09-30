using System;
using System.IO;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// All data files of the server (D2), loaded and cross-checked once at startup. Immutable afterwards.
public sealed class GameData
{
    public const string WeaponsFile = "weapons.json";
    public const string ItemsFile = "items.json";
    public const string LootFile = "loot.json";

    public GameData(WeaponCatalog weapons, ItemCatalog items, LootTable loot)
    {
        Weapons = weapons ?? throw new ArgumentNullException(nameof(weapons));
        Items = items ?? throw new ArgumentNullException(nameof(items));
        Loot = loot ?? throw new ArgumentNullException(nameof(loot));
        if (weapons.SimHz != items.SimHz)
            throw new ArgumentException($"Weapons were built for SimHz {weapons.SimHz}, items for {items.SimHz}.", nameof(items));
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
    public int SimHz => Weapons.SimHz;

    // The files are copied next to the server executable. A missing or invalid file throws, so the host
    // refuses to start (same as an invalid ServerOptions value).
    public static GameData LoadDirectory(string directory, int simHz)
    {
        var weapons = WeaponCatalog.LoadFile(Path.Combine(directory, WeaponsFile), simHz);
        var items = ItemCatalog.LoadFile(Path.Combine(directory, ItemsFile), simHz);
        var loot = LootTable.LoadFile(Path.Combine(directory, LootFile), items);
        try
        {
            return new GameData(weapons, items, loot);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("Invalid game data: " + ex.Message, ex);
        }
    }
}
