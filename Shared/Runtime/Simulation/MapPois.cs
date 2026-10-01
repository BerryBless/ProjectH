using System;

namespace ProjectH.Shared.Simulation
{
    // Phase 6 D7: a named place of the map. The client shows the name of the one the local player is in.
    public readonly struct MapPoi
    {
        public readonly string Name;
        public readonly float X;
        public readonly float Z;
        public readonly float Radius;

        public MapPoi(string name, float x, float z, float radius)
        {
            Name = name;
            X = x;
            Z = z;
            Radius = radius;
        }
    }

    // Placement constants only (game-core-rules §4). Checked by MapPoisTests: inside the walls, short ASCII names.
    public static class MapPois
    {
        private static readonly MapPoi[] s_pois =
        {
            new MapPoi("Crossroads", 0f, 0f, GameMap.PlazaRadius),
            new MapPoi("Rustvale", -46f, 46f, 17f),
            new MapPoi("Gearworks", 46f, 50f, 16f),
            new MapPoi("Lookout", 44f, -44f, 22f),
            new MapPoi("Stonefield", -50f, -54f, 17f),
        };

        public static ReadOnlySpan<MapPoi> All => s_pois;
    }
}
