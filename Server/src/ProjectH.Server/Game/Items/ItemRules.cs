using System;
using System.Numerics;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Items;

// Pickup and drop rules (D8, D9, D12) that do not need the match: ranges, stack room, drop placement.
// Pure functions, no allocation. The client's pickup prompt copies the range rule (PickupRule); keep the
// two in step.
public static class ItemRules
{
    public const float PickupRange = 2f;       // D8: horizontal, from the feet
    public const float PickupHeight = 2f;      // D8: up or down, from the feet
    public const float DropDistance = 1f;      // D12: G drops the weapon 1 m in front
    public const float DeathDropRadius = 1f;   // D12: a dead player's items lie on a 1 m circle
    // Knee height of the "is a box in the way" ray: sees 1 m boxes, passes over the box being stood on.
    private const float BlockCheckHeight = 0.5f;
    // Phase 13 D15: a Material item (a dead player's resources) is picked up by walking within this distance (across the
    // ground; up or down PickupHeight), checked every MaterialPickupEveryTicks ticks. E never picks one.
    public const float MaterialPickupRange = 1.5f;
    public const int MaterialPickupEveryTicks = 3;
    // Review fix C6 (SEC-20): G and an E pickup share this interval, counted from every attempt (each one can send Reliable
    // world and inventory packets to everyone). The resources' auto pickup does not use it.
    public const float ActionIntervalSeconds = 0.25f;

    // 기능: 아이템 행동 간격을 Tick으로 바꾼다(리뷰 수정 C6, 반올림, 최소 1).
    // 입력: simHz - Tick 속도.
    // 출력: Tick 수(30 Hz = 8).
    public static uint ActionIntervalTicks(int simHz) => CombatRules.TicksFromSeconds(ActionIntervalSeconds, simHz);

    // 기능: 이 탄 종류나 소모품을 더 넣을 수 있는 양(D3, D9, Phase 17: 수류탄). 무기는 쌓이지 않는다.
    // 입력: inventory - 인벤토리, items - 카탈로그, kind·defId - 아이템 종류와 정의 id.
    // 출력: 남은 칸 수(0 이상).
    public static int Room(Inventory inventory, ItemCatalog items, ItemKind kind, byte defId)
    {
        switch (kind)
        {
            case ItemKind.Ammo:
                var type = (AmmoType)defId;
                return Math.Max(0, items.Ammo(type).Max - inventory.GetAmmo(type));
            case ItemKind.Consumable:
                var consumable = (ConsumableType)defId;
                int have = ConsumableCount(inventory, consumable);
                return Math.Max(0, items.Consumable(consumable).MaxStack - have);
            default:
                return 0;   // weapons have no stack; Material's room is the building catalog's (Match.PickUpMaterials)
        }
    }

    // 기능: 소지한 소모품 수를 돌려준다(Phase 17: 수류탄 포함).
    // 입력: inventory - 인벤토리, type - 소모품 종류.
    // 출력: 소지 수(모르는 종류는 0).
    public static int ConsumableCount(Inventory inventory, ConsumableType type) => type switch
    {
        ConsumableType.Medkit => inventory.Medkits,
        ConsumableType.ShieldCell => inventory.ShieldCells,
        ConsumableType.Grenade => inventory.Grenades,
        _ => 0,
    };

    // 기능: 아이템 양을 인벤토리에 더한다(탄, 재료, 소모품. Phase 17: 수류탄은 따로 센다).
    // 입력: inventory - 인벤토리, kind·defId - 아이템 종류와 정의 id, amount - 더할 양(Room 이하).
    // 출력: 반환값 없음.
    public static void AddStack(Inventory inventory, ItemKind kind, byte defId, int amount)
    {
        if (kind == ItemKind.Ammo)
        {
            var type = (AmmoType)defId;
            inventory.SetAmmo(type, inventory.GetAmmo(type) + amount);
        }
        else if (kind == ItemKind.Material)
        {
            // Phase 13 D15: explicit, so a Material never falls through to the consumables below.
            var material = (BuildMaterialType)(defId - 1);
            inventory.SetResource(material, inventory.Resource(material) + amount);
        }
        else if (kind != ItemKind.Consumable)
        {
            return;
        }
        else if ((ConsumableType)defId == ConsumableType.Medkit)
        {
            inventory.Medkits += amount;
        }
        else if ((ConsumableType)defId == ConsumableType.Grenade)
        {
            inventory.Grenades += amount;   // Phase 17 D9: never into the shield cells
        }
        else
        {
            inventory.ShieldCells += amount;
        }
    }

