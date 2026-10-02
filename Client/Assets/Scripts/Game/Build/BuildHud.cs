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

        public void SetVisible(bool visible)
        {
            if (_root == null || visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        public void SetResources(int wood, int stone, int metal)
        {
            if (_root == null || (wood == _wood && stone == _stone && metal == _metal)) return;
            _wood = wood;
            _stone = stone;
            _metal = metal;
            _resources.text = UiText.ResourcesLine(wood, stone, metal);
        }

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

        // message: a constant (UiText.BuildRefusal), so a notice allocates nothing.
        public void ShowNotice(string message, float now)
        {
            if (_root == null || message == null) return;
            _notice.text = message;
            _noticeHideTime = now + NoticeSeconds;
        }

        public void Tick(float now)
        {
            if (_root == null || _noticeHideTime < 0f || now < _noticeHideTime) return;
            _noticeHideTime = -1f;
            _notice.text = string.Empty;
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }
    }
}
