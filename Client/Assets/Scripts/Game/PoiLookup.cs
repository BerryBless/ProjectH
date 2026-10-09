using System;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Phase 6 D7 / spec interpretation 11: which named place a point is in. The POI whose circle holds the point (edge
    // included) and whose centre is nearest; on a tie the earlier one; -1 when none. Display only. No allocation.
    public static class PoiLookup
    {
        // 기능: 맵의 POI 목록(MapPois.All)에서 점이 속한 장소를 찾는다.
        // 입력: x·z - 점의 수평 좌표.
        // 출력: MapPois.All의 색인, 어느 원에도 없으면 -1.
        public static int Find(float x, float z) => Find(MapPois.All, x, z);

        // 기능: 점을 원(가장자리 포함) 안에 두는 POI 가운데 중심이 가장 가까운 것을 고른다(같은 거리면 앞의 것; NaN 좌표는 어디에도 속하지 않는다).
        // 입력: pois - POI 목록, x·z - 점의 수평 좌표.
        // 출력: 목록 안의 색인, 없으면 -1.
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
