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

        private GameObject _world;
        private Material _worldMaterial;
        private InputReader _input;
        private ShoulderCamera _camera;
        private Crosshair _crosshair;
        private LocalFireEffects _fireEffects;
        private NetClient _net;
        private LocalPlayerPredictor _predictor;
        private Transform _localView;
        private readonly RemotePlayers _remotePlayers = new RemotePlayers();
        private ServerClock _clock;
        private int _simHz;
        private double _interpolationDelaySeconds;
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
            _fireEffects = new LocalFireEffects();

            _net = new NetClient();
            _net.Joined += OnJoined;
            _net.SpawnReceived += OnSpawned;
            _net.DespawnReceived += OnDespawned;
            _net.SnapshotReceived += OnSnapshot;
            _net.Disconnected += OnDisconnected;
        }

        private void Update()
        {
            _net.Poll();
            _input.Update();
            UpdateCursorAndButtons();

            if (_clock != null && _clock.IsReady)
                _remotePlayers.Render(_clock.RenderTick(Time.unscaledTimeAsDouble, _interpolationDelaySeconds));

            if (_predictor == null) return;

            _camera.ApplyLook(_input.LookDelta, _aiming);
            bool jump = _input.JumpQueued;
            int steps = _predictor.Advance(Time.deltaTime, _input.Move, _camera.Yaw, _input.Sprint, ref jump);
            _input.JumpQueued = jump;
            if (steps > 0 && _predictor.TryBuildInputPacket(out PlayerInputPacket packet)) _net.SendInput(packet);

            _localView.SetPositionAndRotation(_predictor.RenderPosition + Vector3.up, Quaternion.Euler(0f, _predictor.RenderYaw, 0f));
        }

        private void LateUpdate()
        {
            if (_predictor == null) return;
            _camera.Follow(_predictor.RenderPosition, _aiming, Time.deltaTime);
            _crosshair.SetVisible(true);
            // After the camera moved, so the shot goes where the crosshair is this frame.
            _fireEffects.Tick(Time.deltaTime, _fireHeld, _camera.AimRay, _predictor.RenderPosition, _camera.Yaw, Time.time);
        }

        private void OnDestroy()
        {
            _net.Joined -= OnJoined;
            _net.SpawnReceived -= OnSpawned;
            _net.DespawnReceived -= OnDespawned;
            _net.SnapshotReceived -= OnSnapshot;
            _net.Disconnected -= OnDisconnected;
            _net.Dispose();
            ClearMatchState();
            _fireEffects.Dispose();
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

        private void OnSpawned(PlayerSpawned spawned)
        {
            if (spawned.EntityId == MyEntityId)
            {
                if (_predictor != null) return;
                _predictor = new LocalPlayerPredictor(_simHz, new MoveState { Position = spawned.Position, Yaw = spawned.Yaw });
                // A Space pressed while waiting for the spawn must not fire a jump on the first step.
                _input.JumpQueued = false;
                _localView = PlayerViewFactory.Create($"Player {spawned.EntityId} (you)", true);
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
            _remotePlayers.Clear();
            _clock = null;
            _crosshair.SetVisible(false);
            _fireEffects.HideAll();
        }
    }
}
