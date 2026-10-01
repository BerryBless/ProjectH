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

            _net = new NetClient();
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
        }

        private void Update()
        {
            _net.Poll();
            UpdateReconnect();
            // Phase 12: the same block as below (last frame's UI control, this frame's cursor) for the crouch toggle.
            _input.Update(_inputBlocked || Cursor.lockState != CursorLockMode.Locked);
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
            bool blocked = _inputBlocked || Cursor.lockState != CursorLockMode.Locked;
            _blockedThisFrame = blocked;
            _camera.ApplyLook(blocked ? Vector2.zero : _input.LookDelta, _aiming);
            InputButtons held = InputButtons.None;
            if (!blocked && _input.Sprint) held |= InputButtons.Sprint;
            if (!blocked && _input.CrouchHeld) held |= InputButtons.Crouch;   // Phase 12 D7
            if (_fireHeld) held |= InputButtons.Fire;
            if (blocked) _input.QueuedButtons = InputButtons.None;
            InputButtons queued = _input.QueuedButtons;
            _pendingSteps += _predictor.Advance(Time.deltaTime, blocked ? Vector2.zero : _input.Move, _camera.Yaw, held, ref queued);
            _input.QueuedButtons = queued;

            _localView.Place(_predictor.RenderPosition, _predictor.RenderYaw, _predictor.Mode, _predictor.Sprinting);
        }

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

        private string WeaponName(byte weaponId)
        {
            for (int i = 0; i < _weaponCatalog.Length; i++)
            {
                if (_weaponCatalog[i].WeaponId == weaponId) return _weaponCatalog[i].Name;
            }
            return "?";
        }

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
            _net.Dispose();
            ClearMatchState();
            _killFeed.Dispose();
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

        // Runs the local weapon copy over this frame's new inputs, oldest first. Returns how many of them
        // the server is expected to fire.
        private int StepWeapons(int steps)
        {
            if (_weapons == null) return 0;
            int count = Math.Min(steps, LocalPlayerPredictor.HistorySize);
            uint newest = _predictor.LastSeq;
            int shots = 0;
            for (int i = count - 1; i >= 0; i--)
            {
                uint seq = newest - (uint)i;
                // Phase 12 D12: riding, falling, gliding or vaulting, the server takes no action from the input (it only
                // follows the held fire button).
                if (_weapons.Step(seq, _predictor.InputAt(seq).Buttons, _predictor.ActionsAllowedAt(seq))) shots++;
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

        private void OnSpawned(PlayerSpawned spawned)
        {
            _names[spawned.EntityId] = spawned.Name;
            if (spawned.EntityId == MyEntityId)
            {
                if (_predictor != null) return;
                // Phase 12: the spawn carries no mode; a resumed player in the air gets it from the next snapshot.
                _predictor = new LocalPlayerPredictor(_simHz, new MoveState { Position = spawned.Position, Yaw = spawned.Yaw }, _doors);
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

        private void OnDespawned(ushort entityId)
        {
            _remotePlayers.Despawn(entityId);
            _names.Remove(entityId);
        }

        private void OnSnapshot(in WorldSnapshotHeader header, SnapshotEntity[] entities, int count)
        {
            if (_clock == null) return;
            _clock.OnSnapshot(header.ServerTick, Time.unscaledTimeAsDouble);

            // D10: our own health, shield and weapon come with every snapshot.
            _health = header.Self.Health;
            _shield = header.Self.Shield;
            if (_weapons != null) _weapons.ApplyServer(header.Self, header.AckInputSeq);

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
