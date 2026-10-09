using System;
using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Combat;

// Hitscan ray math (D7), server only. No allocation, no exceptions, and no infinite or NaN results:
// callers pass a finite unit direction (CombatRules.TryAimDirection), and anything else is a miss.
public static class HitScan
{
    // Below this a direction component counts as parallel to the slab; 1 / d would overflow to infinity.
    private const float ParallelEpsilon = 1e-8f;

    // 기능: 광선이 처음 맞히는 고체(맵 상자, y = 0 바닥면, 지형 Phase 6 D11)까지의 거리를 구한다. 바닥면은 높은 곳에서 쏜 탄이
    //   지형 격자를 벗어날 때를 위해 남긴다(격자 안은 높이가 0 이상이라 지형이 먼저 맞는다).
    // 입력: origin - 시작점, direction - 단위 방향, range - 최대 거리, world - 맵 상자들, terrain - 지형 높이.
    // 출력: 맞은 거리. 아무것도 더 가깝지 않으면 range.
    public static float TraceWorld(Vector3 origin, Vector3 direction, float range, ReadOnlySpan<Box> world, HeightField terrain)
    {
        float nearest = TraceBoxes(origin, direction, range, world);
        if (direction.Y < -ParallelEpsilon && origin.Y >= 0f)
        {
            float toFloor = -origin.Y / direction.Y;
            if (toFloor < nearest) nearest = toFloor;
        }
        return TraceTerrain(origin, direction, nearest, terrain);
    }

    // 기능: 광선이 처음 맞히는 상자까지의 거리를 구한다(지형은 보지 않는다: 아이템 Drop의 무릎 높이 검사가 이것만 쓴다, Phase 6 해석 4).
    // 입력: origin - 시작점, direction - 단위 방향, range - 최대 거리, world - 상자들.
    // 출력: 맞은 거리. 없으면 range.
    public static float TraceBoxes(Vector3 origin, Vector3 direction, float range, ReadOnlySpan<Box> world)
    {
        float nearest = range;
        for (int i = 0; i < world.Length; i++)
        {
            ref readonly Box box = ref world[i];
            if (IntersectAabb(origin, direction, box.Min, box.Max, nearest, out float distance)) nearest = distance;
        }
        return nearest;
    }

    // 기능: 광선이 처음 닿는 지형 삼각형까지의 거리를 구한다(Phase 6 D11). 지면 자취가 지나는 칸만 2D DDA로 걷고 가장 높은 꼭짓점
    //   아래 구간만 본다. 표면 아래에서 시작한 광선은 시작점에서 맞는다.
    // 입력: origin - 시작점, direction - 단위 방향, range - 최대 거리, terrain - 지형 높이.
    // 출력: 맞은 거리. 없거나 입력이 유한하지 않으면 range.
    // Phase 6 D11: distance along the ray to the first terrain triangle it reaches, or range when none is closer.
    // Walks only the cells the ray's ground track crosses (2D DDA), and only the part of the ray at or below the
    // highest vertex. In each cell the diagonal splits the ray into at most two pieces, one per triangle; on each
    // piece "ray height minus surface height" is linear, so a sign change is an exact hit. A ray that starts at or
    // below the surface hits at its start, like a ray starting inside a box.
    public static float TraceTerrain(Vector3 origin, Vector3 direction, float range, HeightField terrain)
    {
        if (!IsFinite(origin) || !IsFinite(direction) || !float.IsFinite(range) || range <= 0f) return range;

        float tStart = 0f;
        float tEnd = range;
        if (direction.Y < -ParallelEpsilon)
        {
            float enter = (terrain.MaxHeight - origin.Y) / direction.Y;
            if (enter > tStart) tStart = enter;
        }
        else
        {
            if (origin.Y > terrain.MaxHeight) return range;
            if (direction.Y > ParallelEpsilon)
            {
                float leave = (terrain.MaxHeight - origin.Y) / direction.Y;
                if (leave < tEnd) tEnd = leave;
            }
        }

        float cell = terrain.CellSize;
        float minX = terrain.OriginX;
        float minZ = terrain.OriginZ;
        float maxX = minX + (terrain.VertsX - 1) * cell;
        float maxZ = minZ + (terrain.VertsZ - 1) * cell;
        if (!Slab(origin.X, direction.X, minX, maxX, ref tStart, ref tEnd)) return range;
        if (!Slab(origin.Z, direction.Z, minZ, maxZ, ref tStart, ref tEnd)) return range;
        if (tStart > tEnd) return range;   // an exactly vertical ray takes the parallel branch of both slabs, so nothing else enforces this

        int cellsX = terrain.VertsX - 1;
        int cellsZ = terrain.VertsZ - 1;
        float inverseCell = 1f / cell;
        int i = Math.Clamp((int)MathF.Floor((origin.X + direction.X * tStart - minX) * inverseCell), 0, cellsX - 1);
        int j = Math.Clamp((int)MathF.Floor((origin.Z + direction.Z * tStart - minZ) * inverseCell), 0, cellsZ - 1);

        int stepI = direction.X > 0f ? 1 : -1;
        int stepJ = direction.Z > 0f ? 1 : -1;
        float nextX = Boundary(origin.X, direction.X, minX + (i + (stepI > 0 ? 1 : 0)) * cell);
        float nextZ = Boundary(origin.Z, direction.Z, minZ + (j + (stepJ > 0 ? 1 : 0)) * cell);
        float deltaX = MathF.Abs(direction.X) < ParallelEpsilon ? float.PositiveInfinity : cell / MathF.Abs(direction.X);
        float deltaZ = MathF.Abs(direction.Z) < ParallelEpsilon ? float.PositiveInfinity : cell / MathF.Abs(direction.Z);

        float t = tStart;
        while (true)
        {
            float segmentEnd = MathF.Min(MathF.Min(nextX, nextZ), tEnd);
            if (TraceCell(origin, direction, terrain, i, j, t, segmentEnd, out float hit)) return hit;
            if (segmentEnd >= tEnd) return range;
            t = segmentEnd;
            if (nextX <= nextZ)
            {
                i += stepI;
                nextX += deltaX;
            }
            else
            {
                j += stepJ;
                nextZ += deltaZ;
            }
            if (i < 0 || i >= cellsX || j < 0 || j >= cellsZ) return range;
        }
    }

