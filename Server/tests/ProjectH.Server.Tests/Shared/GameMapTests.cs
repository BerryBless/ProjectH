using System;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 6 D1-D6 layout rules of the shared map. Every coordinate is a binary fraction, so exact comparisons are safe.
public class GameMapTests
{
    private const float Dt = 1f / 30f;
    private const float MinGap = 2f * MoveSettings.HalfWidth + 2f * MoveSettings.Skin;

    private static ReadOnlySpan<Box> Boxes => GameMap.Boxes;
    private static HeightField Terrain => GameMap.Terrain;

    [Fact]
    public void Grid_Covers160By160Metres()
    {
        Assert.Equal(-GameMap.HalfSize, Terrain.OriginX);
        Assert.Equal(-GameMap.HalfSize, Terrain.OriginZ);
        Assert.Equal(GameMap.CellSize, Terrain.CellSize);
        Assert.Equal(GameMap.Verts, Terrain.VertsX);
        Assert.Equal(GameMap.Verts, Terrain.VertsZ);
        Assert.Equal(2f * GameMap.HalfSize, (Terrain.VertsX - 1) * Terrain.CellSize);
    }

    [Fact]
    public void HasAtMost128Boxes_EachWithMinBelowMax()
    {
        Assert.InRange(Boxes.Length, 1, 128);
        foreach (Box box in Boxes)
        {
            Assert.True(box.Min.X < box.Max.X);
            Assert.True(box.Min.Y < box.Max.Y);
            Assert.True(box.Min.Z < box.Max.Z);
        }
    }

    // D4: every slope is walkable, so the terrain never needs to block a horizontal move.
    [Fact]
    public void EveryTerrainTriangle_IsAtMostMaxSlope()
    {
        float cell = Terrain.CellSize;
        for (int j = 0; j < Terrain.VertsZ - 1; j++)
        {
            for (int i = 0; i < Terrain.VertsX - 1; i++)
            {
                float h00 = Terrain.VertexHeight(i, j);
                float h10 = Terrain.VertexHeight(i + 1, j);
                float h01 = Terrain.VertexHeight(i, j + 1);
                float h11 = Terrain.VertexHeight(i + 1, j + 1);
                // Triangle (0,0)-(1,0)-(1,1): dh/dx = (h10 - h00) / cell, dh/dz = (h11 - h10) / cell.
                float a = MathF.Sqrt((h10 - h00) * (h10 - h00) + (h11 - h10) * (h11 - h10)) / cell;
                // Triangle (0,0)-(1,1)-(0,1): dh/dx = (h11 - h01) / cell, dh/dz = (h01 - h00) / cell.
                float b = MathF.Sqrt((h11 - h01) * (h11 - h01) + (h01 - h00) * (h01 - h00)) / cell;
                Assert.True(a <= MoveSettings.MaxSlope && b <= MoveSettings.MaxSlope, $"cell ({i}, {j}): slopes {a}, {b}");
            }
        }
    }

    [Fact]
    public void TerrainHeights_AreQuantized_NotNegative_AtMost12m_AndZeroInTheEdgeBandAndThePlaza()
    {
        for (int j = 0; j < Terrain.VertsZ; j++)
        {
            for (int i = 0; i < Terrain.VertsX; i++)
            {
                float h = Terrain.VertexHeight(i, j);
                float x = Terrain.OriginX + i * Terrain.CellSize;
                float z = Terrain.OriginZ + j * Terrain.CellSize;
                Assert.InRange(h, 0f, 12f);
                Assert.Equal(MathF.Floor(h * Hill.UnitsPerMeter), h * Hill.UnitsPerMeter);
                if (MathF.Abs(x) >= GameMap.HalfSize - 10f || MathF.Abs(z) >= GameMap.HalfSize - 10f)
                    Assert.True(h == 0f, $"edge band ({x}, {z}) is {h}");
                if (x * x + z * z <= GameMap.PlazaRadius * GameMap.PlazaRadius)
                    Assert.True(h == 0f, $"plaza ({x}, {z}) is {h}");
            }
        }
    }

