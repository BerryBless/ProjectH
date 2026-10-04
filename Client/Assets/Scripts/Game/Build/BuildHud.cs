using ProjectH.Client.UI;
using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.Game
{
    // Phase 13 D16 (request §41, §107): the resources (bottom right, the server's numbers minus pending placements),
    // the build mode line and keys (above the weapon slots, in build mode), and a refusal notice. Its own canvas without
    // a GraphicRaycaster; each text is rebuilt only when a shown value changes, so an unchanged HUD allocates nothing.
    // Dispose destroys the canvas.
    public sealed class BuildHud : System.IDisposable
    {
        private const float NoticeSeconds = 1.5f;

        private readonly GameObject _root;
        private readonly Text _resources;
        private readonly Text _mode;
        private readonly Text _keys;
        private readonly Text _notice;
        private bool _visible;
        private int _wood = -1;
        private int _stone = -1;
        private int _metal = -1;
        private bool _modeShown;
        private BuildPieceType _piece;
        private BuildMaterialType _material;
        private float _noticeHideTime = -1f;

        // 기능: 자원·건설 모드·키 안내·거절 알림 Text를 담은 HUD Canvas를 만든다.
        // 입력: 없음.
        // 출력: 숨김 상태로 생성된 BuildHud.
        public BuildHud()
        {
            _root = UiFactory.CreateCanvas("BuildHud", 92, interactive: false);
            _resources = UiFactory.CreateText("Resources", _root.transform, string.Empty, 18, TextAnchor.LowerRight, new Vector2(1f, 0f),
                new Vector2(-24f, 20f), new Vector2(420f, 24f));
            _mode = UiFactory.CreateText("Mode", _root.transform, string.Empty, 20, TextAnchor.LowerCenter, new Vector2(0.5f, 0f),
                new Vector2(0f, 150f), new Vector2(520f, 26f));
            _mode.color = UiFactory.AccentColor;
            _keys = UiFactory.CreateText("Keys", _root.transform, string.Empty, 16, TextAnchor.LowerCenter, new Vector2(0.5f, 0f),
                new Vector2(0f, 126f), new Vector2(720f, 22f));
            _notice = UiFactory.CreateText("Notice", _root.transform, string.Empty, 18, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f),
                new Vector2(0f, -170f), new Vector2(520f, 24f));
            _notice.color = new Color(1f, 0.55f, 0.4f);
            _root.SetActive(false);
        }

        // 기능: HUD 표시 여부를 바꾼다.
        // 입력: visible - 표시하려면 true.
        // 출력: 반환값 없음. 값이 바뀔 때만 Canvas 활성 상태가 바뀐다.
        public void SetVisible(bool visible)
        {
            if (_root == null || visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // 기능: 자원 줄을 갱신한다.
        // 입력: wood - 나무 수, stone - 돌 수, metal - 금속 수.
        // 출력: 반환값 없음. 값이 바뀔 때만 자원 Text가 다시 만들어진다.
        public void SetResources(int wood, int stone, int metal)
        {
            if (_root == null || (wood == _wood && stone == _stone && metal == _metal)) return;
            _wood = wood;
            _stone = stone;
            _metal = metal;
            _resources.text = UiText.ResourcesLine(wood, stone, metal);
        }

        // 기능: 건설 모드 줄과 키 안내를 갱신한다.
        // 입력: buildMode - 건설 모드 여부, piece - 선택한 조각 종류, material - 선택한 재료.
        // 출력: 반환값 없음. 표시 값이 바뀔 때만 Text가 바뀌며, 건설 모드가 아니면 비워진다.
        public void SetMode(bool buildMode, BuildPieceType piece, BuildMaterialType material)
        {
            if (_root == null) return;
            if (buildMode == _modeShown && (!buildMode || (piece == _piece && material == _material))) return;
            _modeShown = buildMode;
            _piece = piece;
            _material = material;
            _mode.text = buildMode ? UiText.BuildModeLine(piece, material) : string.Empty;
            _keys.text = buildMode ? UiText.BuildKeys : string.Empty;
        }

        // 기능: 거절 알림을 NoticeSeconds 동안 띄운다.
        // 입력: message - 표시할 상수 문자열(null이면 무시), now - 현재 시간(초).
        // 출력: 반환값 없음. 알림 Text와 숨길 시각이 설정된다.
        // message: a constant (UiText.BuildRefusal), so a notice allocates nothing.
        public void ShowNotice(string message, float now)
        {
            if (_root == null || message == null) return;
            _notice.text = message;
            _noticeHideTime = now + NoticeSeconds;
        }

        // 기능: 알림 표시 시간이 지나면 알림을 지운다.
        // 입력: now - 현재 시간(초).
        // 출력: 반환값 없음. 시간이 지났으면 알림 Text가 비워진다.
        public void Tick(float now)
        {
            if (_root == null || _noticeHideTime < 0f || now < _noticeHideTime) return;
            _noticeHideTime = -1f;
            _notice.text = string.Empty;
        }

        // 기능: HUD Canvas를 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. Canvas GameObject가 파괴된다.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }
    }
}
