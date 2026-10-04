using ProjectH.Client.UI;
using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.Game
{
    // Phase 6 D7: the name of the place the followed player stands in, top left (UGUI legacy Text with UiFont, like
    // MatchHud). The text is set only when the place changes, from the constant names in MapPois, so an
    // unchanged label allocates nothing per frame. No GraphicRaycaster; nothing is a raycast target. Dispose destroys
    // the canvas.
    public sealed class PoiLabel : System.IDisposable
    {
        private const int FontSize = 20;

        private readonly GameObject _root;
        private readonly Text _text;
        private int _shown = -1;
        private bool _visible;

        // 기능: 좌상단 POI 이름 표시용 Overlay Canvas와 Text를 만든다.
        // 입력: 없음.
        // 출력: 비활성 상태이고 표시 중인 POI가 없는 PoiLabel.
        public PoiLabel()
        {
            _root = new GameObject("PoiLabel");
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 93;   // above MatchHud (92), under the crosshair (100)

            var go = new GameObject("Name", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_root.transform, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(20f, -20f);
            rect.sizeDelta = new Vector2(400f, FontSize + 20f);

            _text = go.AddComponent<Text>();
            _text.font = UiFont.Get();
            _text.fontSize = FontSize;
            _text.alignment = TextAnchor.UpperLeft;
            _text.color = new Color(1f, 0.95f, 0.8f);
            _text.raycastTarget = false;
            _text.supportRichText = false;   // Phase 11: every UI text is plain text
            _text.horizontalOverflow = HorizontalWrapMode.Overflow;
            _text.text = string.Empty;

            _root.SetActive(false);
        }

        // 기능: 라벨 Canvas의 표시 여부를 바꾼다.
        // 입력: visible - 표시 여부.
        // 출력: 반환값 없음. 값이 바뀌었고 루트가 살아 있으면 Canvas가 켜지거나 꺼진다.
        public void SetVisible(bool visible)
        {
            // Unity null: the root can be destroyed on teardown before the owner's OnDestroy runs this.
            if (_root == null || visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // 기능: 매 프레임 따라가는 발 위치가 속한 POI를 찾아 이름을 표시한다.
        // 입력: feet - 카메라가 따라가는 플레이어의 발 위치.
        // 출력: 반환값 없음. POI가 바뀐 경우에만 Text가 바뀐다(POI 밖이면 빈 문자열).
        // Once per frame with the feet the camera follows (our own, or the spectated player's).
        public void SetPosition(Vector3 feet)
        {
            if (_root == null) return;
            int index = PoiLookup.Find(feet.x, feet.z);
            if (index == _shown) return;
            _shown = index;
            _text.text = index < 0 ? string.Empty : MapPois.All[index].Name;
        }

        // 기능: 라벨 Canvas를 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 루트 GameObject가 파괴된다.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }
    }
}
