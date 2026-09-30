using System;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// D6: the spawn points are map data next to TestArena. They must stay valid whenever either changes.
public class LootPointsTests
{
    // Jump apex at 30 Hz is about 1.34 m (Networking.md), so a 1 m step is climbable and 1.5 m is not.
    private const float MaxStep = 1f;
    private const float PickupRange = 2f;   // D8 horizontal range

    [Fact]
    public void Count_IsSmall_AndTablesAreNamed()
    {
        Assert.InRange(LootPoints.All.Length, 1, 64);
        foreach (LootPoint p in LootPoints.All)
            Assert.True(p.Table == LootPoints.FloorTable || p.Table == LootPoints.TowerTable, p.Table);
    }

    [Fact]
    public void EveryPoint_StandsClearOfTheBoxes()
    {
        foreach (LootPoint p in LootPoints.All)
            Assert.False(MovementSimulation.OverlapsAny(p.Position, TestArena.Boxes), $"{p.Position} overlaps a box");
    }

    // Spec §6: on the floor outside the 7 m centre, or on top of a box a player can climb to.
    [Fact]
    public void EveryPoint_IsOnTheFloorOutsideTheCentre_OrOnAReachableBoxTop()
    {
        foreach (LootPoint p in LootPoints.All)
        {
            Vector3 pos = p.Position;
            if (pos.Y == 0f)
            {
                float fromCentre = MathF.Sqrt(pos.X * pos.X + pos.Z * pos.Z);
                Assert.True(fromCentre >= TestArena.ClearRadius, $"{pos}: floor point inside the clear centre");
                continue;
            }
            int top = BoxUnder(pos);
            Assert.True(top >= 0, $"{pos}: not on a box top");
            Assert.True(IsReachable(top), $"{pos}: box top out of reach");
        }
    }

    // D8: two points within pickup range of each other would make "nearest" ambiguous.
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

    // The integration test walks straight from its spawn point to the nearest inner-ring item. That walk
    // must never be blocked, for any spawn position.
    [Fact]
    public void StraightWalk_FromEverySpawn_ToEveryInnerPoint_IsClear()
    {
        for (ushort id = 1; id <= 50; id++)
        {
            Vector3 spawn = Match.SpawnPosition(id);
            foreach (LootPoint p in LootPoints.All)
            {
                if (p.Position.Y != 0f || p.Position.Length() > 8.01f) continue;
                for (int step = 0; step <= 100; step++)
                {
                    Vector3 at = Vector3.Lerp(spawn, p.Position, step / 100f);
                    Assert.False(MovementSimulation.OverlapsAny(at, TestArena.Boxes), $"spawn {id} -> {p.Position} blocked at {at}");
                }
            }
        }
    }

    // Index of the box whose top the point stands on (footprint contains it, top at its height), or -1.
    private static int BoxUnder(Vector3 pos)
    {
        ReadOnlySpan<Box> boxes = TestArena.Boxes;
        for (int i = 0; i < boxes.Length; i++)
        {
            Box b = boxes[i];
            if (b.Max.Y == pos.Y && pos.X > b.Min.X && pos.X < b.Max.X && pos.Z > b.Min.Z && pos.Z < b.Max.Z) return i;
        }
        return -1;
    }

    // A box top is reachable if its height above what it stands on is at most one jump, and what it
    // stands on (the floor or another box top) is reachable.
    private static bool IsReachable(int index)
    {
        Box box = TestArena.Boxes[index];
        if (box.Max.Y - box.Min.Y > MaxStep) return false;
        if (box.Min.Y == 0f) return true;
        ReadOnlySpan<Box> boxes = TestArena.Boxes;
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
