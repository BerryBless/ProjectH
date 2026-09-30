using System;
using System.Numerics;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

public class HitScanTests
{
    private static readonly Vector3 BoxMin = new(-1f, 0f, 9f);
    private static readonly Vector3 BoxMax = new(1f, 2f, 11f);

    [Fact]
    public void RayAabb_HeadOn_HitsNearFace()
    {
        Assert.True(HitScan.IntersectAabb(new Vector3(0f, 1f, 0f), Vector3.UnitZ, BoxMin, BoxMax, 100f, out float d));
        Assert.Equal(9f, d, 5);
    }

    [Fact]
    public void RayAabb_PassingBeside_Misses()
    {
        Assert.False(HitScan.IntersectAabb(new Vector3(1.01f, 1f, 0f), Vector3.UnitZ, BoxMin, BoxMax, 100f, out _));
        var diagonal = Vector3.Normalize(new Vector3(1f, 0f, 1f));
        Assert.False(HitScan.IntersectAabb(new Vector3(0f, 1f, 0f), diagonal, BoxMin, BoxMax, 100f, out _));
    }

    [Fact]
    public void RayAabb_StartingInside_HitsAtZero()
    {
        Assert.True(HitScan.IntersectAabb(new Vector3(0f, 1f, 10f), Vector3.UnitZ, BoxMin, BoxMax, 100f, out float d));
        Assert.Equal(0f, d);
    }

    [Fact]
    public void RayAabb_Parallel_HitsOnlyInsideSlab()
    {
        // Direction has zero X and Y: the X and Y slabs never end, so only the origin decides.
        Assert.True(HitScan.IntersectAabb(new Vector3(0.5f, 1f, 0f), Vector3.UnitZ, BoxMin, BoxMax, 100f, out _));
        Assert.False(HitScan.IntersectAabb(new Vector3(0.5f, 2.5f, 0f), Vector3.UnitZ, BoxMin, BoxMax, 100f, out _));
        // Moving away from the box along a parallel line.
        Assert.False(HitScan.IntersectAabb(new Vector3(0f, 1f, 0f), -Vector3.UnitZ, BoxMin, BoxMax, 100f, out _));
    }

    [Theory]
    [InlineData(float.NaN, 0f, 1f)]
    [InlineData(0f, float.PositiveInfinity, 1f)]
    [InlineData(0f, 0f, float.NegativeInfinity)]
    public void RayAabb_NonFiniteDirection_Misses(float x, float y, float z)
    {
        Assert.False(HitScan.IntersectAabb(new Vector3(0f, 1f, 0f), new Vector3(x, y, z), BoxMin, BoxMax, 100f, out float d));
        Assert.Equal(0f, d);
    }

    [Fact]
    public void RayAabb_BeyondMaxDistance_Misses()
    {
        Assert.False(HitScan.IntersectAabb(new Vector3(0f, 1f, 0f), Vector3.UnitZ, BoxMin, BoxMax, 8.99f, out _));
        Assert.True(HitScan.IntersectAabb(new Vector3(0f, 1f, 0f), Vector3.UnitZ, BoxMin, BoxMax, 9f, out _));
    }

    [Fact]
    public void TraceWorld_StopsAtPillar()
    {
        // Pillar (9, 1.5, 9), 1 x 3 x 1: near face at z = 8.5.
        float d = HitScan.TraceWorld(new Vector3(9f, 1.6f, 6f), Vector3.UnitZ, 100f, TestArena.Boxes);
        Assert.Equal(2.5f, d, 4);
    }

    [Fact]
    public void TraceWorld_DownwardRay_StopsAtFloor()
    {
        var down = Vector3.Normalize(new Vector3(0f, -1f, 1f));
        float d = HitScan.TraceWorld(new Vector3(0f, 1.6f, 0f), down, 100f, TestArena.Boxes);
        Assert.Equal(1.6f * MathF.Sqrt(2f), d, 4);
    }

    [Fact]
    public void TraceWorld_NothingInRange_ReturnsRange()
    {
        // From the centre towards +Z: the low box at z 11..13 is only 1 m high, so a 1.6 m ray passes over it,
        // and the north wall is at z = 19.5, beyond the 10 m range.
        Assert.Equal(10f, HitScan.TraceWorld(new Vector3(0f, 1.6f, 0f), Vector3.UnitZ, 10f, TestArena.Boxes));
    }

    [Fact]
    public void TracePlayer_UsesMovementBox()
    {
        var feet = new Vector3(0f, 0f, 5f);
        Assert.True(HitScan.TracePlayer(new Vector3(0.34f, 1.6f, 0f), Vector3.UnitZ, 100f, feet, out float d));
        Assert.Equal(5f - MoveSettings.HalfWidth, d, 5);
        Assert.False(HitScan.TracePlayer(new Vector3(0.36f, 1.6f, 0f), Vector3.UnitZ, 100f, feet, out _));   // beside
        Assert.False(HitScan.TracePlayer(new Vector3(0f, 1.81f, 0f), Vector3.UnitZ, 100f, feet, out _));     // over the head
    }
}
