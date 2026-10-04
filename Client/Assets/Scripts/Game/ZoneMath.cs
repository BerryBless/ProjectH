using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Game
{
    // What the HUD says about the zone (D14).
    public enum ZoneHint
    {
        None,
        ShrinksIn,   // "Zone shrinking in 12s"
        Closing,     // "Zone closing"
    }

    // The safe zone circle from the server's ZoneState, for display only (D11, D14): the server decides damage.
    // Pure (no UnityEngine), so EditMode tests and the server's parity test (ZoneMathParityTests, which compiles
    // this file) can run it.
    //
    // Sample and IsOutside must stay the same formula as the server's SafeZone.Sample and SafeZone.IsOutside
    // (Server/src/ProjectH.Server/Game/Zone/SafeZone.cs). Change both or neither; the parity test compares them.
    public static class ZoneMath
    {
        // 기능: 소수 서버 Tick에서 안전 구역 원의 중심과 반지름을 구한다.
        // 입력: zone - 서버 ZoneState, tick - 서버 Tick(소수).
        // 출력: 반환값 없음. centerX·centerZ·radius에 원이 쓰인다(축소 전 From, 축소 중 선형 보간, 축소 후 To).
        // The circle at a (fractional) server tick: From until ShrinkStartTick, linear to To until ShrinkEndTick,
        // then To.
        public static void Sample(in ZoneState zone, double tick, out float centerX, out float centerZ, out float radius)
        {
            float t;
            if (tick <= zone.ShrinkStartTick) t = 0f;
            else if (tick >= zone.ShrinkEndTick) t = 1f;
            else t = (float)((tick - zone.ShrinkStartTick) / ((double)zone.ShrinkEndTick - zone.ShrinkStartTick));
            centerX = zone.FromX + (zone.ToX - zone.FromX) * t;
            centerZ = zone.FromZ + (zone.ToZ - zone.FromZ) * t;
            radius = zone.FromRadius + (zone.ToRadius - zone.FromRadius) * t;
        }

        // 기능: 위치가 안전 구역 밖인지 판정한다(표시용, 서버 SafeZone.IsOutside와 같은 식).
        // 입력: zone - 서버 ZoneState, x - 월드 X 좌표, z - 월드 Z 좌표, tick - 서버 Tick(소수).
        // 출력: 원 밖이거나 반지름이 0 이하면 true, 안이면 false.
        // Horizontal distance above the radius; a radius-0 circle has no inside.
        public static bool IsOutside(in ZoneState zone, float x, float z, double tick)
        {
            Sample(zone, tick, out float centerX, out float centerZ, out float radius);
            float dx = x - centerX;
            float dz = z - centerZ;
            return radius <= 0f || dx * dx + dz * dz > radius * radius;
        }

        // 기능: HUD에 보여줄 구역 안내를 정한다.
        // 입력: zone - 서버 ZoneState, tick - 현재 서버 Tick 추정값(소수), simHz - 서버 초당 Tick 수.
        // 출력: 축소 전이면 ShrinksIn과 남은 초(올림), 축소 중이면 Closing, 구역 없음·축소 후·simHz 0 이하면 None(seconds 0).
        // Phase 0 (no zone) says nothing. Before the shrink: whole seconds until it starts (rounded up); during
        // the shrink: closing; after it (the last phase stays closed): nothing.
        public static ZoneHint Hint(in ZoneState zone, double tick, int simHz, out int seconds)
        {
            seconds = 0;
            if (zone.Phase == 0 || simHz <= 0) return ZoneHint.None;
            if (tick < zone.ShrinkStartTick)
            {
                double left = (zone.ShrinkStartTick - tick) / simHz;
                seconds = left >= int.MaxValue ? int.MaxValue : (int)System.Math.Ceiling(left);
                return ZoneHint.ShrinksIn;
            }
            return tick < zone.ShrinkEndTick ? ZoneHint.Closing : ZoneHint.None;
        }
    }
}
