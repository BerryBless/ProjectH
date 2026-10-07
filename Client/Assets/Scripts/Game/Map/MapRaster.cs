using System;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game.Map
{
    // Phase 15 D2: the map picture, computed from the shared map data (so it always matches the map): terrain height as
    // green shading, the footprints of boxes (walls, roofs, rocks of the map), doors and harvestables in dark grey, and a
    // black ring of edge pixels. The texture is clamped (MapTextures), so the minimap window past the map edge shows that
    // black ring stretched: "outside the map is black". Pure (no UnityEngine): tests check it outside Unity.
    // Layout: RGBA32, row 0 = south (-Z), column 0 = west (-X): the order Texture2D.SetPixelData expects (row 0 at the bottom).
    public static class MapRaster
    {
        public const int DefaultSize = 256;

        public static readonly byte[] Outside = { 0, 0, 0, 255 };
        public static readonly byte[] Footprint = { 58, 58, 62, 255 };
        // Terrain: low ground dark green, the highest ground pale olive.
        private static readonly byte[] Low = { 62, 104, 58, 255 };
        private static readonly byte[] High = { 176, 186, 128, 255 };

        // 기능: 지도 그림을 RGBA32 바이트 배열에 그린다(한 번만 부른다: 경기 내내 바뀌지 않는다).
        // 입력: rgba - size × size × 4 바이트 이상, size - 한 변 픽셀 수(2 이상).
        // 출력: 반환값 없음. rgba가 채워진다. 크기가 맞지 않으면 ArgumentException(설정 오류: 시작할 때 한 번만 부른다).
        public static void Rasterize(byte[] rgba, int size)
        {
            if (size < 2 || rgba == null || rgba.Length < size * size * 4) throw new ArgumentException("rgba must hold size * size RGBA pixels.");
            HeightField terrain = GameMap.Terrain;
            float top = terrain.MaxHeight > 0.01f ? terrain.MaxHeight : 1f;
            float pixel = MapProjection.Size / size;
            for (int j = 0; j < size; j++)
            {
                float z = -MapProjection.Extent + (j + 0.5f) * pixel;
                for (int i = 0; i < size; i++)
                {
                    float x = -MapProjection.Extent + (i + 0.5f) * pixel;
                    int o = (j * size + i) * 4;
                    float t = terrain.Height(x, z) / top;
                    if (t < 0f) t = 0f;
                    else if (t > 1f) t = 1f;
                    for (int c = 0; c < 4; c++) rgba[o + c] = (byte)(Low[c] + (High[c] - Low[c]) * t + 0.5f);
                }
            }
            foreach (Box box in GameMap.Boxes) Paint(rgba, size, box, Footprint);
            foreach (Box door in GameMap.Doors) Paint(rgba, size, door, Footprint);
            foreach (Harvestable h in GameMap.Harvestables) Paint(rgba, size, h.Bounds, Footprint);
            for (int k = 0; k < size; k++)
            {
                Set(rgba, size, k, 0, Outside);
                Set(rgba, size, k, size - 1, Outside);
                Set(rgba, size, 0, k, Outside);
                Set(rgba, size, size - 1, k, Outside);
            }
        }

        // 기능: 상자의 수평 발자국이 닿는 모든 픽셀을 한 색으로 칠한다(픽셀보다 얇은 벽도 한 픽셀은 칠한다). 맵 밖 부분은 자른다.
        // 입력: rgba·size - 그림, box - 상자, color - RGBA 4바이트.
        // 출력: 반환값 없음.
        private static void Paint(byte[] rgba, int size, in Box box, byte[] color)
        {
            float pixel = MapProjection.Size / size;
            int i0 = Clamp((int)Math.Floor((box.Min.X + MapProjection.Extent) / pixel), size);
            int i1 = Clamp((int)Math.Ceiling((box.Max.X + MapProjection.Extent) / pixel) - 1, size);
            int j0 = Clamp((int)Math.Floor((box.Min.Z + MapProjection.Extent) / pixel), size);
            int j1 = Clamp((int)Math.Ceiling((box.Max.Z + MapProjection.Extent) / pixel) - 1, size);
            // A box entirely outside the map (the outer walls) clamps to one edge column or row: the black ring covers it.
            for (int j = j0; j <= j1; j++)
            {
                for (int i = i0; i <= i1; i++) Set(rgba, size, i, j, color);
            }
        }

        // 기능: 픽셀 번호를 0..size-1로 자른다.
        // 입력: index - 픽셀 번호, size - 한 변 픽셀 수.
        // 출력: 자른 번호.
        private static int Clamp(int index, int size) => index < 0 ? 0 : index >= size ? size - 1 : index;

        // 기능: 픽셀 하나의 색을 쓴다.
        // 입력: rgba·size - 그림, i - 열(서→동), j - 행(남→북), color - RGBA 4바이트.
        // 출력: 반환값 없음.
        private static void Set(byte[] rgba, int size, int i, int j, byte[] color)
        {
            int o = (j * size + i) * 4;
            rgba[o] = color[0];
            rgba[o + 1] = color[1];
            rgba[o + 2] = color[2];
            rgba[o + 3] = color[3];
        }
    }
}