    // D5 (spec interpretation 2): the grid cells around every box, 1 m out, are one flat level, and the box stands
    // on that level or on another box.
    [Fact]
    public void EveryBox_StandsOnFlatTerrain_OrOnAnotherBox()
    {
        for (int k = 0; k < Boxes.Length; k++)
        {
            Box b = Boxes[k];
            int i0 = CellIndex(b.Min.X - 1f), i1 = CellIndex(b.Max.X + 1f) + 1;
            int j0 = CellIndex(b.Min.Z - 1f), j1 = CellIndex(b.Max.Z + 1f) + 1;
            float level = Terrain.VertexHeight(i0, j0);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                    Assert.True(Terrain.VertexHeight(i, j) == level, $"box {k} at {b.Center}: terrain not flat at vertex ({i}, {j})");
            Assert.True(b.Min.Y == level || IsStacked(k), $"box {k} at {b.Center}: base {b.Min.Y}, terrain {level}");
        }
    }

    // D6: two boxes in the same height band are a corner slit apart (narrower than the character), or wide enough to
    // walk through. Touching or overlapping is the next test.
    [Fact]
    public void GapsBetweenBoxes_AreACornerSlit_OrWiderThanTheCharacter()
    {
        for (int i = 0; i < Boxes.Length; i++)
        {
            for (int j = i + 1; j < Boxes.Length; j++)
            {
                if (!SameHeightBand(Boxes[i], Boxes[j])) continue;
                float gap = Gap(Boxes[i], Boxes[j]);
                Assert.True(gap <= 0f || gap <= GameMap.CornerSlit || gap >= MinGap, $"boxes {i} and {j}: gap {gap}");
            }
        }
    }

    // Phase 1 ruling: depenetrating per box traps a character where two boxes side by side share or overlap a
    // vertical face. Stacking (Y ranges only meet) is allowed.
    [Fact]
    public void NoTwoBoxesTouchSideBySide()
    {
        for (int i = 0; i < Boxes.Length; i++)
        {
            for (int j = i + 1; j < Boxes.Length; j++)
            {
                Box a = Boxes[i];
                Box b = Boxes[j];
                bool footprintTouches = a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z;
                Assert.False(SameHeightBand(a, b) && footprintTouches, $"boxes {i} and {j} touch or overlap side by side");
            }
        }
    }

    [Fact]
    public void ThePlaza_HasNoBox()
    {
        foreach (Box box in Boxes)
            Assert.True(FootprintDistance(box, 0f, 0f) >= GameMap.PlazaRadius, $"box at {box.Center} is in the plaza");
    }

    [Fact]
    public void LobbySpawnPositions_DoNotOverlapAnyBox_AndLieInThePlaza()
    {
        for (ushort id = 1; id <= 50; id++)
        {
            Vector3 spawn = Match.SpawnPosition(id);
            Assert.False(MovementSimulation.OverlapsAny(spawn, Boxes), $"entity {id} at {spawn}");
            Assert.True(spawn.Length() < GameMap.PlazaRadius);
        }
    }

