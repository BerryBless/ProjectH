using System;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Flow;

// Phase 12 D5: plans this match's drop transport route. Server only (the seed is a rule, game-core-rules §4); the
// client gets the result in TransportRoute. A straight line through the map's centre in a direction rolled from the
// seed, starting and ending TransportOutsideMargin outside the outer walls (so its length depends on the direction:
// 200 m along an axis, 2 x (half-diagonal 113 m + 20 m) = about 266 m, 13.3 s, on a diagonal), flown at TransportSpeed. One Random per match start.
public static class DropPlanner
{
    public static DropRoute Plan(int seed, uint startTick, int simHz)
    {
        var rng = new Random(seed);
        double angle = rng.NextDouble() * 2.0 * Math.PI;
        float dx = (float)Math.Sin(angle);
        float dz = (float)Math.Cos(angle);
        // Distance from the centre to the outer wall along the direction, plus the margin outside it.
        float half = GameMap.HalfSize / MathF.Max(MathF.Abs(dx), MathF.Abs(dz)) + MovementTuning.TransportOutsideMargin;
        uint duration = (uint)Math.Max(1, (int)Math.Ceiling(2f * half / MovementTuning.TransportSpeed * simHz));
        return new DropRoute
        {
            StartX = -dx * half,
            StartZ = -dz * half,
            EndX = dx * half,
            EndZ = dz * half,
            Altitude = MovementTuning.TransportAltitude,
            StartTick = startTick,
            DurationTicks = duration,
        };
    }
}
