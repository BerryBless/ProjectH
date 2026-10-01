using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.Game
{
    // Phase 6 D7: the name of the place the followed player stands in, top left (UGUI legacy Text with the built-in
    // font, like MatchHud). The text is set only when the place changes, from the constant names in MapPois, so an
    // unchanged label allocates nothing per frame. No GraphicRaycaster; nothing is a raycast target. Dispose destroys
    // the canvas.
    public sealed class PoiLabel : System.IDisposable
    {
        private const int FontSize = 20;

        private readonly GameObject _root;
        private readonly Text _text;
        private int _shown = -1;
        private bool _visible;

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
            _text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _text.fontSize = FontSize;
            _text.alignment = TextAnchor.UpperLeft;
            _text.color = new Color(1f, 0.95f, 0.8f);
            _text.raycastTarget = false;
            _text.horizontalOverflow = HorizontalWrapMode.Overflow;
            _text.text = string.Empty;

            _root.SetActive(false);
        }

        public void SetVisible(bool visible)
        {
            // Unity null: the root can be destroyed on teardown before the owner's OnDestroy runs this.
            if (_root == null || visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // Once per frame with the feet the camera follows (our own, or the spectated player's).
        public void SetPosition(Vector3 feet)
        {
            if (_root == null) return;
            int index = PoiLookup.Find(feet.x, feet.z);
            if (index == _shown) return;
            _shown = index;
            _text.text = index < 0 ? string.Empty : MapPois.All[index].Name;
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }
    }
}