    // D6: a character walking at any corner slit, from either side, never gets through it.
    [Fact]
    public void Character_CannotPassThroughAnyCornerSlit()
    {
        int slits = 0;
        for (int i = 0; i < Boxes.Length; i++)
        {
            for (int j = i + 1; j < Boxes.Length; j++)
            {
                Box a = Boxes[i];
                Box b = Boxes[j];
                if (!SameHeightBand(a, b)) continue;
                float gap = Gap(a, b);
                if (gap <= 0f || gap > GameMap.CornerSlit) continue;
                slits++;

                // The slit runs along the axis where the two footprints overlap; its width is on the other axis.
                bool widthOnZ = MathF.Max(b.Min.Z - a.Max.Z, a.Min.Z - b.Max.Z) > 0f;
                float across = widthOnZ ? (MathF.Max(a.Min.Z, b.Min.Z) + MathF.Min(a.Max.Z, b.Max.Z)) * 0.5f
                                        : (MathF.Max(a.Min.X, b.Min.X) + MathF.Min(a.Max.X, b.Max.X)) * 0.5f;
                float runMin = widthOnZ ? MathF.Max(a.Min.X, b.Min.X) : MathF.Max(a.Min.Z, b.Min.Z);
                float runMax = widthOnZ ? MathF.Min(a.Max.X, b.Max.X) : MathF.Min(a.Max.Z, b.Max.Z);
                float floor = MathF.Max(a.Min.Y, b.Min.Y);
                foreach (float side in new[] { -1f, 1f })
                {
                    float start = side < 0f ? runMin - 1f : runMax + 1f;
                    // Review fix D1: a start outside the outer walls is clamped back inside by the first step (ClampToMap), so the
                    // outer corner slits are walked at from the inside only; nothing can stand outside to walk in.
                    if (MathF.Abs(start) > GameMap.HalfSize - MoveSettings.HalfWidth || MathF.Abs(across) > GameMap.HalfSize - MoveSettings.HalfWidth) continue;
                    var s = new MoveState { Position = widthOnZ ? new Vector3(start, floor, across) : new Vector3(across, floor, start) };
                    // Walk towards the far side: yaw 90 = +X, 270 = -X, 0 = +Z, 180 = -Z.
                    float yaw = widthOnZ ? (side < 0f ? 90f : 270f) : (side < 0f ? 0f : 180f);
                    for (int step = 0; step < 90; step++)
                    {
                        MovementSimulation.Step(ref s, new InputCommand { MoveY = 1f, Yaw = yaw }, Dt, Boxes, Terrain);
                        float along = widthOnZ ? s.Position.X : s.Position.Z;
                        bool through = side < 0f ? along > runMax : along < runMin;
                        Assert.False(through, $"slit between boxes {i} and {j}: through at step {step}, {s.Position}");
                    }
                }
            }
        }
        Assert.True(slits >= 4, $"only {slits} slits found (the four map corners at least)");
    }

    [Fact]
    public void LongRandomWalks_AreDeterministic_NeverOverlap_NeverSinkBelowTheTerrain_AndStayInside()
    {
        ReadOnlySpan<Vector3> starts = DropPoints.All;
        for (int w = 0; w < starts.Length; w++)
        {
            var a = new MoveState { Position = starts[w] };
            var b = a;
            for (int i = 0; i < 900; i++)
            {
                var input = new InputCommand
                {
                    MoveX = ((i + w) % 7) / 7f - 0.4f, MoveY = 1f, Yaw = w * 37f + i * 1.7f,
                    Buttons = (i % 25 == 0 ? InputButtons.Jump : InputButtons.None) | (i % 3 == 0 ? InputButtons.Sprint : InputButtons.None),
                };
                MovementSimulation.Step(ref a, input, Dt, Boxes, Terrain);
                MovementSimulation.Step(ref b, input, Dt, Boxes, Terrain);
                // Phase 12 D8: a vault moves without collision (its path may cross the obstacle's edge); it ends clear.
                if (a.Mode != MovementMode.Vault)
                    Assert.False(MovementSimulation.OverlapsAny(a.Position, Boxes), $"walk {w} step {i}: overlaps at {a.Position}");
                Assert.True(a.Position.Y >= Terrain.Height(a.Position.X, a.Position.Z) - 1e-4f, $"walk {w} step {i}: under the terrain at {a.Position}");
                Assert.True(MathF.Abs(a.Position.X) < GameMap.HalfSize && MathF.Abs(a.Position.Z) < GameMap.HalfSize, $"walk {w} step {i}: outside at {a.Position}");
            }
            Assert.Equal(a.Position, b.Position);
            Assert.Equal(a.VelocityY, b.VelocityY);
            Assert.Equal(a.Yaw, b.Yaw);
        }
    }

