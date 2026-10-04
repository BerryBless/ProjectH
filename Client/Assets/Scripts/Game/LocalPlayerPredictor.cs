using System;
using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Client-side prediction for the local player (Docs/Networking.md).
    // Moves against the world gathered around the character (Phase 13 D3, CollisionWorld): Shared GameMap.Boxes, the
    // predicted closed doors (PredictedDoors), the standing harvestables and the confirmed building pieces, and
    // GameMap.Terrain, gathered exactly as the server's Match.Move does.
    // Runs MovementSimulation at the server's tick rate, keeps a fixed 64-entry history of inputs and
    // results, and on each snapshot replays the inputs the server has not processed yet.
    // While dead (D9, D12) it predicts nothing: the server acks a dead player's inputs without moving it.
    // Phase 12: the whole MoveState (mode, horizontal velocity, energy, vault ticks) is predicted and reconciled. Aboard the
    // drop transport it rides the route at the server tick each input will be simulated at (snapshot tick - ack + seq),
    // and its own shoulder bashes and E on doors are predicted into PredictedDoors.
    public sealed class LocalPlayerPredictor
    {
        public const int HistorySize = 64;                 // ~2 s at 30 Hz; older unacked input -> snap
        private const float SnapDistance = 2f;             // corrections larger than this are not smoothed
        private const float ErrorDecayPerSecond = 10f;
        private const float MatchEpsilon = 0.01f;
        private const float MaxAccumulatedSeconds = 0.25f; // after a hitch, do not burst-simulate

        // Held buttons go into every step; queued presses only into a frame's last step (see Advance).
        private const InputButtons HeldButtons = InputButtons.Sprint | InputButtons.Fire | InputButtons.Crouch;
        private const InputButtons QueuedButtons = InputButtons.Jump | InputButtons.Reload | InputButtons.Slot1 | InputButtons.Slot2 |
                                                   InputButtons.Slot3 | InputButtons.Interact | InputButtons.Drop |
                                                   InputButtons.UseMedkit | InputButtons.UseShieldCell | InputButtons.ToolBuild |
                                                   InputButtons.ToolHarvest;

        private readonly InputCommand[] _inputs = new InputCommand[HistorySize];
        private readonly MoveState[] _results = new MoveState[HistorySize];
        private readonly float _stepSeconds;
        private readonly PredictedDoors _doors;
        private readonly CollisionWorld _collision = new CollisionWorld();
        private MoveState _state;
        private MoveState _previous;
        private float _accumulator;
        private Vector3 _renderError;
        // Seconds simulated so far: the clock of the door predictions.
        private float _time;
        // Phase 12 D5: the route, and server tick = _tickBase + seq for the input seq.
        private bool _hasRoute;
        private DropRoute _route;
        private long _tickBase;
        private bool _hasTickBase;

        // 기능: 서버 Tick 속도와 스폰 상태로 로컬 플레이어 예측기를 만든다.
        // 입력: simHz - 서버 시뮬레이션 Hz(한 step 길이), spawnState - 시작 이동 상태, doors - 공유할 예측 문 상태(null이면 새로 만든다).
        // 출력: 스폰 상태에서 시작하고 입력 기록이 비어 있는(LastSeq 0) 예측기.
        public LocalPlayerPredictor(int simHz, MoveState spawnState, PredictedDoors doors = null)
        {
            _stepSeconds = 1f / simHz;
            _doors = doors ?? new PredictedDoors();
            _state = spawnState;
            _previous = spawnState;
            RenderPosition = spawnState.Position.ToUnity();
        }

        public uint LastSeq { get; private set; }
        public Vector3 RenderPosition { get; private set; }
        public float RenderYaw => _state.Yaw;
        // 기능: 가장 최근 step의 예측 위치를 Unity 좌표로 돌려준다.
        // 입력: 없음.
        // 출력: 서버가 최신 입력을 처리한 뒤 있을 것으로 예측한 발 위치.
        // Newest simulated position: where the server will be after it runs our newest input (SetAim solves
        // each step's eye from that step's own result). RenderPosition trails it by up to one step and carries
        // the reconcile offset; it is for the camera and views only.
        public Vector3 PredictedPosition => _state.Position.ToUnity();
        public bool IsDead { get; private set; }

        // Phase 12: the newest predicted movement state, for the HUD, the camera and the F1 line.
        public MovementMode Mode => _state.Mode;
        public float Energy => _state.Energy;
        public bool Exhausted => _state.Exhausted;
        public bool Sprinting { get; private set; }
        // 기능: 예측된 수평 속도의 크기를 계산한다.
        // 입력: 없음.
        // 출력: 수평 속력(m/s).
        public float HorizontalSpeed => _state.HorizontalVelocity.Length();
        public float VerticalSpeed => _state.VelocityY;
        // How far the newest correction moved the prediction (m), and how many corrections there were.
        public float LastCorrection { get; private set; }
        public int Corrections { get; private set; }
        // 기능: 최신 입력이 서버에서 시뮬레이션될 Tick을 계산한다.
        // 입력: 없음.
        // 출력: _tickBase + LastSeq. HasTickBase 전에는 의미 없는 값.
        // The server tick the newest input will be simulated at (valid once HasTickBase).
        public uint PredictedTick => (uint)(_tickBase + LastSeq);
        // The first snapshot that acks an input gives the tick base; before it the rider is not ride-predicted.
        public bool HasTickBase => _hasTickBase;
        // 기능: RenderPosition이 그려지는 서버 Tick을 두 최신 step 사이의 소수 Tick으로 계산한다.
        // 입력: 없음.
        // 출력: 렌더링 기준 소수 서버 Tick. HasTickBase 전에는 의미 없는 값.
        // The (fractional) server tick RenderPosition is drawn at: between the two newest steps, as RenderPosition. For the
        // transport view while riding, so the transport and the rider (and its camera) move together. Valid once HasTickBase.
        public double RenderTick => (double)(_tickBase + LastSeq - 1) + (double)_accumulator / _stepSeconds;

        // Phase 13 D3, D6: the harvestables the server says are destroyed (HarvestStates) and the confirmed pieces
        // (BuildStore; never the predicted ones). Replays use the newest values, like the doors.
        public ulong DestroyedHarvestables { get; set; }
        public PieceGrid Pieces { get; set; }

        // 기능: 이번 경기의 수송기 경로를 저장해 Transport 모드 예측에 쓰게 한다.
        // 입력: route - 서버가 보낸 수송기 경로(TransportRoute).
        // 출력: 반환값 없음. 경로가 저장되고 경로 보유 상태가 켜진다.
        // D5: the match's transport route (TransportRoute). Kept until the next one or ClearRoute; it acts only in Transport
        // mode.
        public void SetRoute(in DropRoute route)
        {
            _route = route;
            _hasRoute = true;
        }

        // 기능: 새 라운드 카운트다운에서 이전 수송기 경로를 버린다.
        // 입력: 없음.
        // 출력: 반환값 없음. 경로 보유 상태가 꺼진다.
        // A new round's countdown (MatchState back to WaitingForPlayers or Starting): the old route is over.
        public void ClearRoute()
        {
            _hasRoute = false;
        }

        // 기능: 입력 seq의 이동 결과 모드에서 서버가 행동(사격·상호작용 등)을 허용하는지 확인한다.
        // 입력: seq - 최근 HistorySize 안의 입력 번호.
        // 출력: 그 입력 이후 모드에서 행동이 허용되면 true, 아니면 false.
        // D12: whether the server lets the input seq act (the mode after its move).
        public bool ActionsAllowedAt(uint seq) => ActionsAllowed(_results[(int)(seq % HistorySize)].Mode);

        // 기능: 이동 모드가 행동을 허용하는 모드인지 확인한다.
        // 입력: mode - 이동 모드.
        // 출력: Ground·Crouch·Slide면 true, 그 외(공중·수송기 등)면 false.
        public static bool ActionsAllowed(MovementMode mode) =>
            mode == MovementMode.Ground || mode == MovementMode.Crouch || mode == MovementMode.Slide;

        // 기능: 프레임 시간을 누적해 서버 Tick 길이마다 입력을 하나 만들고 서버 Match.Tick과 같은 이동 시뮬레이션을 돌려 기록한 뒤(죽은 동안은 이동·버튼 없는 입력만 만든다), 렌더링 위치를 보간한다.
        // 입력: deltaTime - 이번 프레임 시간(초), move - 이동 입력(x 오른쪽, y 앞), yaw - 캐릭터 yaw(도), held - 누르고 있는 버튼, queued - 이번 프레임까지 쌓인 단발 버튼(마지막 step에서 소비되어 비워진다).
        // 출력: 이번 프레임에 실행한 step 수(각 step이 입력 하나). 예측 상태·입력 기록·RenderPosition이 갱신된다.
        // Returns how many simulation steps ran (each generated one input).
        // held: Sprint, Fire and Crouch, applied to every step. queued: Jump, Reload, Slot1-3, Interact, Drop, UseMedkit,
        // UseShieldCell, ToolBuild and ToolHarvest presses (QueuedButtons); they ride on
        // the last step, because GameClient sends one packet per frame holding only the newest
        // MaxInputsPerPacket inputs, so on a hitch frame an earlier step may never be sent. Consumed bits are cleared.
        public int Advance(float deltaTime, Vector2 move, float yaw, InputButtons held, ref InputButtons queued)
        {
            _accumulator = Mathf.Min(_accumulator + deltaTime, MaxAccumulatedSeconds);
            int steps = 0;
            while (_accumulator >= _stepSeconds)
            {
                _accumulator -= _stepSeconds;
                _time += _stepSeconds;
                _doors.Expire(_time);
                // Same test as the loop condition, so this is true exactly on the frame's final step.
                bool lastStep = _accumulator < _stepSeconds;

                InputCommand command;
                _previous = _state;
                if (IsDead)
                {
                    // Zero move and no buttons: the server may process some of these after the respawn, and
                    // then they must not move or fire. Presses made while dead are dropped.
                    command = new InputCommand { Seq = ++LastSeq, Yaw = yaw };
                    if (lastStep) queued = InputButtons.None;
                }
                else
                {
                    var buttons = held & HeldButtons;
                    if (lastStep)
                    {
                        buttons |= queued & QueuedButtons;
                        queued = InputButtons.None;
                    }
                    command = new InputCommand { Seq = ++LastSeq, MoveX = move.x, MoveY = move.y, Yaw = yaw, Buttons = buttons };
                    Simulate(ref _state, command, out StepResult result);
                    Sprinting = result.Sprinting;
                    // D9: E on a door, as Match.ToggleDoor does after the move (the doorway check sees only ourselves).
                    if ((buttons & InputButtons.Interact) != 0 && ActionsAllowed(_state.Mode)) PredictDoorToggle();
                }

                int slot = (int)(command.Seq % HistorySize);
                _inputs[slot] = command;
                _results[slot] = _state;
                steps++;
            }

            float alpha = _accumulator / _stepSeconds;
            _renderError = Vector3.Lerp(_renderError, Vector3.zero, 1f - Mathf.Exp(-ErrorDecayPerSecond * deltaTime));
            RenderPosition = Vector3.Lerp(_previous.Position.ToUnity(), _state.Position.ToUnity(), alpha) + _renderError;
            return steps;
        }

        // 기능: 입력 하나를 서버 Match.Tick과 같은 방식으로 실행한다. 수송기 탑승 중이면 경로를 따라가고, 아니면 주변 충돌 세계를 모아 MovementSimulation.Step을 돌리며, 돌진 중 닫힌 문에 막히면 그 문을 열린 것으로 예측한다.
        // 입력: state - 갱신할 이동 상태, command - 실행할 입력, result - 이번 step 결과(탑승 중이면 default).
        // 출력: 반환값 없음. state가 한 step 진행되고 필요하면 PredictedDoors가 바뀐다.
        // One input, exactly as Match.Tick runs it: ride the route aboard, otherwise one Step against the predicted world;
        // a charging move blocked by a closed door opens it (D9).
        // Before the first ack the tick an input is simulated at is unknown: a rider is held where it is (Step does nothing
        // in Transport mode) instead of riding from a guessed tick.
        private void Simulate(ref MoveState state, in InputCommand command, out StepResult result)
        {
            if (_hasRoute && _hasTickBase && DropTransport.Ride(ref state, command, _route, (uint)(_tickBase + command.Seq)))
            {
                result = default;
                return;
            }
            _collision.Gather(state.Position, _doors.OpenMask, DestroyedHarvestables, Pieces);
            MovementSimulation.Step(ref state, command, _stepSeconds, _collision, GameMap.Terrain, out result);
            if (result.Charging && !result.BlockedBy.IsNone)
            {
                int door = _doors.DoorBlocking(result);
                if (door >= 0) _doors.Predict(door, true, _time);
            }
        }

        // 기능: 서버 Match.ToggleDoor처럼 E 대상 문을 찾아 닫혀 있으면 열고, 열려 있고 내 몸이 문 자리에 겹치지 않으면 닫힌 것으로 예측한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 대상 문이 있으면 PredictedDoors에 예측이 기록된다.
        private void PredictDoorToggle()
        {
            int door = DoorRule.FindTarget(_state.Position, _state.Yaw, GameMap.Doors);
            if (door < 0) return;
            if (!_doors.IsOpen(door)) _doors.Predict(door, true, _time);
            else if (!MovementSimulation.OverlapsAny(_state.Position, MovementSimulation.CollisionHeight(_state.Mode), GameMap.Doors.Slice(door, 1)))
                _doors.Predict(door, false, _time);
        }

        // 기능: 이번 프레임에 만든 최신 입력들에 각 step 예측 결과의 눈 위치에서 조준 지점을 향하는 조준 각도와 보는 Tick을 기록한다.
        // 입력: steps - 이번 프레임에 만든 입력 수, aimPoint - 조준선이 가리키는 지점, fallbackYaw - 방향을 못 구할 때 쓸 카메라 yaw, fallbackPitch - 방향을 못 구할 때 쓸 카메라 pitch, viewTick - 플레이어가 보고 있던 서버 Tick.
        // 출력: 반환값 없음. 해당 입력들의 AimYaw·AimPitch·ViewTick이 바뀐다.
        // D2/D6: GameClient calls this after the camera moved this frame, so the newest `steps` inputs carry
        // the aim the player saw. Aim does not affect MovementSimulation, so the predicted results stay valid.
        // The server fires each input from its position after that input's step, so each step aims at aimPoint
        // from its own predicted result, not from the newest one (older steps of a multi-step frame are behind).
        // When aimPoint is too close to that eye to give a direction, the fallback (camera) angles are sent.
        // Phase 12 D13: the eye height follows that step's mode (lower crouched or sliding), like the server's.
        public void SetAim(int steps, Vector3 aimPoint, float fallbackYaw, float fallbackPitch, float viewTick)
        {
            if (steps > HistorySize) steps = HistorySize;
            if (steps > LastSeq) steps = (int)LastSeq;
            for (int i = 0; i < steps; i++)
            {
                int slot = (int)((LastSeq - (uint)i) % HistorySize);
                Vector3 eye = _results[slot].Position.ToUnity() + new Vector3(0f, AimSolver.EyeHeightOf(_results[slot].Mode), 0f);
                if (!AimSolver.TrySolve(eye, aimPoint, out float aimYaw, out float aimPitch))
                {
                    aimYaw = fallbackYaw;
                    aimPitch = fallbackPitch;
                }
                _inputs[slot].AimYaw = aimYaw;
                _inputs[slot].AimPitch = aimPitch;
                _inputs[slot].ViewTick = viewTick;
            }
        }

        // 기능: 입력 기록에서 seq의 입력을 꺼낸다.
        // 입력: seq - LastSeq - 63 .. LastSeq 범위의 입력 번호.
        // 출력: 해당 슬롯의 InputCommand(범위 밖이면 덮어쓴 다른 입력).
        // One of the last HistorySize inputs (seq in LastSeq - 63 .. LastSeq).
        public InputCommand InputAt(uint seq) => _inputs[(int)(seq % HistorySize)];

        // 기능: 최신 입력을 오래된 것부터 최대 MaxInputsPerPacket개 담은 입력 Packet을 만든다.
        // 입력: packet - 만들어진 입력 Packet.
        // 출력: 입력이 하나 이상 있으면 true와 Packet, 아직 입력이 없으면 false.
        // Newest inputs, oldest first (up to 3). Resending recent inputs covers single packet loss.
        public bool TryBuildInputPacket(out PlayerInputPacket packet)
        {
            packet = default;
            if (LastSeq == 0) return false;

            int count = (int)Math.Min(LastSeq, (uint)ProtocolConstants.MaxInputsPerPacket);
            packet.Count = (byte)count;
            for (int i = 0; i < count; i++)
            {
                uint seq = LastSeq - (uint)(count - 1 - i);
                packet.Set(i, _inputs[(int)(seq % HistorySize)]);
            }
            return true;
        }

        // 기능: 내 사망(PlayerDied)을 받아 부활 전까지 이동 예측을 멈춘다.
        // 입력: 없음.
        // 출력: 반환값 없음. IsDead가 true가 된다.
        // PlayerDied for us (Reliable): stop predicting until the respawn.
        public void SetDead()
        {
            IsDead = true;
        }

        // 기능: 내 부활(PlayerRespawned)을 받아 상태를 서버가 준 스폰 상태로 순간이동시키고 예측을 다시 시작한다. Seq는 이어간다.
        // 입력: spawn - 서버가 정한 스폰 이동 상태.
        // 출력: 반환값 없음. 예측 상태·렌더링 위치가 초기화되고 IsDead가 false가 된다.
        // PlayerRespawned for us: a teleport. State restarts at the spawn point (Phase 12: in the mode the server says),
        // but Seq continues: the server drops any seq it has already taken, so restarting at 1 would make every later
        // input ignored.
        public void Respawn(MoveState spawn)
        {
            IsDead = false;
            _state = spawn;
            _previous = spawn;
            _renderError = Vector3.zero;
            RenderPosition = spawn.Position.ToUnity();
            Sprinting = false;
            LastCorrection = 0f;
        }

        // 기능: Snapshot의 서버 권위 이동 상태를 ack된 입력의 예측 결과와 비교하고, 다르면 서버 상태에서 미확인 입력을 다시 시뮬레이션한다. ack로 seq와 서버 Tick의 대응(tick base)도 정한다.
        // 입력: server - 내 Entity 상태(위치·VelocityY·모드·플래그), self - Snapshot의 self 블록(수평 속도·에너지·Tick 카운터), ackSeq - 서버가 처리한 마지막 입력 번호, serverTick - Snapshot의 서버 Tick.
        // 출력: 반환값 없음. 예측 상태·입력 결과 기록·렌더 오차·보정 통계가 바뀐다. 비정상 값이나 다른 생애의 Snapshot은 무시한다.
        // Phase 12: the owner's movement state comes in two parts, the entity (position, VelocityY, mode and flags) and the
        // snapshot's self block (horizontal velocity, energy, tick counters). serverTick: the snapshot's tick; with ackSeq
        // it gives the tick every later input is simulated at (D5).
        public void Reconcile(in SnapshotEntity server, in SnapshotSelf self, uint ackSeq, uint serverTick)
        {
            // Snapshot data is untrusted and NetClient does not validate it. A non-finite value would
            // replace the predicted state and every later step would stay NaN, so the entity is ignored.
            if (!IsFinite(server.Position.X) || !IsFinite(server.Position.Y) || !IsFinite(server.Position.Z) ||
                !IsFinite(server.VelocityY) || !IsFinite(server.Yaw) ||
                !IsFinite(self.HorizontalVelocity.X) || !IsFinite(self.HorizontalVelocity.Y))
            {
                return;
            }

            // Death and respawn switch IsDead through Reliable events. A snapshot from the other side of that
            // switch (Sequenced, can arrive before or after the event) describes the other life: skip it.
            // Only a dead snapshot can trail a respawn: Sequenced drops older ticks, and ~3 s of dead snapshots
            // arrive between the death and the respawn, so no alive snapshot of the old life comes after them.
            if (server.IsAlive == IsDead) return;

            // D5: only an ack ties a seq to a server tick. Until the first one the held rider steps were no prediction, so
            // the replay from that ack is not counted as a correction.
            bool firstTickBase = ackSeq > 0 && !_hasTickBase;
            if (ackSeq > 0)
            {
                _tickBase = (long)serverTick - ackSeq;
                _hasTickBase = true;
            }

            var authoritative = new MoveState
            {
                Position = server.Position,
                VelocityY = server.VelocityY,
                Yaw = server.Yaw,
                Mode = server.Mode,
                HorizontalVelocity = self.HorizontalVelocity,
                EnergySpent = (ushort)(MoveState.MaxEnergyHundredths - Math.Min(self.Energy, (ushort)MoveState.MaxEnergyHundredths)),
                EnergyDelayTicks = self.EnergyDelayTicks,
                ModeTicks = self.ModeTicks,
                Exhausted = server.IsExhausted,
            };
            if (IsDead)
            {
                // The server does not move a dead player: its position is final, nothing to replay.
                Snap(authoritative);
                return;
            }

            // Ack 0 says nothing about our inputs: the server has not processed any yet. Once inputs are
            // predicted the local state is newer than this snapshot, so snapping would stutter on join.
            // Phase 12 D16: unless the server is in another mode (a resume mid-air): then its state is the better guess.
            // Aboard before the first ack the rider is held: at the server's position.
            if (ackSeq == 0 && LastSeq > 0)
            {
                if (_state.Mode != authoritative.Mode) Snap(authoritative);
                else if (authoritative.Mode == MovementMode.Transport) Hold(authoritative);
                return;
            }

            if (ackSeq == 0 || ackSeq > LastSeq || LastSeq - ackSeq >= HistorySize)
            {
                // Nothing to replay from (no input sent yet, or history already overwritten).
                Snap(authoritative);
                return;
            }

            MoveState predicted = _results[(int)(ackSeq % HistorySize)];
            if (Matches(predicted, authoritative)) return;

            // Misprediction: restart from the server state and replay unacknowledged inputs.
            System.Numerics.Vector3 oldPosition = _state.Position;
            _state = authoritative;
            _previous = authoritative;
            _results[(int)(ackSeq % HistorySize)] = authoritative;
            for (uint seq = ackSeq + 1; seq <= LastSeq; seq++)
            {
                int slot = (int)(seq % HistorySize);
                _previous = _state;
                Simulate(ref _state, _inputs[slot], out _);
                _results[slot] = _state;
            }

            // Keep the rendered position continuous and let the difference decay, unless it is large.
            Vector3 correction = (oldPosition - _state.Position).ToUnity();
            _renderError = correction.sqrMagnitude > SnapDistance * SnapDistance ? Vector3.zero : _renderError + correction;
            if (firstTickBase && predicted.Mode == MovementMode.Transport) return;   // held, not mispredicted
            LastCorrection = correction.magnitude;
            Corrections++;
        }

        // 기능: 보정 횟수를 세지 않고 서버 상태를 그대로 받아들인다(첫 ack 전 수송기 탑승자).
        // 입력: authoritative - 서버 권위 이동 상태.
        // 출력: 반환값 없음. 예측 상태가 서버 상태로 바뀌고 렌더 오차가 0이 된다.
        // The server's state as it is, without counting a correction (a held rider before the first ack).
        private void Hold(in MoveState authoritative)
        {
            _state = authoritative;
            _previous = authoritative;
            _renderError = Vector3.zero;
        }

        // 기능: 다시 시뮬레이션할 입력 없이 서버 상태로 즉시 맞추고, 살아 있는데 예측과 달랐으면 보정으로 센다.
        // 입력: authoritative - 서버 권위 이동 상태.
        // 출력: 반환값 없음. 예측 상태가 서버 상태로 바뀌고 LastCorrection·Corrections와 렌더 오차가 갱신된다.
        private void Snap(in MoveState authoritative)
        {
            LastCorrection = (_state.Position - authoritative.Position).Length();
            if (!IsDead && !Matches(_state, authoritative)) Corrections++;
            _state = authoritative;
            _previous = authoritative;
            _renderError = Vector3.zero;
        }

        // 기능: 예측 상태가 서버 상태와 같은지 비교한다. 위치·VelocityY·수평 속도는 MatchEpsilon 안, 모드·에너지·Tick 카운터·탈진은 정확히 같아야 한다.
        // 입력: predicted - 예측한 이동 상태, server - 서버 권위 이동 상태.
        // 출력: 같다고 보면 true, 다르면 false.
        // Position, VelocityY and the horizontal velocity within the snapshot's quantization (1/256), and the exact mode,
        // energy, tick counters and exhaustion.
        private static bool Matches(in MoveState predicted, in MoveState server)
        {
            return System.Numerics.Vector3.DistanceSquared(predicted.Position, server.Position) < MatchEpsilon * MatchEpsilon &&
                   Mathf.Abs(predicted.VelocityY - server.VelocityY) < MatchEpsilon &&
                   System.Numerics.Vector2.DistanceSquared(predicted.HorizontalVelocity, server.HorizontalVelocity) < MatchEpsilon * MatchEpsilon &&
                   predicted.Mode == server.Mode && predicted.EnergySpent == server.EnergySpent &&
                   predicted.EnergyDelayTicks == server.EnergyDelayTicks && predicted.ModeTicks == server.ModeTicks &&
                   predicted.Exhausted == server.Exhausted;
        }

        // 기능: 값이 NaN이나 무한대가 아닌지 확인한다.
        // 입력: value - 검사할 값.
        // 출력: 유한한 수면 true, 아니면 false.
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
