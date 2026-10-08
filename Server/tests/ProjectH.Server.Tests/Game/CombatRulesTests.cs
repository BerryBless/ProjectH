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

    // Review fix D2 (STB-1): ViewTick is a uint tick (a float lost whole ticks after about 6 days at 30 Hz). The old float cases
    // map to: NaN and +Infinity -> uint.MaxValue ("now"), a huge future -> 1e9, -Infinity and negatives -> 0 (the oldest).
    [Theory]
    [InlineData(100u, 100.0)]
    [InlineData(97u, 97.0)]
    [InlineData(94u, 94.0)]              // a 6-tick limit (passed in) itself
    [InlineData(10u, 94.0)]              // too old
    [InlineData(0u, 94.0)]
    [InlineData(1_000_000_000u, 100.0)]  // future
    [InlineData(uint.MaxValue, 100.0)]   // "now": no rewind
    public void ClampViewTick_KeepsTheRewindWithinSixTicks(uint viewTick, double expected)
    {
        Assert.Equal(expected, CombatRules.ClampViewTick(viewTick, 100u, 6));
    }

    // Review fix D2: the client says "now" (nothing drawn yet, or no rewind wanted) with uint.MaxValue, which is never a clamp.
    [Fact]
    public void ClampViewTick_UintMax_MeansNow()
    {
        Assert.Equal(100.0, CombatRules.ClampViewTick(uint.MaxValue, 100u, 6, out bool clamped));
        Assert.False(clamped);
        Assert.Equal((double)(uint.MaxValue - 1), CombatRules.ClampViewTick(uint.MaxValue, uint.MaxValue - 1, 6, out clamped));
        Assert.False(clamped);
    }

    // Review fix C3 (SEC-5): the rewind a shooter gets follows its own RTT. ViewTick lags the server by the interpolation
    // (2 snapshot intervals = 2 x SnapshotEveryTicks), the snapshot's trip out plus the input's trip back (one full RTT, which is
    // LiteNetLib's RoundTripTime) and the drain (1) plus one tick of jitter: RTT x SimHz / 1000 + 2 x SnapshotEveryTicks + 2,
    // at least 2 and at most MaxRewindTicks. 30 Hz, snapshots every 2 ticks: 20 ms -> 6, 200 ms -> 12 (the old fixed limit).
    [Theory]
    [InlineData(20, 30, 2, 12, 6)]
    [InlineData(200, 30, 2, 12, 12)]
    [InlineData(100, 30, 2, 12, 9)]
    [InlineData(0, 30, 2, 12, 6)]
    [InlineData(-50, 30, 2, 12, 6)]        // never measured yet (or garbage): counted as 0
    [InlineData(5000, 30, 2, 12, 12)]
    [InlineData(int.MaxValue, 128, 1, 31, 31)]
    [InlineData(0, 30, 0, 12, 2)]          // the floor
    public void AllowedRewind_ForRtt20ms_IsSixTicks_AndFor200ms_IsTwelve(int rttMs, int simHz, int snapshotEveryTicks, int max, int expected)
    {
        Assert.Equal(expected, CombatRules.AllowedRewindTicks(rttMs, simHz, snapshotEveryTicks, max));
    }

    // A ViewTick older than the allowance is cut to it and reported (the shooter's RewindClamped counter); a future or NaN one is
    // "now", not a clamp.
    [Theory]
    [InlineData(94u, 94.0, false)]
    [InlineData(93u, 94.0, true)]
    [InlineData(0u, 94.0, true)]
    [InlineData(1_000_000_000u, 100.0, false)]
    [InlineData(uint.MaxValue, 100.0, false)]
    public void ClampViewTick_ReportsAClampBelowTheAllowance(uint viewTick, double expected, bool clamped)
    {
        Assert.Equal(expected, CombatRules.ClampViewTick(viewTick, 100u, 6, out bool wasClamped));
        Assert.Equal(clamped, wasClamped);
    }

    [Fact]
    public void ClampViewTick_NearMatchStart_NeverGoesBelowZero()
    {
        Assert.Equal(0.0, CombatRules.ClampViewTick(0u, 2u, 6));
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