    // Walking diagonally into each outer corner must not get through the wall (inner faces at +-80).
    [Fact]
    public void Character_CannotLeaveThroughAnOuterCorner()
    {
        foreach (float sx in new[] { -1f, 1f })
        {
            foreach (float sz in new[] { -1f, 1f })
            {
                var s = new MoveState { Position = new Vector3(sx * 78.5f, 0f, sz * 78.5f) };
                float yaw = MathF.Atan2(sx, sz) * 180f / MathF.PI;   // yaw 0 = +Z, 90 = +X
                for (int step = 0; step < 120; step++)
                {
                    MovementSimulation.Step(ref s, new InputCommand { MoveY = 1f, Yaw = yaw }, Dt, Boxes, Terrain);
                    Assert.True(MathF.Abs(s.Position.X) <= 80f && MathF.Abs(s.Position.Z) <= 80f, $"corner ({sx}, {sz}) step {step}: outside at {s.Position}");
                }
            }
        }
    }

    // Every ground-level loot point (inside a building too) and every drop point can be reached on foot from the
    // plaza: a sealed room or an enclosed corner would hold loot nobody can take. Flood fill over a 0.25 m grid of
    // spots where the character, standing on the terrain, overlaps no box (no jumping, so box tops are not needed).
    [Fact]
    public void EveryGroundLootPointAndDropPoint_IsReachableOnFoot()
    {
        const float step = 0.25f;
        const float limit = GameMap.HalfSize - MoveSettings.HalfWidth;
        int size = (int)(2f * limit / step) + 1;
        float Coord(int index) => -limit + index * step;
        bool Free(int i, int j)
        {
            float x = Coord(i), z = Coord(j);
            return !MovementSimulation.OverlapsAny(new Vector3(x, Terrain.Height(x, z), z), Boxes);
        }

        var reached = new bool[size, size];
        var queue = new System.Collections.Generic.Queue<(int, int)>();
        int centre = size / 2;
        Assert.True(Free(centre, centre));
        reached[centre, centre] = true;
        queue.Enqueue((centre, centre));
        while (queue.Count > 0)
        {
            var (i, j) = queue.Dequeue();
            foreach (var (ni, nj) in new[] { (i + 1, j), (i - 1, j), (i, j + 1), (i, j - 1) })
            {
                if (ni < 0 || nj < 0 || ni >= size || nj >= size || reached[ni, nj] || !Free(ni, nj)) continue;
                reached[ni, nj] = true;
                queue.Enqueue((ni, nj));
            }
        }

        bool Reached(Vector3 p)
        {
            int i = (int)MathF.Round((p.X + limit) / step);
            int j = (int)MathF.Round((p.Z + limit) / step);
            return reached[i, j];
        }
        foreach (LootPoint p in LootPoints.All)
        {
            if (p.Position.Y != Terrain.Height(p.Position.X, p.Position.Z)) continue;   // box tops are reached by jumping
            Assert.True(Reached(p.Position), $"loot point {p.Position} ({p.Table}) cannot be reached on foot");
        }
        foreach (Vector3 p in DropPoints.All)
            Assert.True(Reached(p), $"drop point {p} cannot be reached on foot");
    }

    private static int CellIndex(float coordinate)
    {
        int index = (int)MathF.Floor((coordinate - Terrain.OriginX) / Terrain.CellSize);
        return Math.Clamp(index, 0, Terrain.VertsX - 2);
    }

    private static bool IsStacked(int k)
    {
        Box b = Boxes[k];
        for (int m = 0; m < Boxes.Length; m++)
        {
            Box s = Boxes[m];
            if (m != k && s.Max.Y == b.Min.Y && s.Min.X < b.Max.X && s.Max.X > b.Min.X && s.Min.Z < b.Max.Z && s.Max.Z > b.Min.Z)
                return true;
        }
        return false;
    }

    private static bool SameHeightBand(Box a, Box b) => a.Min.Y < b.Max.Y && b.Min.Y < a.Max.Y;

    private static float Gap(Box a, Box b)
    {
        float gapX = MathF.Max(b.Min.X - a.Max.X, a.Min.X - b.Max.X);
        float gapZ = MathF.Max(b.Min.Z - a.Max.Z, a.Min.Z - b.Max.Z);
        return MathF.Max(gapX, gapZ);
    }

    internal static float FootprintDistance(Box b, float x, float z)
    {
        float dx = MathF.Max(MathF.Max(b.Min.X - x, x - b.Max.X), 0f);
        float dz = MathF.Max(MathF.Max(b.Min.Z - z, z - b.Max.Z), 0f);
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
