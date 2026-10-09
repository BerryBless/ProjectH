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
    // 기능: 입력의 도구 키로 손에 든 도구를 바꾼다(D5): 무기 칸 키(1-3) → 무기, F → 채집 도구, Q → 건설 모드(건설 모드에서는 이전
    //   도구로). 함께 눌리면 칸 키, 그다음 F가 이긴다. 도구가 바뀌면 재장전을 취소한다.
    // 입력: player - 플레이어, buttons - 이번 입력에서 눌린 버튼.
    // 출력: 도구가 바뀌었으면 true(Inventory.Tool·PreviousTool·Reloading이 바뀐다), 아니면 false.
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

    // 기능: 채집 휘두르기가 사거리 안에서 처음 닿는 것을 찾는다(D7): 서 있는 채집물이면 그 id, 그보다 먼저 맵 상자·닫힌 문·지형이
    //   막히면 없음. 할당 없음.
    // 입력: origin - 시작점, direction - 단위 방향, range - 사거리, destroyed - 파괴된 채집물 비트 마스크, blockers - 막는 상자들
    //   (채집물 포함), terrain - 지형 높이, distance - 결과.
    // 출력: 맞힌 채집물 id(GameMap.Harvestables 색인)와 그 거리. 없거나 막히면 -1(distance는 range 또는 채집물까지 거리).
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
