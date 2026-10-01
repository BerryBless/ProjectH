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

    // Player hit box: the movement AABB (HalfWidth 0.35 m, Height 1.8 m) with its feet at feet.
    public static bool TracePlayer(Vector3 origin, Vector3 direction, float maxDistance, Vector3 feet, out float distance) =>
        TracePlayer(origin, direction, maxDistance, feet, MoveSettings.Height, out distance);

    // Phase 12 D13: the same box with the mode's height (MovementSimulation.CollisionHeight: 1.2 m crouched or sliding).
    public static bool TracePlayer(Vector3 origin, Vector3 direction, float maxDistance, Vector3 feet, float height, out float distance)
    {
        var min = new Vector3(feet.X - MoveSettings.HalfWidth, feet.Y, feet.Z - MoveSettings.HalfWidth);
        var max = new Vector3(feet.X + MoveSettings.HalfWidth, feet.Y + height, feet.Z + MoveSettings.HalfWidth);
        return IntersectAabb(origin, direction, min, max, maxDistance, out distance);
    }

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

    private static float Boundary(float origin, float direction, float boundary) =>
        MathF.Abs(direction) < ParallelEpsilon ? float.PositiveInfinity : (boundary - origin) / direction;

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

    // Ray height minus surface height at t, the surface being the cell's triangle under that point.
    private static float Above(Vector3 o, Vector3 d, HeightField terrain, int i, int j, float baseX, float baseZ, float inverseCell, float t)
    {
        float u = Math.Clamp((o.X + d.X * t - baseX) * inverseCell, 0f, 1f);
        float v = Math.Clamp((o.Z + d.Z * t - baseZ) * inverseCell, 0f, 1f);
        return o.Y + d.Y * t - terrain.CellHeight(i, j, u, v);
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
