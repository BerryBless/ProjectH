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

        // 기능: 정점 높이 격자로 지형을 만든다. 높이 배열은 복사해 보관하고 최대 높이를 구한다.
        // 입력: originX·originZ - 격자 (0,0) 정점의 월드 위치, cellSize - 칸 한 변(0보다 큼), vertsX·vertsZ - 축별 정점 수(2 이상),
        //   heights - 정점 높이(vertsX x vertsZ개, i + j x vertsX 순, 유한하고 0 이상).
        // 출력: 불변 지형. 조건이 틀리면 ArgumentException.
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

        // 기능: 격자 정점 하나의 높이를 낸다(범위 검사 없음).
        // 입력: i·j - 정점의 X·Z 번호.
        // 출력: 그 정점의 높이.
        public float VertexHeight(int i, int j) => _heights[i + j * VertsX];

        // 기능: 한 점의 지형 표면 높이를 낸다. 격자 밖(NaN 포함)은 가장 가까운 가장자리로 자른다(맵 가장자리는 높이 0).
        // 입력: x·z - 월드 위치.
        // 출력: 그 자리 삼각형 위의 표면 높이.
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

        // 기능: 한 점 아래 삼각형의 기울기를 낸다(Phase 12 D7). Height와 같은 삼각형을 쓴다.
        // 입력: x·z - 월드 위치.
        // 출력: (dh/dx, dh/dz), m당 상승. 격자 밖이나 NaN이면 0 벡터(그곳은 평평하다).
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

        // 기능: 칸 (i, j) 안의 한 점 높이를 두 삼각형 중 하나에서 선형 보간한다(범위 검사 없음).
        // 입력: i·j - 칸 번호(0..Verts-2), u·v - 칸 안의 위치(0..1). u >= v면 (0,0)-(1,0)-(1,1), 아니면 (0,0)-(1,1)-(0,1) 삼각형.
        // 출력: 그 점의 표면 높이.
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