    // 기능: 광선이 선 플레이어의 피격 상자(이동 AABB: 반폭 0.35 m, 높이 1.8 m)를 맞히는지 본다.
    // 입력: origin - 시작점, direction - 단위 방향, maxDistance - 최대 거리, feet - 플레이어 발 위치, distance - 결과.
    // 출력: 맞히면 true와 들어가는 거리, 아니면 false.
    public static bool TracePlayer(Vector3 origin, Vector3 direction, float maxDistance, Vector3 feet, out float distance) =>
        TracePlayer(origin, direction, maxDistance, feet, MoveSettings.Height, out distance);

    // 기능: 광선이 주어진 높이의 플레이어 피격 상자를 맞히는지 본다(Phase 12 D13: 웅크리기·슬라이드는 1.2 m).
    // 입력: origin - 시작점, direction - 단위 방향, maxDistance - 최대 거리, feet - 발 위치, height - 상자 높이, distance - 결과.
    // 출력: 맞히면 true와 들어가는 거리, 아니면 false.
    public static bool TracePlayer(Vector3 origin, Vector3 direction, float maxDistance, Vector3 feet, float height, out float distance)
    {
        var min = new Vector3(feet.X - MoveSettings.HalfWidth, feet.Y, feet.Z - MoveSettings.HalfWidth);
        var max = new Vector3(feet.X + MoveSettings.HalfWidth, feet.Y + height, feet.Z + MoveSettings.HalfWidth);
        return IntersectAabb(origin, direction, min, max, maxDistance, out distance);
    }

    // 기능: 광선과 축 정렬 상자의 교차를 판 검사로 구한다(시작점이 안이면 거리 0).
    // 입력: origin - 시작점, direction - 단위 방향, min·max - 상자 모서리, maxDistance - 최대 거리, distance - 결과.
    // 출력: maxDistance 안에서 들어가면 true와 들어가는 거리, 아니면(유한하지 않은 입력 포함) false.
    public static bool IntersectAabb(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, float maxDistance, out float distance)
    {
        distance = 0f;
        if (!IsFinite(origin) || !IsFinite(direction) || !float.IsFinite(maxDistance) || maxDistance < 0f) return false;

        float tMin = 0f;
        float tMax = maxDistance;
        if (!Slab(origin.X, direction.X, min.X, max.X, ref tMin, ref tMax)) return false;
        if (!Slab(origin.Y, direction.Y, min.Y, max.Y, ref tMin, ref tMax)) return false;
        if (!Slab(origin.Z, direction.Z, min.Z, max.Z, ref tMin, ref tMax)) return false;
        distance = tMin;
        return true;
    }

