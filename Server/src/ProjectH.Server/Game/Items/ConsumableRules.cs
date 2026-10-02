using System;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Items;

// D11: Medkit (4) and Shield Cell (5) are used over a channel time, decided in server ticks. Moving does
// not interrupt; firing, switching slots or tools (Phase 13 final review B9), dropping or pressing the other heal does,
// and so does an accepted placement (Match.TryBuild). Pure state changes on
// PlayerEntity; Match calls them in the spec §2 order (cancel = step 2, start = step 8, finish = step 9).
public static class ConsumableRules
{
    private const InputButtons Interrupts = InputButtons.Fire | InputButtons.Slot1 | InputButtons.Slot2 |
                                            InputButtons.Slot3 | InputButtons.Drop | InputButtons.ToolHarvest | InputButtons.ToolBuild;

    // Step 2, real inputs only. Returns true when a running use was cancelled.
    public static bool CancelIfInterrupted(PlayerEntity player, InputButtons buttons)
    {
        Inventory inventory = player.Inventory;
        if (inventory.Using == ConsumableType.None) return false;
        InputButtons other = inventory.Using == ConsumableType.Medkit ? InputButtons.UseShieldCell : InputButtons.UseMedkit;
        if ((buttons & (Interrupts | other)) == 0) return false;
        Cancel(inventory);
        return true;
    }

    // Step 8, real inputs only: exactly one heal button, none running, one in the inventory, and not
    // already at the maximum it restores. Pressing the running heal again changes nothing.
    public static bool TryStart(PlayerEntity player, ItemCatalog items, InputButtons buttons, uint now)
    {
        ConsumableType type;
        switch (buttons & (InputButtons.UseMedkit | InputButtons.UseShieldCell))
        {
            case InputButtons.UseMedkit: type = ConsumableType.Medkit; break;
            case InputButtons.UseShieldCell: type = ConsumableType.ShieldCell; break;
            default: return false;
        }

        Inventory inventory = player.Inventory;
        if (inventory.Using != ConsumableType.None) return false;
        if (Count(inventory, type) == 0) return false;
        ConsumableDefinition definition = items.Consumable(type);
        bool healsSomething = (definition.Heal > 0 && player.Health < CombatRules.MaxHealth) ||
                              (definition.Shield > 0 && player.Shield < CombatRules.MaxShield);
        if (!healsSomething) return false;

        inventory.Using = type;
        inventory.UseEndTick = now + definition.UseTicks;
        inventory.Changed = true;
        return true;
    }

    // Step 9, every tick of a living player: the channel ends, one item is used up, the values rise to
    // at most the maximum.
    public static bool Complete(PlayerEntity player, ItemCatalog items, uint now)
    {
        Inventory inventory = player.Inventory;
        if (inventory.Using == ConsumableType.None || now < inventory.UseEndTick) return false;

        ConsumableType type = inventory.Using;
        Cancel(inventory);
        if (Count(inventory, type) == 0) return false;   // not reached: nothing removes a stack mid-use but death

        if (type == ConsumableType.Medkit) inventory.Medkits--;
        else inventory.ShieldCells--;
        ConsumableDefinition definition = items.Consumable(type);
        player.Health = Math.Min(CombatRules.MaxHealth, player.Health + definition.Heal);
        player.Shield = Math.Min(CombatRules.MaxShield, player.Shield + definition.Shield);
        return true;
    }

    public static void Cancel(Inventory inventory)
    {
        if (inventory.Using == ConsumableType.None) return;
        inventory.Using = ConsumableType.None;
        inventory.UseEndTick = 0;
        inventory.Changed = true;
    }

    private static int Count(Inventory inventory, ConsumableType type) =>
        type == ConsumableType.Medkit ? inventory.Medkits : inventory.ShieldCells;
}
