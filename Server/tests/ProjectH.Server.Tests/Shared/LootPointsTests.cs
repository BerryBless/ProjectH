using System;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 4 D6 / Phase 6 D10: the loot points are map data next to GameMap. They must stay valid whenever either changes.
public class LootPointsTests
{
    // Jump apex at 30 Hz is about 1.34 m (Networking.md), so a 1 m step is climbable and 1.5 m is not.
    private const float MaxStep = 1f;
    private const float PickupRange = 2f;   // Phase 4 D8 horizontal range
    private const float InnerRing = 8f;

    [Fact]
    public void Count_IsAtMost64_AndTablesAreNamed()
    {
        Assert.InRange(LootPoints.All.Length, 1, 64);
        foreach (LootPoint p in LootPoints.All)
            Assert.True(p.Table == LootPoints.FloorTable || p.Table == LootPoints.BuildingTable || p.Table == LootPoints.TowerTable, p.Table);
    }

    [Fact]
    public void EveryPoint_StandsClearOfTheBoxes()
    {
        foreach (LootPoint p in LootPoints.All)
            Assert.False(MovementSimulation.OverlapsAny(p.Position, GameMap.Boxes), $"{p.Position} overlaps a box");
    }

    // On the terrain, or on top of a box a player can climb to.
    [Fact]
    public void EveryPoint_IsOnTheTerrain_OrOnAReachableBoxTop()
    {
        foreach (LootPoint p in LootPoints.All)
        {
            Vector3 pos = p.Position;
            if (pos.Y == GameMap.Terrain.Height(pos.X, pos.Z)) continue;
            int top = BoxUnder(pos);
            Assert.True(top >= 0, $"{pos}: neither on the terrain nor on a box top");
            Assert.True(IsReachable(top), $"{pos}: box top out of reach");
        }
    }

    // The plaza holds only the four inner-ring points the integration test walks to.
    [Fact]
    public void InsideThePlaza_OnlyTheFourInnerRingPoints()
    {
        int inner = 0;
        foreach (LootPoint p in LootPoints.All)
        {
            float fromCentre = MathF.Sqrt(p.Position.X * p.Position.X + p.Position.Z * p.Position.Z);
            if (fromCentre >= GameMap.PlazaRadius) continue;
            Assert.Equal(InnerRing, fromCentre);
            Assert.Equal(LootPoints.FloorTable, p.Table);
            inner++;
        }
        Assert.Equal(4, inner);
    }

    // D10: a Building point lies under a roof (a box at least 2.5 m up covers it).
    [Fact]
    public void EveryBuildingPoint_IsUnderARoof()
    {
        foreach (LootPoint p in LootPoints.All)
        {
            if (p.Table != LootPoints.BuildingTable) continue;
            bool covered = false;
            foreach (Box b in GameMap.Boxes)
            {
                if (b.Min.Y >= p.Position.Y + 2.5f && p.Position.X > b.Min.X && p.Position.X < b.Max.X && p.Position.Z > b.Min.Z && p.Position.Z < b.Max.Z)
                    covered = true;
            }
            Assert.True(covered, $"{p.Position} is not under a roof");
        }
    }

    // Phase 4 D8: two points within pickup range of each other would make "nearest" ambiguous.
    [Fact]
    public void NoTwoPoints_AreWithinPickupRange()
    {
        ReadOnlySpan<LootPoint> points = LootPoints.All;
        for (int i = 0; i < points.Length; i++)
        {
            for (int j = i + 1; j < points.Length; j++)
            {
                Vector3 d = points[i].Position - points[j].Position;
                Assert.True(d.X * d.X + d.Z * d.Z > PickupRange * PickupRange, $"points {i} and {j}");
            }
        }
    }

    // The integration test walks straight from its lobby spawn point to the nearest inner-ring item. That walk
    // must never be blocked, for any spawn position.
    [Fact]
    public void StraightWalk_FromEveryLobbySpawn_ToEveryInnerPoint_IsClear()
    {
        for (ushort id = 1; id <= 50; id++)
        {
            Vector3 spawn = Match.SpawnPosition(id);
            foreach (LootPoint p in LootPoints.All)
            {
                if (p.Position.Length() > InnerRing + 0.01f) continue;
                for (int step = 0; step <= 100; step++)
                {
                    Vector3 at = Vector3.Lerp(spawn, p.Position, step / 100f);
                    Assert.False(MovementSimulation.OverlapsAny(at, GameMap.Boxes), $"spawn {id} -> {p.Position} blocked at {at}");
                }
            }
        }
    }

    // Index of the box whose top the point stands on (footprint contains it, top at its height), or -1.
    private static int BoxUnder(Vector3 pos)
    {
        ReadOnlySpan<Box> boxes = GameMap.Boxes;
        for (int i = 0; i < boxes.Length; i++)
        {
            Box b = boxes[i];
            if (b.Max.Y == pos.Y && pos.X > b.Min.X && pos.X < b.Max.X && pos.Z > b.Min.Z && pos.Z < b.Max.Z) return i;
        }
        return -1;
    }

    // A box top is reachable if its height above what it stands on is at most one jump, and what it stands on (the
    // terrain under it, or another box top) is reachable.
    private static bool IsReachable(int index)
    {
        Box box = GameMap.Boxes[index];
        if (box.Max.Y - box.Min.Y > MaxStep) return false;
        if (box.Min.Y == GameMap.Terrain.Height(box.Center.X, box.Center.Z)) return true;
        ReadOnlySpan<Box> boxes = GameMap.Boxes;
        for (int i = 0; i < boxes.Length; i++)
        {
            Box below = boxes[i];
            bool supports = i != index && below.Max.Y == box.Min.Y &&
                            below.Min.X < box.Max.X && below.Max.X > box.Min.X &&
                            below.Min.Z < box.Max.Z && below.Max.Z > box.Min.Z;
            if (supports && IsReachable(i)) return true;
        }
        return false;
    }
}
