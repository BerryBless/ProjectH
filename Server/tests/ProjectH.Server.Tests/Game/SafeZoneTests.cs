using System;
using System.Numerics;
using ProjectH.Server.Game.Zone;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Spec §6 SafeZone: seeded circles, containment, arena bound, linear shrink, radius 0 at the end, no allocation.
public class SafeZoneTests
{
    private static SafeZone Started(uint startTick, int seed, string json = TestGameData.ZonesJson)
    {
        var zone = new SafeZone(TestGameData.Zones(json: json));
        zone.Start(startTick, seed);
        return zone;
    }

    [Fact]
    public void BeforeStart_IsTheFirstCircle_WithNoDamage()
    {
        var zone = new SafeZone(TestGameData.Zones());
        Assert.Equal(0, zone.Phase);
        Assert.Equal(0, zone.DamagePerSecond);
        zone.Sample(12345.0, out float x, out float z, out float radius);
        Assert.Equal((0f, 0f, 30f), (x, z, radius));
        Assert.False(zone.Advance(1_000_000));
        // The first circle covers the whole 39 x 39 m arena, corners included (27.6 m from the center).
        Assert.False(zone.IsOutside(new Vector3(19.5f, 0f, -19.5f), 0));

        ZoneState wire = zone.ToWire();
        Assert.Equal(0, wire.Phase);
        Assert.Equal(30f, wire.FromRadius);
        Assert.Equal(30f, wire.ToRadius);
    }

    [Fact]
    public void SameSeed_GivesTheSameCircles()
    {
        SafeZone a = Started(100, seed: 7);
        SafeZone b = Started(900, seed: 7);
        for (int p = 0; p <= a.PhaseCount; p++)
        {
            Assert.Equal(a.CenterX(p), b.CenterX(p));
            Assert.Equal(a.CenterZ(p), b.CenterZ(p));
            Assert.Equal(a.Radius(p), b.Radius(p));
        }
    }

    [Fact]
    public void DifferentSeeds_MoveTheCircles()
    {
        SafeZone a = Started(0, seed: 1);
        SafeZone b = Started(0, seed: 2);
        Assert.NotEqual((a.CenterX(1), a.CenterZ(1)), (b.CenterX(1), b.CenterZ(1)));
    }

    // D6 / Review Focus: each new circle lies completely inside the previous one and its center never leaves the
    // arena bound, for 1000 seeds.
    [Fact]
    public void EveryNextCircle_IsInsideTheCurrent_AndCentersStayInTheArena_For1000Seeds()
    {
        var zone = new SafeZone(TestGameData.Zones());
        for (int seed = 0; seed < 1000; seed++)
        {
            zone.Start(0, seed);
            for (int p = 1; p <= zone.PhaseCount; p++)
            {
                float dx = zone.CenterX(p) - zone.CenterX(p - 1);
                float dz = zone.CenterZ(p) - zone.CenterZ(p - 1);
                Assert.True(MathF.Sqrt(dx * dx + dz * dz) + zone.Radius(p) <= zone.Radius(p - 1),
                    $"seed {seed}: circle {p} leaves circle {p - 1}");
                Assert.InRange(zone.CenterX(p), -19.5f, 19.5f);
                Assert.InRange(zone.CenterZ(p), -19.5f, 19.5f);
            }
        }
    }

    [Fact]
    public void Schedule_FollowsTheData_AndTheLastPhaseNeverEnds()
    {
        SafeZone zone = Started(1000, seed: 3);
        Assert.Equal(1, zone.Phase);
        Assert.Equal(1u, zone.DamagePerSecond);
        Assert.Equal(1600u, zone.ShrinkStartTick);   // 20 s wait
        Assert.Equal(2050u, zone.ShrinkEndTick);     // 15 s shrink

        Assert.False(zone.Advance(2049));
        Assert.True(zone.Advance(2050));
        Assert.Equal(2, zone.Phase);
        Assert.Equal(2u, zone.DamagePerSecond);
        Assert.Equal(2050u + 450u, zone.ShrinkStartTick);
        Assert.Equal(2050u + 450u + 360u, zone.ShrinkEndTick);

        // Late calls do not shift the schedule: phase 3 starts at phase 2's end, not at the call.
        uint phase2End = zone.ShrinkEndTick;
        Assert.True(zone.Advance(phase2End + 100));
        Assert.Equal(phase2End + 360u, zone.ShrinkStartTick);

        Assert.True(zone.Advance(zone.ShrinkEndTick));
        Assert.True(zone.Advance(zone.ShrinkEndTick));
        Assert.True(zone.IsFinalPhase);
        Assert.Equal(5, zone.Phase);
        Assert.Equal(20u, zone.DamagePerSecond);
        Assert.False(zone.Advance(uint.MaxValue));
        Assert.Equal(5, zone.Phase);
    }

