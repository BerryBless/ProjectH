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

        // 기능: 왼쪽 위 장소 이름 캔버스와 Text 하나를 만든다(Phase 6 D7, 숨긴 채).
        // 입력: 없음.
        // 출력: 숨겨진 라벨(Dispose가 캔버스를 파괴한다).
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

        // 기능: 라벨을 보이거나 숨긴다(같은 값이면 아무것도 하지 않는다).
        // 입력: visible - 보일지 여부.
        // 출력: 반환값 없음. 캔버스 Root의 활성 상태가 바뀐다.
        public void SetVisible(bool visible)
        {
            // Unity null: the root can be destroyed on teardown before the owner's OnDestroy runs this.
            if (_root == null || visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // 기능: 프레임마다 카메라가 따라가는 발(내 것 또는 관전 대상)이 있는 장소 이름을 장소가 바뀔 때만 쓴다(MapPois의 상수 이름, 할당 없음).
        // 입력: feet - 카메라가 따라가는 발 위치.
        // 출력: 반환값 없음. 장소 밖이면 빈 문자열이 된다.
        public void SetPosition(Vector3 feet)
        {
            if (_root == null) return;
            int index = PoiLookup.Find(feet.x, feet.z);
            if (index == _shown) return;
            _shown = index;
            _text.text = index < 0 ? string.Empty : MapPois.All[index].Name;
        }

        // 기능: 라벨 캔버스를 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }
    }
}
