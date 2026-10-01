using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.Game
{
    // D14: the match HUD on one Screen Space Overlay canvas built in code (UGUI legacy Text with UiFont, Phase 11 D2):
    // the state line and the zone line at the top, "관전 중" near the bottom (the result is the result screen's, Phase 11
    // D7), and red screen edges while the local player stands outside the zone. No GraphicRaycaster; nothing is a
    // raycast target. Text is set only when MatchHudText rebuilt a string, and the edges only toggle, so an
    // unchanged HUD allocates nothing per frame. Dispose destroys the canvas.
    public sealed class MatchHud : System.IDisposable
    {
        private const int FontSize = 22;
        private const float EdgeThickness = 28f;

        private readonly MatchHudText _text = new MatchHudText();
        private readonly GameObject _root;
        private readonly Text _status;
        private readonly Text _zone;
        private readonly Text _spectating;
        private readonly GameObject _edges;
        private bool _visible;

        public MatchHud()
        {
            _root = new GameObject("MatchHud");
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 92;   // above CombatHud (90) and InventoryHud (91), under the crosshair (100)

            // The edges first, so the texts draw over them.
            _edges = new GameObject("OutsideZoneEdges", typeof(RectTransform));
            var edgesRect = (RectTransform)_edges.transform;
            edgesRect.SetParent(_root.transform, false);
            edgesRect.anchorMin = Vector2.zero;
            edgesRect.anchorMax = Vector2.one;
            edgesRect.offsetMin = Vector2.zero;
            edgesRect.offsetMax = Vector2.zero;
            CreateEdge(edgesRect, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, EdgeThickness));   // bottom
            CreateEdge(edgesRect, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, EdgeThickness));   // top
            CreateEdge(edgesRect, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(EdgeThickness, 0f));   // left
            CreateEdge(edgesRect, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(EdgeThickness, 0f));   // right
            _edges.SetActive(false);

            Font font = UiFont.Get();
            _status = CreateText("Status", font, FontSize, new Vector2(0.5f, 1f), new Vector2(0f, -20f));
            _zone = CreateText("Zone", font, FontSize, new Vector2(0.5f, 1f), new Vector2(0f, -50f));
            _zone.color = new Color(0.6f, 0.85f, 1f);
            _spectating = CreateText("Spectating", font, FontSize, new Vector2(0.5f, 0f), new Vector2(0f, 130f));

            _root.SetActive(false);
        }

        public void SetVisible(bool visible)
        {
            // Unity null: the root can be destroyed on teardown before the owner's OnDestroy runs this.
            if (_root == null || visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        public void SetStatus(MatchFlowState state, int secondsLeft, int alive, int participants, int minPlayers)
        {
            if (_root != null && _text.SetStatus(state, secondsLeft, alive, participants, minPlayers)) _status.text = _text.Status;
        }

        public void SetZone(ZoneHint hint, int seconds)
        {
            if (_root != null && _text.SetZone(hint, seconds)) _zone.text = _text.Zone;
        }

        // 0 hides the line. name: PlayerSpawned's name of that player, null when not known.
        public void SetSpectating(ushort entityId, string name)
        {
            if (_root != null && _text.SetSpectating(entityId, name)) _spectating.text = _text.Spectating;
        }

        // Red screen edges while the local player is outside the zone.
        public void SetOutside(bool outside)
        {
            if (_root != null && _edges.activeSelf != outside) _edges.SetActive(outside);
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }

        private Text CreateText(string name, Font font, int size, Vector2 anchor, Vector2 offset)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_root.transform, false);
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = offset;
            rect.sizeDelta = new Vector2(800f, size + 20f);

            var text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false;
            text.supportRichText = false;   // Phase 11: player names are shown as typed, never as markup
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.text = string.Empty;
            return text;
        }

        // A red bar along one screen edge: anchored to the edge (anchorMin..anchorMax), size = thickness across it.
        private static void CreateEdge(RectTransform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 size)
        {
            var go = new GameObject("Edge", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(anchorMin.x == anchorMax.x ? anchorMin.x : 0.5f, anchorMin.y == anchorMax.y ? anchorMin.y : 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
            var image = go.AddComponent<Image>();   // no sprite: draws a solid rectangle
            image.color = new Color(1f, 0.1f, 0.1f, 0.35f);
            image.raycastTarget = false;
        }
    }
}
