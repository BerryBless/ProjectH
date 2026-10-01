using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.UI
{
    // Phase 11 D10: the kill feed, top right: the newest KillFeedModel.Capacity lines, newest on top, each for 6 s. Its
    // own canvas (no GraphicRaycaster, nothing is a raycast target), so a new or expiring line rebuilds only this canvas,
    // never the HUDs or the screens (ClientPerf 7). Texts are set only when the model's Version changed: an unchanged feed
    // allocates nothing per frame. Owned by GameClient; Dispose destroys the canvas.
    public sealed class KillFeed : System.IDisposable
    {
        private const int FontSize = 22;
        private const float LineHeight = 30f;

        private readonly KillFeedModel _model = new KillFeedModel();
        private readonly GameObject _root;
        private readonly Text[] _lines = new Text[KillFeedModel.Capacity];
        private int _shownVersion;

        public KillFeed()
        {
            _root = UiFactory.CreateCanvas("KillFeed", 94, interactive: false);   // above PoiLabel (93), under the crosshair (100)
            for (int i = 0; i < _lines.Length; i++)
            {
                _lines[i] = UiFactory.CreateText("Line" + i, _root.transform, string.Empty, FontSize, TextAnchor.UpperRight,
                    new Vector2(1f, 1f), new Vector2(-24f, -24f - LineHeight * i), new Vector2(640f, LineHeight));
                _lines[i].horizontalOverflow = HorizontalWrapMode.Overflow;
            }
        }

        // line: built once per death (UiText.KillLine). now: unscaled seconds.
        public void Add(string line, float now) => _model.Add(line, now);

        public void Clear() => _model.Clear();

        // Once per frame.
        public void Tick(float now)
        {
            // Unity null: the canvas can be destroyed on teardown before the owner's OnDestroy runs this.
            if (_root == null) return;
            _model.Expire(now);
            if (_model.Version == _shownVersion) return;
            _shownVersion = _model.Version;
            for (int i = 0; i < _lines.Length; i++) _lines[i].text = i < _model.Count ? _model.Line(i) : string.Empty;
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }
    }
}
