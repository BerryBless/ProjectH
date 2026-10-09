using System;
using System.Numerics;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Build;

// Phase 13 D11: the first building piece along a ray (a shot or a harvest swing). Walks only the build cells the ray's
// ground track crosses (2D DDA, request §126), nearest first, testing the pieces of each cell plus the walls of the next
// cells east and north (a wall stands across its edge, half in each cell), and stops once the next cell starts beyond the
// best hit. Walls and floors are boxes (Phase 13.5: an edited piece is its BuildGrid.PartsOf boxes, so a shot passes an
// opening); a ramp is its slab (a convex solid between two planes), a roof its pyramid (or one-way plane) down to the
// ceiling. Pieces are tested as they are now: shots are not rewound for building (D11, like doors). Pure, no
// allocation.
public static class PieceTrace
{
    private const float ParallelEpsilon = 1e-8f;

    // 기능: 광선이 처음 맞히는 조각을 찾는다(사격·채집·편집 시선). Phase 13.5: 편집된 조각은 PartsOf의 부분으로 본다(창·문으로는 지나간다).
    // 입력: origin·direction - 광선(direction은 단위 벡터), range - 최대 거리, world - 조각들, id·slot·distance - 결과,
    //   ignoreId - 보지 않을 조각 id(0 = 모두 본다, 편집 시선에서 대상 자신).
    // 출력: 맞혔으면 true와 조각 id·slot·거리, 아니면 false(distance = range).
    public static bool Trace(Vector3 origin, Vector3 direction, float range, BuildWorld world, out uint id, out int slot, out float distance,
        uint ignoreId = 0)
    {
        id = 0;
        slot = -1;
        distance = range;
        if (world.Count == 0 || !IsFinite(origin) || !IsFinite(direction) || !(range > 0f) || !float.IsFinite(range)) return false;
        PieceGrid grid = world.Grid;

        // The part of the ray over the grid.
        float tStart = 0f;
        float tEnd = range;
        float minX = BuildGrid.OriginX;
        float minZ = BuildGrid.OriginZ;
        float maxX = minX + BuildGrid.CellsX * BuildGrid.CellSize;
        float maxZ = minZ + BuildGrid.CellsZ * BuildGrid.CellSize;
        if (!Slab(origin.X, direction.X, minX, maxX, ref tStart, ref tEnd)) return false;
        if (!Slab(origin.Z, direction.Z, minZ, maxZ, ref tStart, ref tEnd)) return false;
        if (tStart > tEnd) return false;

        float cell = BuildGrid.CellSize;
        int i = Math.Clamp((int)MathF.Floor((origin.X + direction.X * tStart - minX) / cell), 0, BuildGrid.CellsX - 1);
        int j = Math.Clamp((int)MathF.Floor((origin.Z + direction.Z * tStart - minZ) / cell), 0, BuildGrid.CellsZ - 1);
        int stepI = direction.X > 0f ? 1 : -1;
        int stepJ = direction.Z > 0f ? 1 : -1;
        float nextX = Boundary(origin.X, direction.X, minX + (i + (stepI > 0 ? 1 : 0)) * cell);
        float nextZ = Boundary(origin.Z, direction.Z, minZ + (j + (stepJ > 0 ? 1 : 0)) * cell);
        float deltaX = MathF.Abs(direction.X) < ParallelEpsilon ? float.PositiveInfinity : cell / MathF.Abs(direction.X);
        float deltaZ = MathF.Abs(direction.Z) < ParallelEpsilon ? float.PositiveInfinity : cell / MathF.Abs(direction.Z);

        float cellStart = tStart;
        while (cellStart <= distance && cellStart <= tEnd)
        {
            TestColumn(grid, i, j, false, origin, direction, ignoreId, ref distance, ref slot);
            TestColumn(grid, i + 1, j, true, origin, direction, ignoreId, ref distance, ref slot);
            TestColumn(grid, i, j + 1, true, origin, direction, ignoreId, ref distance, ref slot);
            if (nextX <= nextZ)
            {
                cellStart = nextX;
                i += stepI;
                nextX += deltaX;
            }
            else
            {
                cellStart = nextZ;
                j += stepJ;
                nextZ += deltaZ;
            }
            if (i < 0 || i >= BuildGrid.CellsX || j < 0 || j >= BuildGrid.CellsZ) break;
        }
        if (slot < 0) return false;
        id = grid.IdAt(slot);
        return true;
    }

    // 기능: 한 칸 기둥의 조각들을 광선과 시험해 가장 가까운 것을 남긴다.
    // 입력: grid - 조각 칸 색인, x·z - 칸, wallsOnly - 이웃 칸의 벽만 볼지, o·d - 광선, ignoreId - 건너뛸 조각 id(0 = 없음),
    //   best·bestSlot - 지금까지의 최선(갱신된다).
    // 출력: 반환값 없음.
    private static void TestColumn(PieceGrid grid, int x, int z, bool wallsOnly, Vector3 o, Vector3 d, uint ignoreId, ref float best, ref int bestSlot)
    {
        for (int s = grid.First(x, z); s >= 0; s = grid.Next(s))
        {
            ref readonly BuildPieceShape shape = ref grid.ShapeAt(s);
            if (wallsOnly && shape.Type != BuildPieceType.Wall) continue;
            if (ignoreId != 0 && grid.IdAt(s) == ignoreId) continue;
            if (Hit(shape, o, d, best, out float t) && t < best)
            {
                best = t;
                bestSlot = s;
            }
        }
    }

