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
    // (which also runs on application quit), in reverse order of creation.
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
        // Phase 13.5 D4, D10: the request numbering and send cap placements and edits share, and edit mode (H).
        private readonly BuildRequestCounter _buildCounter = new BuildRequestCounter();
        private BuildEditController _edit;
        private BuildEditOverlay _editOverlay;
        // The newest refusal of either kind (F1).
        private BuildResultCode _lastBuildRefusal;
        // Phase 13 D16: building and harvesting on screen.
        private PieceMeshes _pieceMeshes;
        private BuildPieceViews _pieceViews;
        private BuildPreview _buildPreview;
        private HarvestEffects _harvestEffects;
        private BuildHud _buildHud;
        private readonly BuildAudio _buildAudio = new BuildAudio();
        private Material _buildSource;
        private float _nextSwingAt;
        // Phase 14 D2, D8, D10, D14: our team and its channels (cleared with the match state and at a new round's countdown),
        // the squad HUD, the teammate markers, the reboot station pillars and the newest station cooldowns. _downedFeet is
        // the fixed buffer of downed teammates the revive hint looks at (refilled every frame).
        private readonly SquadState _squad = new SquadState();
        private SquadHud _squadHud;
        private TeammateMarkers _markers;
        private RebootStationViews _stationViews;
        private RebootStationsState _stations;
        private readonly System.Numerics.Vector3[] _downedFeet = new System.Numerics.Vector3[SquadConstants.MaxTeamSize];
        // The largest team size seen this round (a member who leaves may drop out of TeamState; the result still counts
        // teams). Reset with the team.
        private int _roundTeamSize;
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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // QA-5 D29: the -qaRecord input timeline (null without it). Created in Awake, disposed in OnDestroy.
        private Qa.QaInputRecorder _qaRecorder;
#endif
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
        // Phase 10 D10: 0 = no automatic reconnect running; otherwise the attempt (1..MaxReconnectAttempts) that is
        // connecting, or the next one while it waits for its slot.
        public int ReconnectAttempt =>
            _reconnectPending && _net.State == ClientState.Disconnected ? _reconnectAttempt + 1 : _reconnectAttempt;

        // ---- Phase 11: read-only state for the UI (UiRoot draws it; nothing here changes the game) ----

        // D6: why the last connection ended.
        public DisconnectSummary LastDisconnect => new DisconnectSummary
        {
            StartFailed = _net.LastConnectStartFailed,
            Reason = _net.LastDisconnectReason,
            Code = _net.LastDisconnectCode,
            Reject = _net.LastRejectReason,
            Join = _net.LastJoinResult,
        };

        // D6: seconds until the next automatic attempt starts; 0 while an attempt is connecting or none is running.
        public float NextReconnectIn =>
            _reconnectPending && _net.State == ClientState.Disconnected ? Mathf.Max(0f, _reconnectAt - Time.unscaledTime) : 0f;

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
        // Phase 14 D6: our team had more than one member this round (the result counts teams, not players).
        public bool SquadMatch => _roundTeamSize > 1;

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

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // QA-4 D28: read-only values for QaCommandReceiver's /qa/status (main thread). The id is the one the last connect
        // used (null before the first).
        public string QaDevPlayerId => _devPlayerId;
        public bool QaAlive => _predictor != null && !_predictor.IsDead;
        public int QaHealth => _health;

        // QA gameplay input (POST /qa/input): set by QaCommandReceiver while it runs. A QA-launched player is often not
        // focused, so its real cursor never locks; with this set the game (input, look via ShoulderCamera.ApplyLook,
        // fire, aim) treats the cursor as locked whenever a click could lock it (_cursorLockAllowed), so screens and
        // menus still block input exactly as before. A click in a focused window still locks the real cursor too
        // (UpdateCursorAndButtons), so a person playing in the Editor with PROJECTH_QA_PORT set keeps the mouse.
        public bool QaAssumeCursorLocked { get; set; }
        // The predicted tool once joined (null = "none"), and build mode's candidate look (null = "none": not in build
        // mode or no candidate this frame; HasCandidate is false outside build mode, see BuildController.Update).
        public ToolKind? QaTool => State == ClientState.Joined ? _tools.Current : (ToolKind?)null;
        public BuildPreviewState? QaPreview =>
            State == ClientState.Joined && _tools.Current == ToolKind.Build && _build.HasCandidate ? _build.CandidateState : (BuildPreviewState?)null;
        // The cursor lock as the game sees it this frame (QA assumption included).
        public bool QaCursorLocked => CursorLocked;
#endif

        // 기능: 게임 입력 기준으로 커서가 잠겨 있는지 알려 준다.
        // 입력: 없음.
        // 출력: 실제 커서가 잠겨 있으면 true. Editor·Development Build에서는 QA 가정(QaAssumeCursorLocked)이 켜져 있고 클릭으로
        //       잠글 수 있는 화면(_cursorLockAllowed)이어도 true. 그 밖에는 false. Release에서는 실제 잠금 비교 그대로다.
        private bool CursorLocked
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (QaAssumeCursorLocked && _cursorLockAllowed) return true;
#endif
                return Cursor.lockState == CursorLockMode.Locked;
            }
        }

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
        // Phase 13.5 D10: in edit mode Esc only cancels the edit (LateUpdate), so the menu does not see it. Every Update runs
        // before every LateUpdate, so UiRoot reads this before the edit can end in the same frame.
        public bool EscapePressed => _input.EscapePressed && !_edit.Active;
        public bool DebugTogglePressed => _input.DebugTogglePressed;

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

        public void Disconnect()
        {
            CancelReconnect();
            _net.Disconnect();
        }

        private void CancelReconnect()
        {
            _reconnectAttempt = 0;
            _reconnectPending = false;
        }

        // 기능: 월드·입력·카메라·HUD·건설 표시(Phase 13.5: 편집 오버레이)·분대 표시(Phase 14: 분대 HUD, 팀원 표지, 스테이션 기둥)·
        //   네트워크를 만들고 네트워크 이벤트를 구독한다.
        // 입력: 없음(Unity가 한 번 부른다).
        // 출력: 반환값 없음. 만든 것은 모두 OnDestroy가 해제한다. 배치와 편집은 순번 카운터 하나를 같이 쓴다.
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
            _editOverlay = new BuildEditOverlay(_pieceMeshes, _buildSource);
            _harvestEffects = new HarvestEffects(_buildSource);
            _buildHud = new BuildHud();
            _squadHud = new SquadHud();
            _markers = new TeammateMarkers(_buildSource);
            _stationViews = new RebootStationViews(_buildSource);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _qaRecorder = Qa.QaInputRecorder.FromLaunch();
