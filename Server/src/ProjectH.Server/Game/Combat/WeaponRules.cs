using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Combat;

// Magazine, reload, fire interval and weapon switch (D14), enforced in server ticks. Pure state
// changes on PlayerEntity: Match decides when to call them and does the ray and the packets.
// The client copies these rules for presentation (Client WeaponState); keep the two in step.
public static class WeaponRules
{
    // Full magazines, slot 0, nothing pending. Used at join and at respawn.
    public static void Equip(PlayerEntity player, WeaponCatalog weapons)
    {
        player.WeaponSlot = 0;
        for (int slot = 0; slot < WeaponCatalog.SlotCount; slot++)
        {
            player.Ammo[slot] = slot < weapons.LoadoutCount ? weapons[slot].MagazineSize : 0;
            player.NextFireTick[slot] = 0;
        }
        player.Reloading = false;
        player.ReloadEndTick = 0;
        player.FireHeld = false;
    }

    // Runs every tick for a living player, whether or not an input arrived.
    public static void UpdateReload(PlayerEntity player, WeaponCatalog weapons, uint now)
    {
        if (player.Reloading && now >= player.ReloadEndTick)
        {
            player.Reloading = false;
            player.Ammo[player.WeaponSlot] = weapons[player.WeaponSlot].MagazineSize;
        }
    }

    // One input the client sent, in the order switch -> reload -> fire. Returns true when it fires a
    // shot; ammo and the fire interval are already spent then. aimValid false (non-finite aim) is no shot.
    public static bool Apply(PlayerEntity player, WeaponCatalog weapons, InputButtons buttons, bool aimValid, uint now)
    {
        bool slot1 = (buttons & InputButtons.Slot1) != 0;
        bool slot2 = (buttons & InputButtons.Slot2) != 0;
        if (slot1 != slot2)   // both bits at once is contradictory and ignored
        {
            int target = slot1 ? 0 : 1;
            if (target < weapons.LoadoutCount && target != player.WeaponSlot)
            {
                player.WeaponSlot = target;
                player.Reloading = false;   // a reload belongs to the weapon being put away
            }
        }

        int current = player.WeaponSlot;
        WeaponDefinition weapon = weapons[current];

        if ((buttons & InputButtons.Reload) != 0 && !player.Reloading && player.Ammo[current] < weapon.MagazineSize)
            StartReload(player, weapon, now);

        bool fireHeld = (buttons & InputButtons.Fire) != 0;
        bool trigger = fireHeld && (weapon.Automatic || !player.FireHeld);
        player.FireHeld = fireHeld;
        if (!trigger || !aimValid || player.Reloading || now < player.NextFireTick[current]) return false;

        if (player.Ammo[current] == 0)
        {
            StartReload(player, weapon, now);
            return false;
        }

        player.Ammo[current]--;
        player.NextFireTick[current] = now + weapon.FireIntervalTicks;
        if (player.Ammo[current] == 0) StartReload(player, weapon, now);
        return true;
    }

    private static void StartReload(PlayerEntity player, WeaponDefinition weapon, uint now)
    {
        player.Reloading = true;
        player.ReloadEndTick = now + weapon.ReloadTicks;
    }
}
