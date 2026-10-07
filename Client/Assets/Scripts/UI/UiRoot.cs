using System;
using ProjectH.Client.Bootstrap;
using ProjectH.Client.Game;
using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using UnityEngine;

namespace ProjectH.Client.UI
{
    // Phase 11: the game UI. Builds the EventSystem (when the scene has none), one interactive canvas for the screens
    // (title, menu, disconnected, result, statistics) and the F1 debug line; every frame it feeds UiFlow what GameClient
    // reports, shows the screen UiFlow chose (only when its Version changed), passes the cursor and input outputs to
    // GameClient (D5) and updates the one visible screen. Button handlers call GameClient and the flow.
    // Lifetime: lives on GameClient's GameObject for the whole session; OnDestroy destroys everything it made. The font
    // belongs to GameClient (UiFont.Release in its OnDestroy).
    [RequireComponent(typeof(GameClient))]
    public sealed class UiRoot : MonoBehaviour
    {
        // D4: the last address, port and name typed on the title screen.
        private const string HostKey = "ProjectH.Host";
        private const string PortKey = "ProjectH.Port";
        private const string NameKey = "ProjectH.Name";

        private readonly UiFlow _flow = new UiFlow();
        private GameClient _client;
        private GameObject _eventSystem;   // made here (destroyed here), or null when the scene had one
        private GameObject _canvas;
        private TitleScreen _title;
        private MenuScreen _menu;
        private DisconnectScreen _disconnect;
        private ResultScreen _result;
        private StatsWindow _stats;
        private DebugOverlay _debug;
        private int _shownVersion = -1;
        private UiScreen _shownScreen = UiScreen.Title;
        private bool _shownStats;
        // The address of the last connect, for Retry.
        private string _host;
        private int _port;
        private string _name;
        private float _statsSentAt = -1f;
        private TimeSpan _utcOffset;

        private void Awake()
        {
            _client = GetComponent<GameClient>();
            _eventSystem = UiFactory.EnsureEventSystem();
            _canvas = UiFactory.CreateCanvas("UiScreens", 110, interactive: true);   // above every HUD and the crosshair (100)
            Transform root = _canvas.transform;
            // Creation order is drawing order: the statistics window last, over the menu and the result.
            _title = new TitleScreen(root, Connect, CancelConnect, Quit);
            _menu = new MenuScreen(root, _flow.ContinuePressed, _flow.OpenStats, Leave, Quit);
            _disconnect = new DisconnectScreen(root, _client.StopReconnecting, Retry, Leave);
            _result = new ResultScreen(root, _flow.ContinuePressed, _flow.OpenStats);
            _stats = new StatsWindow(root, _flow.CloseStats);
            _debug = new DebugOverlay();
        }

        // After every Awake on this GameObject, so GameClient is ready when the command line connects at once.
        private void Start()
        {
            LaunchArgs args = LaunchArgs.FromCommandLine();
            if (args.AutoConnect)
            {
                _title.Fill(args.Host, args.Port, args.DevPlayerId);
                // Not saved: a test launch's address and name must not replace what the player typed last.
                StartConnect(args.Host, args.Port, args.DevPlayerId);
            }
            else
            {
                _title.Fill(PlayerPrefs.GetString(HostKey, args.Host), PlayerPrefs.GetInt(PortKey, args.Port),
                    PlayerPrefs.GetString(NameKey, args.DevPlayerId));
            }
            Apply();
        }

