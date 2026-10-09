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
        // Phase 15 D3: the minimap holds the top right corner (20 px margin + 200 px), so the feed starts under it.
        private const float Top = -244f;

        private readonly KillFeedModel _model = new KillFeedModel();
        private readonly GameObject _root;
        private readonly Text[] _lines = new Text[KillFeedModel.Capacity];
        private int _shownVersion;

        // 기능: Kill Feed Canvas와 줄을 만든다(오른쪽 위, Phase 15부터 미니맵 아래).
        // 입력: 없음.
        // 출력: 빈 Kill Feed(Dispose가 Canvas를 파괴한다).
        public KillFeed()
        {
            _root = UiFactory.CreateCanvas("KillFeed", 94, interactive: false);   // above PoiLabel (93), under the crosshair (100)
            for (int i = 0; i < _lines.Length; i++)
            {
                _lines[i] = UiFactory.CreateText("Line" + i, _root.transform, string.Empty, FontSize, TextAnchor.UpperRight,
                    new Vector2(1f, 1f), new Vector2(-24f, Top - LineHeight * i), new Vector2(640f, LineHeight));
                _lines[i].horizontalOverflow = HorizontalWrapMode.Overflow;
            }
        }

        // 기능: Kill Feed에 줄 하나를 넣는다(가장 오래된 줄이 밀려날 수 있다).
        // 입력: line - 사망마다 한 번 만든 줄(UiText.KillLine), now - 현재 시각(unscaled 초).
        // 출력: 반환값 없음. 모델의 Version이 올라 다음 Tick에 다시 그린다.
        // line: built once per death (UiText.KillLine). now: unscaled seconds.
        public void Add(string line, float now) => _model.Add(line, now);

        // 기능: 모든 줄을 지운다.
        // 입력: 없음.
        // 출력: 반환값 없음. 줄이 있었으면 Version이 올라 다음 Tick에 비워진다.
        public void Clear() => _model.Clear();

        // 기능: 만료된 줄을 지우고, 모델의 Version이 바뀐 때만 Text들을 다시 쓴다.
        // 입력: now - 현재 시각(unscaled 초).
        // 출력: 반환값 없음. Canvas가 이미 파괴됐으면 아무것도 하지 않는다.
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

        // 기능: Kill Feed Canvas를 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 이미 파괴됐으면 아무것도 하지 않는다.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }
    }
}
