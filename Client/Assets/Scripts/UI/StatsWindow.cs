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

        public void SetVisible(bool visible)
        {
            if (visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
            if (visible) _shownAny = false;   // redraw on the first Tick after opening
        }

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

        private void Set(string status, string summary, string rows)
        {
            _status.text = status;
            _summary.text = summary;
            _rows.text = rows;
        }
    }
}
