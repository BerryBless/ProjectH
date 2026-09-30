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
        private LocalFireEffects _fireEffects;
        private NetClient _net;
        private LocalPlayerPredictor _predictor;
        private Transform _localView;
        private Renderer _localRenderer;
        private readonly RemotePlayers _remotePlayers = new RemotePlayers();
        private ServerClock _clock;
        private WeaponState _weapons;
        private int _simHz;
        private double _interpolationDelaySeconds;
        private double _renderTick;       // server tick remote players are drawn at this frame (ViewTick, D6)
        private int _pendingSteps;        // inputs predicted in Update, aimed and sent in LateUpdate
        private int _health;
        private int _shield;
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
            _fireEffects = new LocalFireEffects();

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
            if (_predictor == null) return;
            float now = Time.time;
            bool alive = !_predictor.IsDead;

            _camera.Follow(_predictor.RenderPosition, _aiming, Time.deltaTime);
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
            if (_weapons != null) _hud.SetWeapon(_weapons.Current.Name, _weapons.Ammo, _weapons.Current.MagazineSize, _weapons.Reloading);
            else _hud.ClearWeapon();
            _hud.Tick(_camera.Yaw, now);
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
            _net.Dispose();
            ClearMatchState();
            _fireEffects.Dispose();
            _hud.Dispose();
            _crosshair.Dispose();
            _input.Dispose();
            PlayerViewFactory.ReleaseMaterials();
            if (_world != null) Destroy(_world);
            if (_worldMaterial != null) Destroy(_worldMaterial);
        }

        // Left click locks a free cursor (only once joined) and fires while it is locked (D12).
        // Aim and fire only count while the cursor is locked, i.e. while the mouse controls the game.
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

            if (!_input.FireHeld) _fireBlockedUntilRelease = false;
            _fireHeld = locked && _input.FireHeld && !_fireBlockedUntilRelease;
            _aiming = locked && _input.AimHeld;
        }

        // What the crosshair is on. Remote players have colliders on PlayerViewFactory.RemoteHitLayer, so the
        // point can be on a player; the server then shoots from our eye towards it (D2).
        private Vector3 FindAimPoint()
        {
            float range = _weapons != null ? _weapons.Current.Range : DefaultAimRange;
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
            _weapons = new WeaponState(weapons);
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
            _hud.ShowDeath(Time.time);
        }

        private void OnPlayerRespawned(PlayerRespawned respawned)
        {
            // Other players' views follow their snapshot flags (RemotePlayers.Push).
            if (respawned.EntityId != MyEntityId || _predictor == null) return;
            _predictor.Respawn(new MoveState { Position = respawned.Position, Yaw = respawned.Yaw });
            _input.QueuedButtons = InputButtons.None;
            if (_weapons != null) _weapons.Refill();
            PlayerViewFactory.SetAlive(_localRenderer, null, true, true);
            _hud.HideDeath();
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
            _pendingSteps = 0;
            _renderTick = 0;
            _crosshair.SetVisible(false);
            _hud.HideDeath();
            _hud.SetVisible(false);
            _fireEffects.HideAll();
        }
    }
}
