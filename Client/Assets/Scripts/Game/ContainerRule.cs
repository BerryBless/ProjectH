using System;
using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Phase 16 D4: copy of the server's container rule (Server ContainerRules), used only to show the "[E] 열기" prompt.
    // The server still decides (it also checks the line of sight). Keep the two in step: the server tests compile this
    // file and compare it with their own. Same geometry as the door rule (DoorRule): horizontal DoorInteractRange from
    // the feet, within DoorInteractHalfAngle of the facing direction, feet within 1 m up or down of the target's bottom.
    // Pure, no allocation, no UnityEngine.
    public static class ContainerRule
    {
        // FindTarget results at or above this are supply drop slots (result - SupplyDropTargetBase); below are container ids.
        public const int SupplyDropTargetBase = 64;

        // 기능: 대상 하나가 E 범위인지 본다(문 규칙과 같은 기하).
        // 입력: feet - 내 발, yaw - 바라보는 방향(도, 0 = +Z), position - 대상 바닥 중심.
        // 출력: 수평 거리 <= DoorInteractRange, 발 높이가 position.Y - 1 .. position.Y + 1, 수평 거리 > 1e-4면 각도 <= DoorInteractHalfAngle일 때
        //   true. distanceSq는 결과와 관계없이 발에서 position까지의 수평 거리 제곱.
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

        // 기능: 가장 가까운 열 수 있는 대상을 고른다. Container를 먼저, Supply Drop을 나중에 보고, 더 가까운 것만 바꾼다(같은 거리면
        //   먼저 본 것 = 작은 id, Container 우선).
        // 입력: feet - 내 발, yaw - 바라보는 방향(도), containers - LootContainers.All, closedMask - 비트 i = Container i가 생성됨 & 안 열림,
        //   dropPositions - Supply Drop 칸 k의 착지 위치 (X, LandY, Z), landedClosedDropMask - 비트 k = 칸 k가 착지 & 안 열림.
        // 출력: Container id(0..63), Supply Drop이면 SupplyDropTargetBase + k, 없으면 -1. distanceSq = 고른 대상의 수평 거리 제곱(없으면 0).
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
        // 입력: feet - 내 발, door - DoorRule.FindTarget 결과(-1 = 문 없음), doors - GameMap.Doors, containerDistanceSq - FindTarget이 낸
        //   Container 쪽 수평 거리 제곱.
        // 출력: 문이 없거나(door < 0 또는 범위 밖 번호) 문 중심(XZ 가운데)까지 수평 거리 제곱이 containerDistanceSq보다 크면 true, 아니면 false.
        public static bool PreferContainer(Vector3 feet, int door, ReadOnlySpan<Box> doors, float containerDistanceSq)
        {
            if (door < 0 || door >= doors.Length) return true;
            ref readonly Box box = ref doors[door];
            float dx = (box.Min.X + box.Max.X) * 0.5f - feet.X;
            float dz = (box.Min.Z + box.Max.Z) * 0.5f - feet.Z;
            return dx * dx + dz * dz > containerDistanceSq;
        }
    }
}
