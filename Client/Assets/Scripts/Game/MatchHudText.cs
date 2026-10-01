using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Game
{
    // The strings of the match HUD (D14), built only when a shown value changes, so a HUD that shows the same
    // thing every frame allocates nothing. Pure (no UnityEngine): MatchHud puts a string on screen when its Set*
    // call returns true. English for the same reason as the other HUDs (the built-in font's Hangul is untested).
    public sealed class MatchHudText
    {
        private MatchFlowState _state = (MatchFlowState)255;   // forces the first SetStatus to build
        private int _statusA = -1;
        private int _statusB = -1;
        private ZoneHint _zoneHint;
        private int _zoneSeconds = -1;
        private int _resultPlacement = -1;
        private int _resultKills = -1;
        private bool _resultWon;
        private ushort _spectating;

        // How many strings were built so far (tests check that unchanged values build nothing).
        public int Rebuilds { get; private set; }
        public string Status { get; private set; } = string.Empty;
        public string Zone { get; private set; } = string.Empty;
        public string Result { get; private set; } = string.Empty;
        public string Spectating { get; private set; } = string.Empty;

        // The top line: "Waiting for players 1/2", "Starting in 7", "Alive 3/5", "Match over". secondsLeft is
        // used while Starting; alive / participants / minPlayers come from MatchState.
        public bool SetStatus(MatchFlowState state, int secondsLeft, int alive, int participants, int minPlayers)
        {
            int a;
            int b;
            switch (state)
            {
                case MatchFlowState.WaitingForPlayers: a = participants; b = minPlayers; break;
                case MatchFlowState.Starting: a = secondsLeft; b = 0; break;
                case MatchFlowState.Playing:
                case MatchFlowState.FinalPhase: a = alive; b = participants; break;
                default: a = 0; b = 0; break;
            }
            if (state == _state && a == _statusA && b == _statusB) return false;
            _state = state;
            _statusA = a;
            _statusB = b;
            switch (state)
            {
                case MatchFlowState.WaitingForPlayers: Status = "Waiting for players " + a + "/" + b; break;
                case MatchFlowState.Starting: Status = "Starting in " + a; break;
                case MatchFlowState.Playing:
                case MatchFlowState.FinalPhase: Status = "Alive " + a + "/" + b; break;
                case MatchFlowState.Finished: Status = "Match over"; break;
                default: Status = string.Empty; break;
            }
            Rebuilds++;
            return true;
        }

        // "Zone shrinking in 12s" / "Zone closing" / nothing.
        public bool SetZone(ZoneHint hint, int seconds)
        {
            if (hint != ZoneHint.ShrinksIn) seconds = 0;
            if (hint == _zoneHint && seconds == _zoneSeconds) return false;
            _zoneHint = hint;
            _zoneSeconds = seconds;
            switch (hint)
            {
                case ZoneHint.ShrinksIn: Zone = "Zone shrinking in " + seconds + "s"; break;
                case ZoneHint.Closing: Zone = "Zone closing"; break;
                default: Zone = string.Empty; break;
            }
            Rebuilds++;
            return true;
        }

        // The result in the middle of the screen: "#1 VICTORY" for the winner, "ELIMINATED #3 — 2 kills"
        // otherwise. placement 0 hides it.
        public bool SetResult(bool won, int placement, int kills)
        {
            if (won == _resultWon && placement == _resultPlacement && kills == _resultKills) return false;
            _resultWon = won;
            _resultPlacement = placement;
            _resultKills = kills;
            if (placement <= 0) Result = string.Empty;
            else if (won) Result = "#" + placement + " VICTORY";
            else Result = "ELIMINATED #" + placement + " — " + kills + (kills == 1 ? " kill" : " kills");
            Rebuilds++;
            return true;
        }

        // "Spectating Player 3" (no names on the wire: the entity id). 0 hides it.
        public bool SetSpectating(ushort entityId)
        {
            if (entityId == _spectating) return false;
            _spectating = entityId;
            Spectating = entityId == 0 ? string.Empty : "Spectating Player " + entityId;
            Rebuilds++;
            return true;
        }
    }
}
