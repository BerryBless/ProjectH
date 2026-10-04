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

    // 기능: 광선이 처음 맞는 월드 고체(맵 상자, 지형, y = 0 바닥면)까지의 거리를 구한다.
    // 입력: origin - 광선 시작점, direction - 광선 방향 단위 벡터, range - 최대 거리, world - 맵 상자들, terrain - 높이 격자 지형.
    // 출력: 가장 가까운 명중 거리, 아무것도 없으면 range.
    // Distance to the first solid thing along the ray: a map box, the terrain (Phase 6 D11) or the y = 0 floor
    // plane. Returns range when nothing is closer. The plane stays because a shot from high ground can clear the
    // outer wall and leave the terrain grid; inside the grid every height is >= 0, so the terrain is hit first.
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

    // 기능: 광선이 처음 맞는 상자까지의 거리를 구한다(지형은 보지 않음).
    // 입력: origin - 광선 시작점, direction - 광선 방향 단위 벡터, range - 최대 거리, world - 검사할 상자들.
    // 출력: 가장 가까운 상자 명중 거리, 없으면 range.
    // Distance to the first box along the ray, or range. The knee-height "is a box in the way" check of a drop uses
    // only this: the terrain never blocks a walk, so it does not block a drop either (Phase 6 spec interpretation 4).
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

    // 기능: 광선이 처음 맞는 지형 삼각형까지의 거리를 지면 투영 격자 DDA로 구한다.
    // 입력: origin - 광선 시작점, direction - 광선 방향 단위 벡터, range - 최대 거리, terrain - 높이 격자 지형.
    // 출력: 지형 명중 거리(시작점이 지면 아래면 0), 없거나 입력이 유효하지 않으면 range.
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

    // 기능: 광선이 서 있는 키(MoveSettings.Height)의 플레이어 히트박스에 맞는지 검사한다.
    // 입력: origin - 광선 시작점, direction - 광선 방향, maxDistance - 최대 거리, feet - 대상 발 위치.
    // 출력: 맞으면 true와 진입 거리, 아니면 false.
    // Player hit box: the movement AABB (HalfWidth 0.35 m, Height 1.8 m) with its feet at feet.
    public static bool TracePlayer(Vector3 origin, Vector3 direction, float maxDistance, Vector3 feet, out float distance) =>
        TracePlayer(origin, direction, maxDistance, feet, MoveSettings.Height, out distance);

    // 기능: 광선이 주어진 키의 플레이어 히트박스에 맞는지 검사한다.
    // 입력: origin - 광선 시작점, direction - 광선 방향, maxDistance - 최대 거리, feet - 대상 발 위치, height - 이동 모드에 따른 히트박스 높이.
    // 출력: 맞으면 true와 진입 거리, 아니면 false.
    // Phase 12 D13: the same box with the mode's height (MovementSimulation.CollisionHeight: 1.2 m crouched or sliding).
    public static bool TracePlayer(Vector3 origin, Vector3 direction, float maxDistance, Vector3 feet, float height, out float distance)
    {
        var min = new Vector3(feet.X - MoveSettings.HalfWidth, feet.Y, feet.Z - MoveSettings.HalfWidth);
        var max = new Vector3(feet.X + MoveSettings.HalfWidth, feet.Y + height, feet.Z + MoveSettings.HalfWidth);
        return IntersectAabb(origin, direction, min, max, maxDistance, out distance);
    }

    // 기능: 광선과 축 정렬 상자(AABB)의 교차를 slab 방식으로 검사한다.
    // 입력: origin - 광선 시작점, direction - 광선 방향, min·max - 상자의 최소·최대 모서리, maxDistance - 최대 거리.
    // 출력: maxDistance 안에서 맞으면 true와 진입 거리, 아니면(입력이 유한하지 않은 경우 포함) false.
    // Slab test. distance is where the ray enters the box, in [0, maxDistance]; a ray starting inside
    // the box hits at distance 0.
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

    // 기능: 광선 구간을 한 축의 [min, max] 판(slab) 안쪽으로 좁힌다.
    // 입력: origin - 그 축의 시작 좌표, direction - 그 축의 방향 성분, min·max - 판의 범위, tMin·tMax - 좁힐 광선 구간.
    // 출력: 남은 구간이 비어 있지 않으면 true, 비면 false. tMin·tMax가 갱신된다.
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

    // 기능: 광선이 한 축의 경계 좌표에 닿는 거리를 구한다.
    // 입력: origin - 그 축의 시작 좌표, direction - 그 축의 방향 성분, boundary - 경계 좌표.
    // 출력: 경계까지의 광선 거리. 축과 평행하면 무한대.
    private static float Boundary(float origin, float direction, float boundary) =>
        MathF.Abs(direction) < ParallelEpsilon ? float.PositiveInfinity : (boundary - origin) / direction;

    // 기능: 지형 한 칸 안의 광선 구간을 대각선에서 나눠 두 삼각형과의 명중을 검사한다.
    // 입력: o - 광선 시작점, d - 광선 방향, terrain - 높이 격자 지형, i, j - 칸 좌표, t0·t1 - 이 칸 안의 광선 구간.
    // 출력: 칸 안에서 지형에 닿으면 true와 명중 거리, 아니면 false.
    // The ray inside cell (i, j) for t in [t0, t1], split where it crosses the cell diagonal (u = v).
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

    // 기능: 한 삼각형 위의 광선 조각에서 "광선 높이 - 지면 높이"의 부호 변화로 명중 지점을 찾는다.
    // 입력: o - 광선 시작점, d - 광선 방향, terrain - 높이 격자 지형, i, j - 칸 좌표, baseX·baseZ - 칸의 최소 모서리, inverseCell - 1 / 칸 크기, s0·s1 - 광선 조각 구간.
    // 출력: 조각 안에서 지면에 닿으면 true와 선형 보간한 명중 거리(시작부터 지면 아래면 s0), 아니면 false.
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

    // 기능: 광선 위 한 지점의 높이와 그 아래 지형 삼각형 높이의 차를 구한다.
    // 입력: o - 광선 시작점, d - 광선 방향, terrain - 높이 격자 지형, i, j - 칸 좌표, baseX·baseZ - 칸의 최소 모서리, inverseCell - 1 / 칸 크기, t - 광선 거리.
    // 출력: 광선 높이 - 지면 높이(양수면 지면 위, 0 이하면 지면 위가 아님).
    // Ray height minus surface height at t, the surface being the cell's triangle under that point.
    private static float Above(Vector3 o, Vector3 d, HeightField terrain, int i, int j, float baseX, float baseZ, float inverseCell, float t)
    {
        float u = Math.Clamp((o.X + d.X * t - baseX) * inverseCell, 0f, 1f);
        float v = Math.Clamp((o.Z + d.Z * t - baseZ) * inverseCell, 0f, 1f);
        return o.Y + d.Y * t - terrain.CellHeight(i, j, u, v);
    }

    // 기능: 벡터의 세 성분이 모두 유한한지 검사한다.
    // 입력: v - 검사할 벡터.
    // 출력: NaN·무한대 성분이 없으면 true, 있으면 false.
    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
