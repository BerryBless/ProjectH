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

        // 기능: 타이틀·끊김 화면에서 접속을 요청했을 때 Connecting 화면으로 바꾼다(UiRoot가 이미 Connect를 불렀다).
        // 입력: 없음.
        // 출력: 반환값 없음. 바뀌면 Version이 오르고, 다른 화면이면 아무것도 하지 않는다.
        public void ConnectRequested()
        {
            if (Screen == UiScreen.Title || Screen == UiScreen.Disconnected) Set(UiScreen.Connecting);
        }

        // 기능: 플레이어가 스스로 나갔을 때(메뉴 "접속 끊기", 끊김 "타이틀로", 접속 중 "취소"; UiRoot가 이미 끊었다) 타이틀 화면으로 바꾼다.
        // 입력: 없음.
        // 출력: 반환값 없음. 통계 창·지도가 닫히고 Version이 오른다. 이후 연결 변화는 타이틀을 움직이지 않는다.
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

        // 기능: 메뉴 "계속"·결과 "관전 계속"을 처리해 게임 화면으로 돌아간다.
        // 입력: 없음.
        // 출력: 반환값 없음. 메뉴·결과 화면이 아니면 아무것도 하지 않는다.
        public void ContinuePressed()
        {
            if (Screen == UiScreen.Menu || Screen == UiScreen.Result) Set(UiScreen.InGame);
        }

        // 기능: 통계 창을 연다(메뉴·결과 화면 위에서만).
        // 입력: 없음.
        // 출력: 반환값 없음. 열리면 Version이 오르고, 이미 열렸거나 다른 화면이면 아무것도 하지 않는다.
        public void OpenStats()
        {
            if (StatsOpen || (Screen != UiScreen.Menu && Screen != UiScreen.Result)) return;
            StatsOpen = true;
            Version++;
        }

        // 기능: 통계 창을 닫는다.
        // 입력: 없음.
        // 출력: 반환값 없음. 닫히면 Version이 오른다.
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

        // 기능: 한 프레임의 연결 상태와 결과 수로 화면 전환을 정한다(접속 성공·실패, 연결 끊김, 새 MatchResult, 다음 판 시작 시 결과 화면 닫기).
        // 입력: connection - 현재 연결 상태, reconnecting - GameClient의 자동 재접속이 진행 중인지, results - 지금까지 도착한 MatchResult 수(지난 값보다 클 때만 새 결과),
        //   hasMatch - MatchState를 받았는지(개발 서버는 보내지 않음), matchState - 최신 MatchState.
        // 출력: 반환값 없음. Screen이나 Reconnecting이 바뀌면 Version이 오른다.
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

        // 기능: 통계 요청을 새로 보내도 되는지 판단한다(보낸 적이 없거나 지난 요청이 ResendSeconds 이상 지났을 때).
        // 입력: now - 현재 시각(초), lastSentAt - 지난 요청 시각(음수면 보낸 적 없음).
        // 출력: 보내도 되면 true, 지난 요청을 다시 써야 하면 false.
        public static bool MaySend(float now, float lastSentAt) => lastSentAt < 0f || now - lastSentAt >= ResendSeconds;

        // 기능: 통계 창의 대기 상태를 정한다.
        // 입력: now - 현재 시각(초), sentAt - 요청 시각(음수면 보낸 적 없음), answeredAt - 마지막 응답 시각(음수면 없음).
        // 출력: 보낸 적이 없거나 AnswerSeconds 안에 응답이 없으면 NoAnswer, 요청 뒤 응답이 왔으면 Answered, 아니면 Waiting.
        public static StatsWaitState Of(float now, float sentAt, float answeredAt)
        {
            if (sentAt < 0f) return StatsWaitState.NoAnswer;
            if (answeredAt >= sentAt) return StatsWaitState.Answered;
            return now - sentAt >= AnswerSeconds ? StatsWaitState.NoAnswer : StatsWaitState.Waiting;
        }
    }
}
