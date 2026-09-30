using System;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Items;

public readonly record struct LoadoutWeapon(byte WeaponId, byte Rarity);

// What a player has at join and after every respawn (D1). Production uses Empty: no weapon, Shield 0,
// Health 100. Tests inject a loadout so combat tests do not depend on picking anything up.
public sealed class StartingLoadout
{
    public static readonly StartingLoadout Empty = new();

    public int Shield { get; init; }
    public LoadoutWeapon[] Weapons { get; init; } = Array.Empty<LoadoutWeapon>();   // slots 0, 1, 2 with full magazines
    public int LightAmmo { get; init; }
    public int MediumAmmo { get; init; }
    public int HeavyAmmo { get; init; }
    public int Medkits { get; init; }
    public int ShieldCells { get; init; }

    // Null when the loadout fits the data and the inventory limits.
    public string? Validate(GameData data)
    {
        if (Shield < 0 || Shield > CombatRules.MaxShield) return $"Shield must be 0-{CombatRules.MaxShield}.";
        if (Weapons.Length > Inventory.SlotCount) return $"At most {Inventory.SlotCount} weapons.";
        foreach (LoadoutWeapon w in Weapons)
        {
            if (!data.Weapons.TryGetById(w.WeaponId, out _)) return $"Unknown weapon id {w.WeaponId}.";
            if (w.Rarity >= ItemConstants.RarityCount) return $"Rarity must be 0-{ItemConstants.RarityCount - 1}.";
        }
        if (LightAmmo < 0 || LightAmmo > data.Items.Ammo(AmmoType.Light).Max ||
            MediumAmmo < 0 || MediumAmmo > data.Items.Ammo(AmmoType.Medium).Max ||
            HeavyAmmo < 0 || HeavyAmmo > data.Items.Ammo(AmmoType.Heavy).Max)
            return "Ammo must be 0 to the max of its type.";
        if (Medkits < 0 || Medkits > data.Items.Consumable(ConsumableType.Medkit).MaxStack ||
            ShieldCells < 0 || ShieldCells > data.Items.Consumable(ConsumableType.ShieldCell).MaxStack)
            return "Medkits and ShieldCells must be 0 to their maxStack.";
        return null;
    }

    // Replaces the whole inventory (Clear marks it changed, so the owner hears of it).
    public void ApplyTo(Inventory inventory, WeaponCatalog weapons)
    {
        inventory.Clear();
        for (int i = 0; i < Weapons.Length; i++)
        {
            weapons.TryGetById(Weapons[i].WeaponId, out WeaponDefinition weapon);
            inventory.Slots[i] = new HeldWeapon { Weapon = weapon, Rarity = Weapons[i].Rarity, MagAmmo = weapon.MagazineSize };
        }
        inventory.SetAmmo(AmmoType.Light, LightAmmo);
        inventory.SetAmmo(AmmoType.Medium, MediumAmmo);
        inventory.SetAmmo(AmmoType.Heavy, HeavyAmmo);
        inventory.Medkits = Medkits;
        inventory.ShieldCells = ShieldCells;
    }
}
