using System;
using System.Numerics;
using ProjectH.Client.Game;
using ProjectH.Server.Game.Zone;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 5 D11, spec §4 / Review Focus: the client's ZoneMath (compiled into this test project from
// Client/Assets/Scripts/Game/ZoneMath.cs) draws exactly the circle SafeZone judges with, given only the ZoneState
// the server sent. Same inputs -> same floats, for every phase, at whole and fractional ticks.
public class ZoneMathParityTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1234)]
    public void ClientCircle_EqualsTheServerCircle_ThroughAWholeMatch(int seed)
    {
        var zone = new SafeZone(TestGameData.Zones());
        zone.Start(500, seed);
        var rng = new Random(seed);
        int checkedSamples = 0;
        for (uint tick = 500; tick < 500 + 4000; tick++)
        {
            zone.Advance(tick);
            ZoneState wire = zone.ToWire();   // what the client last received
            double[] ticks = { tick, tick + 0.25, tick + rng.NextDouble() };
            foreach (double t in ticks)
            {
                zone.Sample(t, out float sx, out float sz, out float sr);
                ZoneMath.Sample(wire, t, out float cx, out float cz, out float cr);
                Assert.Equal(sx, cx);
                Assert.Equal(sz, cz);
                Assert.Equal(sr, cr);

                var feet = new Vector3(sx + (float)(rng.NextDouble() * 30 - 15), 0f, sz + (float)(rng.NextDouble() * 30 - 15));
                Assert.Equal(zone.IsOutside(feet, t), ZoneMath.IsOutside(wire, feet.X, feet.Z, t));
                checkedSamples++;
            }
        }
        Assert.Equal(12000, checkedSamples);
        Assert.True(zone.IsFinalPhase);
    }
}
