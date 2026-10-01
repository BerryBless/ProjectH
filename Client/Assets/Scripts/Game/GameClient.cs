using System;
using ProjectH.Client.Bootstrap;
using ProjectH.Client.CameraControl;
using ProjectH.Client.Input;
using ProjectH.Client.Net;
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
        private Material _worldMaterial;
        private InputReader _input;
        private ShoulderCamera _camera;
        private Crosshair _crosshair;
        private CombatHud _hud;
        private InventoryHud _inventoryHud;
        private WorldItemViews _worldItems;
        private LocalFireEffects _fireEffects;
        private MatchHud _matchHud;
        private ZoneView _zoneView;
        private readonly SpectatorCamera _spectator = new SpectatorCamera();
        private NetClient _net;
        private LocalPlayerPredictor _predictor;
        private Transform _localView;
        private Renderer _localRenderer;
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

        public ClientState State => _net.State;
        public string LastError => _net.LastError;
        public int RoundTripMs => _net.RoundTripMs;
        public ushort MyEntityId { get; private set; }

        public void Connect(string host, int port, string devPlayerId) => _net.Connect(host, port, devPlayerId);

        public void Disconnect() => _net.Disconnect();

        private void Awake()
        {
            _world = TestWorld.Build(out _worldMaterial);
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

            _net = new NetClient();
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
        }

        private void Update()
        {
            _net.Poll();
            _input.Update();
            UpdateCursorAndButtons();

            if (_clock != null && _clock.IsReady)
            {
                // The only place _renderTick is set: LateUpdate sends this exact value as ViewTick (see SetAim).
                _renderTick = _clock.RenderTick(Time.unscaledTimeAsDouble, _interpolationDelaySeconds);
                _remotePlayers.Render(_renderTick);
            }

            if (_predictor == null) return;

            _camera.ApplyLook(_input.LookDelta, _aiming);
            InputButtons held = InputButtons.None;
            if (_input.Sprint) held |= InputButtons.Sprint;
            if (_fireHeld) held |= InputButtons.Fire;
            InputButtons queued = _input.QueuedButtons;
            _pendingSteps += _predictor.Advance(Time.deltaTime, _input.Move, _camera.Yaw, held, ref queued);
            _input.QueuedButtons = queued;

            PlayerViewFactory.Pose(_predictor.RenderPosition, _predictor.RenderYaw, !_predictor.IsDead, out Vector3 position, out Quaternion rotation);
            _localView.SetPositionAndRotation(position, rotation);
        }

        private void LateUpdate()
        {
            // Before the early return: items spin (and are visible) before the local player has spawned. No allocation.
            _worldItems.Tick(Time.time);
            if (_predictor == null) return;
            float now = Time.time;
            bool alive = !_predictor.IsDead;

            // D5: dead in a match, the camera follows the watched player where it is drawn; otherwise our own view.
            _spectator.Update(_remotePlayers);
            Vector3 followFeet = _spectator.TryGetFeet(_remotePlayers, _renderTick, out Vector3 watched) ? watched : _predictor.RenderPosition;
            _camera.Follow(followFeet, _aiming, Time.deltaTime);
            _crosshair.SetVisible(alive);

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
            if (_weapons != null && _weapons.HasWeapon) _hud.SetWeapon(_weapons.Current.Name, _weapons.Ammo, _weapons.Reserve, _weapons.Reloading);
            else _hud.ClearWeapon();
            _hud.Tick(_camera.Yaw, now);

            UpdateInventoryHud(alive, now);
            UpdateMatchHud(alive);
        }

        // D14: state line, zone line and circle, red edges outside the zone, result, spectating. Strings are rebuilt
        // only on change (MatchHudText). Times use the estimated current server tick: the render tick plus the
        // interpolation delay.
        private void UpdateMatchHud(bool alive)
        {
            if (!_hasMatch || _clock == null || !_clock.IsReady || _simHz <= 0) return;
            double tick = _renderTick + _interpolationDelaySeconds * _simHz;

            int secondsLeft = _match.StateEndTick > tick ? (int)Math.Ceiling((_match.StateEndTick - tick) / _simHz) : 0;
            _matchHud.SetStatus(_match.State, secondsLeft, _match.Alive, _match.Participants, _match.MinPlayers);

            bool inMatch = _match.State == MatchFlowState.Playing || _match.State == MatchFlowState.FinalPhase;
            int zoneSeconds = 0;
            ZoneHint hint = inMatch ? ZoneMath.Hint(_zone, tick, _simHz, out zoneSeconds) : ZoneHint.None;
            _matchHud.SetZone(hint, zoneSeconds);
            Vector3 feet = _predictor.PredictedPosition;
            _matchHud.SetOutside(inMatch && alive && ZoneMath.IsOutside(_zone, feet.x, feet.z, tick));
            _zoneView.Tick(tick);

            if (_hasResult) _matchHud.SetResult(_result.WinnerId == MyEntityId, _result.Placement, _result.Kills);
            else _matchHud.SetResult(false, 0, 0);
            _matchHud.SetSpectating(_spectator.Active ? _spectator.Target : (ushort)0);
        }

        // D15: slots, heals, the heal bar and the "[E]" prompt. Strings are rebuilt only on change (InventoryHudText).
        private void UpdateInventoryHud(bool alive, float now)
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
            int target = alive ? PickupRule.FindNearest(_worldItems.Items, _predictor.PredictedPosition.ToNumerics()) : -1;
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
            _net.Dispose();
            ClearMatchState();
            _zoneView.Dispose();
            _matchHud.Dispose();
            _fireEffects.Dispose();
            _worldItems.Dispose();
            _inventoryHud.Dispose();
            _hud.Dispose();
            _crosshair.Dispose();
            _input.Dispose();
            PlayerViewFactory.ReleaseMaterials();
            if (_world != null) Destroy(_world);
            if (_worldMaterial != null) Destroy(_worldMaterial);
        }

        // Left click locks a free cursor (only once joined) and fires while it is locked (D12).
        // Aim and fire only count while the cursor is locked, i.e. while the mouse controls the game.
        // Phase 5 D5: while spectating, a left click on a locked cursor moves to the next player and never fires;
        // the button must be released before it fires again (a click held into the next round does not shoot).
        // The click that locks the cursor neither fires nor cycles.
        private void UpdateCursorAndButtons()
        {
            bool locked = Cursor.lockState == CursorLockMode.Locked;
            if (_input.UnlockCursorPressed)
            {
                Cursor.lockState = CursorLockMode.None;
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
            _fireHeld = locked && _input.FireHeld && !_fireBlockedUntilRelease && !_spectator.Active;
            _aiming = locked && _input.AimHeld;
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
                if (_weapons.Step(seq, _predictor.InputAt(seq).Buttons)) shots++;
            }
            return shots;
        }

        private void OnJoined(JoinMatchResponse response)
        {
            if (response.Result != JoinResult.Ok)
            {
                Debug.LogWarning($"Join failed: {response.Result}");
                return;
            }
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
            if (result.Result == PickupResultCode.Full) _inventoryHud.ShowNotice("Inventory full", Time.time);
            else if (result.Result == PickupResultCode.NothingInRange) _inventoryHud.ShowNotice("Nothing to pick up", Time.time);
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
            if (spawned.EntityId == MyEntityId)
            {
                if (_predictor != null) return;
                _predictor = new LocalPlayerPredictor(_simHz, new MoveState { Position = spawned.Position, Yaw = spawned.Yaw });
                // A key pressed while waiting for the spawn must not act on the first step.
                _input.QueuedButtons = InputButtons.None;
                _localView = PlayerViewFactory.Create($"Player {spawned.EntityId} (you)", true);
                _localRenderer = _localView.GetComponent<Renderer>();
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
                    if (_predictor != null) _predictor.Reconcile(entities[i], header.AckInputSeq);
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
            if (died.VictimId != MyEntityId || _predictor == null) return;
            _predictor.SetDead();
            PlayerViewFactory.SetAlive(_localRenderer, null, true, false);
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
            // Also the match start and the round reset (Phase 5 D3, D13): the same teleport, Seq continues.
            _predictor.Respawn(new MoveState { Position = respawned.Position, Yaw = respawned.Yaw });
            _input.QueuedButtons = InputButtons.None;
            _spectator.End();
            // Empty until the server's InventoryState for the new life arrives (sent right after this event).
            if (_weapons != null) _weapons.Clear();
            PlayerViewFactory.SetAlive(_localRenderer, null, true, true);
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
            // The result stays up during Finished and goes with the next round's countdown.
            if (state.State == MatchFlowState.WaitingForPlayers || state.State == MatchFlowState.Starting) _hasResult = false;
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
        }

        private void OnDisconnected(string reason)
        {
            MyEntityId = 0;
            ClearMatchState();
            Cursor.lockState = CursorLockMode.None;
            Debug.Log($"Disconnected: {reason}");
        }

        private void ClearMatchState()
        {
            _predictor = null;
            if (_localView != null) Destroy(_localView.gameObject);
            _localView = null;
            _localRenderer = null;
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
            _spectator.End();
            _zoneView.Clear();
            _matchHud.SetOutside(false);
            _matchHud.SetResult(false, 0, 0);
            _matchHud.SetSpectating(0);
            _matchHud.SetVisible(false);
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
