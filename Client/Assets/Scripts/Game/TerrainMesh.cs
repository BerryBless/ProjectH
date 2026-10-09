using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Phase 6 D2: the terrain as a render and collision mesh. Same vertices and the same (0,0)-(1,1) cell diagonal as
    // HeightField, so what the camera and the aim ray hit is exactly the surface the server's shot test uses. Front
    // faces point up (Cross(b - a, c - a).y > 0): Unity culls back faces and Physics ignores them by default. Built
    // once at startup (MapWorld); pure, testable outside Unity.
    public static class TerrainMesh
    {
        // 기능: 높이 격자의 정점을 HeightField와 같은 순서(i + j × VertsX)로 월드 좌표 배열에 만든다.
        // 입력: terrain - 지형 높이 격자.
        // 출력: VertsX × VertsZ개의 정점 배열(새로 할당).
        public static Vector3[] Vertices(HeightField terrain)
        {
            var vertices = new Vector3[terrain.VertsX * terrain.VertsZ];
            for (int j = 0; j < terrain.VertsZ; j++)
            {
                for (int i = 0; i < terrain.VertsX; i++)
                {
                    vertices[i + j * terrain.VertsX] = new Vector3(
                        terrain.OriginX + i * terrain.CellSize, terrain.VertexHeight(i, j), terrain.OriginZ + j * terrain.CellSize);
                }
            }
            return vertices;
        }

        // 기능: 셀마다 HeightField와 같은 (0,0)-(1,1) 대각선으로 나눈 삼각형 두 개를 앞면이 위를 보게 감아 색인 배열에 만든다.
        // 입력: terrain - 지형 높이 격자.
        // 출력: 셀 수 × 6개의 정점 색인 배열(새로 할당).
        public static int[] Triangles(HeightField terrain)
        {
            int cellsX = terrain.VertsX - 1;
            int cellsZ = terrain.VertsZ - 1;
            var triangles = new int[cellsX * cellsZ * 6];
            int t = 0;
            for (int j = 0; j < cellsZ; j++)
            {
                for (int i = 0; i < cellsX; i++)
                {
                    int v00 = i + j * terrain.VertsX;
                    int v10 = v00 + 1;
                    int v01 = v00 + terrain.VertsX;
                    int v11 = v01 + 1;
                    // HeightField's u >= v triangle (0,0)-(1,0)-(1,1), wound 00 -> 11 -> 10 so it faces up.
                    triangles[t++] = v00;
                    triangles[t++] = v11;
                    triangles[t++] = v10;
                    // The u < v triangle (0,0)-(1,1)-(0,1), wound 00 -> 01 -> 11.
                    triangles[t++] = v00;
                    triangles[t++] = v01;
                    triangles[t++] = v11;
                }
            }
            return triangles;
        }
    }
}
