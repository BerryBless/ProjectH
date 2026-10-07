using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Phase 14 D10: the reboot stations, one near each outer POI. Placement constants only (game-core-rules §4 exception
    // 2): the use rule, its cooldown and range are the server's (squad.json). A station is not a collider: the client draws
    // a pillar there, players walk through it. The station's index is its id (RebootStations packet bit i, ChannelState
    // target). Checked by RebootStationsTests: on flat terrain, inside the walls, outside the plaza, ClearRadius from every
    // box, door and harvestable, and away from the loot points.
    public static class RebootStations
    {
        public const int Count = 4;
        // No map box, door or harvestable footprint this close to a station (RebootStationsTests).
        public const float ClearRadius = 2f;

        private static readonly Vector3[] s_points =
        {
            Ground(-42f, 42f),    // Rustvale, south-east of the houses
            Ground(52f, 38f),     // Gearworks, south of the warehouse
            Ground(40f, -38f),    // Lookout, on the plateau's north-west edge
            Ground(-50f, -54f),   // Stonefield, among the ruins
        };

        public static ReadOnlySpan<Vector3> All => s_points;

        // 기능: 지형 위의 스테이션 좌표를 만든다(언덕을 옮기면 스테이션도 함께 움직인다).
        // 입력: x, z - 지면 좌표.
        // 출력: 그 지점의 지형 높이를 Y로 가진 좌표.
        private static Vector3 Ground(float x, float z) => new Vector3(x, GameMap.Terrain.Height(x, z), z);
    }
}
