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
        // 기능: (소수) 서버 Tick에서의 원을 구한다. ShrinkStartTick까지 From, ShrinkEndTick까지 To로 선형 이동, 그 뒤 To.
        // 입력: zone - 서버 ZoneState, tick - 서버 Tick, centerX·centerZ - 원 중심, radius - 반지름.
        // 출력: 반환값 없음. 세 out 값이 채워진다.
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

        // 기능: 한 점이 그 Tick의 원 밖에 있는지 본다(수평 거리만, 서버 SafeZone.IsOutside와 같은 식).
        // 입력: zone - 서버 ZoneState, x·z - 점의 수평 좌표, tick - 서버 Tick.
        // 출력: 수평 거리가 반지름을 넘으면 true. 반지름이 0 이하인 원은 안이 없으므로 항상 true.
        public static bool IsOutside(in ZoneState zone, float x, float z, double tick)
        {
            Sample(zone, tick, out float centerX, out float centerZ, out float radius);
            float dx = x - centerX;
            float dz = z - centerZ;
            return radius <= 0f || dx * dx + dz * dz > radius * radius;
        }

        // 기능: HUD에 보일 존 안내를 정한다. Phase 0(존 없음)은 없음, 축소 전은 시작까지의 초(올림), 축소 중은 닫히는 중, 그 뒤(마지막 Phase는 닫힌 채)는 없음.
        // 입력: zone - 서버 ZoneState, tick - 서버 Tick, simHz - 서버 Tick 속도(0 이하면 없음), seconds - 축소 시작까지 남은 초(ShrinksIn일 때만).
        // 출력: None·ShrinksIn·Closing 중 하나.
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
