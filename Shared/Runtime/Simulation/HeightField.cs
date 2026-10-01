using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Phase 6 D2: the terrain, a grid of vertex heights. Each cell is split along its (0,0)-(1,1) diagonal into two
    // triangles and the height is linear on each, so movement, the server's shot test and the client's terrain mesh
    // all use exactly the same surface. Immutable after construction; Height allocates nothing.
    public sealed class HeightField
    {
        // Everything at height 0: the floor plane of the earlier phases. Tests pass it explicitly.
        public static readonly HeightField Flat = new HeightField(0f, 0f, 1f, 2, 2, new float[4]);

        private readonly float[] _heights;   // VertsX * VertsZ, index i + j * VertsX
        private readonly float _inverseCell;

        public HeightField(float originX, float originZ, float cellSize, int vertsX, int vertsZ, float[] heights)
        {
            if (vertsX < 2 || vertsZ < 2) throw new ArgumentException("A height field needs at least 2 x 2 vertices.");
            if (!(cellSize > 0f)) throw new ArgumentException("Cell size must be positive.", nameof(cellSize));
            if (heights == null || heights.Length != vertsX * vertsZ) throw new ArgumentException("One height per vertex.", nameof(heights));
            OriginX = originX;
            OriginZ = originZ;
            CellSize = cellSize;
            VertsX = vertsX;
            VertsZ = vertsZ;
            _heights = (float[])heights.Clone();
            _inverseCell = 1f / cellSize;
            float max = 0f;
            for (int k = 0; k < _heights.Length; k++)
            {
                float h = _heights[k];
                if (float.IsNaN(h) || float.IsInfinity(h) || h < 0f) throw new ArgumentException("Heights must be finite and not negative.", nameof(heights));
                if (h > max) max = h;
            }
            MaxHeight = max;
        }

        public float OriginX { get; }
        public float OriginZ { get; }
        public float CellSize { get; }
        public int VertsX { get; }
        public int VertsZ { get; }
        public float MaxHeight { get; }

        public float VertexHeight(int i, int j) => _heights[i + j * VertsX];

        // Height of the surface at (x, z). Outside the grid the coordinates are clamped to the nearest edge (the map's
        // edge is height 0).
        public float Height(float x, float z)
        {
            float gx = (x - OriginX) * _inverseCell;
            float gz = (z - OriginZ) * _inverseCell;
            // !(g > 0) also catches NaN.
            if (!(gx > 0f)) gx = 0f;
            if (!(gz > 0f)) gz = 0f;
            float lastX = VertsX - 1;
            float lastZ = VertsZ - 1;
            if (gx > lastX) gx = lastX;
            if (gz > lastZ) gz = lastZ;

            int i = (int)gx;
            int j = (int)gz;
            if (i > VertsX - 2) i = VertsX - 2;
            if (j > VertsZ - 2) j = VertsZ - 2;
            return CellHeight(i, j, gx - i, gz - j);
        }

        // Phase 12 D7: the slope of the triangle under (x, z) as (dh/dx, dh/dz), rise per metre. Same triangles as Height.
        // Outside the grid the surface is flat (Height clamps to the edge), so the slope there is 0. Allocates nothing.
        public Vector2 Gradient(float x, float z)
        {
            float gx = (x - OriginX) * _inverseCell;
            float gz = (z - OriginZ) * _inverseCell;
            // !(g >= 0) also catches NaN.
            if (!(gx >= 0f) || !(gz >= 0f) || gx > VertsX - 1 || gz > VertsZ - 1) return Vector2.Zero;

            int i = (int)gx;
            int j = (int)gz;
            if (i > VertsX - 2) i = VertsX - 2;
            if (j > VertsZ - 2) j = VertsZ - 2;
            float u = gx - i;
            float v = gz - j;
            int k = i + j * VertsX;
            float h00 = _heights[k];
            float h11 = _heights[k + 1 + VertsX];
            if (u >= v)
            {
                float h10 = _heights[k + 1];
                return new Vector2((h10 - h00) * _inverseCell, (h11 - h10) * _inverseCell);
            }
            float h01 = _heights[k + VertsX];
            return new Vector2((h11 - h01) * _inverseCell, (h01 - h00) * _inverseCell);
        }

        // Height inside cell (i, j) at local (u, v) in [0, 1]: triangle (0,0)-(1,0)-(1,1) when u >= v, else
        // (0,0)-(1,1)-(0,1).
        public float CellHeight(int i, int j, float u, float v)
        {
            int k = i + j * VertsX;
            float h00 = _heights[k];
            float h11 = _heights[k + 1 + VertsX];
            if (u >= v)
            {
                float h10 = _heights[k + 1];
                return h00 + u * (h10 - h00) + v * (h11 - h10);
            }
            float h01 = _heights[k + VertsX];
            return h00 + v * (h01 - h00) + u * (h11 - h01);
        }
    }
}