#endif
            _net = new NetClient();
            _build = new BuildController(request => _net.SendBuild(request), _buildCounter);
            _edit = new BuildEditController(request => _net.SendBuildEdit(request), _buildCounter);
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
            _net.BuildEditedReceived += OnBuildEdited;
            _net.BuildDestroyedReceived += OnBuildDestroyed;
            _net.BuildResetReceived += OnBuildReset;
            _net.BuildInterestReceived += OnBuildInterest;
            _net.HarvestHitReceived += OnHarvestHit;
            _net.TeamStateReceived += OnTeamState;
            _net.PlayerDownedReceived += OnPlayerDowned;
            _net.ChannelStateReceived += OnChannelState;
            _net.RebootStationsReceived += OnRebootStations;
        }

        // 기능: 한 프레임의 Client 처리: 네트워크 Poll, 재접속, 입력·커서, 원격 플레이어 렌더, 시점과 이동 예측, 로컬 뷰 배치.
        //   Phase 14 D7: E가 눌려 있으면 이번 프레임의 모든 입력에 InteractHeld를 켠다.
        // 입력: 없음(Unity가 매 프레임 부른다).
        // 출력: 반환값 없음. 예측 상태와 보낼 입력 Step 수(_pendingSteps)가 갱신된다. 커서 잠금은 CursorLocked(QA 가정 포함)로 본다.
        private void Update()
        {
            _net.Poll();
            UpdateReconnect();
            // Phase 12: the same block as below (last frame's UI control, this frame's cursor) for the crouch toggle.
            _input.Update(_inputBlocked || !CursorLocked);
            UpdateCursorAndButtons();

            if (_clock != null && _clock.IsReady)
            {
                // The only place _renderTick is set: LateUpdate sends this exact value as ViewTick (see SetAim).
                _renderTick = _clock.RenderTick(Time.unscaledTimeAsDouble, _interpolationDelaySeconds);
                _remotePlayers.Render(_renderTick);
            }

            if (_predictor == null) return;

            // Phase 11 D5: with a screen up or the cursor free, no look, move, sprint or fire, and keys pressed meanwhile
            // are dropped. The predictor still steps, so empty inputs keep going out (the Phase 10 input timeout).
            bool blocked = _inputBlocked || !CursorLocked;
            _blockedThisFrame = blocked;
            _camera.ApplyLook(blocked ? Vector2.zero : _input.LookDelta, _aiming);
            InputButtons held = InputButtons.None;
            if (!blocked && _input.Sprint) held |= InputButtons.Sprint;
            if (!blocked && _input.CrouchHeld) held |= InputButtons.Crouch;   // Phase 12 D7
            if (!blocked && _input.InteractHeld) held |= InputButtons.InteractHeld;   // Phase 14 D7
            if (_fireHeld) held |= InputButtons.Fire;
            held = _edit.MaskHeld(held);   // Phase 13.5 D10: no shot while the left button picks edit tiles
            if (blocked) _input.QueuedButtons = InputButtons.None;
            if (!blocked) UpdateBuildKeys();
            InputButtons queued = _input.QueuedButtons;
            _pendingSteps += _predictor.Advance(Time.deltaTime, blocked ? Vector2.zero : _input.Move, _camera.Yaw, held, ref queued);
            _input.QueuedButtons = queued;

            _localView.Place(_predictor.RenderPosition, _predictor.RenderYaw, _predictor.Mode, _predictor.Sprinting);
        }

        // 기능: 카메라가 움직인 뒤의 프레임 처리: 조준점(과 맞힌 Collider), 입력 전송, 사격 효과, 편집 모드(Phase 13.5), 건설, 표시, HUD.
        //   Phase 14: 분대 관전, 분대 HUD·표지·기절 막대·진행 막대, 소생·재투입 안내(범위 안이면 문·줍기 안내보다 먼저, D7).
        // 입력: 없음(Unity가 매 프레임 부른다).
        // 출력: 반환값 없음. 예측 입력이 전송되고 화면이 갱신된다.
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
            _spectator.Update(_remotePlayers, _squad);
            bool watching = _spectator.TryGetFeet(_remotePlayers, _renderTick, out Vector3 watched);
            Vector3 followFeet = watching ? watched : _predictor.RenderPosition;
            // Phase 12 D14: the camera eases to our own mode (a watched player gets the standard camera).
            MovementMode mode = watching || !alive ? MovementMode.Ground : _predictor.Mode;
            _camera.Follow(followFeet, _aiming, Time.deltaTime, mode, !watching && alive && _predictor.Sprinting);
            // Phase 6 D7: the place name of whoever the camera follows. Text changes only when the place does.
            _poiLabel.SetVisible(true);
            _poiLabel.SetPosition(followFeet);
            // Phase 14 D4: downed, nothing can be aimed or fired.
            _crosshair.SetVisible(alive && !_blockedThisFrame && _predictor.Mode != MovementMode.Downed);

            // After the camera moved, so aim, tracer and the sent inputs all use the crosshair of this frame.
            Vector3 aimPoint = FindAimPoint(out Collider aimCollider);
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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // QA-5 D29: the steps exactly as sent (aim filled in by SetAim above).
                _qaRecorder?.Record(_predictor, _pendingSteps, _simHz, _devPlayerId);