    // 기능: 광선 구간 [tMin, tMax]를 한 축의 [min, max] 판과 겹치는 부분으로 좁힌다.
    // 입력: origin - 그 축의 시작 좌표, direction - 그 축의 방향 성분, min·max - 판 범위, tMin·tMax - 구간(갱신된다).
    // 출력: 좁힌 구간이 비어 있지 않으면 true(축과 평행하면 시작점이 판 안일 때만 true).
    private static bool Slab(float origin, float direction, float min, float max, ref float tMin, ref float tMax)
    {
        if (MathF.Abs(direction) < ParallelEpsilon)
        {
            // Parallel: inside the slab for the whole ray, or never.
            return origin >= min && origin <= max;
        }

        float inverse = 1f / direction;
        float t1 = (min - origin) * inverse;
        float t2 = (max - origin) * inverse;
        if (t1 > t2)
        {
            float swap = t1;
            t1 = t2;
            t2 = swap;
        }
        if (t1 > tMin) tMin = t1;
        if (t2 < tMax) tMax = t2;
        return tMin <= tMax;
    }

    // 기능: 광선이 한 축의 경계 좌표에 닿는 거리 t를 구한다(DDA의 다음 칸 경계).
    // 입력: origin - 그 축의 시작 좌표, direction - 그 축의 방향 성분, boundary - 경계 좌표.
    // 출력: 경계까지의 t. 축과 평행하면 무한대.
    private static float Boundary(float origin, float direction, float boundary) =>
        MathF.Abs(direction) < ParallelEpsilon ? float.PositiveInfinity : (boundary - origin) / direction;

    // 기능: 지형 칸 (i, j) 안의 광선 구간 [t0, t1]을 대각선(u = v)에서 나눠 삼각형마다 교차를 본다.
    // 입력: o - 시작점, d - 단위 방향, terrain - 지형 높이, i·j - 칸, t0·t1 - 칸 안 구간, hit - 결과.
    // 출력: 맞히면 true와 거리, 아니면 false.
    private static bool TraceCell(Vector3 o, Vector3 d, HeightField terrain, int i, int j, float t0, float t1, out float hit)
    {
        float inverseCell = 1f / terrain.CellSize;
        float baseX = terrain.OriginX + i * terrain.CellSize;
        float baseZ = terrain.OriginZ + j * terrain.CellSize;
        // w(t) = u - v is linear in t; it changes sign where the ray crosses the diagonal.
        float w0 = ((o.X - baseX) - (o.Z - baseZ)) * inverseCell;
        float wd = (d.X - d.Z) * inverseCell;
        float split = t1;
        if (MathF.Abs(wd) >= ParallelEpsilon)
        {
            float tc = -w0 / wd;
            if (tc > t0 && tc < t1) split = tc;
        }
        if (Piece(o, d, terrain, i, j, baseX, baseZ, inverseCell, t0, split, out hit)) return true;
        return split < t1 && Piece(o, d, terrain, i, j, baseX, baseZ, inverseCell, split, t1, out hit);
    }

    // 기능: 삼각형 하나 위의 광선 구간 [s0, s1]에서 "광선 높이 - 표면 높이"의 부호가 바뀌는 지점을 선형으로 구한다.
    // 입력: o - 시작점, d - 단위 방향, terrain - 지형 높이, i·j - 칸, baseX·baseZ - 칸 원점, inverseCell - 1 / 칸 크기,
    //   s0·s1 - 구간, hit - 결과.
    // 출력: 구간 시작이 표면 아래이거나 구간 안에서 표면을 지나면 true와 거리, 아니면 false.
    private static bool Piece(Vector3 o, Vector3 d, HeightField terrain, int i, int j, float baseX, float baseZ, float inverseCell,
        float s0, float s1, out float hit)
    {
        hit = s0;
        float f0 = Above(o, d, terrain, i, j, baseX, baseZ, inverseCell, s0);
        if (f0 <= 0f) return true;
        float f1 = Above(o, d, terrain, i, j, baseX, baseZ, inverseCell, s1);
        if (f1 > 0f) return false;
        hit = s0 + (s1 - s0) * (f0 / (f0 - f1));
        return true;
    }

    // 기능: 거리 t에서 광선 높이와 그 아래 지형 삼각형 표면 높이의 차를 구한다.
    // 입력: o - 시작점, d - 단위 방향, terrain - 지형 높이, i·j - 칸, baseX·baseZ - 칸 원점, inverseCell - 1 / 칸 크기, t - 거리.
    // 출력: 광선 높이 - 표면 높이(0 이하면 표면에 닿음).
    private static float Above(Vector3 o, Vector3 d, HeightField terrain, int i, int j, float baseX, float baseZ, float inverseCell, float t)
    {
        float u = Math.Clamp((o.X + d.X * t - baseX) * inverseCell, 0f, 1f);
        float v = Math.Clamp((o.Z + d.Z * t - baseZ) * inverseCell, 0f, 1f);
        return o.Y + d.Y * t - terrain.CellHeight(i, j, u, v);
    }

    // 기능: 벡터의 세 성분이 모두 유한한지 본다.
    // 입력: v - 검사할 벡터.
    // 출력: NaN·무한대가 없으면 true.
    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
