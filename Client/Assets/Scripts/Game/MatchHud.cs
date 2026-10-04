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

        // 기능: 경기 HUD Canvas와 자기장 밖 빨간 화면 가장자리, 상태·자기장·관전 Text를 코드로 만든다.
        // 입력: 없음.
        // 출력: 모든 요소가 생성되고 Root와 가장자리가 비활성인 MatchHud.
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

        // 기능: HUD 전체의 표시 여부를 바꾼다. 값이 같거나 Root가 이미 파괴됐으면 아무것도 하지 않는다.
        // 입력: visible - 보일지 여부.
        // 출력: 반환값 없음. Root GameObject의 활성 상태가 바뀐다.
        public void SetVisible(bool visible)
        {
            // Unity null: the root can be destroyed on teardown before the owner's OnDestroy runs this.
            if (_root == null || visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // 기능: 상단 경기 상태 줄을 문자열이 다시 만들어졌을 때만 화면에 반영한다.
        // 입력: state - 경기 진행 상태, secondsLeft - 시작까지 남은 초, alive - 생존자 수, participants - 참가자 수, minPlayers - 시작 최소 인원.
        // 출력: 반환값 없음. 값이 바뀌면 상태 Text가 갱신된다.
        public void SetStatus(MatchFlowState state, int secondsLeft, int alive, int participants, int minPlayers)
        {
            if (_root != null && _text.SetStatus(state, secondsLeft, alive, participants, minPlayers)) _status.text = _text.Status;
        }

        // 기능: 자기장 줄을 문자열이 다시 만들어졌을 때만 화면에 반영한다.
        // 입력: hint - 자기장 단계 안내, seconds - 축소까지 남은 초.
        // 출력: 반환값 없음. 값이 바뀌면 자기장 Text가 갱신된다.
        public void SetZone(ZoneHint hint, int seconds)
        {
            if (_root != null && _text.SetZone(hint, seconds)) _zone.text = _text.Zone;
        }

        // 기능: 관전 대상 줄을 문자열이 다시 만들어졌을 때만 화면에 반영한다.
        // 입력: entityId - 관전 대상 Entity ID(0이면 숨김), name - 그 플레이어 이름(모르면 null).
        // 출력: 반환값 없음. 값이 바뀌면 관전 Text가 갱신된다.
        // 0 hides the line. name: PlayerSpawned's name of that player, null when not known.
        public void SetSpectating(ushort entityId, string name)
        {
            if (_root != null && _text.SetSpectating(entityId, name)) _spectating.text = _text.Spectating;
        }

        // 기능: 로컬 플레이어가 자기장 밖에 있는 동안 빨간 화면 가장자리를 켠다.
        // 입력: outside - 자기장 밖 여부.
        // 출력: 반환값 없음. 가장자리 GameObject의 활성 상태가 바뀐다.
        // Red screen edges while the local player is outside the zone.
        public void SetOutside(bool outside)
        {
            if (_root != null && _edges.activeSelf != outside) _edges.SetActive(outside);
        }

        // 기능: 경기 HUD Canvas를 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. Root와 모든 자식 UI가 파괴된다.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }

        // 기능: Root 아래에 Raycast 대상이 아니고 Rich Text를 쓰지 않는 가운데 정렬 Text 하나를 만든다.
        // 입력: name - GameObject 이름, font - 사용할 글꼴, size - 글자 크기, anchor - 앵커·피벗 위치, offset - 앵커 기준 위치.
        // 출력: 빈 문자열로 초기화된 Text.
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

        // 기능: 화면 한쪽 가장자리를 따라 반투명 빨간 막대 하나를 만든다.
        // 입력: parent - 부모 RectTransform, anchorMin - 가장자리 앵커 시작, anchorMax - 가장자리 앵커 끝, size - 가장자리에 수직인 두께.
        // 출력: 반환값 없음. 부모 아래에 가장자리 Image가 생긴다.
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
