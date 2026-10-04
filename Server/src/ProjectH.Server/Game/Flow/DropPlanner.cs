using System;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Flow;

// Phase 12 D5: plans this match's drop transport route. Server only (the seed is a rule, game-core-rules §4); the
// client gets the result in TransportRoute. A straight line through the map's centre in a direction rolled from the
// seed, starting and ending TransportOutsideMargin outside the outer walls (so its length depends on the direction:
// 200 m along an axis, 2 x (half-diagonal 113 m + 20 m) = about 266 m, 13.3 s, on a diagonal), flown at TransportSpeed. One Random per match start.
public static class DropPlanner
{
    // 기능: Seed로 방향을 정해 맵 중심을 지나는 수송기 직선 경로를 만든다.
    // 입력: seed - 이번 Match의 경로 난수 Seed, startTick - 수송기가 출발하는 Tick, simHz - 서버 시뮬레이션 Tick 속도.
    // 출력: 시작·끝 좌표, 고도, 출발 Tick, 비행 Tick 수(최소 1)가 채워진 DropRoute.
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
