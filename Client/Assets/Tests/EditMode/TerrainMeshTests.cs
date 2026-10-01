using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Tests
{
    // Phase 6 D2: the render and collision mesh is exactly the HeightField surface, with front faces up.
    public class TerrainMeshTests
    {
        private static HeightField Terrain => GameMap.Terrain;

        [Test]
        public void Vertices_SitOnTheGrid_AtTheVertexHeights()
        {
            Vector3[] vertices = TerrainMesh.Vertices(Terrain);
            Assert.AreEqual(Terrain.VertsX * Terrain.VertsZ, vertices.Length);
            Assert.LessOrEqual(vertices.Length, 65535);   // 16-bit indices are enough
            for (int j = 0; j < Terrain.VertsZ; j++)
            {
                for (int i = 0; i < Terrain.VertsX; i++)
                {
                    Vector3 v = vertices[i + j * Terrain.VertsX];
                    Assert.AreEqual(Terrain.OriginX + i * Terrain.CellSize, v.x);
                    Assert.AreEqual(Terrain.OriginZ + j * Terrain.CellSize, v.z);
                    Assert.AreEqual(Terrain.VertexHeight(i, j), v.y);
                }
            }
        }

        // Unity culls back faces and Physics.queriesHitBackfaces is off by default: a downward-facing terrain would be
        // invisible to the eye, the camera collision and the aim ray. Cross(b - a, c - a) is the front-face normal.
        [Test]
        public void EveryTriangle_FacesUp()
        {
            Vector3[] v = TerrainMesh.Vertices(Terrain);
            int[] t = TerrainMesh.Triangles(Terrain);
            Assert.AreEqual((Terrain.VertsX - 1) * (Terrain.VertsZ - 1) * 6, t.Length);
            for (int k = 0; k < t.Length; k += 3)
                Assert.Greater(Vector3.Cross(v[t[k + 1]] - v[t[k]], v[t[k + 2]] - v[t[k]]).y, 0f, $"triangle {k / 3}");
        }

        // Same split as HeightField.CellHeight: both triangles of a cell share its (0,0)-(1,1) diagonal, so the mesh
        // surface equals HeightField.Height everywhere (checked at every triangle's centroid).
        [Test]
        public void EveryTriangle_UsesTheCellDiagonal_AndMatchesTheHeightField()
        {
            Vector3[] v = TerrainMesh.Vertices(Terrain);
            int[] t = TerrainMesh.Triangles(Terrain);
            int cellsX = Terrain.VertsX - 1;
            for (int k = 0; k < t.Length; k += 3)
            {
                int cell = k / 6;
                int v00 = cell % cellsX + cell / cellsX * Terrain.VertsX;
                int v11 = v00 + Terrain.VertsX + 1;
                CollectionAssert.Contains(new[] { t[k], t[k + 1], t[k + 2] }, v00);
                CollectionAssert.Contains(new[] { t[k], t[k + 1], t[k + 2] }, v11);
                Vector3 centroid = (v[t[k]] + v[t[k + 1]] + v[t[k + 2]]) / 3f;
                Assert.AreEqual(Terrain.Height(centroid.x, centroid.z), centroid.y, 1e-4f, $"triangle {k / 3}");
            }
        }
    }
}
