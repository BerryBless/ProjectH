using System;
using System.Collections.Generic;
using ProjectH.Client.Bootstrap;
using ProjectH.Client.CameraControl;
using ProjectH.Client.Input;
using ProjectH.Client.Net;
using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Composition root of the client. Everything it creates is released in OnDestroy
    // (which also runs on application quit), roughly in reverse order of creation.
    public sealed class GameClient : MonoBehaviour
    {
        // Render remote players two snapshot intervals in the past so one late or lost snapshot
        // still leaves a sample to interpolate towards.
        private const double InterpolationSnapshots = 2.0;
        // Aim ray length before the weapon catalog arrives.
        private const float DefaultAimRange = 300f;

        private GameObject _world;
        private Material[] _worldMaterials;
        private Mesh _terrainMesh;
        private InputReader _input;
        private ShoulderCamera _camera;
        private Crosshair _crosshair;
        private CombatHud _hud;
        private InventoryHud _inventoryHud;
        private WorldItemViews _worldItems;
        private LocalFireEffects _fireEffects;
        private MatchHud _matchHud;
        private ZoneView _zoneView;
        private PoiLabel _poiLabel;
        private KillFeed _killFeed;
        // Phase 12 D14: the drop transport and the doors on screen.
        private TransportView _transportView;
        private DoorViews _doorViews;
        // Phase 13 D6: the harvestables on screen and which the server says are destroyed (the prediction collides with
        // the standing ones).
        private HarvestableViews _harvestables;
        private ulong _destroyedHarvestables;
        // Phase 13 D5, D13-D16: the predicted tool, the confirmed pieces of our interest window (prediction collides with
        // them), and build mode's local side (selection, preview, turbo, pending placements).
        private readonly ToolState _tools = new ToolState();
        private readonly BuildStore _buildStore = new BuildStore();
        private BuildController _build;
        // Phase 13 D16: building and harvesting on screen.
        private PieceMeshes _pieceMeshes;
        private BuildPieceViews _pieceViews;
        private BuildPreview _buildPreview;
        private HarvestEffects _harvestEffects;
        private BuildHud _buildHud;
        private readonly BuildAudio _buildAudio = new BuildAudio();
        private Material _buildSource;
        private float _nextSwingAt;
        // Phase 11 D9: entity id -> name from PlayerSpawned. At most one entry per player in the match: removed on
        // despawn, cleared with the match state (disconnect).
        private readonly Dictionary<ushort, string> _names = new Dictionary<ushort, string>();
        // Phase 11 D5, set by UiRoot every frame from UiFlow: may a click lock the cursor, and is game input blocked.
        private bool _cursorLockAllowed = true;
        private bool _inputBlocked;
        // This frame's game-input block (a screen up or the cursor free), set in Update: the crosshair shows exactly when
        // input goes through.
        private bool _blockedThisFrame;
        // Phase 11 D7: how this round ended for us (from our own PlayerDied); the killer's name is kept because the killer
        // may leave before the result. Reset by our respawn (the next round) and with the match state.
        private bool _died;
        private bool _killedByZone;
        private DeathCause _deathCause;   // Phase 12 D10: with no killer, the zone or a fall
        private string _killerName;
        // Phase 11 D8: the newest statistics answer, when the request in effect was sent and when an answer came
        // (unscaled seconds, negative = never). Cleared with the match state.
        private StatsResponse _stats;
        private float _statsSentAt = -1f;
        private float _statsAnsweredAt = -1f;
        private readonly SpectatorCamera _spectator = new SpectatorCamera();
        // Phase 12 D9: the doors as predicted (server DoorStates plus our own predicted changes); the predictor moves
        // against them. Phase 12 D5: the match's transport route (TransportRoute), until the next one or a disconnect.
        private readonly PredictedDoors _doors = new PredictedDoors();
        private bool _hasRoute;
        private DropRoute _route;
        private NetClient _net;
        private LocalPlayerPredictor _predictor;
        private PlayerView _localView;
        private readonly RemotePlayers _remotePlayers = new RemotePlayers();
        private ServerClock _clock;
        private WeaponState _weapons;
        private WeaponInfo[] _weaponCatalog;
        private ItemCatalogData _itemCatalog;
        private InventoryState _inventory;   // the newest server InventoryState (heals and the use channel)
        private float _useEndTime;           // local time the running heal ends (from UseRemainingTicks)
        private float _useSeconds;           // its whole channel time
        private int _simHz;
        private double _interpolationDelaySeconds;
        private double _renderTick;       // server tick remote players are drawn at this frame (ViewTick, D6)
        private int _pendingSteps;        // inputs predicted in Update, aimed and sent in LateUpdate
        private int _health;
        private int _shield;
        // Phase 5 (D11): the newest MatchState and ZoneState. A dev-respawn server sends neither: _hasMatch stays
        // false and the client behaves as in Phase 4 (respawn countdown, no match HUD, no zone).
        private bool _hasMatch;
        private MatchState _match;
        private ZoneState _zone;
        private bool _hasResult;
        private MatchResult _result;
        private bool _aiming;
        private bool _fireHeld;
        // D12: the click that locks the cursor must not also fire; fire waits for that button's release.
        private bool _fireBlockedUntilRelease;
        // Phase 10 D10: where the last Connect went, and the automatic reconnect.
        //   _reconnectAttempt: attempts started in this cycle (0 = no cycle). Reset by a successful join.
        //   _dropAt: unscaled time the drop that started the cycle was seen. Attempt n starts at
        //     _dropAt + DisconnectCodes.ReconnectOffsetSeconds(n) (1, 3, 7 s), however long the earlier ones took.
        //   _reconnectPending / _reconnectAt: the next attempt's slot. Armed when an attempt starts, so one still
        //     connecting when its successor's slot comes is cancelled and replaced; cleared once an attempt connects.
        //   _established: the current connection got as far as connected (a first connect that fails is not retried).
        private string _host;
        private int _port;
        private string _devPlayerId;
        private int _reconnectAttempt;
        private bool _reconnectPending;
        private float _reconnectAt;
        private float _dropAt;
        private bool _established;

        public ClientState State => _net.State;
        public string LastError => _net.LastError;
        public int RoundTripMs => _net.RoundTripMs;
        public ushort MyEntityId { get; private set; }
        // 기능: 화면에 보여줄 현재 자동 재접속 시도 번호를 계산한다.
        // 입력: 없음.
        // 출력: 재접속이 진행 중이 아니면 0, 다음 슬롯을 기다리는 중이면 다음 시도 번호, 연결 중이면 진행 중인 시도 번호.
        // Phase 10 D10: 0 = no automatic reconnect running; otherwise the attempt (1..MaxReconnectAttempts) that is
        // connecting, or the next one while it waits for its slot.
        public int ReconnectAttempt =>
            _reconnectPending && _net.State == ClientState.Disconnected ? _reconnectAttempt + 1 : _reconnectAttempt;

        // ---- Phase 11: read-only state for the UI (UiRoot draws it; nothing here changes the game) ----

        // 기능: 마지막 연결 종료 원인을 NetClient에서 모아 요약한다.
        // 입력: 없음.
        // 출력: 시작 실패 여부, 종료 이유, 종료 코드, 거절 사유, Join 결과를 담은 DisconnectSummary.
        // D6: why the last connection ended.
        public DisconnectSummary LastDisconnect => new DisconnectSummary
        {
            StartFailed = _net.LastConnectStartFailed,
            Reason = _net.LastDisconnectReason,
            Code = _net.LastDisconnectCode,
            Reject = _net.LastRejectReason,
            Join = _net.LastJoinResult,
        };

        // 기능: 다음 자동 재접속 시도가 시작될 때까지 남은 시간을 계산한다.
        // 입력: 없음.
        // 출력: 남은 초(unscaled). 시도가 연결 중이거나 자동 재접속이 없으면 0.
        // D6: seconds until the next automatic attempt starts; 0 while an attempt is connecting or none is running.
        public float NextReconnectIn =>
            _reconnectPending && _net.State == ClientState.Disconnected ? Mathf.Max(0f, _reconnectAt - Time.unscaledTime) : 0f;

        // 기능: PlayerSpawned로 받은 엔티티 이름을 조회한다.
        // 입력: entityId - 조회할 엔티티 ID.
        // 출력: 알려진 이름, 모르면 null.
        // D9: the name a PlayerSpawned gave this entity, or null when it is not known.
        public string NameOf(ushort entityId) => _names.TryGetValue(entityId, out string name) ? name : null;

        // D7: the match as the HUD sees it, and how the round ended for us.
        public bool HasMatch => _hasMatch;
        public MatchState Match => _match;
        public bool HasResult => _hasResult;
        public MatchResult Result => _result;
        // Counts MatchResults since the start (UiFlow opens the result screen once per new one).
        public int ResultCount { get; private set; }
        public bool DiedThisRound => _died;
        // True when nobody killed us (KillerId 0): the zone or, since Phase 12, a fall (LastDeathCause says which).
        public bool KilledByZone => _killedByZone;
        public DeathCause LastDeathCause => _deathCause;

        // 기능: F1 디버그 오버레이에 로컬 이동 예측 값(모드, 속도, 에너지, 마지막 보정량, 수송기 경로)을 넘긴다.
        // 입력: overlay - 갱신할 디버그 오버레이, now - 호출한 쪽의 현재 시각(초).
        // 출력: 반환값 없음. 오버레이의 이동 줄이 갱신된다. 예측기가 없거나 사망이면 지상 모드와 0 값으로 표시된다.
        // Phase 12 D14: the F1 movement line (UiRoot owns the overlay; the values are the local prediction's).
        public void TickMovementDebug(DebugOverlay overlay, float now)
        {
            bool alive = _predictor != null && !_predictor.IsDead;
            if (!alive)
            {
                overlay.TickMovement(now, false, MovementMode.Ground, 0f, 0f, 0f, 0f, _hasRoute, _route);
                return;
            }
            overlay.TickMovement(now, true, _predictor.Mode, _predictor.HorizontalSpeed, _predictor.VerticalSpeed, _predictor.Energy,
                _predictor.LastCorrection, _hasRoute, _route);
        }
        public string KillerName => _killerName;

        // 기능: 현재 Match 상태 타이머(Starting, Finished)가 끝날 때까지 남은 시간을 추정 서버 Tick으로 계산한다.
        // 입력: 없음.
        // 출력: 올림한 남은 초. Match나 준비된 Clock이 없거나 타이머가 지났으면 0.
        // D7: whole seconds until the current state's timer ends (Starting, Finished), 0 without one.
        public int StateSecondsLeft
        {
            get
            {
                if (!_hasMatch || _clock == null || !_clock.IsReady || _simHz <= 0) return 0;
                double tick = _renderTick + _interpolationDelaySeconds * _simHz;
                return _match.StateEndTick > tick ? (int)Math.Ceiling((_match.StateEndTick - tick) / _simHz) : 0;
            }
        }

        // D4, D5: Esc and F1 of this frame, read by UiRoot (InputReader stays the only Input System user).
        public bool EscapePressed => _input.EscapePressed;
        public bool DebugTogglePressed => _input.DebugTogglePressed;

        // 기능: UiFlow 결과로 커서 잠금 허용 여부와 게임 입력 차단 여부를 설정한다.
        // 입력: allowCursorLock - 클릭으로 커서를 잠글 수 있는지, blockInput - 게임 입력을 막을지.
        // 출력: 반환값 없음. 다음 Update부터 커서와 입력 처리에 반영된다.
        // D5: UiRoot passes UiFlow's outputs every frame. Without a screen up a click locks the cursor as before; with
        // one up the cursor is freed and movement, fire, aim and look are zero (inputs still go out, empty).
        public void SetUiControl(bool allowCursorLock, bool blockInput)
        {
            _cursorLockAllowed = allowCursorLock;
            _inputBlocked = blockInput;
        }

        // D8: the newest answer and the times the stats window compares (StatsWait).
        public StatsResponse LastStats => _stats;
        public float StatsSentAt => _statsSentAt;
        public float StatsAnsweredAt => _statsAnsweredAt;

        // 기능: 재전송 간격(StatsWait.ResendSeconds)이 지났으면 서버에 전적 요청 Packet을 보낸다.
        // 입력: 없음.
        // 출력: 현재 유효한 요청을 보낸 시각(unscaled 초). 보낼 수 없었으면 음수.
        // D8: sends a request unless one went out less than StatsWait.ResendSeconds ago (the server would drop it; its
        // answer is here or on the way). Returns when the request in effect was sent (negative = none could be sent).
        // A send that fails forgets the older request, so the window says "no answer" instead of showing the answer to
        // that older request as this one's.
        public float RequestStats()
        {
            float now = Time.unscaledTime;
            if (StatsWait.MaySend(now, _statsSentAt)) _statsSentAt = _net.RequestStats() ? now : -1f;
            return _statsSentAt;
        }

        // 기능: 자동 재접속을 멈추고, 연결 중이거나 Join 응답을 기다리는 시도를 종료 이벤트 없이 포기한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 재접속 주기가 초기화되고 진행 중인 연결이 취소된다.
        // D6: the disconnected screen's Cancel. Stops the automatic reconnect; an attempt still connecting, or connected
        // and waiting for its join answer, is given up without a disconnect event, so the reason on screen stays the one
        // that started the cycle. Nothing of a match arrives before the join answer, so there is no match state to
        // clear; the attempt no longer counts as established (a later manual connect that fails is not retried).
        public void StopReconnecting()
        {
            CancelReconnect();
            if (_net.State == ClientState.Connecting || _net.State == ClientState.Connected)
            {
                _net.CancelConnect();
                _established = false;
            }
        }

        // 기능: 자동 재접속을 멈추고 지정한 서버에 수동 접속을 시작한다.
        // 입력: host - 서버 주소, port - 서버 포트, devPlayerId - 개발용 플레이어 ID.
        // 출력: 반환값 없음. 접속 주소가 기억되고 연결이 시작된다. 이미 연결이 진행 중이면 무시된다.
        // A manual connect: stops any automatic reconnect and starts over. Ignored while a connection (or an automatic
        // attempt) is in progress, like NetClient.Connect, so the address of the running connection is kept.
        public void Connect(string host, int port, string devPlayerId)
        {
            if (_net.State != ClientState.Disconnected) return;
            CancelReconnect();
            _host = host;
            _port = port;
            _devPlayerId = devPlayerId;
            _net.Connect(host, port, devPlayerId);
        }

        // 기능: 자동 재접속을 멈추고 현재 연결을 끊는다.
        // 입력: 없음.
        // 출력: 반환값 없음. 재접속 주기가 초기화되고 NetClient 연결이 종료된다.
        public void Disconnect()
        {
            CancelReconnect();
            _net.Disconnect();
        }

        // 기능: 자동 재접속 주기를 끝낸다.
        // 입력: 없음.
        // 출력: 반환값 없음. 시도 횟수가 0이 되고 예약된 다음 시도가 취소된다.
        private void CancelReconnect()
        {
            _reconnectAttempt = 0;
            _reconnectPending = false;
        }

        // 기능: 맵, 카메라, HUD, 월드·건설 뷰, NetClient를 만들고 NetClient Packet 이벤트를 구독한다.
        // 입력: 없음.
        // 출력: 반환값 없음. Client 구성 요소가 생성되고 이벤트 Handler가 연결된다(OnDestroy에서 해제). Main Camera가 없으면 새로 만든다.
        private void Awake()
        {
            _world = MapWorld.Build(out _worldMaterials, out _terrainMesh);
            _input = new InputReader();

            Camera main = Camera.main;
            if (main == null)
            {
                var cameraGo = new GameObject("Main Camera") { tag = "MainCamera" };
                main = cameraGo.AddComponent<Camera>();
                cameraGo.AddComponent<AudioListener>();
            }
            _camera = new ShoulderCamera(main);
            _crosshair = new Crosshair();
            _hud = new CombatHud();
            _inventoryHud = new InventoryHud();
            _worldItems = new WorldItemViews();
            _fireEffects = new LocalFireEffects();
            _matchHud = new MatchHud();
            _zoneView = new ZoneView();
            _poiLabel = new PoiLabel();
            _killFeed = new KillFeed();
            _transportView = new TransportView();
            _doorViews = new DoorViews();
            _harvestables = new HarvestableViews();
            // Phase 13 D16: one Lit base for the pieces and effects (LitMaterial: never a primitive's default material).
            var probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _buildSource = LitMaterial.Source(probe.GetComponent<Renderer>().sharedMaterial);
            Destroy(probe);
            _pieceMeshes = new PieceMeshes();
            _pieceViews = new BuildPieceViews(_pieceMeshes, _buildSource);
            _buildPreview = new BuildPreview(_pieceMeshes, _buildSource);
            _harvestEffects = new HarvestEffects(_buildSource);
            _buildHud = new BuildHud();

            _net = new NetClient();
            _build = new BuildController(request => _net.SendBuild(request));
            _net.Connected += OnConnected;
            _net.Joined += OnJoined;
            _net.SpawnReceived += OnSpawned;
            _net.DespawnReceived += OnDespawned;
            _net.SnapshotReceived += OnSnapshot;
            _net.Disconnected += OnDisconnected;
            _net.CatalogReceived += OnCatalog;
            _net.ShotReceived += OnShot;
            _net.HitConfirmedReceived += OnHitConfirmed;
            _net.DamageTakenReceived += OnDamageTaken;
            _net.PlayerDiedReceived += OnPlayerDied;
            _net.PlayerRespawnedReceived += OnPlayerRespawned;
            _net.InventoryReceived += OnInventory;
            _net.ItemCatalogReceived += OnItemCatalog;
            _net.ItemReceived += OnItem;
            _net.ItemRemovedReceived += OnItemRemoved;
            _net.PickupResultReceived += OnPickupResult;
            _net.MatchStateReceived += OnMatchState;
            _net.ZoneStateReceived += OnZoneState;
            _net.MatchResultReceived += OnMatchResult;
            _net.StatsReceived += OnStats;
            _net.TransportRouteReceived += OnTransportRoute;
            _net.DoorStatesReceived += OnDoorStates;
            _net.HarvestStatesReceived += OnHarvestStates;
            _net.BuildCatalogReceived += OnBuildCatalog;
            _net.ResourcesReceived += OnResources;
            _net.BuildResultReceived += OnBuildResult;
            _net.BuildPieceReceived += OnBuildPiece;
            _net.BuildHealthReceived += OnBuildHealth;
            _net.BuildDestroyedReceived += OnBuildDestroyed;
            _net.BuildResetReceived += OnBuildReset;
            _net.BuildInterestReceived += OnBuildInterest;
            _net.HarvestHitReceived += OnHarvestHit;
        }

        // 기능: 매 프레임 Network Poll, 자동 재접속, 입력·커서 처리, 원격 플레이어 렌더 Tick 갱신, 로컬 이동 예측을 진행한다.
        // 입력: 없음.
        // 출력: 반환값 없음. _renderTick, 원격 플레이어 위치, 예측 Step 수(_pendingSteps), 카메라 시점, 로컬 플레이어 뷰가 갱신된다.
        private void Update()
        {
            _net.Poll();
            UpdateReconnect();
            // Phase 12: the same block as below (last frame's UI control, this frame's cursor) for the crouch toggle.
            _input.Update(_inputBlocked || Cursor.lockState != CursorLockMode.Locked);
            UpdateCursorAndButtons();

            if (_clock != null && _clock.IsReady)
            {
                // The only place _renderTick is set (besides the reset to 0 in ClearMatchState): LateUpdate sends this
                // exact value as ViewTick (see SetAim).
                _renderTick = _clock.RenderTick(Time.unscaledTimeAsDouble, _interpolationDelaySeconds);
                _remotePlayers.Render(_renderTick);
            }

            if (_predictor == null) return;

            // Phase 11 D5: with a screen up or the cursor free, no look, move, sprint or fire, and keys pressed meanwhile
            // are dropped. The predictor still steps, so empty inputs keep going out (the Phase 10 input timeout).
            bool blocked = _inputBlocked || Cursor.lockState != CursorLockMode.Locked;
            _blockedThisFrame = blocked;
            _camera.ApplyLook(blocked ? Vector2.zero : _input.LookDelta, _aiming);
            InputButtons held = InputButtons.None;
            if (!blocked && _input.Sprint) held |= InputButtons.Sprint;
            if (!blocked && _input.CrouchHeld) held |= InputButtons.Crouch;   // Phase 12 D7
            if (_fireHeld) held |= InputButtons.Fire;
            if (blocked) _input.QueuedButtons = InputButtons.None;
            if (!blocked) UpdateBuildKeys();
            InputButtons queued = _input.QueuedButtons;
            _pendingSteps += _predictor.Advance(Time.deltaTime, blocked ? Vector2.zero : _input.Move, _camera.Yaw, held, ref queued);
            _input.QueuedButtons = queued;

            _localView.Place(_predictor.RenderPosition, _predictor.RenderYaw, _predictor.Mode, _predictor.Sprinting);
        }

        // 기능: 매 프레임 월드 아이템·킬피드·문·수송기 뷰와 카메라를 갱신하고, 이번 프레임 조준점으로 입력 Packet을 보낸 뒤 무기·건설·HUD 표시를 갱신한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 예측 Step이 있으면 PlayerInputPacket이 전송되고 카메라, 사격 효과, 건설 표시, HUD가 갱신된다.
        private void LateUpdate()
        {
            // Before the early return: items spin (and are visible) before the local player has spawned. No allocation.
            _worldItems.Tick(Time.time);
            _killFeed.Tick(Time.unscaledTime);
            // Phase 12 D14: the doors as predicted, and the transport at the render tick (also while spectating). Our own
            // rider sees it at the tick it is drawn at instead (the predicted tick, or the newest snapshot's before the first
            // ack), so the transport, the rider and its camera move together.
            _doorViews.Tick(_doors);
            bool riding = _predictor != null && !_predictor.IsDead && _predictor.Mode == MovementMode.Transport && _clock != null;
            _transportView.Tick(riding ? RiderTick() : _renderTick);
            if (_predictor == null) _buildStore.ClearChanged();   // nothing reads the changes before the spawn: do not let them pile up
            if (_predictor == null) return;
            float now = Time.time;
            bool alive = !_predictor.IsDead;

            // D5: dead in a match, the camera follows the watched player where it is drawn; otherwise our own view.
            _spectator.Update(_remotePlayers);
            bool watching = _spectator.TryGetFeet(_remotePlayers, _renderTick, out Vector3 watched);
            Vector3 followFeet = watching ? watched : _predictor.RenderPosition;
            // Phase 12 D14: the camera eases to our own mode (a watched player gets the standard camera).
            MovementMode mode = watching || !alive ? MovementMode.Ground : _predictor.Mode;
            _camera.Follow(followFeet, _aiming, Time.deltaTime, mode, !watching && alive && _predictor.Sprinting);
            // Phase 6 D7: the place name of whoever the camera follows. Text changes only when the place does.
            _poiLabel.SetVisible(true);
            _poiLabel.SetPosition(followFeet);
            _crosshair.SetVisible(alive && !_blockedThisFrame);

            // After the camera moved, so aim, tracer and the sent inputs all use the crosshair of this frame.
            Vector3 aimPoint = FindAimPoint();
            int shots = 0;
            if (_pendingSteps > 0)
            {
                // The server fires from its simulated position after each input's step, so SetAim solves every
                // pending step's aim from that step's predicted result, not from RenderPosition (interpolated one
                // step behind, plus the reconcile offset). The camera angles are the fallback when the point is
                // too close to the eye to give a direction.
                // Invariant (D6): ViewTick is exactly the _renderTick passed to _remotePlayers.Render in this
                // frame's Update, so the server rewinds targets to where this frame drew them (and where the
                // crosshair ray above hit them). Float only because that is the wire type.
                // Same tick basis on both sides: the interpolator is fed header.ServerTick, and the server
                // records History[N] after all of tick N's moves and sends that N in the snapshot, so tick N
                // is one moment on both sides. ServerClock.RenderTick is stateful (it never goes backwards):
                // call it once per frame in Update and never recompute it here, or the two values diverge.
                // Never stale while _pendingSteps > 0: _predictor exists only after join, join makes the
                // clock ready, and ClearMatchState nulls both together.
                _predictor.SetAim(_pendingSteps, aimPoint, _camera.Yaw, _camera.Pitch, (float)_renderTick);
                shots = StepWeapons(_pendingSteps);
                if (_predictor.TryBuildInputPacket(out PlayerInputPacket packet)) _net.SendInput(packet);
                _pendingSteps = 0;
            }

            if (alive && shots > 0) _fireEffects.FireLocal(shots, aimPoint, _predictor.RenderPosition, _camera.Yaw, now);
            _fireEffects.Tick(now);
            UpdateBuild(alive, LocalPlayerPredictor.ActionsAllowed(_predictor.Mode), now);
            UpdateBuildPresentation(alive, now);

            _hud.SetVitals(_health, _shield);
            // Phase 12 D14: the energy bar (hidden when full and not sprinting) and the hint line.
            float energy = _predictor.Energy / MovementTuning.MaxEnergy;
            bool onFoot = LocalPlayerPredictor.ActionsAllowed(_predictor.Mode);
            // Shown on foot and in the air alike (the energy recovers while falling, too).
            _hud.SetEnergy(energy, alive && (energy < 1f || _predictor.Sprinting), _predictor.Exhausted);
            int door = alive && onFoot ? DoorRule.FindTarget(_predictor.PredictedPosition.ToNumerics(), _predictor.RenderYaw, GameMap.Doors) : -1;
            _hud.SetHint(Hint(alive, door));
            if (_weapons != null && _weapons.HasWeapon) _hud.SetWeapon(_weapons.Current.Name, _weapons.Ammo, _weapons.Reserve, _weapons.Reloading);
            else _hud.ClearWeapon();
            _hud.Tick(_camera.Yaw, now);

            // D9, D12: E means the door first, and aboard, falling or vaulting it does nothing: no item prompt then.
            UpdateInventoryHud(alive, onFoot && door < 0, now);
            UpdateMatchHud(alive);
        }

        // 기능: 건설 조각 선택 키, 재질 변경 키, 건설 모드의 R(회전)을 처리한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 건설 선택 상태가 바뀌고, 필요하면 ToolBuild 버튼이 큐에 들어가거나 Reload 버튼이 큐에서 빠진다.
        // Phase 13 D16: piece keys select (and enter build mode), T cycles the material; in build mode R turns the piece
        // instead of reloading (the Reload press is taken back before it reaches an input).
        private void UpdateBuildKeys()
        {
            int piece = _input.PiecePressed;
            if (piece >= 0)
            {
                _build.Selection.Select((BuildPieceType)piece);
                if (_tools.Current != ToolKind.Build) _input.QueuedButtons |= InputButtons.ToolBuild;
            }
            if (_tools.Current != ToolKind.Build) return;
            if (_input.MaterialPressed) _build.Selection.NextMaterial();
            if ((_input.QueuedButtons & InputButtons.Reload) != 0)
            {
                _input.QueuedButtons &= ~InputButtons.Reload;
                _build.Selection.Rotate();
            }
        }

        // 기능: 이번 프레임 건설 모드의 후보 위치와 배치 요청을 BuildController로 갱신한다.
        // 입력: alive - 로컬 플레이어 생존 여부, onFoot - 행동할 수 있는 이동 모드인지, now - 현재 시각(Time.time).
        // 출력: 반환값 없음. 건설 후보 상태가 갱신되고, 버튼 입력이 있으면 건설 요청 Packet이 전송될 수 있다.
        // Phase 13 D16: build mode this frame: the candidate, its look, and a placement while the button is held.
        private void UpdateBuild(bool alive, bool onFoot, float now)
        {
            bool inBuildMode = alive && onFoot && _tools.Current == ToolKind.Build && !_blockedThisFrame;
            Vector3 feet = _predictor.PredictedPosition;
            var eye = feet.ToNumerics() + new System.Numerics.Vector3(0f, AimSolver.EyeHeightOf(_predictor.Mode), 0f);
            bool pressed = inBuildMode && _input.FirePressed && !_fireBlockedUntilRelease;
            _build.Update(now, inBuildMode, pressed, inBuildMode && _fireHeld, feet.ToNumerics(), eye, _camera.Yaw, _camera.Pitch, _buildStore);
        }

        // 기능: 확정 건설 조각 뷰, 미리보기, 채집 스윙 효과, 건설 HUD를 갱신한다.
        // 입력: alive - 로컬 플레이어 생존 여부, now - 현재 시각(Time.time).
        // 출력: 반환값 없음. 조각 뷰와 건설 HUD가 갱신되고 BuildStore의 변경 목록이 비워진다.
        // Phase 13 D16: the confirmed pieces (only those that changed, and those still being built), the ghosts, the swing
        // and harvest effects, and the HUD. The store's change list is taken here, every frame.
        private void UpdateBuildPresentation(bool alive, float now)
        {
            _pieceViews.Apply(_buildStore, _build.Catalog, EstimatedServerTick());
            _buildStore.ClearChanged();
            _buildPreview.Update(_build);
            bool onFoot = LocalPlayerPredictor.ActionsAllowed(_predictor.Mode);
            if (alive && onFoot && _tools.Current == ToolKind.Harvest && _fireHeld && !_blockedThisFrame && now >= _nextSwingAt)
            {
                float interval = _build.Catalog != null && _simHz > 0 ? _build.Catalog.HarvestCooldownTicks / (float)_simHz : 0.5f;
                _nextSwingAt = now + Mathf.Max(0.1f, interval);
                _harvestEffects.Swing(_predictor.RenderPosition, _camera.Yaw, now);
                _buildAudio.Play(BuildSound.Swing, _predictor.RenderPosition);
            }
            _harvestEffects.Tick(now);
            _buildHud.SetVisible(alive);
            _buildHud.SetResources(_build.ShownResource(BuildMaterialType.Wood), _build.ShownResource(BuildMaterialType.Stone),
                _build.ShownResource(BuildMaterialType.Metal));
            _buildHud.SetMode(alive && _tools.Current == ToolKind.Build, _build.Selection.Piece, _build.Selection.Material);
            _buildHud.Tick(now);
        }

        // 기능: 현재 서버 Tick을 렌더 Tick과 보간 지연으로 추정한다.
        // 입력: 없음.
        // 출력: 추정 서버 Tick. 준비된 Clock이 없으면 0.
        // The server tick now, estimated as the match HUD does (render tick plus the interpolation delay); 0 before a clock.
        private double EstimatedServerTick() =>
            _clock != null && _clock.IsReady && _simHz > 0 ? _renderTick + _interpolationDelaySeconds * _simHz : 0;

        // 기능: Development Build와 Editor에서 F1 오버레이에 건설 상태 줄(도구, 선택, 조각 수, 요청·거절 수)을 넘긴다.
        // 입력: overlay - 갱신할 디버그 오버레이, now - 호출한 쪽의 현재 시각(초).
        // 출력: 반환값 없음. 오버레이의 건설 줄이 갱신된다. 디버그 빌드가 아니면 아무것도 하지 않는다.
        // Phase 13 D16: the F1 build line (UiRoot owns the overlay). Request §190: Development Builds (and the Editor) only.
        public void TickBuildDebug(DebugOverlay overlay, float now)
        {
            if (!Debug.isDebugBuild) return;
            overlay.TickBuild(now, _tools.Current, _build.Selection.Piece, _build.Selection.Material, _buildStore.Count, _pieceViews.Count,
                _buildStore.Ignored, _build.Sent, _build.Refused, _build.LastRefusal);
        }

        // 기능: 이동 모드와 바라보는 문에 맞는 HUD 안내 문구를 고른다.
        // 입력: alive - 로컬 플레이어 생존 여부, door - 상호작용 대상 문 인덱스(없으면 음수).
        // 출력: 점프, 글라이더, 문 열기·닫기 안내 문자열. 해당 없으면 null.
        // Phase 12 D14: aboard (inside the jump window) "jump", in freefall "glider", next to a door "open"/"close".
        private string Hint(bool alive, int door)
        {
            if (!alive) return null;
            switch (_predictor.Mode)
            {
                case MovementMode.Transport:
                    if (!_hasRoute) return null;
                    _route.JumpWindow(out uint first, out uint last);
                    // The tick the next input is simulated at (before the first ack: the newest snapshot's).
                    uint tick = _predictor.HasTickBase ? _predictor.PredictedTick : _clock.LatestTick;
                    return tick >= first && tick < last ? UiText.HintJump : null;
                case MovementMode.Freefall:
                    return UiText.HintGlide;
            }
            if (door < 0) return null;
            return _doors.IsOpen(door) ? UiText.HintDoorClose : UiText.HintDoorOpen;
        }

        // 기능: 수송기에 탄 로컬 플레이어가 그려지는 서버 Tick을 구한다.
        // 입력: 없음.
        // 출력: 예측 Render Tick. 첫 ack 전이면 최신 Snapshot Tick.
        // The server tick our rider is drawn at: the predicted one, or before the first ack (the rider held at the server's
        // position) the newest snapshot's. Only called with a predictor and a clock.
        private double RiderTick() => _predictor.HasTickBase ? _predictor.RenderTick : _clock.LatestTick;

        // 기능: Match 상태 줄, 자기장 안내와 원, 자기장 밖 경고, 관전 대상 표시를 갱신한다.
        // 입력: alive - 로컬 플레이어 생존 여부.
        // 출력: 반환값 없음. Match HUD와 ZoneView가 갱신된다. Match나 준비된 Clock이 없으면 아무것도 하지 않는다.
        // D14: state line, zone line and circle, red edges outside the zone, spectating. Strings are rebuilt only on
        // change (MatchHudText). Times use the estimated current server tick: the render tick plus the interpolation
        // delay. Phase 11 D7: the result is the result screen's (UiRoot), no longer a HUD line.
        private void UpdateMatchHud(bool alive)
        {
            if (!_hasMatch || _clock == null || !_clock.IsReady || _simHz <= 0) return;
            double tick = _renderTick + _interpolationDelaySeconds * _simHz;

            _matchHud.SetStatus(_match.State, StateSecondsLeft, _match.Alive, _match.Participants, _match.MinPlayers);

            bool inMatch = _match.State == MatchFlowState.Playing || _match.State == MatchFlowState.FinalPhase;
            int zoneSeconds = 0;
            ZoneHint hint = inMatch ? ZoneMath.Hint(_zone, tick, _simHz, out zoneSeconds) : ZoneHint.None;
            _matchHud.SetZone(hint, zoneSeconds);
            Vector3 feet = _predictor.PredictedPosition;
            _matchHud.SetOutside(inMatch && alive && ZoneMath.IsOutside(_zone, feet.x, feet.z, tick));
            _zoneView.Tick(tick);

            ushort watched = _spectator.Active ? _spectator.Target : (ushort)0;
            _matchHud.SetSpectating(watched, NameOf(watched));
        }

        // 기능: 무기 슬롯, 회복 아이템 수, 회복 진행 막대, 아이템 줍기 안내를 갱신한다.
        // 입력: alive - 로컬 플레이어 생존 여부, prompt - 아이템 줍기 안내를 보일지, now - 현재 시각(Time.time).
        // 출력: 반환값 없음. Inventory HUD가 갱신된다. 무기·아이템 카탈로그가 없으면 알림 Tick만 진행된다.
        // D15: slots, heals, the heal bar and the "[E]" prompt. Strings are rebuilt only on change (InventoryHudText).
        // Phase 12: prompt false hides the item prompt only (the heal bar stays).
        private void UpdateInventoryHud(bool alive, bool prompt, float now)
        {
            _inventoryHud.Tick(now);
            if (_weapons == null || _itemCatalog == null) return;

            for (int i = 0; i < WeaponState.SlotCount; i++)
            {
                if (_weapons.TryGetSlot(i, out WeaponInfo weapon, out int rarity, out int ammo))
                    _inventoryHud.SetSlot(i, i == _weapons.Slot, weapon.Name, _itemCatalog.Rarities[rarity].Name, rarity, ammo, _weapons.GetReserve(weapon.AmmoType));
                else
                    _inventoryHud.SetSlot(i, i == _weapons.Slot, null, null, 0, 0, 0);
            }
            _inventoryHud.SetConsumables(_inventory.Medkits, _inventory.ShieldCells);

            bool channel = alive && _inventory.Using != ConsumableType.None && _useSeconds > 0f;
            _inventoryHud.SetUseProgress(channel ? 1f - Mathf.Clamp01((_useEndTime - now) / _useSeconds) : -1f);

            // Same rule as the server (PickupRule): the prompt names the item E will take.
            int target = alive && prompt ? PickupRule.FindNearest(_worldItems.Items, _predictor.PredictedPosition.ToNumerics()) : -1;
            if (target < 0)
            {
                _inventoryHud.SetPrompt(0, 0, null, null);
                return;
            }
            WorldItemData item = _worldItems.Items[target];
            switch (item.Kind)
            {
                case ItemKind.Weapon:
                    _inventoryHud.SetPrompt(item.ItemId, item.Amount, WeaponName(item.DefId), _itemCatalog.Rarities[item.Rarity].Name);
                    break;
                case ItemKind.Ammo:
                    _inventoryHud.SetPrompt(item.ItemId, item.Amount, _itemCatalog.Ammo[item.DefId - 1].Name, null);
                    break;
                default:
                    _inventoryHud.SetPrompt(item.ItemId, item.Amount, _itemCatalog.Consumables[item.DefId - 1].Name, null);
                    break;
            }
        }

        // 기능: 무기 카탈로그에서 무기 ID의 이름을 찾는다.
        // 입력: weaponId - 찾을 무기 ID.
        // 출력: 무기 이름. 카탈로그에 없으면 "?".
        private string WeaponName(byte weaponId)
        {
            for (int i = 0; i < _weaponCatalog.Length; i++)
            {
                if (_weaponCatalog[i].WeaponId == weaponId) return _weaponCatalog[i].Name;
            }
            return "?";
        }

        // 기능: Awake에서 구독한 NetClient 이벤트를 해제하고 생성한 연결, 뷰, HUD, Material, Mesh, 폰트를 해제한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 연결이 닫히고 Match 상태와 Client 리소스가 모두 정리된다.
        private void OnDestroy()
        {
            _net.Connected -= OnConnected;
            _net.Joined -= OnJoined;
            _net.SpawnReceived -= OnSpawned;
            _net.DespawnReceived -= OnDespawned;
            _net.SnapshotReceived -= OnSnapshot;
            _net.Disconnected -= OnDisconnected;
            _net.CatalogReceived -= OnCatalog;
            _net.ShotReceived -= OnShot;
            _net.HitConfirmedReceived -= OnHitConfirmed;
            _net.DamageTakenReceived -= OnDamageTaken;
            _net.PlayerDiedReceived -= OnPlayerDied;
            _net.PlayerRespawnedReceived -= OnPlayerRespawned;
            _net.InventoryReceived -= OnInventory;
            _net.ItemCatalogReceived -= OnItemCatalog;
            _net.ItemReceived -= OnItem;
            _net.ItemRemovedReceived -= OnItemRemoved;
            _net.PickupResultReceived -= OnPickupResult;
            _net.MatchStateReceived -= OnMatchState;
            _net.ZoneStateReceived -= OnZoneState;
            _net.MatchResultReceived -= OnMatchResult;
            _net.StatsReceived -= OnStats;
            _net.TransportRouteReceived -= OnTransportRoute;
            _net.DoorStatesReceived -= OnDoorStates;
            _net.HarvestStatesReceived -= OnHarvestStates;
            _net.BuildCatalogReceived -= OnBuildCatalog;
            _net.ResourcesReceived -= OnResources;
            _net.BuildResultReceived -= OnBuildResult;
            _net.BuildPieceReceived -= OnBuildPiece;
            _net.BuildHealthReceived -= OnBuildHealth;
            _net.BuildDestroyedReceived -= OnBuildDestroyed;
            _net.BuildResetReceived -= OnBuildReset;
            _net.BuildInterestReceived -= OnBuildInterest;
            _net.HarvestHitReceived -= OnHarvestHit;
            _net.Dispose();
            ClearMatchState();
            _killFeed.Dispose();
            _buildHud.Dispose();
            _harvestEffects.Dispose();
            _buildPreview.Dispose();
            _pieceViews.Dispose();
            _pieceMeshes.Dispose();
            _harvestables.Dispose();
            _doorViews.Dispose();
            _transportView.Dispose();
            _zoneView.Dispose();
            _matchHud.Dispose();
            _poiLabel.Dispose();
            _fireEffects.Dispose();
            _worldItems.Dispose();
            _inventoryHud.Dispose();
            _hud.Dispose();
            _crosshair.Dispose();
            _input.Dispose();
            PlayerViewFactory.ReleaseMaterials();
            if (_world != null) Destroy(_world);
            if (_worldMaterials != null)
            {
                foreach (Material material in _worldMaterials)
                {
                    if (material != null) Destroy(material);
                }
            }
            if (_terrainMesh != null) Destroy(_terrainMesh);
            // Last: every HUD that used the font is gone (UiRoot's screens go with this GameObject too).
            UiFont.Release();
        }

        // 기능: 커서 잠금·해제, 관전 대상 전환, 발사·조준 버튼 상태를 이번 프레임 기준으로 정한다.
        // 입력: 없음.
        // 출력: 반환값 없음. Cursor.lockState, _fireHeld, _aiming, _fireBlockedUntilRelease가 갱신되고, 관전 중 클릭이면 관전 대상이 바뀐다.
        // Left click locks a free cursor (only once joined) and fires while it is locked (D12).
        // Aim and fire only count while the cursor is locked, i.e. while the mouse controls the game.
        // Phase 5 D5: while spectating, a left click on a locked cursor moves to the next player and never fires;
        // the button must be released before it fires again (a click held into the next round does not shoot).
        // The click that locks the cursor neither fires nor cycles.
        // Phase 11 D5: Esc no longer unlocks here; it opens the menu (UiFlow), and with any screen up the cursor is
        // freed for its buttons and a click does not lock it.
        private void UpdateCursorAndButtons()
        {
            bool locked = Cursor.lockState == CursorLockMode.Locked;
            if (!_cursorLockAllowed)
            {
                if (locked) Cursor.lockState = CursorLockMode.None;
                locked = false;
            }
            else if (!locked && _input.FirePressed && State == ClientState.Joined)
            {
                Cursor.lockState = CursorLockMode.Locked;
                locked = true;
                _fireBlockedUntilRelease = true;
            }
            else if (locked && _input.FirePressed && _spectator.Active)
            {
                _spectator.Cycle();
                _fireBlockedUntilRelease = true;
            }

            if (!_input.FireHeld) _fireBlockedUntilRelease = false;
            _fireHeld = locked && !_inputBlocked && _input.FireHeld && !_fireBlockedUntilRelease && !_spectator.Active;
            _aiming = locked && !_inputBlocked && _input.AimHeld;
        }

        // 기능: 화면 조준선 Ray가 맞는 지점을 현재 무기 사거리 안에서 찾는다.
        // 입력: 없음.
        // 출력: Ray가 맞은 지점. 맞은 것이 없으면 사거리 끝 지점.
        // What the crosshair is on. Remote players have colliders on PlayerViewFactory.RemoteHitLayer, so the
        // point can be on a player; the server then shoots from our eye towards it (D2).
        private Vector3 FindAimPoint()
        {
            float range = _weapons != null && _weapons.HasWeapon ? _weapons.Current.Range : DefaultAimRange;
            Ray ray = _camera.AimRay;
            // Remote views moved in Update. Physics.autoSyncTransforms is off by default, so without this the
            // ray would test their colliders where the last physics step left them.
            Physics.SyncTransforms();
            return Physics.Raycast(ray, out RaycastHit hit, range, PlayerViewFactory.AimRaycastMask, QueryTriggerInteraction.Ignore)
                ? hit.point
                : ray.GetPoint(range);
        }

        // 기능: 이번 프레임의 새 입력들을 오래된 순서로 로컬 도구·무기 복사본에 적용한다.
        // 입력: steps - 이번 프레임에 예측한 입력 수.
        // 출력: 서버가 발사할 것으로 예상되는 입력 수. 무기 카탈로그가 없으면 도구만 진행하고 0.
        // Runs the local weapon copy over this frame's new inputs, oldest first. Returns how many of them
        // the server is expected to fire.
        private int StepWeapons(int steps)
        {
            int count = Math.Min(steps, LocalPlayerPredictor.HistorySize);
            if (_weapons == null)
            {
                // No catalog yet: the tool still follows the inputs.
                for (int i = count - 1; i >= 0; i--)
                {
                    uint s = _predictor.LastSeq - (uint)i;
                    _tools.Step(s, _predictor.InputAt(s).Buttons, _predictor.ActionsAllowedAt(s));
                }
                return 0;
            }
            uint newest = _predictor.LastSeq;
            int shots = 0;
            for (int i = count - 1; i >= 0; i--)
            {
                uint seq = newest - (uint)i;
                // Phase 12 D12: riding, falling, gliding or vaulting, the server takes no action from the input (it only
                // follows the held fire button). Phase 13 D5: the tool switches first, and only the weapons shoot.
                InputButtons buttons = _predictor.InputAt(seq).Buttons;
                bool acts = _predictor.ActionsAllowedAt(seq);
                ToolKind tool = _tools.Step(seq, buttons, acts);
                if (_weapons.Step(seq, buttons, acts && tool == ToolKind.Weapon)) shots++;
            }
            return shots;
        }

        // 기능: 예약된 자동 재접속 시각이 되면 다음 시도를 시작하고 그다음 슬롯을 예약한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 시도 횟수가 늘고 연결이 시작된다. 즉시 실패하면 재접속 주기가 끝난다.
        // Phase 10 D10: the next attempt starts in its slot. An attempt still connecting then is given up first (it had
        // its connect budget, about 1.5 s, so this is rare). Connect sets State to Connecting at once, or leaves it
        // Disconnected when it failed right away (LastError says why): then the cycle ends.
        private void UpdateReconnect()
        {
            if (!_reconnectPending || Time.unscaledTime < _reconnectAt) return;
            _reconnectPending = false;
            if (_net.State == ClientState.Connecting) _net.CancelConnect();
            else if (_net.State != ClientState.Disconnected) return;   // connected meanwhile

            _reconnectAttempt++;
            Debug.Log($"Reconnecting (attempt {_reconnectAttempt}/{DisconnectCodes.MaxReconnectAttempts})");
            _net.Connect(_host, _port, _devPlayerId, reconnectAttempt: true);
            if (_net.State == ClientState.Disconnected)
            {
                CancelReconnect();
                return;
            }
            if (_reconnectAttempt < DisconnectCodes.MaxReconnectAttempts) ArmReconnect(_reconnectAttempt + 1);
        }

        // 기능: 지정한 시도의 재접속 시각을 끊김을 본 시각(_dropAt) 기준으로 예약한다.
        // 입력: attempt - 예약할 시도 번호(1부터).
        // 출력: 반환값 없음. _reconnectPending과 _reconnectAt이 설정된다.
        private void ArmReconnect(int attempt)
        {
            _reconnectPending = true;
            _reconnectAt = _dropAt + DisconnectCodes.ReconnectOffsetSeconds(attempt);
        }

        // 기능: NetClient 연결 성공 이벤트를 처리한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 현재 연결이 established로 기록되고 다음 재접속 예약이 취소된다.
        private void OnConnected()
        {
            _established = true;
            _reconnectPending = false;   // this attempt got through: no next slot unless it drops again
        }

        // 기능: JoinMatchResponse Packet을 처리해 Join 성공 시 엔티티 ID, SimHz, 서버 시계, 건설·도구 상태를 초기화한다.
        // 입력: response - 서버의 Join 응답.
        // 출력: 반환값 없음. 성공(Ok, Resumed)이면 시도 횟수, MyEntityId, ServerClock이 설정되고, 거절이면 자동 재접속이 중단된다.
        private void OnJoined(JoinMatchResponse response)
        {
            // Phase 10 D2: Resumed is our own character back; everything else arrives as after a join.
            if (response.Result != JoinResult.Ok && response.Result != JoinResult.Resumed)
            {
                // The server refused this join (MatchFull) and closes the connection: trying again would be refused
                // the same way.
                Debug.LogWarning($"Join failed: {response.Result}");
                CancelReconnect();
                return;
            }
            _reconnectAttempt = 0;
            MyEntityId = response.MyEntityId;
            _simHz = response.SimHz;
            // Phase 13 D8: a new connection numbers its build requests from 1; the server resends the tool and the pieces.
            _build.Reset();
            _build.SimHz = response.SimHz;
            _tools.Reset();
            _interpolationDelaySeconds = InterpolationSnapshots / response.SnapshotHz;
            _clock = new ServerClock(response.SimHz);
            _clock.OnSnapshot(response.ServerTick, Time.unscaledTimeAsDouble);
            Debug.Log($"Joined as entity {response.MyEntityId} (SimHz {response.SimHz}, SnapshotHz {response.SnapshotHz})");
        }

        // 기능: 무기 카탈로그 Packet을 저장하고 로컬 무기 상태를 새로 만든다.
        // 입력: weapons - 서버 무기 목록.
        // 출력: 반환값 없음. _weaponCatalog와 새 WeaponState가 설정된다.
        private void OnCatalog(WeaponInfo[] weapons)
        {
            _weaponCatalog = weapons;
            _weapons = new WeaponState(weapons);
        }

        // 기능: 아이템 카탈로그 Packet을 저장한다.
        // 입력: items - 서버 아이템 카탈로그.
        // 출력: 반환값 없음. _itemCatalog가 교체된다.
        private void OnItemCatalog(ItemCatalogData items)
        {
            _itemCatalog = items;
        }

        // 기능: 월드 아이템 Packet을 받아 바닥 아이템 뷰를 추가하거나 갱신한다.
        // 입력: item - 월드 아이템 데이터.
        // 출력: 반환값 없음. WorldItemViews에 반영된다.
        private void OnItem(WorldItemData item)
        {
            _worldItems.Upsert(item);
        }

        // 기능: 월드 아이템 제거 Packet을 받아 해당 아이템 뷰를 지운다.
        // 입력: itemId - 제거된 아이템 ID.
        // 출력: 반환값 없음. WorldItemViews에서 제거된다.
        private void OnItemRemoved(ushort itemId)
        {
            _worldItems.Remove(itemId);
        }

        // 기능: 줍기 결과 Packet을 받아 실패 사유 알림을 띄운다.
        // 입력: result - 서버 줍기 결과.
        // 출력: 반환값 없음. 가방이 가득 찼거나 범위 안에 물건이 없으면 Inventory HUD 알림이 표시된다.
        // Feedback only (the inventory changes through InventoryState). Constant strings: no allocation.
        private void OnPickupResult(PickupResult result)
        {
            if (result.Result == PickupResultCode.Full) _inventoryHud.ShowNotice("가방이 가득 찼습니다", Time.time);
            else if (result.Result == PickupResultCode.NothingInRange) _inventoryHud.ShowNotice("주울 수 있는 물건이 없습니다", Time.time);
        }

        // 기능: InventoryState Packet을 저장하고 무기 상태와 회복 채널 시간을 갱신한다.
        // 입력: inventory - 서버 인벤토리 상태.
        // 출력: 반환값 없음. _inventory, 무기 슬롯·예비 탄약, 회복 진행 시간(_useSeconds, _useEndTime)이 갱신된다.
        // Phase 4: what sits in each slot, the reserves, heals and the heal channel (D14). The current slot
        // comes with snapshots. The channel is shown from the remaining ticks at arrival; the server decides it.
        private void OnInventory(InventoryState inventory)
        {
            _inventory = inventory;
            if (_weapons != null) _weapons.ApplyInventory(inventory);
            _useSeconds = 0f;
            if (inventory.Using != ConsumableType.None && _itemCatalog != null && _simHz > 0)
            {
                _useSeconds = (float)_itemCatalog.Consumables[(int)inventory.Using - 1].UseTicks / _simHz;
                _useEndTime = Time.time + (float)inventory.UseRemainingTicks / _simHz;
            }
        }

        // 기능: PlayerSpawned Packet을 처리해 이름을 기록하고, 자신이면 예측기와 로컬 뷰를, 다른 플레이어면 원격 플레이어를 만든다.
        // 입력: spawned - 스폰된 플레이어 정보.
        // 출력: 반환값 없음. 이름 사전과 로컬 예측기·HUD 또는 RemotePlayers가 갱신된다. 예측기가 이미 있으면 자신의 스폰은 무시된다.
        private void OnSpawned(PlayerSpawned spawned)
        {
            _names[spawned.EntityId] = spawned.Name;
            if (spawned.EntityId == MyEntityId)
            {
                if (_predictor != null) return;
                // Phase 12: the spawn carries no mode; a resumed player in the air gets it from the next snapshot.
                _predictor = new LocalPlayerPredictor(_simHz, new MoveState { Position = spawned.Position, Yaw = spawned.Yaw }, _doors);
                _predictor.DestroyedHarvestables = _destroyedHarvestables;
                _predictor.Pieces = _buildStore.Grid;   // Phase 13 D3: confirmed pieces only
                if (_hasRoute) _predictor.SetRoute(_route);
                // A key pressed while waiting for the spawn must not act on the first step, and it starts standing.
                _input.QueuedButtons = InputButtons.None;
                _input.ResetCrouch();
                _localView = PlayerViewFactory.Create($"Player {spawned.EntityId} (you)", true);
                _health = 0;
                _shield = 0;
                _hud.SetVisible(true);
                _inventoryHud.SetVisible(true);
                return;
            }
            _remotePlayers.Spawn(spawned, _clock != null ? _clock.LatestTick : 0);
        }

        // 기능: PlayerDespawned Packet을 처리해 원격 플레이어와 이름을 지운다.
        // 입력: entityId - 사라진 엔티티 ID.
        // 출력: 반환값 없음. RemotePlayers와 이름 사전에서 제거된다.
        private void OnDespawned(ushort entityId)
        {
            _remotePlayers.Despawn(entityId);
            _names.Remove(entityId);
        }

        // 기능: World Snapshot Packet을 처리해 서버 시계와 자기 체력·실드·무기·도구를 갱신하고, 로컬 예측을 보정하며 원격 플레이어 샘플을 쌓는다.
        // 입력: header - Snapshot 헤더(서버 Tick, 자기 상태, ack 입력 Seq), entities - 엔티티 배열, count - 유효한 엔티티 수.
        // 출력: 반환값 없음. Clock이 없으면(Join 전) 무시된다.
        private void OnSnapshot(in WorldSnapshotHeader header, SnapshotEntity[] entities, int count)
        {
            if (_clock == null) return;
            _clock.OnSnapshot(header.ServerTick, Time.unscaledTimeAsDouble);

            // D10: our own health, shield and weapon come with every snapshot. Phase 13 D5: and our tool.
            _health = header.Self.Health;
            _shield = header.Self.Shield;
            if (_weapons != null) _weapons.ApplyServer(header.Self, header.AckInputSeq);
            _tools.ApplyServer(header.Self.Tool, header.AckInputSeq);

            for (int i = 0; i < count; i++)
            {
                if (entities[i].EntityId == MyEntityId)
                {
                    if (_predictor != null) _predictor.Reconcile(entities[i], header.Self, header.AckInputSeq, header.ServerTick);
                }
                else
                {
                    _remotePlayers.Push(header.ServerTick, entities[i]);
                }
            }
        }

        // 기능: ShotFired Packet을 받아 다른 플레이어의 사격 궤적을 그린다.
        // 입력: shot - 사격 정보.
        // 출력: 반환값 없음. 원격 사격 효과가 표시된다. 자신의 사격은 무시된다.
        // D12: our own shots were already drawn when predicted.
        private void OnShot(ShotFired shot)
        {
            if (shot.ShooterId == MyEntityId) return;
            _fireEffects.ShowRemoteShot(shot.Start.ToUnity(), shot.End.ToUnity(), Time.time);
        }

        // 기능: HitConfirmed Packet을 받아 명중 표시를 띄운다.
        // 입력: hit - 명중 확인(처치 여부 포함).
        // 출력: 반환값 없음. HUD 명중 표시가 나타난다.
        private void OnHitConfirmed(HitConfirmed hit)
        {
            _hud.ShowHit(hit.Killed, Time.time);
        }

        // 기능: DamageTaken Packet을 받아 피격 방향 표시를 띄운다.
        // 입력: damage - 받은 피해 정보.
        // 출력: 반환값 없음. HUD 피격 방향 표시가 나타난다.
        private void OnDamageTaken(DamageTaken damage)
        {
            _hud.ShowDamage(damage.FromDirection.ToUnity(), Time.time);
        }

        // 기능: PlayerDied Packet을 처리해 킬피드 줄을 추가하고, 자신이 죽었으면 사망 정보를 기록하고 관전 또는 리스폰 카운트다운을 시작한다.
        // 입력: died - 사망 정보.
        // 출력: 반환값 없음. 킬피드, 사망 상태(_died 등), 예측기, 로컬 뷰, 관전 카메라 또는 HUD 사망 표시가 갱신된다. Join 시 관전 안내는 킬피드와 사망 기록에서 빠진다.
        private void OnPlayerDied(PlayerDied died)
        {
            // Phase 11 D10: every death but the "you are spectating" notice a newcomer gets at join (Match sends it with
            // no killer and no placement; a zone death in a match has a placement, and the dev sandbox has no zone). The
            // line is built once here, so the feed allocates per death, never per frame.
            // Phase 12 D10: a fall also has no killer and, in the dev sandbox, no placement: its cause tells it apart.
            bool notice = died.KillerId == 0 && died.Placement == 0 && died.Cause == DeathCause.Zone;
            string killer = died.KillerId == 0 ? null : UiText.NameOr(NameOf(died.KillerId), died.KillerId);
            if (!notice) _killFeed.Add(UiText.KillLine(killer, UiText.NameOr(NameOf(died.VictimId), died.VictimId), died.Cause), Time.unscaledTime);

            if (died.VictimId != MyEntityId || _predictor == null) return;
            if (!notice)
            {
                _died = true;
                _killedByZone = died.KillerId == 0;
                _deathCause = died.Cause;
                _killerName = killer;
            }
            _predictor.SetDead();
            _input.ResetCrouch();
            _localView.SetAlive(false);
            // D4, D5: in a match death is permanent, so no respawn countdown: watch the killer instead. A newcomer
            // during a match is told the same way (KillerId 0). The dev sandbox keeps the Phase 3 countdown.
            if (_hasMatch)
            {
                _spectator.Begin(died.KillerId);
                // A fire button held through the death must not shoot when the next round respawns us: it has to
                // be released first (a held button never makes FirePressed, so the cycle branch cannot block it).
                _fireBlockedUntilRelease = true;
            }
            else _hud.ShowDeath(Time.time);
        }

        // 기능: PlayerRespawned Packet을 처리해 다른 플레이어는 순간이동시키고, 자신이면 예측기를 새 위치로 리스폰하고 사망·관전 상태를 초기화한다.
        // 입력: respawned - 리스폰 정보.
        // 출력: 반환값 없음. 원격 플레이어 위치 또는 로컬 예측기, 입력, 도구, 무기, 관전, 사망 표시가 갱신된다.
        private void OnPlayerRespawned(PlayerRespawned respawned)
        {
            // Other players' alive state follows their snapshot flags (RemotePlayers.Push). Their teleport is told
            // here too, because the match start and the round reset do not flip the flag (alive -> alive).
            if (respawned.EntityId != MyEntityId)
            {
                _remotePlayers.Teleport(respawned.EntityId, respawned.Position.ToUnity());
                return;
            }
            if (_predictor == null) return;
            // Also the match start and the round reset (Phase 5 D3, D13): the same teleport, Seq continues. Phase 12: aboard
            // the drop transport at a match start.
            _predictor.Respawn(new MoveState { Position = respawned.Position, Yaw = respawned.Yaw, Mode = respawned.Mode });
            _input.QueuedButtons = InputButtons.None;
            _input.ResetCrouch();
            _tools.Reset();   // Phase 13: a new life starts with the weapons out
            _spectator.End();
            _died = false;
            _killedByZone = false;
            _deathCause = DeathCause.Zone;
            _killerName = null;
            // Empty until the server's InventoryState for the new life arrives (sent right after this event).
            if (_weapons != null) _weapons.Clear();
            _localView.SetAlive(true);
            _hud.HideDeath();
        }

        // 기능: MatchState Packet을 저장하고 처음 받으면 Match HUD를 보인다.
        // 입력: state - 서버 Match 상태.
        // 출력: 반환값 없음. _match가 갱신되고, 대기·시작 카운트다운 상태면 이전 결과와 수송기 경로가 지워진다.
        private void OnMatchState(MatchState state)
        {
            _match = state;
            if (!_hasMatch)
            {
                _hasMatch = true;
                _matchHud.SetVisible(true);
            }
            // The result stays up during Finished and goes with the next round's countdown. Phase 12: so does the last
            // round's transport route (the next one comes with the next match start, after this state).
            if (state.State == MatchFlowState.WaitingForPlayers || state.State == MatchFlowState.Starting)
            {
                _hasResult = false;
                ClearRoute();
            }
        }

        // 기능: ZoneState Packet을 저장하고 자기장 뷰에 반영한다.
        // 입력: zone - 서버 자기장 상태.
        // 출력: 반환값 없음. _zone과 ZoneView가 갱신된다.
        private void OnZoneState(ZoneState zone)
        {
            _zone = zone;
            _zoneView.SetZone(zone);
        }

        // 기능: MatchResult Packet을 저장하고 결과 수를 늘린다.
        // 입력: result - 이번 Match 결과.
        // 출력: 반환값 없음. _result, _hasResult, ResultCount가 갱신된다.
        // Only participants get one (D11).
        private void OnMatchResult(MatchResult result)
        {
            _result = result;
            _hasResult = true;
            ResultCount++;
        }

        // 기능: TransportRoute Packet의 수송기 경로를 저장하고 예측기와 수송기 뷰에 적용한다.
        // 입력: route - 이번 Match의 수송기 경로.
        // 출력: 반환값 없음. _route가 설정되고 예측기와 TransportView에 반영된다.
        // Phase 12 D5: sent before the match start's respawns (and at a join or resume during the match).
        private void OnTransportRoute(DropRoute route)
        {
            _route = route;
            _hasRoute = true;
            if (_predictor != null) _predictor.SetRoute(route);
            _transportView.SetRoute(route);
        }

        // 기능: 저장된 수송기 경로를 지운다.
        // 입력: 없음.
        // 출력: 반환값 없음. 예측기 경로와 수송기 뷰가 정리된다. 경로가 없으면 아무것도 하지 않는다.
        private void ClearRoute()
        {
            if (!_hasRoute) return;
            _hasRoute = false;
            if (_predictor != null) _predictor.ClearRoute();
            _transportView.Clear();
        }

        // 기능: DoorStates Packet의 문 열림 비트를 예측 문 상태에 적용한다.
        // 입력: openMask - 문별 열림 비트 마스크.
        // 출력: 반환값 없음. PredictedDoors가 서버 상태로 갱신된다.
        private void OnDoorStates(byte openMask)
        {
            _doors.ApplyServer(openMask);
        }

        // 기능: BuildCatalog Packet을 저장하고 관심 영역 한 칸의 건설 셀 수를 계산한다.
        // 입력: catalog - 서버 건설 카탈로그.
        // 출력: 반환값 없음. BuildController 카탈로그와 BuildStore.CellsPerInterest가 설정된다.
        // Phase 13 D4: what the client needs of the building numbers.
        private void OnBuildCatalog(BuildCatalogData catalog)
        {
            _build.Catalog = catalog;
            _buildStore.CellsPerInterest = Mathf.Max(1, Mathf.RoundToInt(catalog.InterestCellSize / BuildGrid.CellSize));
        }

        // 기능: ResourcesState Packet의 건설 재료 수량을 BuildController에 넘긴다.
        // 입력: resources - 서버 재료 수량.
        // 출력: 반환값 없음. BuildController.Resources가 교체된다.
        private void OnResources(ResourcesState resources) => _build.Resources = resources;

        // 기능: BuildResult Packet을 처리해 대기 중인 배치를 정리하고 성공·거절 소리와 거절 알림을 낸다.
        // 입력: result - 건설 요청 결과.
        // 출력: 반환값 없음. 대기 배치가 정리되고 거절이면 건설 HUD 알림이 표시된다.
        private void OnBuildResult(BuildResult result)
        {
            _build.OnResult(result);
            Vector3 at = _predictor != null ? _predictor.RenderPosition : Vector3.zero;
            if (result.Code == BuildResultCode.Ok)
            {
                _buildAudio.Play(BuildSound.Placed, at);
                return;
            }
            _buildHud.ShowNotice(UiText.BuildRefusal(result.Code), Time.time);
            _buildAudio.Play(BuildSound.Refused, at);
        }

        // 기능: 자신의 HarvestHit Packet을 받아 약점 표시, 파괴 효과, 소리를 낸다.
        // 입력: hit - 채집 타격 결과.
        // 출력: 반환값 없음. 채집 효과와 소리가 재생된다.
        // Phase 13 D7: our own harvest hit: the weak point marker, a fall's puff, the sounds.
        private void OnHarvestHit(HarvestHit hit)
        {
            _harvestEffects.OnHit(hit, Time.time);
            Vector3 at = hit.WeakPoint.ToUnity();
            _buildAudio.Play(hit.Destroyed ? BuildSound.HarvestDestroyed : hit.WeakPointHit ? BuildSound.WeakPointHit : BuildSound.HarvestHit, at);
        }

        // 기능: BuildPiece Packet의 조각을 BuildStore에 추가하거나 갱신한다.
        // 입력: piece - 조각 기록, version - 건설 스트림 버전.
        // 출력: 반환값 없음. BuildStore에 반영된다(관심 영역 밖이거나 가득 차면 무시).
        // Phase 13 D13, D14: the building stream, applied by id (BuildStore).
        private void OnBuildPiece(BuildPieceRecord piece, uint version) => _buildStore.ApplyPiece(piece, version);

        // 기능: BuildHealth Packet의 조각 피해 값을 BuildStore에 반영한다.
        // 입력: id - 조각 ID, damage - 서버가 보낸 피해 값, version - 건설 스트림 버전.
        // 출력: 반환값 없음. 해당 조각의 피해 값이 갱신된다(모르는 조각이면 무시).
        private void OnBuildHealth(uint id, ushort damage, uint version) => _buildStore.ApplyHealth(id, damage, version);

        // 기능: BuildDestroyed Packet을 처리해 파괴된 조각 자리에 먼지 효과와 소리를 내고 BuildStore에서 지운다.
        // 입력: id - 파괴된 조각 ID, version - 건설 스트림 버전.
        // 출력: 반환값 없음. 조각이 BuildStore에서 제거된다.
        private void OnBuildDestroyed(uint id, uint version)
        {
            // Destroyed (not just out of the window): a puff where it was drawn.
            if (_pieceViews.TryGetCenter(id, out Vector3 center))
            {
                _harvestEffects.Puff(center, BuildGrid.CellSize * 0.6f, Time.time);
                _buildAudio.Play(BuildSound.PieceDestroyed, center);
            }
            _buildStore.ApplyDestroyed(id, version);
        }

        // 기능: BuildReset Packet을 받아 BuildStore를 비운다.
        // 입력: version - 건설 스트림 버전(사용하지 않음).
        // 출력: 반환값 없음. 모든 확정 조각이 지워진다.
        private void OnBuildReset(uint version) => _buildStore.Reset();

        // 기능: BuildInterest Packet의 관심 셀 마스크를 BuildStore에 적용한다.
        // 입력: cells - 관심 영역 셀 비트 마스크.
        // 출력: 반환값 없음. 관심 영역 밖 조각이 BuildStore에서 제거된다.
        private void OnBuildInterest(ulong cells) => _buildStore.ApplyInterest(cells);

        // 기능: HarvestStates Packet의 파괴된 채집 대상 비트를 화면, 효과, 예측 충돌에 적용한다.
        // 입력: destroyed - 파괴된 채집 대상 비트 마스크.
        // 출력: 반환값 없음. 채집 대상 뷰, 채집 효과, 예측기 충돌 대상이 갱신된다.
        // Phase 13 D6: a destroyed harvestable leaves the screen and the predicted collision at once.
        private void OnHarvestStates(ulong destroyed)
        {
            _destroyedHarvestables = destroyed;
            _harvestables.Apply(destroyed);
            _harvestEffects.OnStates(destroyed);
            if (_predictor != null) _predictor.DestroyedHarvestables = destroyed;
        }

        // 기능: StatsResponse Packet을 저장하고 응답 시각을 기록한다.
        // 입력: response - 서버 전적 응답.
        // 출력: 반환값 없음. _stats와 _statsAnsweredAt이 갱신된다.
        private void OnStats(StatsResponse response)
        {
            _stats = response;
            _statsAnsweredAt = Time.unscaledTime;
        }

        // 기능: 연결 종료 이벤트를 처리해 Match 상태를 비우고 커서를 풀고 필요하면 자동 재접속을 예약한다.
        // 입력: reason - 연결 종료 이유.
        // 출력: 반환값 없음. MyEntityId와 Match 상태가 초기화되고 재접속이 예약되거나 끝난다.
        private void OnDisconnected(string reason)
        {
            MyEntityId = 0;
            ClearMatchState();
            Cursor.lockState = CursorLockMode.None;
            Debug.Log($"Disconnected: {reason}");
            ScheduleReconnect();
        }

        // 기능: 연결 종료 뒤 재시도 가능 여부를 판단해 다음 자동 재접속을 예약하거나 주기를 끝낸다.
        // 입력: 없음.
        // 출력: 반환값 없음. established 표시가 지워지고 _dropAt과 재접속 예약이 설정되거나 취소된다.
        // Phase 10 D10: retry only what DisconnectCodes allows (a lost connection, a match reset), only for a connection
        // that was established or an attempt of a running cycle, and at most MaxReconnectAttempts times until a join.
        // A new cycle remembers when the drop was seen; every slot is measured from it. A failed attempt whose
        // successor's slot is already armed just waits for it.
        private void ScheduleReconnect()
        {
            bool cycle = _established || _reconnectAttempt > 0;
            _established = false;
            // Checked first: a reject or a non-retryable close ends the cycle even with a slot armed.
            if (!_net.LastDisconnectRetryable || !cycle || _host == null)
            {
                CancelReconnect();
                return;
            }
            if (_reconnectAttempt == 0) _dropAt = Time.unscaledTime;
            if (_reconnectPending) return;
            if (_reconnectAttempt >= DisconnectCodes.MaxReconnectAttempts)
            {
                CancelReconnect();
                return;
            }
            ArmReconnect(_reconnectAttempt + 1);
            Debug.Log($"Reconnecting in {Mathf.Max(0f, _reconnectAt - Time.unscaledTime):F1} s (attempt {_reconnectAttempt + 1}/{DisconnectCodes.MaxReconnectAttempts})");
        }

        // 기능: 연결·Match에 묶인 Client 상태와 뷰를 모두 초기 상태로 되돌린다.
        // 입력: 없음.
        // 출력: 반환값 없음. 예측기, 원격 플레이어, 시계, 카탈로그, 인벤토리, 건설, 문, 경로, 이름, 전적이 비워지고 HUD가 숨겨진다.
        private void ClearMatchState()
        {
            _predictor = null;
            _localView?.Destroy();
            _localView = null;
            _remotePlayers.Clear();
            _clock = null;
            _weapons = null;
            _weaponCatalog = null;
            _itemCatalog = null;
            _inventory = default;
            _useSeconds = 0f;
            _worldItems.Clear();
            _pendingSteps = 0;
            _renderTick = 0;
            _hasMatch = false;
            _match = default;
            _zone = default;
            _hasResult = false;
            _names.Clear();
            _killFeed.Clear();
            _died = false;
            _killedByZone = false;
            _deathCause = DeathCause.Zone;
            _killerName = null;
            _stats = null;
            _statsSentAt = -1f;
            _statsAnsweredAt = -1f;
            _doors.Reset();
            _destroyedHarvestables = 0;
            _harvestables.Apply(0);
            _buildStore.Reset();
            _buildStore.ClearChanged();
            _pieceViews.Clear();
            _buildPreview.HideAll();
            _harvestEffects.HideAll();
            _buildHud.SetVisible(false);
            _build.Reset();
            _tools.Reset();
            _nextSwingAt = 0f;
            _hasRoute = false;
            _transportView.Clear();
            _spectator.End();
            _zoneView.Clear();
            _matchHud.SetOutside(false);
            _matchHud.SetSpectating(0, null);
            _matchHud.SetVisible(false);
            _poiLabel.SetVisible(false);
            _crosshair.SetVisible(false);
            _hud.HideDeath();
            _hud.SetVisible(false);
            _inventoryHud.SetUseProgress(-1f);
            _inventoryHud.SetPrompt(0, 0, null, null);
            _inventoryHud.SetVisible(false);
            _fireEffects.HideAll();
        }
    }
}
