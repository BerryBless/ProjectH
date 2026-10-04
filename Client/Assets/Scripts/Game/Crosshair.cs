using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.Game
{
    // Screen-centre aim mark (D14): one Screen Space Overlay canvas built in code. It has no
    // GraphicRaycaster and its images are not raycast targets (never clicked), and nothing on it
    // changes after creation, so it costs no per-frame UI rebuild. Dispose destroys it.
    public sealed class Crosshair : System.IDisposable
    {
        private const float Gap = 5f;
        private const float Length = 8f;
        private const float Thickness = 2f;

        private readonly GameObject _root;
        private bool _visible;

        // 기능: 화면 중앙 조준선 Canvas와 막대 다섯 개를 코드로 만든다.
        // 입력: 없음.
        // 출력: Root가 비활성인 Crosshair.
        public Crosshair()
        {
            _root = new GameObject("Crosshair");
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            float barOffset = Gap + Length * 0.5f;
            AddBar(new Vector2(0f, barOffset), new Vector2(Thickness, Length));
            AddBar(new Vector2(0f, -barOffset), new Vector2(Thickness, Length));
            AddBar(new Vector2(-barOffset, 0f), new Vector2(Length, Thickness));
            AddBar(new Vector2(barOffset, 0f), new Vector2(Length, Thickness));
            AddBar(Vector2.zero, new Vector2(Thickness, Thickness));

            _root.SetActive(false);
        }

        // 기능: 조준선 표시 여부를 바꾼다. 값이 같거나 Root가 이미 파괴됐으면 아무것도 하지 않는다.
        // 입력: visible - 보일지 여부.
        // 출력: 반환값 없음. Root GameObject의 활성 상태가 바뀐다.
        public void SetVisible(bool visible)
        {
            // Unity null: on scene or play-mode teardown the root can be destroyed before its owner's
            // OnDestroy runs this; throwing there would skip the owner's remaining cleanup.
            if (_root == null) return;
            if (visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // 기능: 조준선 Canvas를 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. Root와 막대가 파괴된다.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }

        // 기능: 화면 중앙 기준 위치에 Raycast 대상이 아닌 흰색 막대 하나를 추가한다.
        // 입력: offset - 중앙 기준 위치, size - 막대 크기.
        // 출력: 반환값 없음. Root 아래에 막대 Image가 생긴다.
        private void AddBar(Vector2 offset, Vector2 size)
        {
            var bar = new GameObject("Bar");
            var rect = bar.AddComponent<RectTransform>();
            rect.SetParent(_root.transform, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = offset;

            var image = bar.AddComponent<Image>();   // no sprite: draws a solid rectangle
            image.color = new Color(1f, 1f, 1f, 0.9f);
            image.raycastTarget = false;
        }
    }
}
