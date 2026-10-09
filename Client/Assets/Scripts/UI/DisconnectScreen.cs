using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ProjectH.Client.UI
{
    // Phase 11 D6: why the connection ended, in Korean. While GameClient reconnects on its own it shows the attempt and
    // the seconds to the next one, and Cancel; otherwise Retry and To Title. Texts change only when what they show
    // changes: the reason is one of UiText's constant strings (compared by reference), the progress line is rebuilt only
    // when the attempt or the whole second changes.
    public sealed class DisconnectScreen
    {
        private readonly GameObject _root;
        private readonly Text _reason;
        private readonly Text _progress;
        private readonly GameObject _cancel;
        private readonly GameObject _retry;
        private readonly Button _retryButton;
        private bool _retryEnabled = true;
        private readonly GameObject _toTitle;
        private bool _visible = true;
        private bool _reconnecting = true;   // forces the first SetReconnecting(false) to lay the buttons out
        private string _shownReason;
        private int _shownAttempt = -1;
        private int _shownSeconds = -1;

        // 기능: 끊김 화면(제목·이유·진행 줄·재접속 취소/다시 접속/타이틀로 버튼)을 Canvas 아래에 만든다.
        // 입력: canvas - 부모 Canvas, onCancel - 재접속 취소 처리, onRetry - 다시 접속 처리, onToTitle - 타이틀로 처리.
        // 출력: 보이는 상태로 시작하고 Retry·To Title 버튼이 보이는 DisconnectScreen.
        public DisconnectScreen(Transform canvas, UnityAction onCancel, UnityAction onRetry, UnityAction onToTitle)
        {
            _root = UiFactory.CreateScreen("Disconnected", canvas, dim: true).gameObject;
            RectTransform panel = UiFactory.CreatePanel("Panel", _root.transform, new Vector2(760f, 420f));
            Vector2 center = new Vector2(0.5f, 0.5f);
            Text title = UiFactory.CreateText("Title", panel, "연결이 끊겼습니다", 40, TextAnchor.MiddleCenter, center, new Vector2(0f, 140f), new Vector2(700f, 60f));
            title.color = UiFactory.AccentColor;
            _reason = UiFactory.CreateText("Reason", panel, string.Empty, 28, TextAnchor.MiddleCenter, center, new Vector2(0f, 55f), new Vector2(700f, 80f));
            _progress = UiFactory.CreateText("Progress", panel, string.Empty, 24, TextAnchor.MiddleCenter, center, new Vector2(0f, -25f), new Vector2(700f, 50f));
            _cancel = UiFactory.CreateButton("Cancel", panel, "재접속 취소", new Vector2(0f, -125f), new Vector2(260f, 60f), onCancel).gameObject;
            _retryButton = UiFactory.CreateButton("Retry", panel, "다시 접속", new Vector2(-140f, -125f), new Vector2(240f, 60f), onRetry);
            _retry = _retryButton.gameObject;
            _toTitle = UiFactory.CreateButton("ToTitle", panel, "타이틀로", new Vector2(140f, -125f), new Vector2(240f, 60f), onToTitle).gameObject;
            SetReconnecting(false);
        }

        // 기능: 화면을 보이거나 숨긴다.
        // 입력: visible - 보일지.
        // 출력: 반환값 없음. 바뀔 때만 루트 GameObject가 켜지거나 꺼진다.
        public void SetVisible(bool visible)
        {
            if (visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // 기능: 끊긴 이유 글자를 바꾼다(같은 참조면 다시 쓰지 않는다).
        // 입력: reason - UiText.Disconnect가 돌려준 상수 문자열.
        // 출력: 반환값 없음. 이유 Text가 바뀐다.
        // reason: one of UiText's constants (UiText.Disconnect).
        public void SetReason(string reason)
        {
            if (ReferenceEquals(reason, _shownReason)) return;
            _shownReason = reason;
            _reason.text = reason;
        }

        // 기능: 다시 접속 버튼을 누를 수 있게 하거나 막는다(이전 연결이 닫히는 중이면 막는다).
        // 입력: enabled - 누를 수 있는지.
        // 출력: 반환값 없음. 바뀔 때만 버튼의 interactable이 바뀐다.
        // Off while the previous connection is still closing (Retry would be ignored), like the title's Connect.
        public void SetRetryEnabled(bool enabled)
        {
            if (enabled == _retryEnabled) return;
            _retryEnabled = enabled;
            _retryButton.interactable = enabled;
        }

        // 기능: 자동 재접속 중 배치(진행 줄 + 취소)와 끝난 배치(다시 접속 + 타이틀로)를 바꾼다.
        // 입력: reconnecting - 자동 재접속이 진행 중인지.
        // 출력: 반환값 없음. 바뀔 때만 버튼이 바뀌고, 재접속이 끝나면 진행 줄이 비워진다.
        public void SetReconnecting(bool reconnecting)
        {
            if (reconnecting == _reconnecting) return;
            _reconnecting = reconnecting;
            _cancel.SetActive(reconnecting);
            _retry.SetActive(!reconnecting);
            _toTitle.SetActive(!reconnecting);
            if (!reconnecting)
            {
                _progress.text = string.Empty;
                _shownAttempt = -1;
                _shownSeconds = -1;
            }
        }

        // 기능: 재접속 진행 줄("재접속 중 (n/m) - k초 뒤 다시 시도")을 갱신한다.
        // 입력: attempt - 현재 시도 번호, maxAttempts - 최대 시도 수, secondsLeft - 다음 시도까지 초(0 = 지금 연결 중).
        // 출력: 반환값 없음. 재접속 중이고 시도 번호나 초가 바뀐 때만 진행 Text를 다시 만든다.
        // secondsLeft 0 = the attempt is connecting now.
        public void SetProgress(int attempt, int maxAttempts, int secondsLeft)
        {
            if (!_reconnecting || (attempt == _shownAttempt && secondsLeft == _shownSeconds)) return;
            _shownAttempt = attempt;
            _shownSeconds = secondsLeft;
            _progress.text = UiText.Reconnecting(attempt, maxAttempts, secondsLeft);
        }
    }
}
