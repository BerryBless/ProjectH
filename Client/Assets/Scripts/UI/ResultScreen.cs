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

        public void SetVisible(bool visible)
        {
            if (visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // Once when the result screen opens. winnerName null = no winner; killerName is used when died && !byZone.
        public void Show(bool won, int placement, int participants, int kills, string winnerName, bool died, bool byZone, string killerName)
        {
            _title.text = UiText.ResultTitle(won);
            _placement.text = UiText.Placement(placement, participants);
            _kills.text = UiText.Kills(kills);
            _winner.text = UiText.Winner(winnerName);
            _killedBy.text = UiText.KilledBy(died && !won, byZone, killerName);
            _shownSeconds = -1;
        }

        public void SetSecondsLeft(int seconds)
        {
            if (seconds == _shownSeconds) return;
            _shownSeconds = seconds;
            _nextRound.text = UiText.NextRound(seconds);
        }
    }
}
