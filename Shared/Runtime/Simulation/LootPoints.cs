using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // One loot spawn point: where the item lies and which loot.json table fills it.
    public readonly struct LootPoint
    {
        public readonly Vector3 Position;
        public readonly string Table;

        public LootPoint(Vector3 position, string table)
        {
            Position = position;
            Table = table;
        }
    }

    // Map placement data (Phase 4 D6, Phase 6 D10), kept next to GameMap so both change together. Only the server
    // reads it: clients learn item positions from server events. game-core-rules §4 allows this (placement constants,
    // no rules). Checked by LootPointsTests: every point stands clear of the boxes, on the terrain or on a box top a
    // player can climb to, and no two points are within pickup range of each other.
    public static class LootPoints
    {
        public const string FloorTable = "Floor";
        public const string BuildingTable = "Building";
        public const string TowerTable = "Tower";

        private static readonly LootPoint[] s_points =
        {
            // Inner ring, 8 m from the centre: the straight walk from any lobby spawn point is clear.
            Ground(0f, 8f, FloorTable),
            Ground(8f, 0f, FloorTable),
            Ground(0f, -8f, FloorTable),
            Ground(-8f, 0f, FloorTable),

            // Rustvale: two points inside each house, two outside.
            Ground(-54f, 54f, BuildingTable),
            Ground(-56f, 56f, BuildingTable),
            Ground(-38f, 52f, BuildingTable),
            Ground(-36f, 54f, BuildingTable),
            Ground(-50f, 38f, BuildingTable),
            Ground(-52f, 36f, BuildingTable),
            Ground(-46f, 46f, FloorTable),
            Ground(-60f, 44f, FloorTable),

            // Gearworks: four inside the warehouse, the two low crate tops, two outside.
            Ground(42f, 47f, BuildingTable),
            Ground(50f, 47f, BuildingTable),
            Ground(42f, 53f, BuildingTable),
            Ground(50f, 53f, BuildingTable),
            Top(36f, 1f, 40f, TowerTable),
            Top(57f, 1f, 40f, TowerTable),
            Ground(46f, 40f, FloorTable),
            Ground(46f, 62f, FloorTable),

            // Stonefield: the platform base and its step, the two low boxes, four on the ground.
            Top(-46.75f, 1f, -64.75f, TowerTable),
            Top(-48.5f, 2f, -66.5f, TowerTable),
            Top(-44f, 1f, -40f, TowerTable),
            Top(-60f, 1f, -46f, TowerTable),
            Ground(-50f, -46f, FloorTable),
            Ground(-56f, -50f, FloorTable),
            Ground(-44f, -56f, FloorTable),
            Ground(-54f, -62f, FloorTable),

            // Lookout: three on the plateau, two on its slopes.
            Ground(44f, -44f, TowerTable),
            Ground(44f, -40.5f, TowerTable),
            Ground(46f, -46f, TowerTable),
            Ground(44f, -30f, FloorTable),
            Ground(30f, -44f, FloorTable),

            // Hill tops and one slope.
            Ground(0f, 46f, FloorTable),
            Ground(50f, 4f, FloorTable),
            Ground(-50f, 0f, FloorTable),
            Ground(-4f, -48f, FloorTable),
            Ground(8f, 40f, FloorTable),

            // Open ground next to the cover.
            Ground(18f, 20f, FloorTable),
            Ground(-20f, 18f, FloorTable),
            Ground(18f, -20.5f, FloorTable),
            Ground(-20f, -18f, FloorTable),
            Ground(30f, 0f, FloorTable),
            Ground(-30f, 0f, FloorTable),
            Ground(0f, 30f, FloorTable),
            Ground(0f, -30f, FloorTable),

            // Corners.
            Ground(68f, 68f, FloorTable),
            Ground(-68f, 68f, FloorTable),
            Ground(68f, -68f, FloorTable),
            Ground(-68f, -68f, FloorTable),
        };

        public static ReadOnlySpan<LootPoint> All => s_points;

        // On the terrain: the height comes from GameMap.Terrain, so moving a hill moves its points with it.
        private static LootPoint Ground(float x, float z, string table) => new LootPoint(new Vector3(x, GameMap.Terrain.Height(x, z), z), table);

        // On a box top at height y.
        private static LootPoint Top(float x, float y, float z, string table) => new LootPoint(new Vector3(x, y, z), table);
    }
}
