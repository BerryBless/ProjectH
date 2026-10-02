using System.Numerics;
using ProjectH.Bots;
using ProjectH.Shared.Simulation;

namespace ProjectH.QA;

// Stress (D38): where group workloads put their actors, computed from the shared map data (boxes, doors, harvestables,
// terrain, the build grid) so a scenario does not list 100 coordinates. Pure and deterministic: the same arguments give
// the same places in the same order (tests check it).
public static class StressMap
{
    public const float Margin = 4f;               // keep clear of the outer walls
    private const float StandClearance = 0.6f;    // a body is 0.8 m wide: leave a little room around it

    private static readonly Box[] s_blockers = BuildBlockers();

    private static Box[] BuildBlockers()
    {
        var list = new List<Box>(GameMap.Boxes.ToArray());
        list.AddRange(GameMap.Doors.ToArray());
        foreach (Harvestable h in GameMap.Harvestables) list.Add(h.Bounds);
        return list.ToArray();
    }

    public static Vector3 Ground(float x, float z) => new(x, GameMap.Terrain.Height(x, z), z);

    public static bool InsideMap(float x, float z) => MathF.Abs(x) <= GameMap.HalfSize - Margin && MathF.Abs(z) <= GameMap.HalfSize - Margin;

    // A body can stand here: inside the walls and clear (with room to spare) of every box, door and harvestable.
    public static bool CanStand(Vector3 feet)
    {
        if (!InsideMap(feet.X, feet.Z)) return false;
        foreach (Box b in s_blockers)
        {
            if (feet.X > b.Min.X - StandClearance && feet.X < b.Max.X + StandClearance && feet.Z > b.Min.Z - StandClearance
                && feet.Z < b.Max.Z + StandClearance && b.Min.Y < feet.Y + 2f && b.Max.Y > feet.Y) return false;
        }
        return true;
    }

    // Eye of one body to the chest of another, past every blocker and the terrain (the bots' own approximation).
    public static bool InSight(Vector3 fromFeet, Vector3 toFeet) =>
        LineOfSight.Clear(BotAim.Eye(fromFeet), BotAim.Chest(toFeet), s_blockers, GameMap.Terrain)
        && LineOfSight.Clear(BotAim.Chest(fromFeet), BotAim.Chest(toFeet), s_blockers, GameMap.Terrain);

    // ---- combat spots ----

    // `count` spots for fights of `size` actors (2 = a pair): each spot's members stand on a circle of diameter
    // `distance` around the spot centre, and every member sees the next one (the one it shoots). Candidates lie on a
    // grid of `spacing` inside the circle (center, radius) and are taken nearest to the centre first. Fewer than
    // `count` when the area has no more room (the caller reports it).
    public static List<Vector3[]> FightSpots(Vector2 center, float radius, float spacing, float distance, int size, int count)
    {
        var result = new List<Vector3[]>(count);
        if (count <= 0 || size < 2) return result;
        spacing = MathF.Max(spacing, 2f);
        var candidates = new List<Vector2>();
        int steps = (int)MathF.Ceiling(radius / spacing);
        for (int i = -steps; i <= steps; i++)
        {
            for (int j = -steps; j <= steps; j++)
            {
                var c = new Vector2(center.X + i * spacing, center.Y + j * spacing);
                if (Vector2.Distance(c, center) <= radius) candidates.Add(c);
            }
        }
        candidates.Sort((a, b) =>
        {
            int d = Vector2.DistanceSquared(a, center).CompareTo(Vector2.DistanceSquared(b, center));
            if (d != 0) return d;
            d = a.X.CompareTo(b.X);
            return d != 0 ? d : a.Y.CompareTo(b.Y);
        });
        foreach (Vector2 c in candidates)
        {
            if (result.Count >= count) break;
            Vector3[]? members = Members(c, distance, size);
            if (members != null) result.Add(members);
        }
        return result;
    }

    private static Vector3[]? Members(Vector2 c, float distance, int size)
    {
        var members = new Vector3[size];
        float r = distance * 0.5f;
        for (int k = 0; k < size; k++)
        {
            // Pairs face each other west-east; bigger groups sit evenly around the circle.
            float yaw = 270f + 360f * k / size;
            float rad = yaw * MathF.PI / 180f;
            Vector3 feet = Ground(c.X + MathF.Sin(rad) * r, c.Y + MathF.Cos(rad) * r);
            if (!CanStand(feet)) return null;
            members[k] = feet;
        }
        for (int k = 0; k < size; k++)
        {
            Vector3 a = members[k], b = members[(k + 1) % size];
            if (MathF.Abs(a.Y - b.Y) > 1.5f || !InSight(a, b) || !InSight(b, a)) return null;
        }
        return members;
    }

    public static float YawTo(Vector3 from, Vector3 to) => BotAim.YawTo(from, to);

    // ---- build sites ----

    // One site: a target cell T and the cell south of it, where the builder stands (1 m south of T's south edge). Order:
    // far pieces first, so nothing the builder already placed stands between its eye and the next piece (the server's
    // line-of-sight check), then the near south walls last.
    public sealed record BuildSite(int CellX, int CellZ, Vector3 Stand, IReadOnlyList<BuildPlan> Pieces);

