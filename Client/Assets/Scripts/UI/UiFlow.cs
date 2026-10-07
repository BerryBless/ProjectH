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
    // draw the result. Version changes whenever Screen, StatsOpen, MapOpen or Reconnecting changes, so UiRoot redraws only then.
    // Phase 15 D4: the full map is not a screen either: it opens over InGame only (MapOpen), frees the cursor and blocks game
    // input like a screen, closes with M or Esc (Esc closes it before anything else in the game), and any screen change
    // closes it (a disconnect, a new result).
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
        // Phase 15 D4: the full map is open (only ever over InGame).
        public bool MapOpen { get; private set; }
        // On the Disconnected screen: an automatic reconnect is running (show its progress and Cancel, not Retry).
        public bool Reconnecting { get; private set; }
        public int Version { get; private set; }

        // D5: a click may lock the cursor only in the game with no screen up. Phase 15 D4: nor with the full map open.
        public bool AllowCursorLock => Screen == UiScreen.InGame && !MapOpen;
        // D5: movement, fire, aim and look are zero while any screen is up (GameClient also blocks them while the cursor is
        // free). Inputs keep going to the server, empty, so the Phase 10 input timeout never closes a player in a menu.
        // Phase 15 D4: the full map blocks them the same way (the player stops while it is open).
        public bool BlocksGameInput => Screen != UiScreen.InGame || MapOpen;

        // The player asked to connect from the title or the disconnected screen (UiRoot already called Connect).
        public void ConnectRequested()
        {
            if (Screen == UiScreen.Title || Screen == UiScreen.Disconnected) Set(UiScreen.Connecting);
        }

        // The player left on purpose (UiRoot already disconnected): Menu "disconnect", Disconnected "to title",
        // Connecting "cancel". Nothing that happens to the connection afterwards moves the title.
        public void LeaveRequested()
        {
            Set(UiScreen.Title);
        }

        // 기능: Esc 한 번을 처리한다: 한 단계 뒤로(통계 창, 그다음 전체 지도(Phase 15), 그다음 메뉴나 결과). 게임 화면에서는 메뉴를 연다.
        // 입력: 없음.
        // 출력: 반환값 없음. 상태가 바뀌면 Version이 오른다.
        public void EscapePressed()
        {
            if (StatsOpen)
            {
                CloseStats();
                return;
            }
            if (MapOpen)
            {
                CloseMap();
                return;
            }
            switch (Screen)
            {
                case UiScreen.InGame: Set(UiScreen.Menu); break;
                case UiScreen.Menu:
                case UiScreen.Result: Set(UiScreen.InGame); break;
            }
        }

        // Menu "continue", Result "keep spectating".
        public void ContinuePressed()
        {
            if (Screen == UiScreen.Menu || Screen == UiScreen.Result) Set(UiScreen.InGame);
        }

        public void OpenStats()
        {
            if (StatsOpen || (Screen != UiScreen.Menu && Screen != UiScreen.Result)) return;
            StatsOpen = true;
            Version++;
        }

        public void CloseStats()
        {
            if (!StatsOpen) return;
            StatsOpen = false;
            Version++;
        }

        // 기능: M 한 번을 처리한다(Phase 15 D4): 게임 화면이면 전체 지도를 열거나 닫는다.
        // 입력: 없음.
        // 출력: 반환값 없음. 게임 화면이 아니면(메뉴·결과·접속 화면) 아무것도 하지 않는다.
        public void ToggleMap()
        {
            if (MapOpen) CloseMap();
            else OpenMap();
        }

        // 기능: 전체 지도를 연다(게임 화면에서만, Phase 15 D4).
        // 입력: 없음.
        // 출력: 반환값 없음. 열리면 Version이 오른다.
        public void OpenMap()
        {
            if (MapOpen || Screen != UiScreen.InGame) return;
            MapOpen = true;
            Version++;
        }

        // 기능: 전체 지도를 닫는다(Phase 15 D4).
        // 입력: 없음.
        // 출력: 반환값 없음. 닫히면 Version이 오른다.
        public void CloseMap()
        {
            if (!MapOpen) return;
            MapOpen = false;
            Version++;
        }

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

        // 기능: 화면을 바꾼다. 통계 창과 전체 지도(Phase 15: 게임 화면 위에서만 열린다)는 닫는다.
        // 입력: screen - 새 화면.
        // 출력: 반환값 없음. 바뀌었으면 Version이 오른다(같은 화면이고 통계 창이 닫혀 있으면 아무것도 하지 않는다).
        private void Set(UiScreen screen)
        {
            if (screen == Screen && !StatsOpen) return;
            Screen = screen;
            StatsOpen = false;
            MapOpen = false;
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

        public static bool MaySend(float now, float lastSentAt) => lastSentAt < 0f || now - lastSentAt >= ResendSeconds;

        public static StatsWaitState Of(float now, float sentAt, float answeredAt)
        {
            if (sentAt < 0f) return StatsWaitState.NoAnswer;
            if (answeredAt >= sentAt) return StatsWaitState.Answered;
            return now - sentAt >= AnswerSeconds ? StatsWaitState.NoAnswer : StatsWaitState.Waiting;
        }
    }
}
