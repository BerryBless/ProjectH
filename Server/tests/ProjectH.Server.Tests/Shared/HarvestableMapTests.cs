using System;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 13 D6: the map's harvestable objects follow the map's layout rules (GameMapTests) and keep clear of what the
// earlier phases placed: loot points, drop points, doors, the plaza.
public class HarvestableMapTests
{
    private const float MinGap = 2f * MoveSettings.HalfWidth + 2f * MoveSettings.Skin;

    private static ReadOnlySpan<Harvestable> All => GameMap.Harvestables;

    [Fact]
    public void ThereAreAbout40_AtMost64_OfEveryKind()
    {
        Assert.Equal(41, All.Length);
        Assert.True(All.Length <= GameMap.MaxHarvestables);
        int[] kinds = new int[4];
        foreach (Harvestable h in All) kinds[(int)h.Kind]++;
        Assert.Equal(new[] { 23, 8, 6, 4 }, kinds);
        Assert.Equal(BuildMaterialType.Wood, new Harvestable(default, HarvestKind.Tree).Material);
        Assert.Equal(BuildMaterialType.Wood, new Harvestable(default, HarvestKind.Crate).Material);
        Assert.Equal(BuildMaterialType.Stone, new Harvestable(default, HarvestKind.Rock).Material);
        Assert.Equal(BuildMaterialType.Metal, new Harvestable(default, HarvestKind.Wreck).Material);
    }

    [Fact]
    public void EachStandsOnFlatTerrain_InsideTheWalls_OutsideThePlaza()
    {
        foreach (Harvestable h in All)
        {
            Box b = h.Bounds;
            float level = GameMap.Terrain.Height(b.Center.X, b.Center.Z);
            Assert.Equal(level, b.Min.Y);
            for (float z = b.Min.Z - 1f; z <= b.Max.Z + 1f; z += 0.5f)
                for (float x = b.Min.X - 1f; x <= b.Max.X + 1f; x += 0.5f)
                    Assert.True(GameMap.Terrain.Height(x, z) == level, $"{h.Kind} at {b.Center}: terrain not flat at ({x}, {z})");
            Assert.True(MathF.Abs(b.Center.X) <= GameMap.HalfSize - 5f && MathF.Abs(b.Center.Z) <= GameMap.HalfSize - 5f);
            Assert.True(new Vector2(b.Center.X, b.Center.Z).Length() >= GameMap.PlazaRadius + 2f);
        }
    }

    // GameMapTests' rule for boxes, for harvestables among themselves and against the boxes and doors: never touching side
    // by side, and a gap the character either fits through or not at all.
    [Fact]
    public void NoneTouchesABoxADoorOrAnother_AndEveryGapIsWalkable()
    {
        for (int i = 0; i < All.Length; i++)
        {
            Box a = All[i].Bounds;
            for (int j = i + 1; j < All.Length; j++) AssertApart(a, All[j].Bounds, $"harvestables {i} and {j}");
            for (int k = 0; k < GameMap.Boxes.Length; k++) AssertApart(a, GameMap.Boxes[k], $"harvestable {i} and box {k}");
            for (int d = 0; d < GameMap.Doors.Length; d++) AssertApart(a, GameMap.Doors[d], $"harvestable {i} and door {d}");
        }
    }

    private static void AssertApart(Box a, Box b, string what)
    {
        bool sameBand = a.Min.Y < b.Max.Y && b.Min.Y < a.Max.Y;
        if (!sameBand) return;
        float gap = MathF.Max(MathF.Max(b.Min.X - a.Max.X, a.Min.X - b.Max.X), MathF.Max(b.Min.Z - a.Max.Z, a.Min.Z - b.Max.Z));
        Assert.True(gap >= MinGap, $"{what}: gap {gap}");
    }

    [Fact]
    public void LootAndDropPointsAndDoors_StayClear()
    {
        foreach (Harvestable h in All)
        {
            var c = new Vector2(h.Bounds.Center.X, h.Bounds.Center.Z);
            foreach (LootPoint loot in LootPoints.All)
                Assert.True(Vector2.Distance(c, new Vector2(loot.Position.X, loot.Position.Z)) >= 3f, $"{h.Kind} at {c} by loot {loot.Position}");
            foreach (Vector3 drop in DropPoints.All)
                Assert.True(Vector2.Distance(c, new Vector2(drop.X, drop.Z)) >= DropPoints.ClearRadius + 2f, $"{h.Kind} at {c} by drop {drop}");
            foreach (Box door in GameMap.Doors)
                Assert.True(Vector2.Distance(c, new Vector2(door.Center.X, door.Center.Z)) >= 5f, $"{h.Kind} at {c} by a door");
        }
    }

    // The four crates moved from the boxes (no loot point on their tops); the two the earlier tests use stay boxes.
    [Fact]
    public void TheCrates_AreTheFourWithoutLoot_AndTheTestedOnesAreStillBoxes()
    {
        Vector3[] crates = { new(58f, 0.75f, 60f), new(8f, 0.5f, -24f), new(70f, 0.75f, -10f), new(-70f, 0.75f, 20f) };
        int n = 0;
        foreach (Harvestable h in All)
        {
            if (h.Kind != HarvestKind.Crate) continue;
            Assert.Equal(crates[n++], h.Bounds.Center);
        }
        foreach (Box box in GameMap.Boxes)
        {
            foreach (Vector3 c in crates) Assert.NotEqual(c, box.Center);
        }
        bool vaultCrate = false;
        bool predictionCrate = false;
        foreach (Box box in GameMap.Boxes)
        {
            vaultCrate |= box.Center == new Vector3(34f, 0.75f, 60f);
            predictionCrate |= box.Center == new Vector3(0f, 0.5f, 22f);
        }
        Assert.True(vaultCrate && predictionCrate);
    }

    [Fact]
    public void AStandingHarvestable_BlocksAWalk_AndADestroyedOneDoesNot()
    {
        Box trunk = All[0].Bounds;   // tree 0 at (-36, 64)
        var world = new CollisionWorld();
        foreach (ulong destroyed in new[] { 0UL, 1UL })
        {
            var s = new MoveState { Position = new Vector3(-36f, 0f, 62f) };
            for (int i = 0; i < 40; i++)
            {
                world.Gather(s.Position, 0, destroyed, null);
                MovementSimulation.Step(ref s, new InputCommand { MoveY = 1f, Yaw = 0f }, 1f / 30f, world, GameMap.Terrain, out _);
            }
            if (destroyed == 0) Assert.True(s.Position.Z <= trunk.Min.Z - MoveSettings.HalfWidth);
            else Assert.True(s.Position.Z > trunk.Max.Z + 1f);
        }
    }

    [Fact]
    public void LobbySpawns_AndTheMapsBoxesRule_StillHold()
    {
        for (ushort id = 1; id <= 50; id++)
        {
            Vector3 spawn = Match.SpawnPosition(id);
            foreach (Harvestable h in All) Assert.False(MovementSimulation.OverlapsAny(spawn, new[] { h.Bounds }));
        }
    }
}
