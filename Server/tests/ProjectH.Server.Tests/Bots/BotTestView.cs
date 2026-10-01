using System.Numerics;
using ProjectH.Bots;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Tests.Bots;

// A hand-made BotView for brain tests: joined, alive, at the origin of the plaza, two weapons in the catalog
// (an automatic with Medium rounds and a semi-automatic with Heavy rounds), nothing held.
internal static class BotTestView
{
    public const byte AutoId = 1;
    public const byte SemiId = 2;

    public static BotView Create(Vector3? position = null)
    {
        var view = new BotView
        {
            Joined = true,
            MyId = 1,
            HasSnapshot = true,
            Alive = true,
            MyPosition = position ?? Vector3.Zero,
            ServerTick = 1000,
            Self = new SnapshotSelf { Health = 100, Shield = 0 },
            HasInventory = true,
            Weapons = new[]
            {
                new WeaponInfo { WeaponId = AutoId, Name = "Auto", Damage = 20, FireIntervalTicks = 3, MagazineSize = 30, ReloadTicks = 60, Range = 100f, Automatic = true, AmmoType = AmmoType.Medium },
                new WeaponInfo { WeaponId = SemiId, Name = "Semi", Damage = 90, FireIntervalTicks = 30, MagazineSize = 5, ReloadTicks = 75, Range = 300f, Automatic = false, AmmoType = AmmoType.Heavy },
            },
            Catalog = new ItemCatalogData
            {
                Rarities = Array.Empty<RarityInfo>(),
                Ammo = new[]
                {
                    new AmmoInfo { Type = AmmoType.Light, Name = "L", Max = 180 },
                    new AmmoInfo { Type = AmmoType.Medium, Name = "M", Max = 150 },
                    new AmmoInfo { Type = AmmoType.Heavy, Name = "H", Max = 30 },
                },
                Consumables = new[]
                {
                    new ConsumableInfo { Type = ConsumableType.Medkit, Name = "Medkit", MaxStack = 3 },
                    new ConsumableInfo { Type = ConsumableType.ShieldCell, Name = "Cell", MaxStack = 6 },
                },
            },
        };
        return view;
    }

    // Slot 0 holds the automatic with a full magazine (the snapshot self block carries the current magazine).
    public static BotView Armed(Vector3? position = null)
    {
        BotView view = Create(position);
        view.Inventory.Slot0 = new InventorySlotState { WeaponId = AutoId, MagAmmo = 30 };
        view.Inventory.CurrentSlot = 0;
        view.Self.Ammo = 30;
        return view;
    }

    public static void AddOther(BotView view, ushort id, Vector3 position, bool alive = true)
    {
        view.Others[view.OtherCount++] = new SnapshotEntity { EntityId = id, Position = position, Flags = alive ? SnapshotEntity.AliveFlag : (byte)0 };
    }

    public static void AddItem(BotView view, ushort id, ItemKind kind, byte defId, Vector3 position, ushort amount = 1)
    {
        view.ApplyItem(new WorldItemData { ItemId = id, Kind = kind, DefId = defId, Amount = amount, Position = position });
    }

    // Ticks the brain once per 1/30 s from `start`, returning the commands (a dead tick gives default).
    public static InputCommand[] Run(BotBrain brain, BotView view, float start, int ticks)
    {
        var commands = new InputCommand[ticks];
        for (int i = 0; i < ticks; i++) brain.Tick(view, start + i / 30f, out commands[i]);
        return commands;
    }

    public static float AngleBetween(float a, float b)
    {
        float d = MathF.Abs((a - b) % 360f);
        return d > 180f ? 360f - d : d;
    }
}
