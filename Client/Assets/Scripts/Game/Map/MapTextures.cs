using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.Game.Map
{
    // Phase 15 D2, D12: the textures every map UI shares, made once at start and destroyed in Dispose:
    //   Map   - the map picture (MapRaster, 256 x 256, clamped: past the edge the black ring stretches),
    //   Ring  - one circle outline; zone rings only change their size,
    //   Dot   - a filled circle (teammates, pings, POIs),
    //   Arrow - a triangle pointing up (the player, screen-edge arrows).
    // The raster's byte array lives only during construction; Apply(..., makeNoLongerReadable) drops the CPU copies.
    public sealed class MapTextures : System.IDisposable
    {
        private const int RingSize = 256;
        private const float RingWidth = 4f;   // px of the 256 texture
        private const int DotSize = 32;
        private const int ArrowSize = 32;

        // The ring's line sits RingWidth / 2 + 1 px inside the texture edge, so a ring of radius r px is drawn this big.
        // 기능: 반지름 r px의 원을 그리려면 Ring 텍스처를 몇 px 크기로 둬야 하는지 계산한다.
        // 입력: radiusPx - 원 반지름(px).
        // 출력: RawImage 한 변(px).
        public static float RingDiameter(float radiusPx) => 2f * radiusPx * (RingSize * 0.5f) / (RingSize * 0.5f - 0.5f - RingWidth * 0.5f - 1f);

        public Texture2D Map { get; }
        public Texture2D Ring { get; }
        public Texture2D Dot { get; }
        public Texture2D Arrow { get; }

        // 기능: 지도 그림과 아이콘 텍스처 네 장을 만든다.
        // 입력: 없음.
        // 출력: 텍스처를 가진 객체(Dispose가 모두 파괴한다).
        public MapTextures()
        {
            int size = MapRaster.DefaultSize;
            var rgba = new byte[size * size * 4];
            MapRaster.Rasterize(rgba, size);
            Map = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "MapPicture",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            Map.SetPixelData(rgba, 0);
            Map.Apply(false, true);

            // Mipmaps: a big ring texture drawn small (a late zone on the full map) keeps a visible line instead of aliasing away.
            Ring = Make("MapRing", RingSize, true, (x, y, c) =>
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                float edge = c - RingWidth * 0.5f - 1f;   // centre of the line, one pixel inside the texture border
                return Mathf.Clamp01(RingWidth * 0.5f + 0.5f - Mathf.Abs(d - edge));
            });
            Dot = Make("MapDot", DotSize, false, (x, y, c) =>
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                return Mathf.Clamp01(c - 0.5f - d);
            });
            Arrow = Make("MapArrow", ArrowSize, false, (x, y, c) =>
            {
                // Apex at the top centre, base along the bottom: inside when |x - c| <= (top - y) / 2.
                float top = ArrowSize - 1.5f;
                float half = (top - y) * 0.5f;
                return y < 1.5f || y > top ? 0f : Mathf.Clamp01(half - Mathf.Abs(x - c) + 0.5f);
            });
        }

        // 기능: 텍스처를 모두 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            if (Map != null) Object.Destroy(Map);
            if (Ring != null) Object.Destroy(Ring);
            if (Dot != null) Object.Destroy(Dot);
            if (Arrow != null) Object.Destroy(Arrow);
        }

        // 기능: 흰색에 알파만 다른 정사각형 텍스처를 만든다(색은 RawImage.color가 정한다).
        // 입력: name - 이름, size - 한 변 픽셀, mips - Mipmap을 만들지, alpha - 픽셀 중심(x, y)과 중심 좌표 c를 받아 0..1 알파를 내는 함수.
        // 출력: 읽을 수 없게 올린(Apply) Clamp 텍스처. 시작할 때 한 번만 부른다(배열 하나를 할당한다).
        private static Texture2D Make(string name, int size, bool mips, System.Func<float, float, float, float> alpha)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, mips)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color32[size * size];
            float c = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++) pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha(x, y, c) * 255f + 0.5f));
            }
            texture.SetPixels32(pixels);
            texture.Apply(mips, true);
            return texture;
        }
    }

    // Phase 15: the colours of the map and the world markers (D3, D4, D11).
    public static class MapColors
    {
        public static readonly Color Location = new Color(1f, 0.9f, 0.2f);
        public static readonly Color Enemy = new Color(1f, 0.25f, 0.2f);
        public static readonly Color Item = new Color(0.45f, 0.82f, 1f);
        public static readonly Color Danger = new Color(1f, 0.55f, 0.1f);
        public static readonly Color Self = new Color(1f, 1f, 1f);
        public static readonly Color Teammate = new Color(0.3f, 0.95f, 0.4f);
        public static readonly Color Downed = new Color(1f, 0.25f, 0.2f);
        public static readonly Color ZoneNow = new Color(1f, 1f, 1f, 0.95f);
        public static readonly Color ZoneNext = new Color(0.3f, 0.6f, 1f, 0.95f);
        public static readonly Color Poi = new Color(1f, 1f, 1f, 0.7f);
        public static readonly Color Station = new Color(0.35f, 0.85f, 1f);
        public static readonly Color StationCooling = new Color(0.5f, 0.5f, 0.5f);
        public static readonly Color MyWaypoint = new Color(1f, 1f, 1f);
        public static readonly Color TeamWaypoint = new Color(0.45f, 1f, 0.75f);
        public static readonly Color Route = new Color(1f, 0.85f, 0.3f, 0.8f);
        // Phase 16 D8: a supply drop falling, landed (closed) and opened.
        public static readonly Color SupplyFalling = new Color(0.6f, 0.8f, 1f);
        public static readonly Color SupplyLanded = new Color(0.2f, 0.45f, 1f);
        public static readonly Color SupplyOpened = new Color(0.5f, 0.5f, 0.55f);

        // 기능: Supply Drop 상태의 색을 고른다(Phase 16 D8: 낙하 연한 파랑, 착지 파랑, 열림 회색).
        // 입력: state - Supply Drop 상태.
        // 출력: 색.
        public static Color Of(SupplyDropState state)
        {
            switch (state)
            {
                case SupplyDropState.Falling: return SupplyFalling;
                case SupplyDropState.Landed: return SupplyLanded;
                default: return SupplyOpened;
            }
        }

        // 기능: Ping 종류의 색을 고른다(D11: Location 노랑, Enemy 빨강, Item 하늘, Danger 주황).
        // 입력: kind - Ping 종류.
        // 출력: 색.
        public static Color Of(MapMarkerKind kind)
        {
            switch (kind)
            {
                case MapMarkerKind.Enemy: return Enemy;
                case MapMarkerKind.Item: return Item;
                case MapMarkerKind.Danger: return Danger;
                default: return Location;
            }
        }
    }

    // Phase 15 D12: one map icon (a RawImage), made once and reused. Its anchor and pivot sit at the parent's bottom-left /
    // its own centre, so the position is MapProjection's rectangle pixels. Position, angle, size and colour are written only
    // when they change (the position by more than half a pixel), so an unchanged icon costs no canvas rebuild.
    internal sealed class MapIcon
    {
        private readonly RectTransform _rect;
        private readonly GameObject _go;
        private readonly RawImage _image;
        private float _x = float.NaN;
        private float _y = float.NaN;
        private float _angle;
        private float _size;
        private Color _color;
        private bool _shown;

        // 기능: 아이콘 하나를 숨긴 채 만든다.
        // 입력: name - 이름, parent - 지도 사각형, texture - 텍스처(null이면 흰 사각형), size - 한 변(px), color - 색.
        // 출력: 숨겨진 아이콘(부모가 파괴될 때 함께 사라진다).
        public MapIcon(string name, Transform parent, Texture texture, float size, Color color)
        {
            _rect = UiFactory.CreateRect(name, parent, Vector2.zero, Vector2.zero, new Vector2(size, size));
            _rect.pivot = new Vector2(0.5f, 0.5f);
            _go = _rect.gameObject;
            _image = _go.AddComponent<RawImage>();
            _image.texture = texture;
            _image.color = color;
            _image.raycastTarget = false;
            _size = size;
            _color = color;
            _go.SetActive(false);
        }

        public bool Shown => _shown;
        public float X => _x;
        public float Y => _y;

        // 기능: 아이콘을 보이고 그 위치에 둔다(0.5 px 넘게 움직였을 때만 쓴다).
        // 입력: x, y - 부모 왼쪽 아래 기준 위치(px).
        // 출력: 반환값 없음.
        public void Place(float x, float y)
        {
            Show(true);
            if (!(Mathf.Abs(x - _x) <= 0.5f && Mathf.Abs(y - _y) <= 0.5f))
            {
                _x = x;
                _y = y;
                _rect.anchoredPosition = new Vector2(x, y);
            }
        }

        // 기능: 아이콘을 돌린다(0.5도 넘게 바뀌었을 때만).
        // 입력: degrees - Z축 각도(반시계 +).
        // 출력: 반환값 없음.
        public void SetAngle(float degrees)
        {
            if (Mathf.Abs(Mathf.DeltaAngle(degrees, _angle)) <= 0.5f) return;
            _angle = degrees;
            _rect.localEulerAngles = new Vector3(0f, 0f, degrees);
        }

        // 기능: 아이콘 크기(정사각형 한 변)를 바꾼다(0.5 px 넘게 바뀌었을 때만).
        // 입력: size - 한 변(px).
        // 출력: 반환값 없음.
        public void SetSize(float size)
        {
            if (Mathf.Abs(size - _size) <= 0.5f) return;
            _size = size;
            _rect.sizeDelta = new Vector2(size, size);
        }

        // 기능: 가로·세로가 다른 크기로 바꾼다(경로 선).
        // 입력: width, height - 크기(px).
        // 출력: 반환값 없음. 정사각형 크기 기억은 width로 둔다.
        public void SetSize(float width, float height)
        {
            _size = width;
            _rect.sizeDelta = new Vector2(width, height);
        }

        // 기능: 색을 바꾼다(다를 때만).
        // 입력: color - 색.
        // 출력: 반환값 없음.
        public void SetColor(Color color)
        {
            if (color == _color) return;
            _color = color;
            _image.color = color;
        }

        // 기능: 아이콘을 보이거나 숨긴다(바뀔 때만 SetActive).
        // 입력: shown - 보일지.
        // 출력: 반환값 없음.
        public void Show(bool shown)
        {
            if (shown == _shown) return;
            _shown = shown;
            _go.SetActive(shown);
        }
    }

    // Phase 15 D4: one map text (a POI or teammate name, the full-map hint), made once; text and position change only when
    // they differ (the text by reference: callers pass cached strings).
    internal sealed class MapLabel
    {
        private readonly RectTransform _rect;
        private readonly Text _text;
        private float _x = float.NaN;
        private float _y = float.NaN;
        private bool _shown;

        // 기능: 숨긴 글자 하나를 만든다.
        // 입력: name - 이름, parent - 부모, fontSize - 글자 크기, color - 색, size - 상자 크기(px).
        // 출력: 숨겨진 글자(부모가 파괴될 때 함께 사라진다).
        public MapLabel(string name, Transform parent, int fontSize, Color color, Vector2 size)
        {
            Text text = UiFactory.CreateText(name, parent, string.Empty, fontSize, TextAnchor.MiddleCenter, Vector2.zero, Vector2.zero, size);
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            _text = text;
            _rect = text.rectTransform;
            _rect.pivot = new Vector2(0.5f, 0.5f);
            _rect.gameObject.SetActive(false);
        }

        // 기능: 글자를 보이고 내용과 위치를 맞춘다(바뀐 것만 쓴다).
        // 입력: text - 문자열(null이면 빈 문자열), x, y - 부모 왼쪽 아래 기준 위치(px).
        // 출력: 반환값 없음.
        public void Set(string text, float x, float y)
        {
            if (!_shown)
            {
                _shown = true;
                _rect.gameObject.SetActive(true);
            }
            string value = text ?? string.Empty;
            if (!ReferenceEquals(value, _text.text) && value != _text.text) _text.text = value;
            if (Mathf.Abs(x - _x) <= 0.5f && Mathf.Abs(y - _y) <= 0.5f) return;
            _x = x;
            _y = y;
            _rect.anchoredPosition = new Vector2(x, y);
        }

        // 기능: 글자를 숨긴다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Hide()
        {
            if (!_shown) return;
            _shown = false;
            _rect.gameObject.SetActive(false);
        }
    }
}
