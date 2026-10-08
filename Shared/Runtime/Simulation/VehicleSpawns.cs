using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Phase 19 D8: where the match start places the vehicles, by the roads between the POIs. Placement constants only
    // (game-core-rules §4 exception 2): creating, damaging and removing vehicles are the server's. The car's centre is at the
    // point, facing Heading. Checked by VehicleSpawnsTests: on the terrain, inside the map bound, outside the plaza and every
    // POI, ClearRadius from every box, door and harvestable footprint, and from the loot points, containers and stations.
    public static class VehicleSpawns
    {
        public const int Count = 4;
        // Nothing of the map this close to a spawn point (the footprint reaches at most about 3.1 m from the centre).
        public const float ClearRadius = 4f;

        private static readonly Vector3[] s_points =
        {
            Ground(30f, 12f),     // east road, between Crossroads and Gearworks
            Ground(-30f, 10f),    // west road, towards Rustvale
            Ground(-14f, -30f),   // south road, west of the south hill
            Ground(-12f, 34f),    // north road, below the north hill
        };

        private static readonly float[] s_headings = { 90f, 270f, 180f, 0f };

        public static ReadOnlySpan<Vector3> All => s_points;

        // 기능: 생성 지점의 차량 방향을 돌려준다.
        // 입력: index - 지점 번호(0..Count-1).
        // 출력: 방향(도).
        public static float Heading(int index) => s_headings[index];

        // 기능: 지형 위의 생성 좌표를 만든다(지면 X·Z, Y는 그 지점의 지형 높이. 서버는 생성 때 VehicleSimulation.GroundHeight로 다시 맞춘다).
        // 입력: x, z - 지면 좌표.
        // 출력: 좌표.
        private static Vector3 Ground(float x, float z) => new Vector3(x, GameMap.Terrain.Height(x, z), z);
    }
}
