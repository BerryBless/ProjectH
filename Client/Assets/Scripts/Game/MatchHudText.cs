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

        // 기능: 위쪽 상태 줄을 보이는 값이 바뀔 때만 만든다("플레이어를 기다리는 중 1/2", "시작까지 7초", "생존 3/5", "경기 종료").
        // 입력: state - 경기 상태, secondsLeft - 남은 초(Starting에서만 쓴다), alive - 생존자 수, participants - 참가자 수, minPlayers - 시작
        //   최소 인원(MatchState 값; 상태에 따라 쓰는 값만 비교한다).
        // 출력: 문자열을 새로 만들었으면 true, 값이 같아 그대로면 false.
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

        // 기능: 자기장 줄을 바뀔 때만 만든다("자기장 축소까지 12초", "자기장 축소 중", 아니면 빈 문자열).
        // 입력: hint - 자기장 안내 종류, seconds - 축소까지 남은 초(ShrinksIn이 아니면 0으로 본다).
        // 출력: 문자열을 새로 만들었으면 true, 값이 같아 그대로면 false.
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

        // 기능: 관전 줄을 대상이나 이름(참조 비교)이 바뀔 때만 만든다(Phase 11 D9: "관전 중: alice", 이름을 모르면 "관전 중: 플레이어 3").
        // 입력: entityId - 관전 대상(0이면 빈 문자열), name - PlayerSpawned가 준 이름(모르면 null).
        // 출력: 문자열을 새로 만들었으면 true, 값이 같아 그대로면 false.
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