    [Fact]
    public void Shrink_IsLinear_BetweenTheTwoCircles()
    {
        SafeZone zone = Started(0, seed: 11);
        uint start = zone.ShrinkStartTick;
        uint end = zone.ShrinkEndTick;

        zone.Sample(start, out float x0, out float z0, out float r0);
        Assert.Equal((zone.CenterX(0), zone.CenterZ(0), zone.Radius(0)), (x0, z0, r0));
        zone.Sample(start - 50, out _, out _, out float waiting);
        Assert.Equal(30f, waiting);

        zone.Sample((start + end) / 2.0, out float xm, out float zm, out float rm);
        Assert.Equal((zone.CenterX(0) + zone.CenterX(1)) / 2f, xm, 4);
        Assert.Equal((zone.CenterZ(0) + zone.CenterZ(1)) / 2f, zm, 4);
        Assert.Equal(25f, rm, 4);

        zone.Sample(start + (end - start) / 4.0, out _, out _, out float quarter);
        Assert.Equal(27.5f, quarter, 4);

        zone.Sample(end, out float x1, out float z1, out float r1);
        Assert.Equal((zone.CenterX(1), zone.CenterZ(1), 20f), (x1, z1, r1));
        zone.Sample(end + 5000, out _, out _, out float after);
        Assert.Equal(20f, after);
    }

    [Fact]
    public void LastPhase_ClosesToRadiusZero_AndNobodyIsInside()
    {
        SafeZone zone = Started(0, seed: 5, TestGameData.ShortZonesJson);
        Assert.True(zone.Advance(zone.ShrinkEndTick));
        Assert.True(zone.IsFinalPhase);
        uint end = zone.ShrinkEndTick;

        zone.Sample(end, out float x, out float z, out float radius);
        Assert.Equal(0f, radius);
        // Spec fix: even the exact final center is outside a radius-0 circle, so the last phase always ends the match.
        Assert.True(zone.IsOutside(new Vector3(x, 0f, z), end));
        Assert.True(zone.IsOutside(new Vector3(x, 5f, z), end + 1000));
    }

    [Fact]
    public void IsOutside_UsesTheHorizontalDistance_AndTheEdgeIsInside()
    {
        SafeZone zone = Started(0, seed: 9);
        uint end = zone.ShrinkEndTick;   // phase 1 done: radius 20 around circle 1
        float cx = zone.CenterX(1);
        float cz = zone.CenterZ(1);

        Assert.False(zone.IsOutside(new Vector3(cx, 0f, cz), end));
        Assert.False(zone.IsOutside(new Vector3(cx + 20f, 0f, cz), end));    // on the edge
        Assert.True(zone.IsOutside(new Vector3(cx + 20.01f, 0f, cz), end));
        Assert.False(zone.IsOutside(new Vector3(cx, 50f, cz + 19.9f), end)); // height does not count
        // During the wait the circle is still the first one.
        Assert.False(zone.IsOutside(new Vector3(cx + 20.01f, 0f, cz), zone.ShrinkStartTick - 1));
    }

    [Fact]
    public void ToWire_DescribesTheCurrentPhase()
    {
        SafeZone zone = Started(10, seed: 4);
        zone.Advance(zone.ShrinkEndTick);
        ZoneState wire = zone.ToWire();

        Assert.Equal(2, wire.Phase);
        Assert.Equal(zone.CenterX(1), wire.FromX);
        Assert.Equal(zone.CenterZ(1), wire.FromZ);
        Assert.Equal(20f, wire.FromRadius);
        Assert.Equal(zone.CenterX(2), wire.ToX);
        Assert.Equal(zone.CenterZ(2), wire.ToZ);
        Assert.Equal(12f, wire.ToRadius);
        Assert.Equal(zone.ShrinkStartTick, wire.ShrinkStartTick);
        Assert.Equal(zone.ShrinkEndTick, wire.ShrinkEndTick);
        Assert.Equal(2, wire.DamagePerSecond);
    }

    [Fact]
    public void Reset_GoesBackToNoZone()
    {
        SafeZone zone = Started(0, seed: 4);
        zone.Advance(zone.ShrinkEndTick);
        zone.Reset();
        Assert.Equal(0, zone.Phase);
        Assert.Equal(0, zone.DamagePerSecond);
        Assert.Equal(30f, zone.ToWire().ToRadius);
    }

    [Fact]
    public void SampleAdvanceAndIsOutside_DoNotAllocate()
    {
        SafeZone zone = Started(0, seed: 4);
        var feet = new Vector3(3f, 0f, 4f);
        bool outside = false;
        zone.IsOutside(feet, 1);   // JIT before measuring
        zone.ToWire();

        long start = GC.GetAllocatedBytesForCurrentThread();
        for (uint tick = 0; tick < 5000; tick++)
        {
            zone.Advance(tick);
            zone.Sample(tick + 0.5, out _, out _, out _);
            outside |= zone.IsOutside(feet, tick);
            zone.ToWire();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
        Assert.True(outside);   // the loop reached the closing circles
    }
}
