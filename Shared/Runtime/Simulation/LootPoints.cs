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

    // Map placement data of the test arena (Phase 4 D6), kept next to TestArena so both change together
    // when the map changes. Only the server reads it: clients learn item positions from server events.
    // game-core-rules §4 allows this (map placement constants, no rules). Checked by LootPointsTests:
    // every point stands clear of the boxes, either on the floor outside TestArena.ClearRadius or on a
    // box top a player can reach, and no two points are within pickup range of each other.
    public static class LootPoints
    {
        public const string FloorTable = "Floor";
        public const string TowerTable = "Tower";

        private static readonly LootPoint[] s_points =
        {
            // Inner ring, 8 m from the centre: the straight walk from any spawn point is clear.
            new LootPoint(new Vector3(0f, 0f, 8f), FloorTable),
            new LootPoint(new Vector3(8f, 0f, 0f), FloorTable),
            new LootPoint(new Vector3(0f, 0f, -8f), FloorTable),
            new LootPoint(new Vector3(-8f, 0f, 0f), FloorTable),

            // Outer floor, between the walls and pillars.
            new LootPoint(new Vector3(15f, 0f, -15f), FloorTable),
            new LootPoint(new Vector3(-16f, 0f, 8f), FloorTable),
            new LootPoint(new Vector3(16f, 0f, 3f), FloorTable),
            new LootPoint(new Vector3(-4f, 0f, 16f), FloorTable),
            new LootPoint(new Vector3(5f, 0f, 16f), FloorTable),
            new LootPoint(new Vector3(-16f, 0f, -4f), FloorTable),
            new LootPoint(new Vector3(4f, 0f, -16f), FloorTable),
            new LootPoint(new Vector3(16f, 0f, -10f), FloorTable),

            // Box tops: the three 1 m boxes, the platform base and its step.
            new LootPoint(new Vector3(0f, 1f, 12f), TowerTable),
            new LootPoint(new Vector3(12f, 1f, -3f), TowerTable),
            new LootPoint(new Vector3(-12f, 1f, 3f), TowerTable),
            new LootPoint(new Vector3(-12.75f, 1f, -12.75f), TowerTable),
            new LootPoint(new Vector3(-14.5f, 2f, -14.5f), TowerTable),
        };

        public static ReadOnlySpan<LootPoint> All => s_points;
    }
}
