using System;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Phase 6 D7 / spec interpretation 11: which named place a point is in. The POI whose circle holds the point (edge
    // included) and whose centre is nearest; on a tie the earlier one; -1 when none. Display only. No allocation.
    public static class PoiLookup
    {
        public static int Find(float x, float z) => Find(MapPois.All, x, z);

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
