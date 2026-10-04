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

    // 기능: 시작 장비가 게임 데이터와 인벤토리 한도(보호막, 슬롯 수, 무기 ID, 등급, 탄약·회복 최대치)에 맞는지 검증한다.
    // 입력: data - 무기·아이템 카탈로그를 가진 게임 데이터.
    // 출력: 문제가 없으면 null, 있으면 첫 번째 오류 메시지.
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

    // 기능: 인벤토리를 비우고 시작 장비(무기는 탄창 가득, 탄약, 회복 아이템)로 채운다. Validate를 통과한 장비를 전제로 한다.
    // 입력: inventory - 채울 인벤토리, weapons - 무기 ID로 정의를 찾을 무기 카탈로그.
    // 출력: 반환값 없음. 인벤토리 전체가 시작 장비로 바뀌고 Changed가 설정된다.
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