    // Every target cell whose site (cells x-1..x+1, rows z-1..z) is on flat ground clear of every box, door and
    // harvestable, inside (center, radius), taken nearest first and only when its cells overlap no site taken before (so
    // two sites never share a cell). Deterministic.
    public static List<BuildSite> BuildSites(Vector2 center, float radius, IReadOnlyCollection<BuildPieceType> pieces, BuildMaterialType material)
    {
        var candidates = new List<(int X, int Z, Vector3 Stand, float Distance)>();
        for (int x = 1; x + 1 < BuildGrid.CellsX; x++)
        {
            for (int z = 1; z < BuildGrid.CellsZ; z++)
            {
                float cx = BuildGrid.CellMinX(x) + BuildGrid.CellSize * 0.5f;
                var stand = Ground(cx, BuildGrid.CellMinZ(z) - 1f);
                float distance = Vector2.Distance(new Vector2(stand.X, stand.Z), center);
                if (distance > radius || !SiteClear(x, z) || !CanStand(stand)) continue;
                candidates.Add((x, z, stand, distance));
            }
        }
        candidates.Sort((a, b) =>
        {
            int d = a.Distance.CompareTo(b.Distance);
            if (d != 0) return d;
            d = a.X.CompareTo(b.X);
            return d != 0 ? d : a.Z.CompareTo(b.Z);
        });
        var taken = new bool[BuildGrid.CellsX, BuildGrid.CellsZ];
        var sites = new List<BuildSite>();
        foreach (var c in candidates)
        {
            bool free = true;
            for (int x = c.X - 1; x <= c.X + 1 && free; x++)
                for (int z = c.Z - 1; z <= c.Z && free; z++) free = !taken[x, z];
            if (!free) continue;
            IReadOnlyList<BuildPlan> plan = SitePieces(c.X, c.Z, pieces, material);
            if (plan.Count == 0) continue;
            for (int x = c.X - 1; x <= c.X + 1; x++)
                for (int z = c.Z - 1; z <= c.Z; z++) taken[x, z] = true;
            sites.Add(new BuildSite(c.X, c.Z, c.Stand, plan));
        }
        return sites;
    }

    public static readonly BuildPieceType[] AllPieces = { BuildPieceType.Wall, BuildPieceType.Floor, BuildPieceType.Ramp, BuildPieceType.Roof };

    // Cells x-1..x+1, rows z-1..z: on the grid, nearly flat (the pieces sit on level 0) and free of map objects up to
    // two levels.
    private static bool SiteClear(int x, int z)
    {
        if (x - 1 < 0 || x + 1 >= BuildGrid.CellsX || z - 1 < 0 || z >= BuildGrid.CellsZ) return false;
        float minX = BuildGrid.CellMinX(x - 1), maxX = BuildGrid.CellMinX(x + 2);
        float minZ = BuildGrid.CellMinZ(z - 1), maxZ = BuildGrid.CellMinZ(z + 1);
        if (!InsideMap(minX, minZ) || !InsideMap(maxX, maxZ)) return false;
        float low = float.MaxValue, high = float.MinValue;
        for (float px = minX; px <= maxX; px += 1f)
        {
            for (float pz = minZ; pz <= maxZ; pz += 1f)
            {
                float h = GameMap.Terrain.Height(px, pz);
                low = MathF.Min(low, h);
                high = MathF.Max(high, h);
            }
        }
        if (high - low > 0.5f || high > 0.25f) return false;
        foreach (Box b in s_blockers)
        {
            if (b.Max.X > minX - 0.25f && b.Min.X < maxX + 0.25f && b.Max.Z > minZ - 0.25f && b.Min.Z < maxZ + 0.25f && b.Min.Y < 2 * BuildGrid.LevelHeight)
                return false;
        }
        return true;
    }

    // The order the builder places one site's pieces (T = target cell, W/E = its west and east neighbours, S = the
    // stand cell): ramps in W and E (rising north), a floor on T, T's west, east and north walls, the same three walls
    // one level up, a roof over T, the floor over T one level up, then T's south wall below and above
    // (nearest last). Only the piece types asked for.
    // The roof sits on T's level-1 walls (its eaves at level 2); it goes before the level-1 floor, which would stand
    // between the eye and it.
    public static IReadOnlyList<BuildPlan> SitePieces(int x, int z, IReadOnlyCollection<BuildPieceType> types, BuildMaterialType material)
    {
        var order = new (BuildPieceType Type, int X, int Y, int Z, int Rotation)[]
        {
            (BuildPieceType.Ramp, x - 1, 0, z, 0),
            (BuildPieceType.Ramp, x + 1, 0, z, 0),
            (BuildPieceType.Floor, x, 0, z, 0),
            (BuildPieceType.Wall, x, 0, z, 1),
            (BuildPieceType.Wall, x, 0, z, 3),
            (BuildPieceType.Wall, x, 0, z, 2),
            (BuildPieceType.Wall, x, 1, z, 1),
            (BuildPieceType.Wall, x, 1, z, 3),
            (BuildPieceType.Wall, x, 1, z, 2),
            (BuildPieceType.Roof, x, 1, z, 0),
            (BuildPieceType.Floor, x, 1, z, 0),
            (BuildPieceType.Wall, x, 0, z, 0),
            (BuildPieceType.Wall, x, 1, z, 0),
        };
        var plans = new List<BuildPlan>(order.Length);
        foreach (var p in order)
        {
            if (!types.Contains(p.Type)) continue;
            if (!BuildGrid.TryNormalize(p.Type, p.X, p.Y, p.Z, p.Rotation, out BuildPieceShape shape)) continue;
            plans.Add(new BuildPlan(p.Type, material, shape.X, shape.Y, shape.Z, shape.Rotation, Vec3.From(BuildGrid.CenterOf(shape))));
        }
        return plans;
    }
}
