using System;
using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 6 D9: the match start places the participants on these points.
public class DropPointsTests
{
    [Fact]
    public void ThereAre24_AtLeastTheDefaultMaxPlayers()
    {
        Assert.Equal(24, DropPoints.All.Length);
        Assert.True(DropPoints.All.Length >= new ProjectH.Server.ServerOptions().MaxPlayers);
    }

    [Fact]
    public void EveryPoint_IsOnTheTerrain_InsideTheWalls_AndOutsideThePlaza()
    {
        foreach (Vector3 p in DropPoints.All)
        {
            Assert.Equal(GameMap.Terrain.Height(p.X, p.Z), p.Y);
            Assert.True(MathF.Abs(p.X) <= GameMap.HalfSize - DropPoints.WallMargin, $"{p} too close to a wall");
            Assert.True(MathF.Abs(p.Z) <= GameMap.HalfSize - DropPoints.WallMargin, $"{p} too close to a wall");
            Assert.True(MathF.Sqrt(p.X * p.X + p.Z * p.Z) >= DropPoints.MinFromCentre, $"{p} too close to the centre");
        }
    }

    [Fact]
    public void NoBox_WithinTheClearRadius()
    {
        foreach (Vector3 p in DropPoints.All)
            foreach (Box b in GameMap.Boxes)
                Assert.True(GameMapTests.FootprintDistance(b, p.X, p.Z) >= DropPoints.ClearRadius, $"{p}: box at {b.Center}");
    }

    [Fact]
    public void ThePointsAreAtLeastMinSpacingApart()
    {
        ReadOnlySpan<Vector3> points = DropPoints.All;
        for (int i = 0; i < points.Length; i++)
        {
            for (int j = i + 1; j < points.Length; j++)
            {
                float dx = points[i].X - points[j].X;
                float dz = points[i].Z - points[j].Z;
                Assert.True(MathF.Sqrt(dx * dx + dz * dz) >= DropPoints.MinSpacing, $"points {i} and {j}");
            }
        }
    }
}
