using System;
using System.Numerics;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Harvest;

// Phase 13 D5, D7: the tool in hand and what a harvest swing reaches. Pure rules on PlayerEntity and plain data: Match
// decides when to call them and sends the packets.
public static class HarvestRules
{
    // D5: one input's tool keys. A weapon slot key (1-3) takes the weapons out (request §112: at once, whichever tool is
    // in hand), F the harvest tool, Q build mode or, from build mode, back to the tool before it. When several are
    // pressed together the slot key wins, then F. Changing tools cancels a reload (the weapon is put away). Returns true
    // when the tool changed.
    public static bool SelectTool(PlayerEntity player, InputButtons buttons)
    {
        var inventory = player.Inventory;
        ToolKind before = inventory.Tool;
        ToolKind target;
        if ((buttons & (InputButtons.Slot1 | InputButtons.Slot2 | InputButtons.Slot3)) != 0) target = ToolKind.Weapon;
        else if ((buttons & InputButtons.ToolHarvest) != 0) target = ToolKind.Harvest;
        else if ((buttons & InputButtons.ToolBuild) != 0) target = before == ToolKind.Build ? inventory.PreviousTool : ToolKind.Build;
        else return false;
        if (target == before) return false;
        if (target == ToolKind.Build) inventory.PreviousTool = before;
        inventory.Tool = target;
        player.Reloading = false;
        return true;
    }

    // D7: the first thing a swing from origin along direction (unit) meets within range: a standing harvestable (returns
    // its id), or anything else in blockers (the map boxes, closed doors, other standing harvestables) or the terrain
    // (returns -1). distance: where the ray met the harvestable. Allocates nothing.
    public static int Trace(Vector3 origin, Vector3 direction, float range, ulong destroyed, ReadOnlySpan<Box> blockers, HeightField terrain,
        out float distance)
    {
        distance = range;
        int target = -1;
        ReadOnlySpan<Harvestable> all = GameMap.Harvestables;
        for (int i = 0; i < all.Length; i++)
        {
            if ((destroyed & (1UL << i)) != 0) continue;
            ref readonly Box box = ref all[i].Bounds;
            if (HitScan.IntersectAabb(origin, direction, box.Min, box.Max, distance, out float d) && d < distance)
            {
                distance = d;
                target = i;
            }
        }
        if (target < 0) return -1;
        // Anything solid in front of it (the blockers hold the harvestables too: the one found is not in front of itself).
        float world = HitScan.TraceWorld(origin, direction, distance, blockers, terrain);
        return world < distance ? -1 : target;
    }
}