        // 기능: 한 프레임의 UI 흐름: F1·Esc·M(Phase 15 전체 지도)을 UiFlow에 넘기고, 연결 상태로 화면을 정하고, 커서·입력 막기와 전체 지도
        //   상태를 GameClient에 넘기고, 보이는 화면을 갱신한다.
        // 입력: 없음(Unity가 매 프레임 부른다).
        // 출력: 반환값 없음.
        private void Update()
        {
            if (_client.DebugTogglePressed) _debug.Toggle();
            if (_client.EscapePressed) _flow.EscapePressed();
            if (_client.MapTogglePressed) _flow.ToggleMap();

            UiConnection connection = _client.State == ClientState.Joined ? UiConnection.Joined
                : _client.State == ClientState.Disconnected ? UiConnection.Offline
                : UiConnection.Connecting;
            _flow.Update(connection, _client.ReconnectAttempt > 0, _client.ResultCount, _client.HasMatch, _client.Match.State);
            if (_flow.Version != _shownVersion) Apply();
            _client.SetUiControl(_flow.AllowCursorLock, _flow.BlocksGameInput);
            _client.SetMapOpen(_flow.MapOpen);

            switch (_flow.Screen)
            {
                case UiScreen.Title:
                    _title.SetConnectEnabled(_client.State == ClientState.Disconnected);
                    break;
                case UiScreen.Disconnected:
                    // Constant strings: no allocation. The progress line is rebuilt once per second at most.
                    _disconnect.SetReason(UiText.Disconnect(_client.LastDisconnect));
                    _disconnect.SetRetryEnabled(_client.State == ClientState.Disconnected);
                    if (_flow.Reconnecting)
                        _disconnect.SetProgress(_client.ReconnectAttempt, DisconnectCodes.MaxReconnectAttempts, Mathf.CeilToInt(_client.NextReconnectIn));
                    break;
                case UiScreen.Result:
                    _result.SetSecondsLeft(_client.StateSecondsLeft);
                    break;
            }
            if (_flow.StatsOpen) _stats.Tick(Time.unscaledTime, _statsSentAt, _client.StatsAnsweredAt, _client.LastStats, _utcOffset);
            _debug.Tick(_client.State, _client.RoundTripMs, _client.MyEntityId);
            _client.TickMovementDebug(_debug, Time.unscaledTime);
            _client.TickBuildDebug(_debug, Time.unscaledTime);
        }

        // Shows what UiFlow chose. Runs only when its Version changed (or at start).
        private void Apply()
        {
            UiScreen screen = _flow.Screen;
            UiScreen previous = _shownScreen;

            _title.SetVisible(screen == UiScreen.Title || screen == UiScreen.Connecting);
            _title.SetConnecting(screen == UiScreen.Connecting);
            if (screen == UiScreen.Title && previous != UiScreen.Title)
            {
                // Back on the title: why the connection ended, unless the player left on purpose.
                _title.SetMessage(previous == UiScreen.Disconnected ? UiText.Disconnect(_client.LastDisconnect) : string.Empty);
            }
            _menu.SetVisible(screen == UiScreen.Menu);
            _disconnect.SetVisible(screen == UiScreen.Disconnected);
            _disconnect.SetReconnecting(_flow.Reconnecting);
            _result.SetVisible(screen == UiScreen.Result);
            if (screen == UiScreen.Result && previous != UiScreen.Result) ShowResult();

            _stats.SetVisible(_flow.StatsOpen);
            if (_flow.StatsOpen && !_shownStats)
            {
                // D8: one request per opening (GameClient reuses one younger than the server's limit).
                _statsSentAt = _client.RequestStats();
                _utcOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
            }

            _shownScreen = screen;
            _shownStats = _flow.StatsOpen;
            _shownVersion = _flow.Version;
        }

        // 기능: 결과 화면에 최신 MatchResult를 채운다(결과 화면이 열릴 때 한 번). Phase 14 D6: 우승 = 배치 1, 분대면 팀 단위 문구.
        // 입력: 없음(GameClient의 결과와 이번 판의 사망 기록을 읽는다).
        // 출력: 반환값 없음.
        private void ShowResult()
        {
            MatchResult r = _client.Result;
            // Phase 14 D6: placements are the team's (Solo: a team of one), so placement 1 is a win for every member of the
            // winning team, also one who is out waiting for its card. WinnerId is the winning team's smallest entity id.
            bool won = r.Placement == 1;
            bool teams = _client.SquadMatch;
            string winner = r.WinnerId == 0 ? null : UiText.NameOr(_client.NameOf(r.WinnerId), r.WinnerId);
            _result.Show(won, r.Placement, r.Participants, teams, r.Kills, winner, _client.DiedThisRound, _client.KilledByZone, _client.LastDeathCause,
                _client.KillerName);
        }

