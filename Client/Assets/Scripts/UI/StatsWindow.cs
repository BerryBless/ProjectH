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

        // 기능: 내 전적 창(제목, 상태, 합계, 최근 경기 줄, 닫기 버튼)을 만든다.
        // 입력: canvas - 부모 Canvas, onClose - 닫기 버튼 Handler.
        // 출력: 내용 Text가 빈 상태로 초기화된 StatsWindow 객체.
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

        // 기능: 창 표시 여부를 바꾼다.
        // 입력: visible - 보일지 여부.
        // 출력: 반환값 없음. 값이 바뀌었을 때만 활성 상태가 바뀌고, 열릴 때는 다음 Tick에서 내용을 다시 그리게 된다.
        public void SetVisible(bool visible)
        {
            if (visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
            if (visible) _shownAny = false;   // redraw on the first Tick after opening
        }

        // 기능: 요청 대기 상태와 응답에 따라 상태·합계·최근 경기 Text를 갱신한다.
        // 입력: now - 현재 시각(unscaled 초), sentAt - 요청 시각, answeredAt - 응답 시각, latest - 최신 StatsResponse, utcOffset - 경기 시각 표시용 시간대.
        // 출력: 반환값 없음. 대기 상태나 응답 참조가 바뀌었을 때만 Text가 다시 쓰인다.
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

        // 기능: 세 내용 Text를 한 번에 바꾼다.
        // 입력: status - 상태 줄, summary - 합계 줄, rows - 최근 경기 줄.
        // 출력: 반환값 없음. 상태·합계·최근 경기 Text가 바뀐다.
        private void Set(string status, string summary, string rows)
        {
            _status.text = status;
            _summary.text = summary;
            _rows.text = rows;
        }
    }
}
