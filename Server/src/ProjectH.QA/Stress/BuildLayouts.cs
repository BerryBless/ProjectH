using System.Numerics;
using ProjectH.Bots;
using ProjectH.Shared.Simulation;

namespace ProjectH.QA;

// Stress phase B (§24-29): piece layouts for spawnBuildPieces, which places them through the existing QA command
// (spawnBuildPiece: the server checks the slot, the match budget and the support; not reach, view, cost or players).
// A layout is a list of waves: the pieces of one wave never depend on each other, every piece of a wave rests on the
// ground or on pieces of earlier waves, so a wave can go out in parallel and the next one waits for it. Pure and
// deterministic (tests).
public readonly record struct PieceSpec(BuildPieceType Piece, int X, int Y, int Z, int Rotation)
{
    public Vector3 Center => BuildGrid.TryNormalize(Piece, X, Y, Z, Rotation, out BuildPieceShape s) ? BuildGrid.CenterOf(s) : Vector3.Zero;
}

public static class BuildLayouts
{
    // Cells per batch of the field: the batch's columns rise together, wave by wave, so a count that stops early leaves
    // a compact block (64 cells = 40 x 40 m) rather than one layer over the whole map.
    public const int FieldBatchCells = 64;

    // §24-26 (piece count): columns on every cell of the grid, nearest `center` first. Each column starts at the level of
    // the terrain under the cell centre (that floor is buried or on the ground, so it is grounded) and goes up to the
    // top level: per level a floor, then the south and west walls standing on its edges; the next level's floor rests
    // on those walls. Waves: per batch of cells, step 0 = the floors of the first level, step 1 = its walls, step 2 =
    // the next floors... At most 32 x 32 cells x 16 levels x 3 = 49,152 pieces, more than the match budget (20,000).
    public static IEnumerable<IReadOnlyList<PieceSpec>> Field(Vector2 center)
    {
        var cells = new List<(int X, int Z, int Base, float Distance)>();
        for (int x = 0; x < BuildGrid.CellsX; x++)
        {
            for (int z = 0; z < BuildGrid.CellsZ; z++)
            {
                float cx = BuildGrid.CellMinX(x) + BuildGrid.CellSize * 0.5f, cz = BuildGrid.CellMinZ(z) + BuildGrid.CellSize * 0.5f;
                int level = Math.Clamp(BuildGrid.Level(GameMap.Terrain.Height(cx, cz)), 0, BuildGrid.Levels - 1);
                cells.Add((x, z, level, Vector2.Distance(new Vector2(cx, cz), center)));
            }
        }
        cells.Sort((a, b) =>
        {
            int d = a.Distance.CompareTo(b.Distance);
            if (d != 0) return d;
            d = a.X.CompareTo(b.X);
            return d != 0 ? d : a.Z.CompareTo(b.Z);
        });
        for (int start = 0; start < cells.Count; start += FieldBatchCells)
        {
            var batch = cells.GetRange(start, Math.Min(FieldBatchCells, cells.Count - start));
            for (int step = 0; step < BuildGrid.Levels * 2; step++)
            {
                var wave = new List<PieceSpec>();
                foreach (var c in batch)
                {
                    int level = c.Base + step / 2;
                    if (level >= BuildGrid.Levels) continue;
                    if (step % 2 == 0) wave.Add(new PieceSpec(BuildPieceType.Floor, c.X, level, c.Z, 0));
                    else
                    {
                        wave.Add(new PieceSpec(BuildPieceType.Wall, c.X, level, c.Z, 0));
                        wave.Add(new PieceSpec(BuildPieceType.Wall, c.X, level, c.Z, 1));
                    }
                }
                if (wave.Count > 0) yield return wave;
            }
        }
    }

    // §27-29 (destruction): one structure of exactly `count` pieces that stands on a single grounded piece. A column of
    // south walls on the corner cell (the foundation at the bottom, the only piece touching the ground) carries a block
    // of side x side cells starting `Hang` levels up: per level floors, then south and west walls on them, then the
    // next floors. Destroying the foundation leaves the rest without a grounded piece: the server collapses all of it.
    // Every piece but the foundation is checked to be clear of the terrain and of map boxes by the server's own rule
    // (WouldRest mirrors BuildSupport.IsGrounded); the shooter's stand point south of the foundation must be clear and
    // see the foundation's centre. Null when no place within the map fits.
    public sealed record HangingStructure(PieceSpec Foundation, IReadOnlyList<IReadOnlyList<PieceSpec>> Waves, int Count, int Side, int FirstLevel, Vector3 Aim, Vector3 Shooter);

