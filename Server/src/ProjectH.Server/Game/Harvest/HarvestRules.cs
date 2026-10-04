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
    // 기능: 한 입력의 도구 키(무기 슬롯, F, Q)로 들고 있는 도구를 바꾼다.
    // 입력: player - 입력을 보낸 플레이어, buttons - 그 입력의 버튼 상태.
    // 출력: 도구가 바뀌면 true(장전 취소, 건설 모드 진입 시 이전 도구 기록), 도구 키가 없거나 같은 도구면 false.
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

    // 기능: 채집 도구 휘두르기가 사거리 안에서 처음 맞히는 채집 대상을 찾는다.
    // 입력: origin - 휘두르기 시작점, direction - 단위 방향, range - 사거리, destroyed - 파괴된 채집 대상 비트 마스크, blockers - 가로막는 박스(맵 박스·닫힌 문·서 있는 채집 대상), terrain - 지형 높이 격자, distance - 맞힌 거리.
    // 출력: 가로막히지 않은 채집 대상을 맞히면 그 ID와 맞힌 거리, 아니면 -1.
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
