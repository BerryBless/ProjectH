using System;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Items;

// One weapon slot. Weapon null = empty slot.
public struct HeldWeapon
{
    public WeaponDefinition? Weapon;
    public byte Rarity;
    public int MagAmmo;
    // Per slot (Phase 3 D14): switching slots never skips a weapon's fire interval.
    public uint NextFireTick;

    public bool IsEmpty => Weapon == null;
}

// A player's inventory (D10), owned by the server: 3 weapon slots, the current slot, ammo reserves per
// type, Medkits and Shield Cells. Utility has no slot content yet (Projectile phase). Game loop thread only;
// fixed-size arrays, so nothing here allocates after construction.
public sealed class Inventory
{
    public const int SlotCount = ItemConstants.WeaponSlotCount;

    public readonly HeldWeapon[] Slots = new HeldWeapon[SlotCount];
    private readonly int[] _ammo = new int[ItemConstants.AmmoTypeCount];   // index = AmmoType - 1

    public int CurrentSlot;
    public int Medkits;
    public int ShieldCells;
    // The latest NextFireTick of any weapon this player dropped. A weapon picked up cannot fire before it,
    // so dropping a weapon and picking it up again (maybe into another slot) never skips its fire interval.
    public uint DroppedFireLockTick;
    // The heal being used (D11) and the tick its channel ends; None when nothing is being used.
    public ConsumableType Using;
    public uint UseEndTick;
    // Set by every change the owner must hear about (not by shots: the snapshot carries the magazine).
    // Match sends one InventoryState at the end of a tick in which it was set, then clears it (D14).
    public bool Changed;

    public ref HeldWeapon Current => ref Slots[CurrentSlot];

    public int GetAmmo(AmmoType type) => _ammo[(int)type - 1];

    public void SetAmmo(AmmoType type, int value) => _ammo[(int)type - 1] = value;

    public void Clear()
    {
        Array.Clear(Slots);
        Array.Clear(_ammo);
        CurrentSlot = 0;
        Medkits = 0;
        ShieldCells = 0;
        DroppedFireLockTick = 0;
        Using = ConsumableType.None;
        UseEndTick = 0;
        Changed = true;
    }

    // now = the current server tick; the client counts the rest of a running use down from it.
    public InventoryState ToWire(uint now)
    {
        var state = new InventoryState
        {
            Using = Using,
            UseRemainingTicks = Using == ConsumableType.None ? (ushort)0
                : (ushort)Math.Clamp(UseEndTick > now ? UseEndTick - now : 1u, 1u, ushort.MaxValue),
            CurrentSlot = (byte)CurrentSlot,
            LightAmmo = (ushort)GetAmmo(AmmoType.Light),
            MediumAmmo = (ushort)GetAmmo(AmmoType.Medium),
            HeavyAmmo = (ushort)GetAmmo(AmmoType.Heavy),
            Medkits = (byte)Medkits,
            ShieldCells = (byte)ShieldCells,
        };
        for (int i = 0; i < SlotCount; i++)
        {
            ref HeldWeapon held = ref Slots[i];
            if (held.IsEmpty) continue;
            state.SetSlot(i, new InventorySlotState { WeaponId = held.Weapon!.Id, Rarity = held.Rarity, MagAmmo = (byte)held.MagAmmo });
        }
        return state;
    }
}
