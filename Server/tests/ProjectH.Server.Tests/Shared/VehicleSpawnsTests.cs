using System;
using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 19 D8: where the match start places the vehicles.
public class VehicleSpawnsTests
{
    [Fact]
    public void ThereAreFour_WithinTheVehicleLimit()
    {
        Assert.Equal(VehicleSpawns.Count, VehicleSpawns.All.Length);
        Assert.True(VehicleSpawns.Count <= VehicleSettings.MaxVehicles);
    }

    [Fact]
    public void EveryPoint_IsOnTheTerrain_InsideTheBound_OutsideThePlazaAndEveryPoi()
    {
        foreach (Vector3 p in VehicleSpawns.All)
        {
            Assert.Equal(GameMap.Terrain.Height(p.X, p.Z), p.Y);
            Assert.True(MathF.Abs(p.X) <= VehicleSettings.MapBound - VehicleSpawns.ClearRadius, $"{p}");
            Assert.True(MathF.Abs(p.Z) <= VehicleSettings.MapBound - VehicleSpawns.ClearRadius, $"{p}");
            foreach (MapPoi poi in MapPois.All)
            {
                float d = MathF.Sqrt((p.X - poi.X) * (p.X - poi.X) + (p.Z - poi.Z) * (p.Z - poi.Z));
                Assert.True(d >= poi.Radius, $"{p} is in {poi.Name}");
            }
        }
    }

    [Fact]
    public void NothingOfTheMap_WithinTheClearRadius()
    {
        foreach (Vector3 p in VehicleSpawns.All)
        {
            foreach (Box b in GameMap.Boxes)
                Assert.True(GameMapTests.FootprintDistance(b, p.X, p.Z) >= VehicleSpawns.ClearRadius, $"{p}: box at {b.Center}");
            foreach (Box b in GameMap.Doors)
                Assert.True(GameMapTests.FootprintDistance(b, p.X, p.Z) >= VehicleSpawns.ClearRadius, $"{p}: door at {b.Center}");
            foreach (Harvestable h in GameMap.Harvestables)
                Assert.True(GameMapTests.FootprintDistance(h.Bounds, p.X, p.Z) >= VehicleSpawns.ClearRadius, $"{p}: harvestable at {h.Bounds.Center}");
            foreach (LootPoint l in LootPoints.All)
                Assert.True(Flat(l.Position, p) >= VehicleSpawns.ClearRadius, $"{p}: loot point {l.Position}");
            foreach (LootContainer c in LootContainers.All)
                Assert.True(Flat(c.Position, p) >= VehicleSpawns.ClearRadius, $"{p}: container {c.Position}");
            foreach (Vector3 s in RebootStations.All)
                Assert.True(Flat(s, p) >= VehicleSpawns.ClearRadius, $"{p}: station {s}");
        }
    }

    // The start-overlap rule would let a car spawned inside a box drive through it: the footprint at the spawn heading must
    // overlap nothing the gather returns, and the terrain under it must be gentle enough to drive.
    [Fact]
    public void TheFootprint_AtTheSpawnHeading_OverlapsNothing_AndTheGroundIsDrivable()
    {
        var world = new CollisionWorld();
        for (int i = 0; i < VehicleSpawns.Count; i++)
        {
            Vector3 p = VehicleSpawns.All[i];
            float heading = VehicleSpawns.Heading(i);
            float y = VehicleSimulation.GroundHeight(p.X, p.Z, heading, GameMap.Terrain);
            var centre = new Vector3(p.X, y, p.Z);
            world.Gather(centre, 0, 0, null);
            Box front = VehicleSimulation.FootprintBox(centre, heading, 0);
            Box rear = VehicleSimulation.FootprintBox(centre, heading, 1);
            foreach (Box b in world.Boxes)
                Assert.False(VehicleSimulation.Touches(front, b) || VehicleSimulation.Touches(rear, b), $"spawn {i} overlaps {b.Center}");
            Vector2 g = GameMap.Terrain.Gradient(p.X, p.Z);
            Assert.True(g.Length() <= VehicleSettings.MaxClimbSlope, $"spawn {i} slope {g}");
            Assert.True(y - p.Y < 1f, $"spawn {i} sits {y - p.Y} m above its point");
        }
    }

    // 기능: 두 점의 지면(X·Z) 거리를 잰다.
    // 입력: a, b - 점.
    // 출력: 거리.
    private static float Flat(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