    // 기능: 지면 위에서 yaw 방향으로 distance만큼의 변위를 만든다(yaw 0 = +Z, 90 = +X, Camera 규약).
    // 입력: yawDegrees - 수평 각도, distance - 거리.
    // 출력: Y가 0인 변위 벡터.
    public static Vector3 Offset(float yawDegrees, float distance)
    {
        float radians = yawDegrees * (MathF.PI / 180f);
        return new Vector3(MathF.Sin(radians) * distance, 0f, MathF.Cos(radians) * distance);
    }

    // 기능: 떨어뜨린 아이템이 놓일 자리를 정한다: 발 + offset, 무릎 높이 Ray가 상자에 막히거나 그 점이 상자 안이면 발 아래. 높이는
    //   그 자리의 땅(지형, 발 이하의 가장 높은 상자 윗면, 걸어 오를 수 있는 경사면).
    // 입력: feet - 발 위치, offset - 놓을 변위, world - 맵 상자들, terrain - 지형 높이, slopes - 발 주변 경사면(Phase 13 B10).
    // 출력: 아이템 위치(땅 위).
    // Where a dropped item lies: feet + offset, unless a box is in the way (a wall, even a thin one, or a
    // box the offset would end inside), then under the feet. Always on the ground there: the terrain, or the
    // highest box top at or below the feet (Phase 6 D11). Items do not fall later, so a drop in mid-air or over a
    // box edge must land now, where a player can reach it. Only boxes block: the terrain never blocks a walk, so a
    // drop up a slope lands on the slope ahead (Phase 6 spec interpretation 4). Phase 13 final review B10: slopes (ramps
    // and roofs, gathered around the feet) are ground too, up to where a walk from the feet could climb (MaxSlope).
    public static Vector3 DropPosition(Vector3 feet, Vector3 offset, ReadOnlySpan<Box> world, HeightField terrain,
        ReadOnlySpan<Slope> slopes = default)
    {
        Vector3 p = feet + offset;
        float distance = offset.Length();
        if (distance > 0f)
        {
            Vector3 origin = feet + new Vector3(0f, BlockCheckHeight, 0f);
            if (HitScan.TraceBoxes(origin, offset / distance, distance, world) < distance) p = feet;
        }
        p.Y = GroundHeight(p, feet, world, terrain, slopes);
        // In mid-air next to a box the knee-height ray can pass over the box, and its top is above the feet
        // so it is not ground: the point would lie inside the box. Then under the feet, which are never
        // inside a box (the character box cannot overlap one).
        if (InsideAnyBox(p, world))
        {
            p = feet;
            p.Y = GroundHeight(p, feet, world, terrain, slopes);
        }
        return p;
    }

    // 기능: 점이 어떤 상자 안에 있는지 본다(바닥면 포함, 윗면 제외: 상자 위에 놓이는 것은 괜찮다; 옆면은 열린 구간).
    // 입력: p - 검사할 점, world - 맵 상자들.
    // 출력: 상자 안이면 true.
    private static bool InsideAnyBox(Vector3 p, ReadOnlySpan<Box> world)
    {
        for (int i = 0; i < world.Length; i++)
        {
            ref readonly Box b = ref world[i];
            if (p.X > b.Min.X && p.X < b.Max.X && p.Z > b.Min.Z && p.Z < b.Max.Z && p.Y >= b.Min.Y && p.Y < b.Max.Y)
                return true;
        }
        return false;
    }

    // 기능: 그 자리의 땅 높이를 구한다: 지형, 그보다 높고 발 이하(GroundProbe 여유)인 상자 윗면, 발에서 걸어 오를 수 있는 경사면 중 가장 높은 것.
    // 입력: p - 자리(X·Z), feet - 발 위치, world - 맵 상자들, terrain - 지형 높이, slopes - 경사면들.
    // 출력: 땅 높이.
    private static float GroundHeight(Vector3 p, Vector3 feet, ReadOnlySpan<Box> world, HeightField terrain, ReadOnlySpan<Slope> slopes)
    {
        float x = p.X;
        float z = p.Z;
        float feetY = feet.Y;
        float ground = terrain.Height(x, z);
        float dx = x - feet.X;
        float dz = z - feet.Z;
        float climb = feetY + MathF.Sqrt(dx * dx + dz * dz) * MoveSettings.MaxSlope + MoveSettings.GroundProbe;
        for (int i = 0; i < slopes.Length; i++)
        {
            if (slopes[i].Range(x, z, x, z, out _, out float high, out _) && high <= climb && high > ground) ground = high;
        }
        for (int i = 0; i < world.Length; i++)
        {
            ref readonly Box b = ref world[i];
            if (x >= b.Min.X && x <= b.Max.X && z >= b.Min.Z && z <= b.Max.Z &&
                b.Max.Y <= feetY + MoveSettings.GroundProbe && b.Max.Y > ground)
                ground = b.Max.Y;
        }
        return ground;
    }
}