    public static HangingStructure? Hanging(Vector2 center, int count, int? side = null)
    {
        if (count < 4) return null;
        var corners = new List<(int X, int Z, float Distance)>();
        for (int x = 0; x < BuildGrid.CellsX; x++)
            for (int z = 1; z < BuildGrid.CellsZ; z++)
                corners.Add((x, z, Vector2.Distance(new Vector2(BuildGrid.CellMinX(x), BuildGrid.CellMinZ(z)), center)));
        corners.Sort((a, b) =>
        {
            int d = a.Distance.CompareTo(b.Distance);
            if (d != 0) return d;
            d = a.X.CompareTo(b.X);
            return d != 0 ? d : a.Z.CompareTo(b.Z);
        });
        foreach (var c in corners)
        {
            HangingStructure? s = TryHanging(c.X, c.Z, count, side);
            if (s != null) return s;
        }
        return null;
    }

    private static HangingStructure? TryHanging(int cx, int cz, int count, int? sideWanted)
    {
        // The foundation: the south wall of the corner cell, on the ground (level of the lowest terrain along its edge).
        float x0 = BuildGrid.CellMinX(cx), z0 = BuildGrid.CellMinZ(cz);
        float edgeLow = float.MaxValue;
        for (float t = 0.1f; t <= BuildGrid.CellSize - 0.1f; t += 0.5f) edgeLow = MathF.Min(edgeLow, GameMap.Terrain.Height(x0 + t, z0));
        int foundationLevel = Math.Max(0, BuildGrid.Level(edgeLow));
        for (int side = sideWanted ?? 3; side <= (sideWanted ?? 10); side++)
        {
            if (cx + side > BuildGrid.CellsX || cz + side > BuildGrid.CellsZ) return null;
            float maxX = BuildGrid.CellMinX(cx + side), maxZ = BuildGrid.CellMinZ(cz + side);
            if (!StressMap.InsideMap(x0, z0 - 6f) || !StressMap.InsideMap(maxX, maxZ)) return null;
            // The block starts two levels above the highest ground under it: nothing but the foundation can rest.
            float high = float.MinValue;
            for (float px = x0; px <= maxX; px += 1f)
                for (float pz = z0; pz <= maxZ; pz += 1f) high = MathF.Max(high, GameMap.Terrain.Height(px, pz));
            int first = Math.Max(foundationLevel + 2, BuildGrid.Level(high) + 2);
            int perLevel = 3 * side * side;
            int column = first - foundationLevel;
            if (first >= BuildGrid.Levels || column + perLevel * (BuildGrid.Levels - first) < count)
            {
                if (sideWanted != null) return null;
                continue;
            }
            var waves = new List<IReadOnlyList<PieceSpec>>();
            int total = 0;
            void Add(List<PieceSpec> wave)
            {
                if (wave.Count == 0) return;
                waves.Add(wave);
                total += wave.Count;
            }
            for (int y = foundationLevel; y < first && total < count; y++) Add(new List<PieceSpec> { new(BuildPieceType.Wall, cx, y, cz, 0) });
            for (int y = first; y < BuildGrid.Levels && total < count; y++)
            {
                // Floors: the first level spreads from the column by diagonals (each floor touches one of the previous
                // diagonal); higher levels rest on the walls below, all at once.
                if (y == first)
                {
                    for (int d = 0; d <= 2 * (side - 1) && total < count; d++)
                    {
                        var diagonal = new List<PieceSpec>();
                        for (int i = 0; i < side && total + diagonal.Count < count; i++)
                        {
                            int j = d - i;
                            if (j >= 0 && j < side) diagonal.Add(new PieceSpec(BuildPieceType.Floor, cx + i, y, cz + j, 0));
                        }
                        Add(diagonal);
                    }
                }
                else
                {
                    var floors = new List<PieceSpec>();
                    for (int j = 0; j < side && total + floors.Count < count; j++)
                        for (int i = 0; i < side && total + floors.Count < count; i++) floors.Add(new PieceSpec(BuildPieceType.Floor, cx + i, y, cz + j, 0));
                    Add(floors);
                }
                var walls = new List<PieceSpec>();
                for (int j = 0; j < side && total + walls.Count < count; j++)
                {
                    for (int i = 0; i < side && total + walls.Count < count; i++)
                    {
                        walls.Add(new PieceSpec(BuildPieceType.Wall, cx + i, y, cz + j, 0));
                        if (total + walls.Count < count) walls.Add(new PieceSpec(BuildPieceType.Wall, cx + i, y, cz + j, 1));
                    }
                }
                Add(walls);
            }
            if (total != count) return null;
            PieceSpec foundation = waves[0][0];
            // Only the foundation may rest on the ground or a map box; the block must also be clear of map boxes.
            bool clear = true;
            foreach (IReadOnlyList<PieceSpec> wave in waves)
            {
                foreach (PieceSpec p in wave)
                {
                    if (p == foundation) continue;
                    if (WouldRest(p) || Blocked(p)) clear = false;
                }
            }
            if (!WouldRest(foundation) || Blocked(foundation) || !clear) return null;
            Vector3 aim = foundation.Center;
            Vector3 shooter = StressMap.Ground(x0 + BuildGrid.CellSize * 0.5f, z0 - 4f);
            if (!StressMap.CanStand(shooter) || MathF.Abs(shooter.Y - BuildGrid.LevelBase(foundationLevel)) > 1f) return null;
            if (!LineOfSight.Clear(BotAim.Eye(shooter), aim, GameMap.Boxes, GameMap.Terrain)) return null;
            return new HangingStructure(foundation, waves, total, side, first, aim, shooter);
        }
        return null;
    }

