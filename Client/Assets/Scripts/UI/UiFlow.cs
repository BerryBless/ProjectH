using ProjectH.Shared.Protocol;

namespace ProjectH.Client.UI
{
    // Phase 11 D3: the screens. Stats is not a screen: it opens over Menu or Result (UiFlow.StatsOpen).
    public enum UiScreen : byte
    {
        Title,
        Connecting,
        InGame,
        Menu,
        Disconnected,
        Result,
    }

    // What the flow needs to know about the connection: NetClient's Disconnected, Connecting/Connected and Joined.
    public enum UiConnection : byte
    {
        Offline,
        Connecting,
        Joined,
    }

    // Phase 11 D3: which screen shows, whether the cursor may be locked and whether game input is blocked. Pure (no
    // UnityEngine) so every transition is tested by the server test project (source link, like ZoneMath). UiRoot calls
    // Update once per frame with what GameClient reports, and the command methods from buttons and Esc; the screens only
    // draw the result. Version changes whenever Screen, StatsOpen or Reconnecting changes, so UiRoot redraws only then.
    //
    //   Title --connect--> Connecting --joined--> InGame <--Esc/continue--> Menu
    //   Connecting --failed (refused, full, unreachable)--> Disconnected --retry--> Connecting
    //   InGame/Menu/Result --connection lost--> Disconnected --automatic reconnect joined--> InGame
    //   Connecting/Disconnected --joined together with a new MatchResult (a resume during Finished)--> Result
    //   InGame/Menu --new MatchResult--> Result --continue, Esc, or the next round's WaitingForPlayers/Starting--> InGame
    //   Menu "disconnect", Disconnected "to title", Connecting "cancel" --> Title
    public sealed class UiFlow
    {
        private int _seenResults;

        public UiScreen Screen { get; private set; } = UiScreen.Title;
        public bool StatsOpen { get; private set; }
        // On the Disconnected screen: an automatic reconnect is running (show its progress and Cancel, not Retry).
        public bool Reconnecting { get; private set; }
        public int Version { get; private set; }

        // 기능: 클릭으로 커서를 잠가도 되는지 알려준다.
        // 입력: 없음.
        // 출력: InGame 화면이면 true, 다른 화면이면 false.
        // D5: a click may lock the cursor only in the game with no screen up.
        public bool AllowCursorLock => Screen == UiScreen.InGame;
        // 기능: 게임 입력을 막아야 하는지 알려준다.
        // 입력: 없음.
        // 출력: InGame이 아닌 화면이면 true, InGame이면 false.
        // D5: movement, fire, aim and look are zero while any screen is up (GameClient also blocks them while the cursor is
        // free). Inputs keep going to the server, empty, so the Phase 10 input timeout never closes a player in a menu.
        public bool BlocksGameInput => Screen != UiScreen.InGame;

        // 기능: 접속 요청 시 접속 중 화면으로 바꾼다.
        // 입력: 없음.
        // 출력: 반환값 없음. Title 또는 Disconnected 화면이면 Connecting으로 바뀐다.
        // The player asked to connect from the title or the disconnected screen (UiRoot already called Connect).
        public void ConnectRequested()
        {
            if (Screen == UiScreen.Title || Screen == UiScreen.Disconnected) Set(UiScreen.Connecting);
        }

        // 기능: 플레이어가 스스로 나갔을 때 타이틀로 돌아간다.
        // 입력: 없음.
        // 출력: 반환값 없음. 화면이 Title로 바뀌고 전적 창이 닫힌다.
        // The player left on purpose (UiRoot already disconnected): Menu "disconnect", Disconnected "to title",
        // Connecting "cancel". Nothing that happens to the connection afterwards moves the title.
        public void LeaveRequested()
        {
            Set(UiScreen.Title);
        }

        // 기능: Esc 입력을 한 단계 뒤로 가기로 처리한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 전적 창이 열려 있으면 닫히고, 아니면 InGame은 Menu로, Menu·Result는 InGame으로 바뀐다.
        // Esc goes back one level: the stats window, then the menu or the result. In the game it opens the menu.
        public void EscapePressed()
        {
            if (StatsOpen)
            {
                CloseStats();
                return;
            }
            switch (Screen)
            {
                case UiScreen.InGame: Set(UiScreen.Menu); break;
                case UiScreen.Menu:
                case UiScreen.Result: Set(UiScreen.InGame); break;
            }
        }

        // 기능: 계속하기·계속 관전 버튼을 처리한다.
        // 입력: 없음.
        // 출력: 반환값 없음. Menu 또는 Result 화면이면 InGame으로 바뀐다.
        // Menu "continue", Result "keep spectating".
        public void ContinuePressed()
        {
            if (Screen == UiScreen.Menu || Screen == UiScreen.Result) Set(UiScreen.InGame);
        }

        // 기능: 전적 창을 연다.
        // 입력: 없음.
        // 출력: 반환값 없음. Menu·Result 화면에서 닫혀 있었으면 StatsOpen이 true가 되고 Version이 증가한다.
        public void OpenStats()
        {
            if (StatsOpen || (Screen != UiScreen.Menu && Screen != UiScreen.Result)) return;
            StatsOpen = true;
            Version++;
        }