#endif
                if (_predictor.TryBuildInputPacket(out PlayerInputPacket packet)) _net.SendInput(packet);
                _pendingSteps = 0;
            }

            if (alive && shots > 0) _fireEffects.FireLocal(shots, aimPoint, _predictor.RenderPosition, _camera.Yaw, now);
            _fireEffects.Tick(now);
            UpdateEdit(alive, LocalPlayerPredictor.ActionsAllowed(_predictor.Mode), now, aimCollider);
            UpdateBuild(alive, LocalPlayerPredictor.ActionsAllowed(_predictor.Mode), now);
            UpdateBuildPresentation(alive, now);

            _hud.SetVitals(_health, _shield);
            // Phase 12 D14: the energy bar (hidden when full and not sprinting) and the hint line.
            float energy = _predictor.Energy / MovementTuning.MaxEnergy;
            bool onFoot = LocalPlayerPredictor.ActionsAllowed(_predictor.Mode);
            // Shown on foot and in the air alike (the energy recovers while falling, too).
            _hud.SetEnergy(energy, alive && (energy < 1f || _predictor.Sprinting), _predictor.Exhausted);
            // Phase 14 D7: a revive or reboot in range (or under way) comes before the door and the item prompt, like the server.
            string squadHint = UpdateSquad(alive, out bool squadTarget);
            int door = alive && onFoot && !squadTarget ? DoorRule.FindTarget(_predictor.PredictedPosition.ToNumerics(), _predictor.RenderYaw, GameMap.Doors) : -1;
            _hud.SetHint(squadHint ?? Hint(alive, door));
            if (_weapons != null && _weapons.HasWeapon) _hud.SetWeapon(_weapons.Current.Name, _weapons.Ammo, _weapons.Reserve, _weapons.Reloading);
            else _hud.ClearWeapon();
            _hud.Tick(_camera.Yaw, now);

            // D9, D12: E means the door first, and aboard, falling or vaulting it does nothing: no item prompt then.
            UpdateInventoryHud(alive, onFoot && door < 0 && !squadTarget, now);
            UpdateMatchHud(alive);
        }

        // 기능: 건설 키를 처리한다(Update, 입력이 막히지 않았을 때). 조각 키는 고르고 건설 모드로 들어가며, T는 재료를 바꾸고,
        //   건설 모드의 R은 재장전 대신 회전한다(Reload 누름을 입력에 들어가기 전에 되돌린다).
        //   Phase 13.5 D10: 무기 키·Q·F(또는 건설 모드로 들어가는 조각 키)가 대기 중이면 편집 모드를 취소한다. 그 키는 그대로 서버로 가서 도구를 바꾼다.
        // 입력: 없음(InputReader의 이번 프레임 키와 대기 버튼).
        // 출력: 반환값 없음. 선택·대기 버튼·편집 모드가 바뀔 수 있다.
        private void UpdateBuildKeys()
        {
            int piece = _input.PiecePressed;
            if (piece >= 0)
            {
                _build.Selection.Select((BuildPieceType)piece);
                if (_tools.Current != ToolKind.Build) _input.QueuedButtons |= InputButtons.ToolBuild;
            }
            const InputButtons cancelsEdit = InputButtons.Slot1 | InputButtons.Slot2 | InputButtons.Slot3 | InputButtons.ToolBuild | InputButtons.ToolHarvest;
            if ((_input.QueuedButtons & cancelsEdit) != 0) _edit.Cancel();
            if (_tools.Current != ToolKind.Build) return;
            if (_input.MaterialPressed) _build.Selection.NextMaterial();
            if ((_input.QueuedButtons & InputButtons.Reload) != 0)
            {
                _input.QueuedButtons &= ~InputButtons.Reload;
                _build.Selection.Rotate();
            }
        }

        // 기능: 편집 모드 한 프레임(Phase 13.5 D10, D11): H로 조준한 내 조각의 편집을 시작하거나 확정하고, Esc로 취소, 오른쪽 클릭으로
        //   Reset을 보내며, 왼쪽 버튼으로 칸을 고른다. 편집이 끝날 때 왼쪽 버튼이 눌려 있으면 떼기 전까지 쏘지 않게 막는다.
        //   보낸 지 오래된 편집 예측도 여기서 지운다.
        // 입력: alive - 살아 있음, onFoot - 행동 가능 모드, now - 현재 시각, aimCollider - 이번 프레임 조준 Raycast가 맞힌 Collider(null 가능).
        // 출력: 반환값 없음. 편집 모드·선택·예측이 바뀌고, 요청이 나갈 수 있다. 할당 없음(안내 문구는 상수).
        private void UpdateEdit(bool alive, bool onFoot, float now, Collider aimCollider)
        {
            bool wasActive = _edit.Active;
            bool canAct = alive && onFoot && !_blockedThisFrame && !_spectator.Active;
            var eye = _predictor.PredictedPosition.ToNumerics() + new System.Numerics.Vector3(0f, AimSolver.EyeHeightOf(_predictor.Mode), 0f);
            float range = _build.Catalog != null ? _build.Catalog.BuildRange : 7f;
            if (canAct && _input.EditPressed)
            {
                if (_edit.Active)
                {
                    EditSendResult sent = _edit.Confirm(now, _buildStore);
                    if (sent == EditSendResult.Invalid) _buildHud.ShowNotice(UiText.EditInvalid, now);
                    else if (sent == EditSendResult.Busy) _buildHud.ShowNotice(UiText.BuildRefusal(BuildResultCode.RateLimited), now);
                }
                else if (_pieceViews.TryGetPieceId(aimCollider, out uint pieceId))
                {
                    EditBeginResult begin = _edit.TryBegin(pieceId, _buildStore, MyEntityId, eye, range);
                    if (begin == EditBeginResult.NotOwner) _buildHud.ShowNotice(UiText.BuildRefusal(BuildResultCode.NotOwner), now);
                    else if (begin == EditBeginResult.OutOfRange) _buildHud.ShowNotice(UiText.BuildRefusal(BuildResultCode.OutOfRange), now);
                }
            }
            if (_edit.Active)
            {
                if (canAct && _input.EscapePressed)
                {
                    _edit.Cancel();
                }
                else if (canAct && _input.AimPressed)
                {
                    if (_edit.ResetPiece(now, _buildStore) == EditSendResult.Busy)
                        _buildHud.ShowNotice(UiText.BuildRefusal(BuildResultCode.RateLimited), now);
                }
                Ray ray = _camera.AimRay;
                _edit.Update(canAct, _buildStore, eye, range, ray.origin.ToNumerics(), ray.direction.ToNumerics(),
                    canAct && _input.FirePressed, canAct && _input.FireHeld);
            }
            // Fire comes back with the next press, not with the button held for painting or confirming (D10, request §29).
            if (wasActive && !_edit.Active && _input.FireHeld) _fireBlockedUntilRelease = true;
            _buildStore.ExpirePredictions(now);
        }

        // 기능: 건설 모드 한 프레임: 후보, 그 판정, 버튼을 누르고 있으면 배치 요청.
        // 입력: alive - 살아 있음, onFoot - 행동 가능 모드, now - 현재 시각.
        // 출력: 반환값 없음. 편집 모드(Phase 13.5) 중에는 건설 모드로 보지 않는다(후보 없음, 배치 없음).
        private void UpdateBuild(bool alive, bool onFoot, float now)
        {
            bool inBuildMode = alive && onFoot && _tools.Current == ToolKind.Build && !_blockedThisFrame && !_edit.Active;
            Vector3 feet = _predictor.PredictedPosition;
            var eye = feet.ToNumerics() + new System.Numerics.Vector3(0f, AimSolver.EyeHeightOf(_predictor.Mode), 0f);
            bool pressed = inBuildMode && _input.FirePressed && !_fireBlockedUntilRelease;
            _build.Update(now, inBuildMode, pressed, inBuildMode && _fireHeld, feet.ToNumerics(), eye, _camera.Yaw, _camera.Pitch, _buildStore);
        }

        // 기능: 건설 표시 한 프레임: 바뀐 조각과 짓는 중인 조각, 유령, 편집 오버레이(Phase 13.5), 휘두르기·채집 효과, HUD.
        //   저장소의 변경 목록은 여기서 매 프레임 비운다.
        // 입력: alive - 살아 있음, now - 현재 시각.
        // 출력: 반환값 없음. 화면 Object와 HUD가 갱신된다.
        private void UpdateBuildPresentation(bool alive, float now)
        {
            _pieceViews.Apply(_buildStore, _build.Catalog, EstimatedServerTick());
            _buildStore.ClearChanged();
            _buildPreview.Update(_build);
            _editOverlay.Update(_edit);
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
            _buildHud.SetMode(alive && _tools.Current == ToolKind.Build, _build.Selection.Piece, _build.Selection.Material, _edit.Active, _edit.Target.Type);
            _buildHud.Tick(now);
        }

        // The server tick now, estimated as the match HUD does (render tick plus the interpolation delay); 0 before a clock.
        private double EstimatedServerTick() =>
            _clock != null && _clock.IsReady && _simHz > 0 ? _renderTick + _interpolationDelaySeconds * _simHz : 0;

        // Phase 13 D16: the F1 build line (UiRoot owns the overlay). Request §190: Development Builds (and the Editor) only.
        // 기능: F1 건설 줄을 갱신한다(Development Build·Editor만).
        // 입력: overlay - 디버그 오버레이, now - 현재 시각.
        // 출력: 반환값 없음. 요청·거절 수는 배치와 편집을 합친 값이다(서버의 초당 상한을 같이 쓴다).
        public void TickBuildDebug(DebugOverlay overlay, float now)
        {
            if (!Debug.isDebugBuild) return;
            // Phase 13.5: requests and refusals of both kinds (they share the server's per-second cap).
            overlay.TickBuild(now, _tools.Current, _build.Selection.Piece, _build.Selection.Material, _buildStore.Count, _pieceViews.Count,
                _buildStore.Ignored, _build.Sent + _edit.Sent, _build.Refused + _edit.Refused, _lastBuildRefusal);
        }

        // 기능: 분대 표시 한 프레임(Phase 14 D14): 분대 HUD 줄(2명 이상일 때), 소지 카드, 팀원 표지(경기 안의 팀원, 기절이면 빨강),
        //   내 기절 막대, 나와 관련된 소생·재투입 진행 막대(서버 Tick 기준), 그리고 소생·재투입 안내를 고른다.
        // 입력: alive - 살아 있음, targetInRange - 결과(소생·재투입 대상이 범위 안이거나 내가 진행 중이면 true: 문·줍기 안내를 끈다).
        // 출력: 안내 문구(UiText 상수) 또는 null. 문자열은 바뀔 때만 만들고 이 함수는 할당하지 않는다.
        private string UpdateSquad(bool alive, out bool targetInRange)
        {
            targetInRange = false;
            double tick = EstimatedServerTick();
            _squad.ExpireChannels(tick, _simHz);
            _squadHud.SetVisible(true);

            int rows = 0;
            if (_squad.Count > 1)
            {
                for (int i = 0; i < _squad.Count; i++)
                {
                    TeamMember member = _squad.Member(i);
                    _squadHud.SetRow(i, member.EntityId, NameOf(member.EntityId), member.State, member.Health, member.Flags, member.EntityId == MyEntityId);
                    rows = i + 1;
                }
            }
            _squadHud.HideRowsFrom(rows);
            _squadHud.SetCards(alive ? _inventory.RebootCards : 0);

            // Teammates in play that are drawn: a marker each, and the downed ones are revive candidates.
            int markers = 0;
            int downed = 0;
            for (int i = 0; i < _squad.Count; i++)
            {
                TeamMember member = _squad.Member(i);
                if (member.EntityId == MyEntityId || !_squad.IsInPlay(member.EntityId)) continue;
                if (!_remotePlayers.TryGetPose(member.EntityId, _renderTick, out Vector3 feet, out MovementMode mode, out bool drawnAlive) ||
                    !drawnAlive || mode == MovementMode.Transport)
                    continue;
                bool isDowned = member.State == TeamMemberState.Downed || mode == MovementMode.Downed;
                _markers.Show(markers++, feet, PlayerPose.For(mode, false, true).BodyHeight, isDowned, _camera.Yaw);
                if (isDowned && downed < _downedFeet.Length) _downedFeet[downed++] = feet.ToNumerics();
            }
            _markers.HideFrom(markers);

            bool meDowned = alive && _predictor.Mode == MovementMode.Downed;
            _squadHud.SetBleed(meDowned ? SquadPrompt.BleedSecondsLeft(_health) : -1, _health);

            string label = null;
            float progress = 0f;
            if (alive && _squad.TryGetChannelOf(MyEntityId, out ChannelView channel))
            {
                label = channel.ActorId != MyEntityId ? UiText.ChannelRevived
                    : channel.Kind == ChannelKind.Revive ? UiText.ChannelReviving : UiText.ChannelRebooting;
                progress = SquadState.Progress(channel.StartTick, channel.EndTick, tick);
            }
            _squadHud.SetChannel(label, progress);

            bool up = alive && LocalPlayerPredictor.ActionsAllowed(_predictor.Mode) && !_spectator.Active;
            if (!up) return null;
            if (label != null)
            {
                // Our own channel: the bar says it all, and E keeps holding it.
                targetInRange = true;
                return null;
            }
            System.Numerics.Vector3 myFeet = _predictor.PredictedPosition.ToNumerics();
            if (SquadPrompt.NearestDowned(myFeet, _downedFeet, downed) >= 0)
            {
                targetInRange = true;
                return UiText.HintRevive;
            }
            if (_inventory.RebootCards == 0) return null;
            int station = SquadPrompt.NearestStation(myFeet);
            if (station < 0) return null;
            if (SquadPrompt.IsCoolingDown(_stations, station, tick)) return UiText.HintRebootCooling;
            targetInRange = true;
            return UiText.HintReboot;
        }

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

        // The server tick our rider is drawn at: the predicted one, or before the first ack (the rider held at the server's
        // position) the newest snapshot's. Only called with a predictor and a clock.
        private double RiderTick() => _predictor.HasTickBase ? _predictor.RenderTick : _clock.LatestTick;

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

        // 기능: 슬롯, 회복템, 회복 막대, "[E]" 줍기 안내를 갱신한다(D15, 문자열은 바뀔 때만 InventoryHudText가 만든다).
        //   Phase 14 D9: 재투입 카드는 "[E] 줍기: 주인 [재투입 카드]"로 안내한다.
        // 입력: alive - 살아 있음, prompt - 줍기 안내를 보일지(false면 안내만 숨기고 회복 막대는 남긴다, Phase 12), now - 현재 시각.
        // 출력: 반환값 없음.
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
                case ItemKind.RebootCard:
                    // Phase 14 D9: Amount is the card's owner. Its cached name (or a constant), so asking every frame does not allocate.
                    _inventoryHud.SetPrompt(item.ItemId, item.Amount, NameOf(item.Amount) ?? UiText.Teammate, UiText.RebootCardName);
                    break;
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

        private string WeaponName(byte weaponId)
        {
            for (int i = 0; i < _weaponCatalog.Length; i++)
            {
                if (_weaponCatalog[i].WeaponId == weaponId) return _weaponCatalog[i].Name;
            }
            return "?";
        }

        // 기능: 구독을 풀고 만든 것을 만든 역순으로 해제한다(편집 오버레이는 공유 Mesh보다 먼저, Phase 14 분대 표시 포함).
        // 입력: 없음(Unity가 부른다, 종료 때도).
        // 출력: 반환값 없음.
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
            _net.BuildEditedReceived -= OnBuildEdited;
            _net.BuildDestroyedReceived -= OnBuildDestroyed;
            _net.BuildResetReceived -= OnBuildReset;
            _net.BuildInterestReceived -= OnBuildInterest;
            _net.HarvestHitReceived -= OnHarvestHit;
            _net.TeamStateReceived -= OnTeamState;
            _net.PlayerDownedReceived -= OnPlayerDowned;
            _net.ChannelStateReceived -= OnChannelState;
            _net.RebootStationsReceived -= OnRebootStations;
            _net.Dispose();
            ClearMatchState();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _qaRecorder?.Dispose();
            _qaRecorder = null;
