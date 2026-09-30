using System;
using System.Numerics;

namespace ProjectH.Server.Game.Combat;

// Combat constants and pure rules (D7, D8, D14). Server only: the client never decides a hit.
public static class CombatRules
{
    public const int MaxHealth = 100;   // D8 test values
    // Phase 4 D11: Shield Cells fill up to 100. Players start with the loadout's shield (0 in production, D1).
    public const int MaxShield = 100;
    // Shots start at feet + 1.6 m. The client aims from the same height (AimSolver.EyeHeight).
    public const float EyeHeight = 1.6f;
    public const float MaxPitch = 89f;
    public const float RespawnSeconds = 3f;
    // D6: a shot may rewind other players by at most this much (12 ticks at 30 Hz). The client draws
    // remote players ~133 ms (2 snapshots at 15 Hz) behind the newest snapshot, which uses part of this
    // window; 400 ms keeps RTT up to ~200 ms hittable (CombatRulesTests.MaxRewind_*).
    public const float MaxRewindSeconds = 0.4f;

    // D8: the shield absorbs first, the rest comes off health (never below 0). Returns true when this
    // damage took health from above 0 to 0.
    public static bool ApplyDamage(ref int health, ref int shield, int damage)
    {
        if (damage <= 0 || health <= 0) return false;
        int absorbed = Math.Min(shield, damage);
        shield -= absorbed;
        health = Math.Max(0, health - (damage - absorbed));
        return health == 0;
    }

    // Phase 4 D4: a weapon's damage times its rarity multiplier, rounded half away from zero, at least 1.
    // decimal, not float: 1.15f is 1.1499999..., and 90 x 1.15 must round to 104 as written in the data.
    // Casting the float to decimal keeps its 7 significant digits (1.15).
    public static ushort ScaledDamage(ushort damage, float multiplier)
    {
        decimal scaled = Math.Round(damage * (decimal)multiplier, MidpointRounding.AwayFromZero);
        return (ushort)Math.Clamp(scaled, 1m, ushort.MaxValue);
    }

    // D14: non-finite angles are no shot. Pitch is clamped to +-89 degrees. Same convention as the
    // client camera (ShoulderCameraMath.Forward): yaw 0 faces +Z, yaw 90 faces +X, positive pitch looks down.
    public static bool TryAimDirection(float yawDegrees, float pitchDegrees, out Vector3 direction)
    {
        direction = default;
        if (!float.IsFinite(yawDegrees) || !float.IsFinite(pitchDegrees)) return false;

        // % keeps huge finite yaws in a range where sin/cos stay accurate.
        float yaw = (yawDegrees % 360f) * (MathF.PI / 180f);
        float pitch = Math.Clamp(pitchDegrees, -MaxPitch, MaxPitch) * (MathF.PI / 180f);
        float cosPitch = MathF.Cos(pitch);
        direction = new Vector3(MathF.Sin(yaw) * cosPitch, -MathF.Sin(pitch), MathF.Cos(yaw) * cosPitch);
        return true;
    }

    // D14: the tick a shot rewinds targets to. The client's ViewTick is untrusted: NaN means "now", and
    // anything outside [latestTick - maxRewindTicks, latestTick] is clamped into it (never below tick 0).
    public static double ClampViewTick(float viewTick, uint latestTick, int maxRewindTicks)
    {
        double latest = latestTick;
        if (float.IsNaN(viewTick)) return latest;
        double oldest = latestTick > (uint)maxRewindTicks ? latestTick - (uint)maxRewindTicks : 0u;
        if (viewTick > latest) return latest;
        if (viewTick < oldest) return oldest;
        return viewTick;
    }

    // MaxRewindSeconds in ticks, cut to what PositionHistory holds (a high SimHz would otherwise reach
    // outside the ring: 0.4 s at 128 Hz is 51 ticks, the ring keeps Capacity - 1 = 31 behind the newest).
    public static int MaxRewindTicks(int simHz)
    {
        return Math.Min((int)TicksFromSeconds(MaxRewindSeconds, simHz), PositionHistory.Capacity - 1);
    }

    // Whole ticks at simHz, at least 1 (3 s at 30 Hz = 90).
    public static uint TicksFromSeconds(float seconds, int simHz)
    {
        return (uint)Math.Max(1, (int)MathF.Round(seconds * simHz, MidpointRounding.AwayFromZero));
    }
}