        // 기능: 전적 창을 닫는다.
        // 입력: 없음.
        // 출력: 반환값 없음. 열려 있었으면 StatsOpen이 false가 되고 Version이 증가한다.
        public void CloseStats()
        {
            if (!StatsOpen) return;
            StatsOpen = false;
            Version++;
        }

        // 기능: 연결 상태와 경기 결과를 보고 화면 전환을 결정한다.
        // 입력: connection - 연결 단계, reconnecting - 자동 재접속 진행 여부, results - 지금까지 받은 MatchResult 수, hasMatch - MatchState 수신 여부, matchState - 최신 경기 흐름 상태.
        // 출력: 반환값 없음. 필요하면 Screen·Reconnecting이 바뀌고 Version이 증가한다.
        // Once per frame. reconnecting: GameClient's automatic reconnect is running. results: how many MatchResults
        // arrived so far (a new one opens the result screen once). hasMatch / matchState: the newest MatchState (a dev
        // server sends none); the result screen closes by itself when the next round begins.
        public void Update(UiConnection connection, bool reconnecting, int results, bool hasMatch, MatchFlowState matchState)
        {
            if (reconnecting != Reconnecting)
            {
                Reconnecting = reconnecting;
                Version++;
            }
            // Only a count above the last one is a new result: GameClient's count may restart from 0 on a new connection,
            // and that must not open the screen.
            bool newResult = results > _seenResults;
            _seenResults = results;

            // A resume during Finished brings JoinResponse and MatchResult in the same tick: the join goes straight to
            // the result, or the screen would be lost.
            UiScreen joined = newResult ? UiScreen.Result : UiScreen.InGame;
            switch (Screen)
            {
                case UiScreen.Title:
                    break;
                case UiScreen.Connecting:
                    if (connection == UiConnection.Joined) Set(joined);
                    else if (connection == UiConnection.Offline) Set(UiScreen.Disconnected);
                    break;
                case UiScreen.Disconnected:
                    if (connection == UiConnection.Joined) Set(joined);
                    break;
                default:   // InGame, Menu, Result
                    if (connection != UiConnection.Joined) Set(UiScreen.Disconnected);
                    else if (newResult && Screen != UiScreen.Result) Set(UiScreen.Result);
                    else if (Screen == UiScreen.Result && hasMatch &&
                             (matchState == MatchFlowState.WaitingForPlayers || matchState == MatchFlowState.Starting))
                        Set(UiScreen.InGame);
                    break;
            }
        }

        // 기능: 화면을 바꾸고 전적 창을 닫는다.
        // 입력: screen - 보일 화면.
        // 출력: 반환값 없음. 같은 화면이고 전적 창이 닫혀 있으면 변화 없음, 아니면 Screen이 바뀌고 StatsOpen이 false가 되며 Version이 증가한다.
        private void Set(UiScreen screen)
        {
            if (screen == Screen && !StatsOpen) return;
            Screen = screen;
            StatsOpen = false;
            Version++;
        }
    }

    public enum StatsWaitState : byte
    {
        Waiting,    // "loading"
        Answered,   // show the newest StatsResponse
        NoAnswer,   // nothing within AnswerSeconds (or nothing could be sent)
    }

    // Phase 11 D8: the stats window's wait for its answer. Times are the caller's clock in seconds; negative = never.
    public static class StatsWait
    {
        // D8: the window gives up after this long.
        public const float AnswerSeconds = 5f;
        // The server answers one request per 2 s per connection and drops the rest without an answer. A window opened
        // again sooner reuses the previous request (its answer is here or on the way) instead of sending one that would
        // be dropped; the extra half second covers network jitter between the two clocks.
        public const float ResendSeconds = 2.5f;

        // 기능: 전적 요청을 새로 보내도 되는지 판단한다.
        // 입력: now - 현재 시각(초), lastSentAt - 마지막 요청 시각(음수면 보낸 적 없음).
        // 출력: 보낸 적이 없거나 ResendSeconds 이상 지났으면 true, 아니면 false.
        public static bool MaySend(float now, float lastSentAt) => lastSentAt < 0f || now - lastSentAt >= ResendSeconds;

        // 기능: 전적 요청의 대기 상태를 계산한다.
        // 입력: now - 현재 시각(초), sentAt - 요청 시각(음수면 보내지 못함), answeredAt - 응답 시각.
        // 출력: 보내지 못했거나 AnswerSeconds 안에 응답이 없으면 NoAnswer, 요청 이후 응답이 왔으면 Answered, 아직 기다리는 중이면 Waiting.
        public static StatsWaitState Of(float now, float sentAt, float answeredAt)
        {
            if (sentAt < 0f) return StatsWaitState.NoAnswer;
            if (answeredAt >= sentAt) return StatsWaitState.Answered;
            return now - sentAt >= AnswerSeconds ? StatsWaitState.NoAnswer : StatsWaitState.Waiting;
        }
    }
}
