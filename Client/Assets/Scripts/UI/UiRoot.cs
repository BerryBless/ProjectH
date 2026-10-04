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

        // 기능: EventSystem, 화면 Canvas, 각 화면과 디버그 오버레이를 만들고 버튼을 연결한다.
        // 입력: 없음.
        // 출력: 반환값 없음. UI 객체가 생성되고 버튼이 GameClient·UiFlow 처리에 연결된다.
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

        // 기능: 명령줄 인자나 저장된 값으로 타이틀 입력 칸을 채우고, 자동 접속 인자면 바로 접속한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 타이틀 입력이 채워지고 첫 화면이 표시된다.
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

        // 기능: 매 프레임 GameClient 상태를 UiFlow에 전달하고 현재 화면과 디버그 줄을 갱신한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 화면 전환, 커서 잠금·입력 차단 설정, 보이는 화면의 내용과 디버그 줄이 갱신된다.
        private void Update()
        {
            if (_client.DebugTogglePressed) _debug.Toggle();
            if (_client.EscapePressed) _flow.EscapePressed();

            UiConnection connection = _client.State == ClientState.Joined ? UiConnection.Joined
                : _client.State == ClientState.Disconnected ? UiConnection.Offline
                : UiConnection.Connecting;
            _flow.Update(connection, _client.ReconnectAttempt > 0, _client.ResultCount, _client.HasMatch, _client.Match.State);
            if (_flow.Version != _shownVersion) Apply();
            _client.SetUiControl(_flow.AllowCursorLock, _flow.BlocksGameInput);

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

        // 기능: UiFlow가 고른 화면만 보이게 하고 전환 시 필요한 처리를 한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 화면 표시 상태가 바뀌고, 결과 화면이 열리면 결과가 채워지며, 전적 창이 열리면 전적 요청이 보내진다.
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

        // 기능: GameClient의 최신 MatchResult로 결과 화면을 채운다.
        // 입력: 없음.
        // 출력: 반환값 없음. 결과 화면 Text가 갱신된다.
        private void ShowResult()
        {
            MatchResult r = _client.Result;
            bool won = r.WinnerId != 0 && r.WinnerId == _client.MyEntityId;
            string winner = r.WinnerId == 0 ? null : UiText.NameOr(_client.NameOf(r.WinnerId), r.WinnerId);
            _result.Show(won, r.Placement, r.Participants, r.Kills, winner, _client.DiedThisRound, _client.KilledByZone, _client.LastDeathCause,
                _client.KillerName);
        }

        // 기능: 타이틀 접속 버튼 콜백: 주소·포트·이름을 저장하고 접속을 시작한다.
        // 입력: host - 서버 주소, port - 서버 포트, name - 플레이어 이름.
        // 출력: 반환값 없음. 연결이 끊긴 상태일 때만 PlayerPrefs에 저장되고 접속이 시작된다.
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

        // 기능: 접속 주소를 기억하고 GameClient 접속과 Connecting 화면 전환을 시작한다.
        // 입력: host - 서버 주소, port - 서버 포트, name - 플레이어 이름.
        // 출력: 반환값 없음. Disconnected 상태일 때만 접속이 시작되고 화면이 Connecting으로 바뀐다.
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

        // 기능: 다시 접속 버튼 Handler: 마지막 주소로 다시 접속한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 이전 접속 주소가 있으면 접속이 시작된다.
        private void Retry()
        {
            if (_host == null) return;
            StartConnect(_host, _port, _name);
        }

        // 기능: 접속 중 화면의 취소 버튼 Handler.
        // 입력: 없음.
        // 출력: 반환값 없음. 연결이 끊기고 타이틀 화면으로 돌아간다.
        // Connecting "cancel".
        private void CancelConnect()
        {
            _client.Disconnect();
            _flow.LeaveRequested();
        }

        // 기능: 메뉴의 접속 끊기·연결 끊김 화면의 타이틀로 버튼 Handler.
        // 입력: 없음.
        // 출력: 반환값 없음. 연결과 자동 재접속이 중단되고 타이틀 화면으로 돌아간다.
        // Menu "disconnect", Disconnected "to title": also stops an automatic reconnect.
        private void Leave()
        {
            _client.Disconnect();
            _flow.LeaveRequested();
        }

        // 기능: 게임 종료 버튼 Handler.
        // 입력: 없음.
        // 출력: 반환값 없음. 빌드된 Player가 종료된다(Editor에서는 변화 없음).
        // A built player closes. In the Editor Application.Quit does nothing (stop Play Mode instead).
        private static void Quit()
        {
            Application.Quit();
        }

        // 기능: UiRoot가 만든 UI 객체를 정리한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 디버그 오버레이, 화면 Canvas, 직접 만든 EventSystem이 파괴된다.
        private void OnDestroy()
        {
            _debug?.Dispose();
            if (_canvas != null) Destroy(_canvas);
            if (_eventSystem != null) Destroy(_eventSystem);
        }
    }
}