        // The title's Connect: the only path that saves the address, port and name (D4).
        private void Connect(string host, int port, string name)
        {
            if (_client.State != ClientState.Disconnected) return;
            PlayerPrefs.SetString(HostKey, host);
            PlayerPrefs.SetInt(PortKey, port);
            PlayerPrefs.SetString(NameKey, name);
            PlayerPrefs.Save();
            StartConnect(host, port, name);
        }

        // Only from Disconnected: GameClient.Connect ignores a connect while the previous connection is still closing,
        // and the flow must not show "connecting" for a connect that never started.
        private void StartConnect(string host, int port, string name)
        {
            if (_client.State != ClientState.Disconnected) return;
            _host = host;
            _port = port;
            _name = name;
            _client.Connect(host, port, name);
            _flow.ConnectRequested();
        }

        private void Retry()
        {
            if (_host == null) return;
            StartConnect(_host, _port, _name);
        }

        // Connecting "cancel".
        private void CancelConnect()
        {
            _client.Disconnect();
            _flow.LeaveRequested();
        }

        // Menu "disconnect", Disconnected "to title": also stops an automatic reconnect.
        private void Leave()
        {
            _client.Disconnect();
            _flow.LeaveRequested();
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // ---- QA-4 D28: QaCommandReceiver's view of the UI and its commands (main thread) ----
        public UiScreen QaScreen => _flow.Screen;
        public bool QaStatsOpen => _flow.StatsOpen;
        public bool QaMapOpen => _flow.MapOpen;
        public bool QaDebugVisible => _debug.Visible;

        // 기능: QA UI 명령 하나를 Esc·메뉴 버튼·F1·M과 같은 흐름 호출로 적용한다(가짜 입력 없음). Phase 15: openMap·closeMap.
        //   화면은 바로 다시 그리므로 같은 프레임에 요청한 스크린샷에 결과가 보인다. 전체 지도는 GameClient에도 바로 알린다.
        // 입력: command - 적용할 명령.
        // 출력: 적용했으면 true, 지금 화면에 맞지 않으면 false(아무것도 바뀌지 않음).
        public bool QaApply(Qa.QaUiCommand command)
        {
            bool applied = false;
            switch (command)
            {
                case Qa.QaUiCommand.OpenMenu:
                    applied = _flow.Screen == UiScreen.InGame && !_flow.StatsOpen;
                    if (applied) _flow.EscapePressed();
                    break;
                case Qa.QaUiCommand.CloseMenu:
                    // Only the menu: ContinuePressed would also dismiss the result screen.
                    applied = _flow.Screen == UiScreen.Menu && !_flow.StatsOpen;
                    if (applied) _flow.ContinuePressed();
                    break;
                case Qa.QaUiCommand.OpenStats:
                    applied = (_flow.Screen == UiScreen.Menu || _flow.Screen == UiScreen.Result) && !_flow.StatsOpen;
                    if (applied) _flow.OpenStats();
                    break;
                case Qa.QaUiCommand.CloseStats:
                    applied = _flow.StatsOpen;
                    if (applied) _flow.CloseStats();
                    break;
                case Qa.QaUiCommand.ToggleDebug:
                    applied = true;
                    _debug.Toggle();
                    break;
                case Qa.QaUiCommand.OpenMap:
                    applied = _flow.Screen == UiScreen.InGame && !_flow.MapOpen;
                    if (applied) _flow.OpenMap();
                    break;
                case Qa.QaUiCommand.CloseMap:
                    applied = _flow.MapOpen;
                    if (applied) _flow.CloseMap();
                    break;
            }
            if (_flow.Version != _shownVersion) Apply();
            _client.SetUiControl(_flow.AllowCursorLock, _flow.BlocksGameInput);
            _client.SetMapOpen(_flow.MapOpen);
            return applied;
        }
#endif

        // A built player closes. In the Editor Application.Quit does nothing (stop Play Mode instead).
        private static void Quit()
        {
            Application.Quit();
        }

        private void OnDestroy()
        {
            _debug?.Dispose();
            if (_canvas != null) Destroy(_canvas);
            if (_eventSystem != null) Destroy(_eventSystem);
        }
    }
}
