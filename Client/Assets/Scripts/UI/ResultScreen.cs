using ProjectH.Shared.Protocol;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ProjectH.Client.UI
{
    // Phase 11 D7: the match result (MatchResult plus the names and our own PlayerDied). Show builds its strings once per
    // result; the countdown to the next round is rebuilt only when the whole second changes. Damage and survival time are
    // not in the result packet: "내 전적" opens the statistics from the database.
    public sealed class ResultScreen
    {
        private readonly GameObject _root;
        private readonly Text _title;
        private readonly Text _placement;
        private readonly Text _kills;
        private readonly Text _winner;
        private readonly Text _killedBy;
        private readonly Text _nextRound;
        private bool _visible = true;
        private int _shownSeconds = -1;

        // 기능: 경기 결과 화면(제목, 순위, 처치, 승자, 탈락 원인, 다음 판 카운트다운, 계속 관전·내 전적 버튼)을 만든다.
        // 입력: canvas - 부모 Canvas, onContinue - 계속 관전 Handler, onStats - 내 전적 Handler.
        // 출력: 결과 Text가 빈 상태로 초기화된 ResultScreen 객체.
        public ResultScreen(Transform canvas, UnityAction onContinue, UnityAction onStats)
        {
            _root = UiFactory.CreateScreen("Result", canvas, dim: true).gameObject;
            RectTransform panel = UiFactory.CreatePanel("Panel", _root.transform, new Vector2(760f, 560f));
            Vector2 center = new Vector2(0.5f, 0.5f);
            _title = UiFactory.CreateText("Title", panel, string.Empty, 56, TextAnchor.MiddleCenter, center, new Vector2(0f, 205f), new Vector2(700f, 80f));
            _title.color = UiFactory.AccentColor;
            _placement = UiFactory.CreateText("Placement", panel, string.Empty, 30, TextAnchor.MiddleCenter, center, new Vector2(0f, 125f), new Vector2(700f, 50f));
            _kills = UiFactory.CreateText("Kills", panel, string.Empty, 30, TextAnchor.MiddleCenter, center, new Vector2(0f, 75f), new Vector2(700f, 50f));
            _winner = UiFactory.CreateText("Winner", panel, string.Empty, 26, TextAnchor.MiddleCenter, center, new Vector2(0f, 20f), new Vector2(700f, 50f));
            _killedBy = UiFactory.CreateText("KilledBy", panel, string.Empty, 26, TextAnchor.MiddleCenter, center, new Vector2(0f, -30f), new Vector2(700f, 50f));
            _nextRound = UiFactory.CreateText("NextRound", panel, string.Empty, 24, TextAnchor.MiddleCenter, center, new Vector2(0f, -95f), new Vector2(700f, 50f));
            UiFactory.CreateButton("Continue", panel, "계속 관전", new Vector2(-140f, -200f), new Vector2(240f, 60f), onContinue);
            UiFactory.CreateButton("Stats", panel, "내 전적", new Vector2(140f, -200f), new Vector2(240f, 60f), onStats);
        }

        // 기능: 화면 표시 여부를 바꾼다.
        // 입력: visible - 보일지 여부.
        // 출력: 반환값 없음. 값이 바뀌었을 때만 화면 GameObject의 활성 상태가 바뀐다.
        public void SetVisible(bool visible)
        {
            if (visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // 기능: 경기 결과 문자열을 한 번 만들어 화면에 채운다.
        // 입력: won - 승리 여부, placement - 순위, participants - 참가자 수, kills - 처치 수, winnerName - 승자 이름(null이면 승자 없음), died - 이번 판 사망 여부, noKiller - 처치한 플레이어 없이 죽었는지 여부, cause - 처치자 없는 사망의 원인, killerName - 처치한 플레이어 이름.
        // 출력: 반환값 없음. 결과 Text들이 갱신되고 카운트다운 표시가 다음 SetSecondsLeft에서 다시 쓰이도록 초기화된다.
        // Once when the result screen opens. winnerName null = no winner; killerName is used when died && !noKiller, and the
        // cause (the zone or a fall, Phase 12 D10) when noKiller.
        public void Show(bool won, int placement, int participants, int kills, string winnerName, bool died, bool noKiller,
            DeathCause cause, string killerName)
        {
            _title.text = UiText.ResultTitle(won);
            _placement.text = UiText.Placement(placement, participants);
            _kills.text = UiText.Kills(kills);
            _winner.text = UiText.Winner(winnerName);
            _killedBy.text = UiText.KilledBy(died && !won, noKiller, cause, killerName);
            _shownSeconds = -1;
        }

        // 기능: 다음 판까지 남은 초를 표시한다.
        // 입력: seconds - 남은 초.
        // 출력: 반환값 없음. 초가 바뀌었을 때만 카운트다운 Text가 갱신된다.
        public void SetSecondsLeft(int seconds)
        {
            if (seconds == _shownSeconds) return;
            _shownSeconds = seconds;
            _nextRound.text = UiText.NextRound(seconds);
        }
    }
}
