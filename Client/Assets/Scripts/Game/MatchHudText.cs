using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Game
{
    // The strings of the match HUD (D14), built only when a shown value changes, so a HUD that shows the same
    // thing every frame allocates nothing. Pure (no UnityEngine): MatchHud puts a string on screen when its Set*
    // call returns true. Phase 11 D2, D11: Korean (UiFont has Hangul); the match result moved to the result screen.
    public sealed class MatchHudText
    {
        private MatchFlowState _state = (MatchFlowState)255;   // forces the first SetStatus to build
        private int _statusA = -1;
        private int _statusB = -1;
        private ZoneHint _zoneHint;
        private int _zoneSeconds = -1;
        private ushort _spectating;
        private string _spectatingName;

        // How many strings were built so far (tests check that unchanged values build nothing).
        public int Rebuilds { get; private set; }
        public string Status { get; private set; } = string.Empty;
        public string Zone { get; private set; } = string.Empty;
        public string Spectating { get; private set; } = string.Empty;

        // 기능: 경기 상태별로 표시할 두 숫자를 고르고, 상태나 숫자가 바뀌었을 때만 상단 상태 문자열을 다시 만든다.
        // 입력: state - 경기 진행 상태, secondsLeft - 시작까지 남은 초(Starting에서 사용), alive - 생존자 수, participants - 참가자 수, minPlayers - 시작 최소 인원.
        // 출력: 문자열을 다시 만들었으면 true(Rebuilds 증가), 값이 같으면 false.
        // The top line: "플레이어를 기다리는 중 1/2", "시작까지 7초", "생존 3/5", "경기 종료". secondsLeft is used while
        // Starting; alive / participants / minPlayers come from MatchState.
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
                case MatchFlowState.WaitingForPlayers: Status = "플레이어를 기다리는 중 " + a + "/" + b; break;
                case MatchFlowState.Starting: Status = "시작까지 " + a + "초"; break;
                case MatchFlowState.Playing:
                case MatchFlowState.FinalPhase: Status = "생존 " + a + "/" + b; break;
                case MatchFlowState.Finished: Status = "경기 종료"; break;
                default: Status = string.Empty; break;
            }
            Rebuilds++;
            return true;
        }

        // 기능: 자기장 안내나 남은 초가 바뀌었을 때만 자기장 문자열을 다시 만든다. ShrinksIn이 아니면 초는 0으로 본다.
        // 입력: hint - 자기장 단계 안내, seconds - 축소까지 남은 초.
        // 출력: 문자열을 다시 만들었으면 true(Rebuilds 증가), 값이 같으면 false.
        // "자기장 축소까지 12초" / "자기장 축소 중" / nothing.
        public bool SetZone(ZoneHint hint, int seconds)
        {
            if (hint != ZoneHint.ShrinksIn) seconds = 0;
            if (hint == _zoneHint && seconds == _zoneSeconds) return false;
            _zoneHint = hint;
            _zoneSeconds = seconds;
            switch (hint)
            {
                case ZoneHint.ShrinksIn: Zone = "자기장 축소까지 " + seconds + "초"; break;
                case ZoneHint.Closing: Zone = "자기장 축소 중"; break;
                default: Zone = string.Empty; break;
            }
            Rebuilds++;
            return true;
        }

        // 기능: 관전 대상 ID나 이름 참조가 바뀌었을 때만 관전 문자열을 다시 만든다.
        // 입력: entityId - 관전 대상 Entity ID(0이면 빈 문자열), name - PlayerSpawned로 받은 이름(모르면 null).
        // 출력: 문자열을 다시 만들었으면 true(Rebuilds 증가), 값이 같으면 false.
        // Phase 11 D9: "관전 중: alice", or "관전 중: 플레이어 3" when that player's name is not known. 0 hides it. The name
        // is PlayerSpawned's string, compared by reference.
        public bool SetSpectating(ushort entityId, string name)
        {
            if (entityId == _spectating && ReferenceEquals(name, _spectatingName)) return false;
            _spectating = entityId;
            _spectatingName = name;
            Spectating = entityId == 0 ? string.Empty : "관전 중: " + UiText.NameOr(name, entityId);
            Rebuilds++;
            return true;
        }
    }
}
