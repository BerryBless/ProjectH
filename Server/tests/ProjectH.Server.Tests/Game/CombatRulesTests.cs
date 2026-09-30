using System;
using System.Numerics;
using ProjectH.Server;
using ProjectH.Server.Game.Combat;
using Xunit;

namespace ProjectH.Server.Tests.Game;

public class CombatRulesTests
{
    [Theory]
    // health, shield, damage -> health, shield, killed
    [InlineData(100, 50, 20, 100, 30, false)]   // shield only
    [InlineData(100, 50, 90, 60, 0, false)]     // shield then health
    [InlineData(100, 50, 150, 0, 0, true)]      // exactly 0
    [InlineData(10, 0, 90, 0, 0, true)]         // overkill stops at 0
    [InlineData(100, 50, 0, 100, 50, false)]    // no damage
    public void ApplyDamage_ShieldFirstThenHealth(int health, int shield, int damage, int expectedHealth, int expectedShield, bool expectedKilled)
    {
        bool killed = CombatRules.ApplyDamage(ref health, ref shield, damage);
        Assert.Equal(expectedHealth, health);
        Assert.Equal(expectedShield, shield);
        Assert.Equal(expectedKilled, killed);
    }

    [Fact]
    public void ApplyDamage_OnDeadPlayer_DoesNotKillAgain()
    {
        int health = 0, shield = 0;
        Assert.False(CombatRules.ApplyDamage(ref health, ref shield, 20));
        Assert.Equal(0, health);
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 0f, 1f)]
    [InlineData(90f, 0f, 1f, 0f, 0f)]
    [InlineData(180f, 0f, 0f, 0f, -1f)]
    [InlineData(450f, 0f, 1f, 0f, 0f)]      // yaw wraps
    [InlineData(-90f, 0f, -1f, 0f, 0f)]
    public void AimDirection_FollowsCameraConvention(float yaw, float pitch, float x, float y, float z)
    {
        Assert.True(CombatRules.TryAimDirection(yaw, pitch, out Vector3 d));
        Assert.Equal(x, d.X, 5);
        Assert.Equal(y, d.Y, 5);
        Assert.Equal(z, d.Z, 5);
    }

    [Fact]
    public void AimDirection_PositivePitchLooksDown_AndIsClampedTo89()
    {
        Assert.True(CombatRules.TryAimDirection(0f, 30f, out Vector3 down));
        Assert.Equal(-0.5f, down.Y, 5);

        Assert.True(CombatRules.TryAimDirection(0f, 1000f, out Vector3 steep));
        Assert.Equal(-MathF.Sin(89f * MathF.PI / 180f), steep.Y, 5);
        Assert.True(steep.Z > 0f);
        Assert.Equal(1f, steep.Length(), 5);
    }

    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(0f, float.NaN)]
    [InlineData(float.PositiveInfinity, 0f)]
    [InlineData(0f, float.NegativeInfinity)]
    public void AimDirection_NonFinite_IsNoShot(float yaw, float pitch)
    {
        Assert.False(CombatRules.TryAimDirection(yaw, pitch, out _));
    }

    [Fact]
    public void AimDirection_HugeFiniteYaw_StaysUnitLength()
    {
        Assert.True(CombatRules.TryAimDirection(3e38f, 0f, out Vector3 d));
        Assert.Equal(1f, d.Length(), 4);
    }

    [Theory]
    [InlineData(100f, 100.0)]
    [InlineData(97.5f, 97.5)]
    [InlineData(94f, 94.0)]              // a 6-tick limit (passed in) itself
    [InlineData(10f, 94.0)]              // too old
    [InlineData(-5f, 94.0)]
    [InlineData(1e9f, 100.0)]            // future
    [InlineData(float.PositiveInfinity, 100.0)]
    [InlineData(float.NegativeInfinity, 94.0)]
    [InlineData(float.NaN, 100.0)]       // no usable claim: no rewind
    public void ClampViewTick_KeepsTheRewindWithinSixTicks(float viewTick, double expected)
    {
        Assert.Equal(expected, CombatRules.ClampViewTick(viewTick, 100u, 6));
    }

    [Fact]
    public void ClampViewTick_NearMatchStart_NeverGoesBelowZero()
    {
        Assert.Equal(0.0, CombatRules.ClampViewTick(-3f, 2u, 6));
    }

    // D6 budget. The client draws remote players InterpolationSnapshots snapshot intervals in the past
    // (GameClient.InterpolationSnapshots), and ViewTick only reaches the server after about 1 RTT, so:
    //   rewind ticks >= InterpolationSnapshots * SnapshotEveryTicks (drawn behind the newest snapshot)
    //                 + 1                                             (input buffer drain)
    //                 + RTT allowance                                 (snapshot there + input back)
    // At 30 Hz: 12 = 2 * 2 + 1 + 7, so the RTT allowance is 7 ticks (~233 ms): RTT up to ~200 ms stays hittable.
    private const double ClientInterpolationSnapshots = 2.0;   // mirrors GameClient.InterpolationSnapshots
    private const int DrainTicks = 1;
    private const int MinRttAllowanceTicks = 6;                // ~200 ms at 30 Hz

    [Fact]
    public void MaxRewind_CoversInterpolationDelayPlus200msRtt_At30Hz()
    {
        var options = new ServerOptions();
        Assert.Equal(30, options.SimHz);
        int interpolationTicks = (int)Math.Ceiling(ClientInterpolationSnapshots * options.SnapshotEveryTicks);
        int rewind = CombatRules.MaxRewindTicks(options.SimHz);

        Assert.Equal(12, rewind);
        Assert.True(rewind - interpolationTicks - DrainTicks >= MinRttAllowanceTicks,
            $"rewind {rewind} leaves {rewind - interpolationTicks - DrainTicks} ticks for RTT");
    }

    // The history ring must hold the whole rewind at the default rate, and at a high SimHz the
    // rewind is cut to what the ring holds instead of reaching outside it.
    [Fact]
    public void MaxRewind_NeverExceedsTheHistoryRing()
    {
        Assert.True(PositionHistory.Capacity - 1 >= CombatRules.TicksFromSeconds(CombatRules.MaxRewindSeconds, 30));
        Assert.Equal(12, CombatRules.MaxRewindTicks(30));

        Assert.True(CombatRules.TicksFromSeconds(CombatRules.MaxRewindSeconds, 128) > PositionHistory.Capacity - 1);
        Assert.Equal(PositionHistory.Capacity - 1, CombatRules.MaxRewindTicks(128));
    }

    [Fact]
    public void TicksFromSeconds_RoundsToWholeTicks()
    {
        Assert.Equal(90u, CombatRules.TicksFromSeconds(CombatRules.RespawnSeconds, 30));
        Assert.Equal(6u, CombatRules.TicksFromSeconds(0.2f, 30));
        Assert.Equal(1u, CombatRules.TicksFromSeconds(0.001f, 30));
    }
}
