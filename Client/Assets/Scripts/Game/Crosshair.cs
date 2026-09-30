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

        public void SetVisible(bool visible)
        {
            // Unity null: on scene or play-mode teardown the root can be destroyed before its owner's
            // OnDestroy runs this; throwing there would skip the owner's remaining cleanup.
            if (_root == null) return;
            if (visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }

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
