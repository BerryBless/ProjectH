using System;
using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// Phase 16 D4: which loot container (or landed supply drop) E acts on. Server rule, like DoorRules (not in Shared, game-core-rules
// §4): the client keeps a copy (Client/Assets/Scripts/Game/ContainerRule.cs) only for the "[E] 열기" prompt, and
// ContainerRulesTests compares the two. Same geometry as the door rule: horizontal DoorInteractRange from the feet, within
// DoorInteractHalfAngle of the facing direction, the feet within 1 m up or down of the target's bottom. Pure, no allocation.
public static class ContainerRules
{
    // FindTarget results at or above this are supply drop slots (result - SupplyDropTargetBase); below are container ids.
    public const int SupplyDropTargetBase = 64;

    // 기능: 대상 하나가 E 범위인지 본다(문 규칙과 같은 기하).
    // 입력: feet - 발 위치, yaw - 바라보는 방향(도, 0 = +Z), position - 대상 바닥 중심.
    // 출력: 범위 안이면 true. distanceSq는 결과와 관계없이 발에서 position까지의 수평 거리 제곱.
    public static bool InReach(Vector3 feet, float yaw, Vector3 position, out float distanceSq)
    {
        float dx = position.X - feet.X;
        float dz = position.Z - feet.Z;
        distanceSq = dx * dx + dz * dz;
        if (feet.Y < position.Y - 1f || feet.Y > position.Y + 1f) return false;
        if (distanceSq > MovementTuning.DoorInteractRange * MovementTuning.DoorInteractRange) return false;
        float distance = MathF.Sqrt(distanceSq);
        if (distance <= 1e-4f) return true;
        float radians = yaw * (MathF.PI / 180f);
        float cosLimit = MathF.Cos(MovementTuning.DoorInteractHalfAngle * (MathF.PI / 180f));
        return dx * MathF.Sin(radians) + dz * MathF.Cos(radians) >= cosLimit * distance;
    }

    // 기능: 가장 가까운 열 수 있는 대상을 고른다. Container를 먼저, Supply Drop을 나중에 보고 엄격히 더 가까운 것만 바꾼다(같은 거리면
    //   작은 id, Container 우선).
    // 입력: feet·yaw - 행위자, containers - LootContainers.All, closedMask - 비트 i = Container i가 생성됨 & 닫힘,
    //   dropPositions - Supply Drop 칸 k의 착지 위치, landedClosedDropMask - 비트 k = 칸 k가 착지 & 닫힘.
    // 출력: Container id, Supply Drop이면 SupplyDropTargetBase + k, 없으면 -1. distanceSq = 고른 대상의 수평 거리 제곱(없으면 0).
    public static int FindTarget(Vector3 feet, float yaw, ReadOnlySpan<LootContainer> containers, ulong closedMask,
        ReadOnlySpan<Vector3> dropPositions, int landedClosedDropMask, out float distanceSq)
    {
        int best = -1;
        float bestSq = float.MaxValue;
        int containerCount = Math.Min(containers.Length, SupplyDropTargetBase);
        for (int i = 0; i < containerCount; i++)
        {
            if ((closedMask & (1UL << i)) == 0) continue;
            if (!InReach(feet, yaw, containers[i].Position, out float sq) || sq >= bestSq) continue;
            best = i;
            bestSq = sq;
        }
        int dropCount = Math.Min(dropPositions.Length, 31);
        for (int k = 0; k < dropCount; k++)
        {
            if ((landedClosedDropMask & (1 << k)) == 0) continue;
            if (!InReach(feet, yaw, dropPositions[k], out float sq) || sq >= bestSq) continue;
            best = SupplyDropTargetBase + k;
            bestSq = sq;
        }
        distanceSq = best >= 0 ? bestSq : 0f;
        return best;
    }

    // 기능: 문과 Container가 함께 범위에 들 때 Container를 고를지 정한다(D4: 더 가까운 쪽, 같은 거리면 문).
    // 입력: feet - 발 위치, door - DoorRules.FindTarget 결과(-1 = 없음), doors - GameMap.Doors, containerDistanceSq - FindTarget의 거리 제곱.
    // 출력: 문이 없거나(범위 밖 번호 포함) 문 중심까지 수평 거리 제곱이 containerDistanceSq보다 크면 true.
    public static bool PreferContainer(Vector3 feet, int door, ReadOnlySpan<Box> doors, float containerDistanceSq)
    {
        if (door < 0 || door >= doors.Length) return true;
        ref readonly Box box = ref doors[door];
        float dx = (box.Min.X + box.Max.X) * 0.5f - feet.X;
        float dz = (box.Min.Z + box.Max.Z) * 0.5f - feet.Z;
        return dx * dx + dz * dz > containerDistanceSq;
    }
}