    // Mirrors the server's BuildSupport.IsGrounded (Phase 13 D12): the piece's bottom touches the terrain (or lies under
    // it) within 0.5 m at one of its sample points, or lies within 0.5 m of a map box's top. Used only to choose a place;
    // the server decides (and the scenario checks the collapse it reports).
    public static bool WouldRest(PieceSpec p)
    {
        if (!BuildGrid.TryNormalize(p.Piece, p.X, p.Y, p.Z, p.Rotation, out BuildPieceShape s)) return false;
        float x0 = BuildGrid.CellMinX(s.X), z0 = BuildGrid.CellMinZ(s.Z), y = BuildGrid.LevelBase(s.Y);
        const float c = BuildGrid.CellSize, inset = 0.1f;
        IEnumerable<(float X, float Z)> points = s.Type switch
        {
            BuildPieceType.Wall when s.Rotation == 0 => new[] { (x0 + inset, z0), (x0 + c * 0.5f, z0), (x0 + c - inset, z0) },
            BuildPieceType.Wall => new[] { (x0, z0 + inset), (x0, z0 + c * 0.5f), (x0, z0 + c - inset) },
            _ => new[] { (x0 + c * 0.5f, z0 + c * 0.5f), (x0 + inset, z0 + inset), (x0 + c - inset, z0 + inset), (x0 + inset, z0 + c - inset), (x0 + c - inset, z0 + c - inset) },
        };
        if (s.Type == BuildPieceType.Roof) y = BuildGrid.LevelBase(s.Y + 1);
        foreach ((float px, float pz) in points)
        {
            if (GameMap.Terrain.Height(px, pz) >= y - 0.5f) return true;
            foreach (Box b in GameMap.Boxes)
                if (px >= b.Min.X && px <= b.Max.X && pz >= b.Min.Z && pz <= b.Max.Z && MathF.Abs(b.Max.Y - y) <= 0.5f) return true;
        }
        return false;
    }

    // A map box overlapping the piece's bounds (the structure should hang in the open).
    private static bool Blocked(PieceSpec p)
    {
        if (!BuildGrid.TryNormalize(p.Piece, p.X, p.Y, p.Z, p.Rotation, out BuildPieceShape s)) return true;
        Box bounds = BuildGrid.BoundsOf(s);
        foreach (Box b in GameMap.Boxes)
        {
            if (b.Max.X > bounds.Min.X && b.Min.X < bounds.Max.X && b.Max.Z > bounds.Min.Z && b.Min.Z < bounds.Max.Z && b.Max.Y > bounds.Min.Y && b.Min.Y < bounds.Max.Y)
                return true;
        }
        return false;
    }
}
