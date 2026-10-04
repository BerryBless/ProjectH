using System;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Phase 6 D7 / spec interpretation 11: which named place a point is in. The POI whose circle holds the point (edge
    // included) and whose centre is nearest; on a tie the earlier one; -1 when none. Display only. No allocation.
    public static class PoiLookup
    {
        // 기능: 맵의 POI 목록(MapPois.All)에서 점이 속한 POI를 찾는다.
        // 입력: x - 월드 X 좌표, z - 월드 Z 좌표.
        // 출력: 점을 포함하는 POI 중 중심이 가장 가까운 것의 인덱스, 없으면 -1.
        public static int Find(float x, float z) => Find(MapPois.All, x, z);

        // 기능: 원 안(경계 포함)에 점이 있고 중심이 가장 가까운 POI를 찾는다.
        // 입력: pois - 검사할 POI 목록, x - 월드 X 좌표, z - 월드 Z 좌표.
        // 출력: 찾은 POI 인덱스(거리가 같으면 앞의 것), 없거나 좌표가 NaN이면 -1.
        public static int Find(ReadOnlySpan<MapPoi> pois, float x, float z)
        {
            int best = -1;
            float bestDistanceSq = 0f;
            for (int i = 0; i < pois.Length; i++)
            {
                float dx = x - pois[i].X;
                float dz = z - pois[i].Z;
                float distanceSq = dx * dx + dz * dz;
                // !(a <= b) also rejects NaN.
                if (!(distanceSq <= pois[i].Radius * pois[i].Radius)) continue;
                if (best < 0 || distanceSq < bestDistanceSq)
                {
                    best = i;
                    bestDistanceSq = distanceSq;
                }
            }
            return best;
        }
    }
}
