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

        // 기능: 이름 있는 장소 하나를 만든다.
        // 입력: name - 표시 이름, x·z - 중심의 평면 위치, radius - 그 장소로 치는 반지름.
        // 출력: 주어진 값을 담은 장소.
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
