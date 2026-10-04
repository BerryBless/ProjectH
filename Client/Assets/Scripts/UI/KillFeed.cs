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

        // 기능: 킬 피드 전용 Canvas와 줄 Text들을 만든다.
        // 입력: 없음.
        // 출력: 빈 줄 KillFeedModel.Capacity개를 가진 KillFeed 객체.
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

        // 기능: 킬 피드에 새 줄을 추가한다.
        // 입력: line - 표시할 줄, now - 현재 시각(unscaled 초).
        // 출력: 반환값 없음. 모델에 줄이 추가되고 다음 Tick에 화면에 반영된다.
        // line: built once per death (UiText.KillLine). now: unscaled seconds.
        public void Add(string line, float now) => _model.Add(line, now);

        // 기능: 킬 피드의 모든 줄을 지운다.
        // 입력: 없음.
        // 출력: 반환값 없음. 모델이 비워지고 다음 Tick에 화면에서 사라진다.
        public void Clear() => _model.Clear();

        // 기능: 만료된 줄을 제거하고 모델이 바뀌었을 때만 줄 Text를 다시 쓴다.
        // 입력: now - 현재 시각(unscaled 초).
        // 출력: 반환값 없음. 모델 Version이 바뀌었으면 줄 Text가 최신순으로 갱신된다.
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

        // 기능: 킬 피드 Canvas를 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. Canvas와 줄 Text가 파괴된다.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }
    }
}