    // 기능: 광선과 조각 하나의 교차를 구한다. 벽·바닥·평지붕·통로는 PartsOf의 상자들(가장 가까운 것), Ramp는 판, 사각뿔 지붕은
    //   천장까지 찬 피라미드, 한쪽 경사 지붕(Phase 13.5 RoofSlope)은 천장까지 찬 경사 평면 아래 고체다.
    // 입력: shape - 조각 모양, o·d - 광선, maxDistance - 최대 거리, t - 결과.
    // 출력: maxDistance 안에서 고체에 들어가면 true와 들어가는 거리(시작점이 안이면 0), 아니면 false.
    public static bool Hit(in BuildPieceShape shape, Vector3 o, Vector3 d, float maxDistance, out float t)
    {
        Span<Box> parts = stackalloc Box[BuildGrid.MaxPartsPerPiece];
        int count = BuildGrid.PartsOf(shape, parts, out bool isSlope);
        if (!isSlope)
        {
            bool hit = false;
            t = maxDistance;
            for (int p = 0; p < count; p++)
            {
                if (HitScan.IntersectAabb(o, d, parts[p].Min, parts[p].Max, t, out float tp) && (!hit || tp < t))
                {
                    t = tp;
                    hit = true;
                }
            }
            if (!hit) t = 0f;
            return hit;
        }
        Slope slope = BuildGrid.SlopeOf(shape);
        float t0 = 0f;
        float t1 = maxDistance;
        t = 0f;
        if (!Slab(o.X, d.X, slope.MinX, slope.MaxX, ref t0, ref t1)) return false;
        if (!Slab(o.Z, d.Z, slope.MinZ, slope.MaxZ, ref t0, ref t1)) return false;
        const float rise = BuildGrid.RampRise / BuildGrid.CellSize;   // 0.6, the pyramid's slope too
        if (slope.IsPlane)
        {
            // along(p) = a . (x, z) + c for the rising direction; the slab: base - T <= y - rise x along <= base.
            float ax = 0f, az = 0f, c;
            switch (slope.Direction)
            {
                case 0: az = 1f; c = -slope.MinZ; break;
                case 1: ax = 1f; c = -slope.MinX; break;
                case 2: az = -1f; c = slope.MaxZ; break;
                default: ax = -1f; c = slope.MaxX; break;
            }
            // g(t) = y - rise x along at the ray's point t, linear in t.
            float planeRise = slope.Rise / BuildGrid.CellSize;   // ramp 0.6, one-way roof 0.3
            float g0 = o.Y - planeRise * (ax * o.X + az * o.Z + c);
            float gd = d.Y - planeRise * (ax * d.X + az * d.Z);
            if (slope.Kind == SlopeKind.Ramp)
            {
                if (!Slab(g0, gd, slope.BaseY - BuildGrid.SlopeThickness, slope.BaseY, ref t0, ref t1)) return false;
            }
            else
            {
                // A one-way roof: under its plane and down to the ceiling, like the pyramid.
                if (!Below(g0, gd, slope.BaseY, ref t0, ref t1)) return false;
                if (!Below(-o.Y, -d.Y, -(slope.BaseY - BuildGrid.SlopeThickness), ref t0, ref t1)) return false;
            }
        }
        else
        {
            // y + rise |x - cx| <= top and y + rise |z - cz| <= top (four planes), and y >= the ceiling.
            float cx = (slope.MinX + slope.MaxX) * 0.5f;
            float cz = (slope.MinZ + slope.MaxZ) * 0.5f;
            float top = slope.Top;
            if (!Below(o.Y + rise * (o.X - cx), d.Y + rise * d.X, top, ref t0, ref t1)) return false;
            if (!Below(o.Y - rise * (o.X - cx), d.Y - rise * d.X, top, ref t0, ref t1)) return false;
            if (!Below(o.Y + rise * (o.Z - cz), d.Y + rise * d.Z, top, ref t0, ref t1)) return false;
            if (!Below(o.Y - rise * (o.Z - cz), d.Y - rise * d.Z, top, ref t0, ref t1)) return false;
            if (!Below(-o.Y, -d.Y, -(slope.BaseY - BuildGrid.SlopeThickness), ref t0, ref t1)) return false;
        }
        t = t0;
        return t0 <= t1;
    }

    // 기능: 광선 구간 [t0, t1]을 value0 + rate x t <= limit인 부분으로 좁힌다(반평면 하나와의 교차).
    // 입력: value0 - t = 0에서의 값, rate - t당 변화량, limit - 상한, t0·t1 - 구간(갱신된다).
    // 출력: 좁힌 구간이 비어 있지 않으면 true.
    private static bool Below(float value0, float rate, float limit, ref float t0, ref float t1)
    {
        if (MathF.Abs(rate) < ParallelEpsilon) return value0 <= limit;
        float t = (limit - value0) / rate;
        if (rate > 0f)
        {
            if (t < t1) t1 = t;
        }
        else if (t > t0)
        {
            t0 = t;
        }
        return t0 <= t1;
    }

    // 기능: 광선 구간 [tMin, tMax]를 한 축의 [min, max] 판과 겹치는 부분으로 좁힌다.
    // 입력: origin - 그 축의 시작 좌표, direction - 그 축의 방향 성분, min·max - 판 범위, tMin·tMax - 구간(갱신된다).
    // 출력: 좁힌 구간이 비어 있지 않으면 true(축과 평행하면 시작점이 판 안일 때만 true).
    private static bool Slab(float origin, float direction, float min, float max, ref float tMin, ref float tMax)
    {
        if (MathF.Abs(direction) < ParallelEpsilon) return origin >= min && origin <= max;
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

    // 기능: 벡터의 세 성분이 모두 유한한지 본다.
    // 입력: v - 검사할 벡터.
    // 출력: NaN·무한대가 없으면 true.
    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
