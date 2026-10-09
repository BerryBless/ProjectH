using System;
using ProjectH.Shared.Protocol;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ProjectH.Client.UI
{
    // Phase 11 D8: "내 전적" over the menu or the result. UiRoot asks GameClient for a request when it opens; this window
    // shows "불러오는 중..." until the answer, "응답 없음" after StatsWait.AnswerSeconds, then the status, the totals and up
    // to 10 recent matches. Its texts are rebuilt only when the wait state or the answer changes.
    public sealed class StatsWindow
    {
        private readonly GameObject _root;
        private readonly Text _status;
        private readonly Text _summary;
        private readonly Text _rows;
        private bool _visible = true;
        private StatsWaitState _shownState;
        private StatsResponse _shownResponse;
        private bool _shownAny;

        // 기능: 전적 창(제목·상태·합계·최근 경기 줄과 닫기 버튼)을 Canvas 아래에 만든다.
        // 입력: canvas - 부모 Canvas, onClose - 닫기 버튼 처리.
        // 출력: 보이는 상태로 시작하고 글자가 비어 있는 StatsWindow.
        public StatsWindow(Transform canvas, UnityAction onClose)
        {
            _root = UiFactory.CreateScreen("Stats", canvas, dim: true).gameObject;
            RectTransform panel = UiFactory.CreatePanel("Panel", _root.transform, new Vector2(1000f, 700f));
            Vector2 center = new Vector2(0.5f, 0.5f);
            Text title = UiFactory.CreateText("Title", panel, "내 전적", 40, TextAnchor.MiddleCenter, center, new Vector2(0f, 300f), new Vector2(940f, 60f));
            title.color = UiFactory.AccentColor;
            _status = UiFactory.CreateText("Status", panel, string.Empty, 26, TextAnchor.MiddleCenter, center, new Vector2(0f, 235f), new Vector2(940f, 50f));
            _summary = UiFactory.CreateText("Summary", panel, string.Empty, 26, TextAnchor.UpperCenter, center, new Vector2(0f, 175f), new Vector2(940f, 80f));
            _rows = UiFactory.CreateText("Rows", panel, string.Empty, 22, TextAnchor.UpperCenter, center, new Vector2(0f, 60f), new Vector2(940f, 300f));
            _rows.verticalOverflow = VerticalWrapMode.Overflow;
            // Top-aligned blocks: with the pivot on the top edge, the anchored position is where the first line starts.
            ((RectTransform)_rows.transform).pivot = new Vector2(0.5f, 1f);
            ((RectTransform)_summary.transform).pivot = new Vector2(0.5f, 1f);
            UiFactory.CreateButton("Close", panel, "닫기", new Vector2(0f, -295f), new Vector2(220f, 60f), onClose);
        }

        // 기능: 전적 창을 보이거나 숨긴다. 열릴 때는 첫 Tick에 다시 그리게 표시한다.
        // 입력: visible - 보일지.
        // 출력: 반환값 없음. 바뀔 때만 루트 GameObject가 켜지거나 꺼진다.
        public void SetVisible(bool visible)
        {
            if (visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
            if (visible) _shownAny = false;   // redraw on the first Tick after opening
        }

        // 기능: 열려 있는 동안 매 프레임 대기 상태(불러오는 중·응답 없음·답)를 정하고, 상태나 답이 바뀐 때만 글자를 다시 쓴다.
        // 입력: now - 현재 시각(unscaled 초), sentAt·answeredAt - GameClient의 요청·응답 시각(음수 = 없음), latest - 최신 응답,
        //   utcOffset - 날짜 표시용 시간대 차이.
        // 출력: 반환값 없음. 상태·합계·줄 Text가 바뀔 수 있다.
        // Every frame while open. sentAt / answeredAt: GameClient's request and answer times (unscaled seconds).
        public void Tick(float now, float sentAt, float answeredAt, StatsResponse latest, TimeSpan utcOffset)
        {
            StatsWaitState state = StatsWait.Of(now, sentAt, answeredAt);
            StatsResponse response = state == StatsWaitState.Answered ? latest : null;
            if (_shownAny && state == _shownState && ReferenceEquals(response, _shownResponse)) return;
            _shownAny = true;
            _shownState = state;
            _shownResponse = response;

            switch (state)
            {
                case StatsWaitState.Waiting:
                    Set(UiText.StatsLoading, string.Empty, string.Empty);
                    break;
                case StatsWaitState.NoAnswer:
                    Set(UiText.StatsNoAnswer, string.Empty, string.Empty);
                    break;
                default:
                    if (response == null) Set(UiText.StatsNoAnswer, string.Empty, string.Empty);
                    else if (response.Status != StatsStatus.Ok) Set(UiText.StatsStatusText(response.Status), string.Empty, string.Empty);
                    else Set(string.Empty, UiText.StatsSummaryText(response.Summary), UiText.StatsRowsText(response.Rows, utcOffset));
                    break;
            }
        }

        // 기능: 세 Text(상태·합계·줄)를 한 번에 쓴다.
        // 입력: status - 상태 문구, summary - 합계 문구, rows - 최근 경기 줄들.
        // 출력: 반환값 없음. 세 Text가 바뀐다.
        private void Set(string status, string summary, string rows)
        {
            _status.text = status;
            _summary.text = summary;
            _rows.text = rows;
        }
    }
}
