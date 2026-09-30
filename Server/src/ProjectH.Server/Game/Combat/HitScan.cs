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

    // Distance to the first solid thing along the ray: an arena box or the y = 0 floor. Returns range
    // when nothing is closer.
    public static float TraceWorld(Vector3 origin, Vector3 direction, float range, ReadOnlySpan<Box> world)
    {
        float nearest = range;
        for (int i = 0; i < world.Length; i++)
        {
            ref readonly Box box = ref world[i];
            if (IntersectAabb(origin, direction, box.Min, box.Max, nearest, out float distance)) nearest = distance;
        }
        if (direction.Y < -ParallelEpsilon && origin.Y >= 0f)
        {
            float toFloor = -origin.Y / direction.Y;
            if (toFloor < nearest) nearest = toFloor;
        }
        return nearest;
    }

    // Player hit box: the movement AABB (HalfWidth 0.35 m, Height 1.8 m) with its feet at feet.
    public static bool TracePlayer(Vector3 origin, Vector3 direction, float maxDistance, Vector3 feet, out float distance)
    {
        var min = new Vector3(feet.X - MoveSettings.HalfWidth, feet.Y, feet.Z - MoveSettings.HalfWidth);
        var max = new Vector3(feet.X + MoveSettings.HalfWidth, feet.Y + MoveSettings.Height, feet.Z + MoveSettings.HalfWidth);
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

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
