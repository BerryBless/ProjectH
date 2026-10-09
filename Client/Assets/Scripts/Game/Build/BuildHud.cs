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
        private bool _editShown;
        private BuildPieceType _piece;
        private BuildMaterialType _material;
        private float _noticeHideTime = -1f;

        // 기능: 건설 HUD Canvas(자원·모드·키·알림 글자)를 숨긴 채 만든다.
        // 입력: 없음.
        // 출력: 숨겨진 HUD(Dispose가 Canvas를 파괴한다).
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

        // 기능: HUD 전체를 보이거나 숨긴다(바뀔 때만 SetActive).
        // 입력: visible - 보일지.
        // 출력: 반환값 없음.
        public void SetVisible(bool visible)
        {
            if (_root == null || visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // 기능: 자원 줄을 정한다. 세 값 중 하나라도 바뀌었을 때만 문자열을 다시 만든다.
        // 입력: wood, stone, metal - 보일 자원 수(서버 수치에서 대기 배치 비용을 뺀 것).
        // 출력: 반환값 없음.
        public void SetResources(int wood, int stone, int metal)
        {
            if (_root == null || (wood == _wood && stone == _stone && metal == _metal)) return;
            _wood = wood;
            _stone = stone;
            _metal = metal;
            _resources.text = UiText.ResourcesLine(wood, stone, metal);
        }

        // 기능: 건설·편집 모드 안내 줄과 키 줄을 정한다. 보이는 값이 바뀔 때만 문자열을 다시 만든다.
        // 입력: buildMode - 건설 모드, piece·material - 고른 조각과 재료, editing - 편집 모드(Phase 13.5 D10, 건설 모드보다 우선),
        //   editPiece - 편집하는 조각 종류.
        // 출력: 반환값 없음. 편집 중이면 "편집: …"과 편집 키, 건설 모드면 건설 줄과 건설 키, 아니면 빈 줄.
        public void SetMode(bool buildMode, BuildPieceType piece, BuildMaterialType material, bool editing = false, BuildPieceType editPiece = BuildPieceType.Wall)
        {
            if (_root == null) return;
            if (editing)
            {
                if (_editShown && editPiece == _piece) return;
                _editShown = true;
                _modeShown = false;
                _piece = editPiece;
                _mode.text = UiText.EditModeLine(editPiece);
                _keys.text = UiText.EditKeys;
                return;
            }
            if (!_editShown && buildMode == _modeShown && (!buildMode || (piece == _piece && material == _material))) return;
            _editShown = false;
            _modeShown = buildMode;
            _piece = piece;
            _material = material;
            _mode.text = buildMode ? UiText.BuildModeLine(piece, material) : string.Empty;
            _keys.text = buildMode ? UiText.BuildKeys : string.Empty;
        }

        // 기능: 거절 알림을 NoticeSeconds 동안 보인다(이미 보이는 중이면 시간을 늘린다).
        // 입력: message - 알림 문구(상수 UiText.BuildRefusal: 할당 없음, null이면 무시), now - 현재 시각(초).
        // 출력: 반환값 없음. Tick이 시간이 지나면 지운다.
        // message: a constant (UiText.BuildRefusal), so a notice allocates nothing.
        public void ShowNotice(string message, float now)
        {
            if (_root == null || message == null) return;
            _notice.text = message;
            _noticeHideTime = now + NoticeSeconds;
        }

        // 기능: 알림의 표시 시간이 지났으면 지운다(매 프레임).
        // 입력: now - 현재 시각(초).
        // 출력: 반환값 없음.
        public void Tick(float now)
        {
            if (_root == null || _noticeHideTime < 0f || now < _noticeHideTime) return;
            _noticeHideTime = -1f;
            _notice.text = string.Empty;
        }

        // 기능: HUD Canvas를 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }
    }
}
