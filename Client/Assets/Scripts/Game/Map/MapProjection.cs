using System;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game.Map
{
    // Phase 15 D1 (request §51): the one coordinate transform every map UI uses (minimap, full map, map-click waypoint).
    //   world (x, z) <-> map uv in [0, 1] (u east, v north: north is up) <-> pixels of a map rectangle (from its bottom-left).
    // The map covers the play area inside the outer walls, ±GameMap.HalfSize on both axes. Pure (no UnityEngine): EditMode
    // tests run it outside Unity. Nothing here allocates.
    public static class MapProjection
    {
        public const float Extent = GameMap.HalfSize;   // half the side, in metres
        public const float Size = 2f * Extent;          // the side, in metres

        // 기능: 월드 수평 좌표를 지도 정규 좌표로 바꾼다.
        // 입력: x - 월드 X(동쪽 +), z - 월드 Z(북쪽 +).
        // 출력: u(서쪽 끝 0, 동쪽 끝 1)·v(남쪽 끝 0, 북쪽 끝 1). 맵 밖이면 [0, 1] 밖의 값이다(자르지 않는다).
        public static void WorldToUv(float x, float z, out float u, out float v)
        {
            u = (x + Extent) / Size;
            v = (z + Extent) / Size;
        }

        // 기능: 지도 정규 좌표를 월드 수평 좌표로 바꾼다(WorldToUv의 역).
        // 입력: u, v - 지도 정규 좌표.
        // 출력: 월드 x, z.
        public static void UvToWorld(float u, float v, out float x, out float z)
        {
            x = u * Size - Extent;
            z = v * Size - Extent;
        }

        // 기능: 지도 정규 좌표를 지도 사각형 안의 픽셀 위치로 바꾼다(사각형 왼쪽 아래 기준).
        // 입력: u, v - 지도 정규 좌표, width·height - 사각형 크기(px).
        // 출력: px, py - 왼쪽 아래에서의 위치(px).
        public static void UvToRect(float u, float v, float width, float height, out float px, out float py)
        {
            px = u * width;
            py = v * height;
        }

        // 기능: 지도 사각형 안의 픽셀 위치를 지도 정규 좌표로 바꾼다(UvToRect의 역).
        // 입력: px, py - 왼쪽 아래에서의 위치(px), width·height - 사각형 크기(px, 0 이하이면 0을 낸다).
        // 출력: u, v.
        public static void RectToUv(float px, float py, float width, float height, out float u, out float v)
        {
            u = width > 0f ? px / width : 0f;
            v = height > 0f ? py / height : 0f;
        }

        // 기능: 미터 길이를 지도 정규 길이로 바꾼다(자기장 반지름 등).
        // 입력: meters - 길이(m).
        // 출력: 지도 한 변을 1로 본 길이.
        public static float MetersToUv(float meters) => meters / Size;

        // 기능: 지도 정규 좌표가 맵 안(양 끝 포함)인지 본다(지도 클릭 Waypoint).
        // 입력: u, v - 지도 정규 좌표.
        // 출력: 0..1 안이면 true(NaN이면 false).
        public static bool InsideUv(float u, float v) => u >= 0f && u <= 1f && v >= 0f && v <= 1f;

        // 기능: 미니맵 창(D3)을 구한다: 중심 주변 windowMeters 정사각형을 지도 정규 좌표로(RawImage.uvRect와 같은 뜻).
        // 입력: centerX, centerZ - 창 중심(월드), windowMeters - 창 한 변(m).
        // 출력: minU, minV - 창 왼쪽 아래(정규), sizeUv - 창 한 변(정규). 맵 가장자리에서는 [0, 1] 밖으로 나간다(자르지 않는다:
        //   나는 항상 미니맵 가운데에 있고, 맵 밖은 텍스처의 검은 테두리가 늘어나 보인다).
        public static void Window(float centerX, float centerZ, float windowMeters, out float minU, out float minV, out float sizeUv)
        {
            WorldToUv(centerX, centerZ, out float cu, out float cv);
            sizeUv = MetersToUv(windowMeters);
            minU = cu - sizeUv * 0.5f;
            minV = cv - sizeUv * 0.5f;
        }

        // 기능: 지도 정규 좌표를 미니맵 창 안의 정규 좌표로 바꾼다(창 왼쪽 아래 0, 오른쪽 위 1).
        // 입력: u, v - 지도 정규 좌표, minU·minV·sizeUv - Window의 결과.
        // 출력: wx, wy - 창 정규 좌표(창 밖이면 [0, 1] 밖).
        public static void UvToWindow(float u, float v, float minU, float minV, float sizeUv, out float wx, out float wy)
        {
            wx = sizeUv > 0f ? (u - minU) / sizeUv : 0.5f;
            wy = sizeUv > 0f ? (v - minV) / sizeUv : 0.5f;
        }

        // 기능: 창 정규 좌표가 창 안쪽 [inset, 1 - inset] 밖이면 창 가운데에서 그 점으로 가는 방향을 유지한 채 테두리 위로 옮긴다
        //   (D3: 밖으로 나간 팀원·Ping·Waypoint를 테두리에 붙인다).
        // 입력: wx, wy - 창 정규 좌표(바뀔 수 있다), inset - 테두리에서 안쪽으로 둘 여유(0..0.5, 아이콘 반 크기 / 창 크기).
        // 출력: 옮겼으면 true, 이미 안쪽이면 false. NaN이면 가운데(0.5, 0.5)로 두고 true.
        public static bool ClampToEdge(ref float wx, ref float wy, float inset)
        {
            float lo = inset;
            float hi = 1f - inset;
            if (wx >= lo && wx <= hi && wy >= lo && wy <= hi) return false;
            float dx = wx - 0.5f;
            float dy = wy - 0.5f;
            if (float.IsNaN(dx) || float.IsNaN(dy))
            {
                wx = 0.5f;
                wy = 0.5f;
                return true;
            }
            if (float.IsInfinity(dx) || float.IsInfinity(dy))
            {
                // Only the infinite axes keep a direction (a finite one is negligible next to them).
                dx = float.IsInfinity(dx) ? Math.Sign(dx) : 0f;
                dy = float.IsInfinity(dy) ? Math.Sign(dy) : 0f;
            }
            float half = 0.5f - inset;
            // Outside the inner square, so the larger axis is above half (and above 0).
            float scale = half / Math.Max(Math.Abs(dx), Math.Abs(dy));
            wx = 0.5f + dx * scale;
            wy = 0.5f + dy * scale;
            return true;
        }
    }

    // Phase 15 D11: where a world marker's label and arrow go on screen. Pure (no UnityEngine).
    public static class ScreenEdge
    {
        // 기능: 화면 좌표의 표지가 화면 안쪽(여백 margin)에 보이는지 보고, 아니면 화면 가운데에서 그쪽 방향의 가장자리 점과 각도를 낸다.
        // 입력: sx, sy - 화면 좌표(px, 왼쪽 아래 0), behind - 카메라 뒤에 있음(이때 좌표는 뒤집혀 나오므로 방향을 뒤집는다),
        //   width·height - 화면 크기(px), margin - 가장자리 여백(px).
        // 출력: 화면 안이면 false(ex·ey = sx·sy, angle 0). 밖이거나 뒤면 true와 가장자리 점(ex, ey), 화살표 각도(도, 0 = 오른쪽,
        //   반시계 +). 정확히 가운데 뒤면 아래쪽을 가리킨다.
        public static bool ToEdge(float sx, float sy, bool behind, float width, float height, float margin, out float ex, out float ey,
            out float angle)
        {
            float cx = width * 0.5f;
            float cy = height * 0.5f;
            if (!behind && sx >= margin && sx <= width - margin && sy >= margin && sy <= height - margin)
            {
                ex = sx;
                ey = sy;
                angle = 0f;
                return false;
            }
            float dx = sx - cx;
            float dy = sy - cy;
            if (behind)
            {
                dx = -dx;
                dy = -dy;
            }
            if (float.IsNaN(dx) || float.IsNaN(dy) || (Math.Abs(dx) < 1e-3f && Math.Abs(dy) < 1e-3f))
            {
                dx = 0f;
                dy = -1f;
            }
            float halfW = Math.Max(1f, cx - margin);
            float halfH = Math.Max(1f, cy - margin);
            float scale = Math.Min(halfW / Math.Max(Math.Abs(dx), 1e-6f), halfH / Math.Max(Math.Abs(dy), 1e-6f));
            ex = cx + dx * scale;
            ey = cy + dy * scale;
            angle = (float)(Math.Atan2(dy, dx) * (180.0 / Math.PI));
            return true;
        }
    }
}