#endif
            _killFeed.Dispose();
            _stationViews.Dispose();
            _markers.Dispose();
            _squadHud.Dispose();
            _buildHud.Dispose();
            _harvestEffects.Dispose();
            _buildPreview.Dispose();
            _editOverlay.Dispose();
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

        // 기능: 커서 잠금과 발사·조준 버튼 상태를 이번 프레임 값으로 정한다.
        // 입력: 없음(InputReader, UI 제어 값, 관전 상태를 읽는다).
        // 출력: 반환값 없음. Cursor.lockState, _fireHeld, _aiming, _fireBlockedUntilRelease가 바뀌고 관전 중 클릭이면 대상이 넘어간다.
        //       Phase 13.5: 편집 모드 중에는 _fireHeld와 _aiming이 false다(왼쪽은 칸 선택, 오른쪽은 Reset).
        // Left click locks a free cursor (only once joined) and fires while it is locked (D12).
        // Aim and fire only count while the cursor is locked, i.e. while the mouse controls the game.
        // Phase 5 D5: while spectating, a left click on a locked cursor moves to the next player and never fires;
        // the button must be released before it fires again (a click held into the next round does not shoot).
        // The click that locks the cursor neither fires nor cycles.
        // Phase 11 D5: Esc no longer unlocks here; it opens the menu (UiFlow), and with any screen up the cursor is
        // freed for its buttons and a click does not lock it.
        // QA: with QaAssumeCursorLocked the cursor counts as locked (CursorLocked), so a QA click fires instead of locking.
        // In the !_cursorLockAllowed branch CursorLocked is the real lock, so screens behave exactly as without QA.
        // With the QA assumption a click in a focused window also locks the real cursor (a person in the Editor); that
        // click fires as well, because the game already counted the cursor as locked.
        private void UpdateCursorAndButtons()
        {
            bool locked = CursorLocked;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (QaAssumeCursorLocked && _cursorLockAllowed && Cursor.lockState != CursorLockMode.Locked && Application.isFocused &&
                _input.FirePressed && State == ClientState.Joined)
                Cursor.lockState = CursorLockMode.Locked;
#endif
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
            // Phase 13.5 D10: in edit mode the left button picks tiles and the right one resets: neither fires nor aims, so
            // Fire never reaches an input (the server does not shoot) and the camera does not zoom.
            _fireHeld = locked && !_inputBlocked && _input.FireHeld && !_fireBlockedUntilRelease && !_spectator.Active && !_edit.Active;
            _aiming = locked && !_inputBlocked && _input.AimHeld && !_edit.Active;
        }

        // 기능: 조준선이 가리키는 점과 그곳의 Collider를 낸다. 원격 플레이어는 PlayerViewFactory.RemoteHitLayer에 Collider가 있어
        //   점이 플레이어 위일 수 있고, 서버는 우리 눈에서 그 점으로 쏜다(D2).
        // 입력: hitCollider - 결과(Phase 13.5 D10: 편집 대상 조각을 찾는 데 쓴다).
        // 출력: 맞으면 맞은 점과 그 Collider, 아니면 사거리 끝 점과 null.
        private Vector3 FindAimPoint(out Collider hitCollider)
        {
            float range = _weapons != null && _weapons.HasWeapon ? _weapons.Current.Range : DefaultAimRange;
            Ray ray = _camera.AimRay;
            // Remote views moved in Update. Physics.autoSyncTransforms is off by default, so without this the
            // ray would test their colliders where the last physics step left them.
            Physics.SyncTransforms();
            if (Physics.Raycast(ray, out RaycastHit hit, range, PlayerViewFactory.AimRaycastMask, QueryTriggerInteraction.Ignore))
            {
                hitCollider = hit.collider;
                return hit.point;
            }
            hitCollider = null;
            return ray.GetPoint(range);
        }

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

        private void ArmReconnect(int attempt)
        {
            _reconnectPending = true;
            _reconnectAt = _dropAt + DisconnectCodes.ReconnectOffsetSeconds(attempt);
        }

        private void OnConnected()
        {
            _established = true;
            _reconnectPending = false;   // this attempt got through: no next slot unless it drops again
        }

        // 기능: 참가 응답을 처리한다. 성공(또는 재개)이면 Entity·Tick 정보와 시계를 정하고 건설·편집·도구 상태를 처음으로 되돌린다.
        // 입력: response - 서버의 참가 응답.
        // 출력: 반환값 없음. 거절이면 자동 재접속을 멈춘다.
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
            // Phase 13.5 D4: edits share that numbering (BuildController.Reset restarts the shared counter).
            _build.Reset();
            _edit.Reset();
            _build.SimHz = response.SimHz;
            _tools.Reset();
            _interpolationDelaySeconds = InterpolationSnapshots / response.SnapshotHz;
            _clock = new ServerClock(response.SimHz);
            _clock.OnSnapshot(response.ServerTick, Time.unscaledTimeAsDouble);
            Debug.Log($"Joined as entity {response.MyEntityId} (SimHz {response.SimHz}, SnapshotHz {response.SnapshotHz})");
        }

        private void OnCatalog(WeaponInfo[] weapons)
        {
            _weaponCatalog = weapons;
            _weapons = new WeaponState(weapons);
        }

        private void OnItemCatalog(ItemCatalogData items)
        {
            _itemCatalog = items;
        }

        private void OnItem(WorldItemData item)
        {
            _worldItems.Upsert(item);
        }

        private void OnItemRemoved(ushort itemId)
        {
            _worldItems.Remove(itemId);
        }

        // Feedback only (the inventory changes through InventoryState). Constant strings: no allocation.
        private void OnPickupResult(PickupResult result)
        {
            if (result.Result == PickupResultCode.Full) _inventoryHud.ShowNotice("가방이 가득 찼습니다", Time.time);
            else if (result.Result == PickupResultCode.NothingInRange) _inventoryHud.ShowNotice("주울 수 있는 물건이 없습니다", Time.time);
        }

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

        // 기능: 플레이어 등장을 처리한다. 나면 예측과 내 뷰를 만들고, 남이면 원격 뷰를 만든다(Phase 14: 팀원이면 초록).
        // 입력: spawned - 등장 이벤트(이름 포함).
        // 출력: 반환값 없음. 이름 표에 등록된다.
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
            _remotePlayers.SetTeammate(spawned.EntityId, _squad.Contains(spawned.EntityId));   // Phase 14 D14
        }

        private void OnDespawned(ushort entityId)
        {
            _remotePlayers.Despawn(entityId);
            _names.Remove(entityId);
        }

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

        // D12: our own shots were already drawn when predicted.
        private void OnShot(ShotFired shot)
        {
            if (shot.ShooterId == MyEntityId) return;
            _fireEffects.ShowRemoteShot(shot.Start.ToUnity(), shot.End.ToUnity(), Time.time);
        }

        private void OnHitConfirmed(HitConfirmed hit)
        {
            _hud.ShowHit(hit.Killed, Time.time);
        }

        private void OnDamageTaken(DamageTaken damage)
        {
            _hud.ShowDamage(damage.FromDirection.ToUnity(), Time.time);
        }

        // 기능: 사망(탈락) 사건을 처리한다: Kill Feed 줄, 내 사망이면 예측 정지·관전 시작(경기 중) 또는 부활 카운트다운(개발 모드).
        // 입력: died - 사망 사건.
        // 출력: 반환값 없음. 팀이 살아 있는 중의 Placement는 잠정 값이라 쓰지 않는다(최종 순위는 MatchResult, Phase 14 D6).
        private void OnPlayerDied(PlayerDied died)
        {
            // Phase 11 D10: every death but the "you are spectating" notice a newcomer gets at join (Match sends it about the
            // newcomer itself, with no killer and no placement; a zone death in a match has a placement, and the dev sandbox
            // has no zone). The line is built once here, so the feed allocates per death, never per frame.
            // Phase 12 D10: a fall also has no killer and, in the dev sandbox, no placement: its cause tells it apart.
            // Phase 14: a newcomer has no team (TeamId 0, no TeamState), so a player on a team is never told the notice.
            bool notice = died.VictimId == MyEntityId && died.KillerId == 0 && died.Placement == 0 && died.Cause == DeathCause.Zone &&
                          !_squad.Contains(MyEntityId);
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

        // 기능: 부활(경기 시작·라운드 초기화 포함)을 처리한다. 남이면 순간이동만, 나면 예측·입력·도구·편집 모드·관전을 새 생명으로 되돌린다.
        // 입력: respawned - 부활 이벤트.
        // 출력: 반환값 없음.
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
            _edit.Cancel();
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

        // 기능: 경기 상태를 저장한다. 새 라운드 카운트다운(대기·시작)이면 결과·수송기 경로와 Phase 14 팀·채널을 지운다.
        // 입력: state - 받은 MatchState.
        // 출력: 반환값 없음. 처음 받으면 경기 HUD를 보인다.
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
                // Phase 14 D2: the old round's team is over; the next one comes with the match start.
                _roundTeamSize = 0;
                if (_squad.HasTeam || _squad.ChannelCount > 0)
                {
                    _squad.Clear();
                    _remotePlayers.ApplyTeam(_squad);
                }
            }
        }

        // 기능: 우리 팀 상태(Phase 14 D2)를 적용하고 원격 플레이어의 팀원 색을 다시 맞춘다.
        // 입력: team - 받은 TeamState(우리 팀만 온다).
        // 출력: 반환값 없음.
        private void OnTeamState(TeamState team)
        {
            _squad.Apply(team);
            if (team.Count > _roundTeamSize) _roundTeamSize = team.Count;
            _remotePlayers.ApplyTeam(_squad);
        }

        // 기능: 기절 사건(Phase 14 D5)을 Kill Feed에 "A ▸ B 기절"로 더한다(사건마다 한 번 문자열을 만든다).
        // 입력: downed - 기절 사건(AttackerId 0이면 원인 이름).
        // 출력: 반환값 없음.
        private void OnPlayerDowned(PlayerDowned downed)
        {
            string attacker = downed.AttackerId == 0 ? null : UiText.NameOr(NameOf(downed.AttackerId), downed.AttackerId);
            _killFeed.Add(UiText.DownedLine(attacker, UiText.NameOr(NameOf(downed.VictimId), downed.VictimId), downed.Cause), Time.unscaledTime);
        }

        // 기능: 소생·재투입 진행 상태(Phase 14 D8)를 적용한다. 진행 막대는 받은 순간의 추정 서버 Tick에서 끝 Tick까지다.
        // 입력: channel - 받은 ChannelState.
        // 출력: 반환값 없음.
        private void OnChannelState(ChannelState channel)
        {
            double tick = EstimatedServerTick();
            _squad.ApplyChannel(channel, tick > 0 ? tick : channel.EndTick);
        }

        // 기능: 스테이션 대기 상태(Phase 14 D10)를 저장하고 기둥 색에 반영한다.
        // 입력: stations - 받은 RebootStations 상태.
        // 출력: 반환값 없음.
        private void OnRebootStations(RebootStationsState stations)
        {
            _stations = stations;
            _stationViews.Apply(stations);
        }

        private void OnZoneState(ZoneState zone)
        {
            _zone = zone;
            _zoneView.SetZone(zone);
        }

        // Only participants get one (D11).
        private void OnMatchResult(MatchResult result)
        {
            _result = result;
            _hasResult = true;
            ResultCount++;
        }

        // Phase 12 D5: sent before the match start's respawns (and at a join or resume during the match).
        private void OnTransportRoute(DropRoute route)
        {
            _route = route;
            _hasRoute = true;
            if (_predictor != null) _predictor.SetRoute(route);
            _transportView.SetRoute(route);
        }

        private void ClearRoute()
        {
            if (!_hasRoute) return;
            _hasRoute = false;
            if (_predictor != null) _predictor.ClearRoute();
            _transportView.Clear();
        }

        private void OnDoorStates(byte openMask)
        {
            _doors.ApplyServer(openMask);
        }

        // Phase 13 D4: what the client needs of the building numbers.
        private void OnBuildCatalog(BuildCatalogData catalog)
        {
            _build.Catalog = catalog;
            _buildStore.CellsPerInterest = Mathf.Max(1, Mathf.RoundToInt(catalog.InterestCellSize / BuildGrid.CellSize));
        }

        private void OnResources(ResourcesState resources) => _build.Resources = resources;

        // 기능: 건설·편집 요청의 BuildResult를 순번으로 주인에게 보낸다(Phase 13.5 D9: 편집이면 편집 예측, 아니면 배치 대기).
        // 입력: result - 서버 결과.
        // 출력: 반환값 없음. 거절이면 롤백·안내 문구·소리, Ok면 소리.
        private void OnBuildResult(BuildResult result)
        {
            // An edit's result never reaches BuildController, so it cannot take an edit's Ok as a placement.
            if (!_edit.OnResult(result, _buildStore, Time.time)) _build.OnResult(result);
            Vector3 at = _predictor != null ? _predictor.RenderPosition : Vector3.zero;
            if (result.Code == BuildResultCode.Ok)
            {
                _buildAudio.Play(BuildSound.Placed, at);
                return;
            }
            _lastBuildRefusal = result.Code;
            _buildHud.ShowNotice(UiText.BuildRefusal(result.Code), Time.time);
            _buildAudio.Play(BuildSound.Refused, at);
        }

        // Phase 13 D7: our own harvest hit: the weak point marker, a fall's puff, the sounds.
        private void OnHarvestHit(HarvestHit hit)
        {
            _harvestEffects.OnHit(hit, Time.time);
            Vector3 at = hit.WeakPoint.ToUnity();
            _buildAudio.Play(hit.Destroyed ? BuildSound.HarvestDestroyed : hit.WeakPointHit ? BuildSound.WeakPointHit : BuildSound.HarvestHit, at);
        }

        // Phase 13 D13, D14: the building stream, applied by id (BuildStore).
        private void OnBuildPiece(BuildPieceRecord piece, uint version) => _buildStore.ApplyPiece(piece, version);

        private void OnBuildHealth(uint id, ushort damage, uint version) => _buildStore.ApplyHealth(id, damage, version);

        // 기능: Edited 기록(Phase 13.5 D8)을 저장소에 적용한다.
        // 입력: id - 조각 id, state - 편집 뒤 상태, version - 건설 스트림 버전.
        // 출력: 반환값 없음. 그 조각에 맞지 않는 상태는 저장소가 무시하고 Ignored로 센다(F1).
        private void OnBuildEdited(uint id, ushort state, uint version) => _buildStore.ApplyEdited(id, state, version);

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

        private void OnBuildReset(uint version) => _buildStore.Reset();

        private void OnBuildInterest(ulong cells) => _buildStore.ApplyInterest(cells);

        // Phase 13 D6: a destroyed harvestable leaves the screen and the predicted collision at once.
        private void OnHarvestStates(ulong destroyed)
        {
            _destroyedHarvestables = destroyed;
            _harvestables.Apply(destroyed);
            _harvestEffects.OnStates(destroyed);
            if (_predictor != null) _predictor.DestroyedHarvestables = destroyed;
        }

        private void OnStats(StatsResponse response)
        {
            _stats = response;
            _statsAnsweredAt = Time.unscaledTime;
        }

        private void OnDisconnected(string reason)
        {
            MyEntityId = 0;
            ClearMatchState();
            Cursor.lockState = CursorLockMode.None;
            Debug.Log($"Disconnected: {reason}");
            ScheduleReconnect();
        }

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

        // 기능: 경기 상태 전체를 비운다(끊김, 종료). 조각·편집 예측·편집 모드와 오버레이, Phase 14 팀·채널·스테이션·분대 표시도 비운다.
        // 입력: 없음.
        // 출력: 반환값 없음. 화면 Object는 숨기거나 풀로 돌아간다.
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
            _edit.Reset();
            _editOverlay.Hide();
            _lastBuildRefusal = BuildResultCode.Ok;
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
            _squad.Clear();
            _roundTeamSize = 0;
            _stations = default;
            _stationViews.Apply(default);
            _markers.HideFrom(0);
            _squadHud.HideRowsFrom(0);
            _squadHud.SetCards(0);
            _squadHud.SetBleed(-1, 0);
            _squadHud.SetChannel(null, 0f);
            _squadHud.SetVisible(false);
        }
    }
}
