using System;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Flow;

// Phase 12 D5: plans this match's drop transport route. Server only (the seed is a rule, game-core-rules §4); the
// client gets the result in TransportRoute. A straight line through the map's centre in a direction rolled from the
// seed, starting and ending TransportOutsideMargin outside the outer walls (so its length depends on the direction:
// 200 m along an axis, 2 x (half-diagonal 113 m + 20 m) = about 266 m, 13.3 s, on a diagonal), flown at TransportSpeed. One Random per match start.
public static class DropPlanner
{
    // 기능: 수송기 경로를 만든다: seed로 굴린 방향의 맵 중심 직선, 바깥 벽 밖 TransportOutsideMargin에서 시작·끝, TransportSpeed로 비행.
    // 입력: seed - 경기 시작 때 정한 난수 Seed, startTick - 비행 시작 Tick, simHz - Tick 속도.
    // 출력: 시작·끝 좌표, 고도, 시작 Tick, 비행 Tick 수를 담은 DropRoute.
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
