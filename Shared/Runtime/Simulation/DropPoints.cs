using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Phase 6 D9: where the match start places the participants, spread over the map. Placement constants only (the
    // server shuffles and assigns them, game-core-rules §4). Checked by DropPointsTests: at least MinSpacing apart, no
    // box within ClearRadius, inside the walls by WallMargin, outside the lobby plaza, on the terrain.
    public static class DropPoints
    {
        public const float MinSpacing = 20f;
        public const float ClearRadius = 4f;
        public const float WallMargin = 6f;
        public const float MinFromCentre = 20f;

        private static readonly Vector3[] s_points =
        {
            Ground(-68f, 68f), Ground(-30f, 70f), Ground(12f, 70f), Ground(40f, 68f), Ground(68f, 68f),
            Ground(-68f, 40f), Ground(-24f, 40f), Ground(16f, 44f), Ground(68f, 40f),
            Ground(-44f, 18f), Ground(38f, 20f),
            Ground(-70f, -4f), Ground(-24f, -24f), Ground(24f, -28f), Ground(62f, -12f),
            Ground(-70f, -34f), Ground(-26f, -48f), Ground(12f, -48f), Ground(60f, -34f),
            Ground(-68f, -70f), Ground(-26f, -70f), Ground(0f, -66f), Ground(36f, -70f), Ground(70f, -70f),
        };

        public static ReadOnlySpan<Vector3> All => s_points;

        private static Vector3 Ground(float x, float z) => new Vector3(x, GameMap.Terrain.Height(x, z), z);
    }
}
