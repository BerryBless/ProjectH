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
