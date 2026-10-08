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
    // Phase 19 D9, D15: seated in a vehicle (VehicleStates says so; the mode stays Ground) the movement is not predicted or
    // reconciled: inputs are still made and recorded (the server reads them as the vehicle's), the position is the seat, no
    // door is predicted and no action is allowed. In the driver seat the owned VehiclePredictor steps with every input. Leaving
    // the seat snaps the movement prediction to the next snapshot at or after the exit tick (the history is stale by then).
    public sealed class LocalPlayerPredictor
    {
        public const int HistorySize = 64;                 // ~2 s at 30 Hz; older unacked input -> snap
        private const float SnapDistance = 2f;             // corrections larger than this are not smoothed
        private const float ErrorDecayPerSecond = 10f;
        private const float MatchEpsilon = 0.01f;
        private const float MaxAccumulatedSeconds = 0.25f; // after a hitch, do not burst-simulate

        // Held buttons go into every step; queued presses only into a frame's last step (see Advance).
        // Phase 14 D7: InteractHeld (E down) is held: a revive or reboot goes on only while every input carries it.
        private const InputButtons HeldButtons = InputButtons.Sprint | InputButtons.Fire | InputButtons.Crouch | InputButtons.InteractHeld;
        private const InputButtons QueuedButtons = InputButtons.Jump | InputButtons.Reload | InputButtons.Slot1 | InputButtons.Slot2 |
                                                   InputButtons.Slot3 | InputButtons.Interact | InputButtons.Drop |
                                                   InputButtons.UseMedkit | InputButtons.UseShieldCell | InputButtons.ToolBuild |
                                                   InputButtons.ToolHarvest | InputButtons.ThrowGrenade;   // Phase 17 D9: 6

        private readonly InputCommand[] _inputs = new InputCommand[HistorySize];
        private readonly MoveState[] _results = new MoveState[HistorySize];
        // Phase 19: whether the input of each slot was made while seated (no action for it, like a non-acting mode).
        private readonly bool[] _seatedAt = new bool[HistorySize];
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
        // Phase 19 D15: after leaving a seat, the next snapshot at or after this tick replaces the state (no comparison).
        private bool _snapPending;
        private uint _snapFromTick;
        // Phase 19 review: the seq of the seated input that carried the exit press (0 = none). The server unseats on that input
        // and walks the next ones, so later seated inputs drop the held brake (Jump would be a jump on foot) until the exit is
        // known (EndSeated) or refused (ExitRefused).
        private uint _exitSeq;

        // 기능: 이동 예측기를 만든다(Phase 19: 운전 예측기를 같이 만든다).
        // 입력: simHz - 서버 Tick률, spawnState - 시작 상태, doors - 예측 문(null이면 새로 만든다).
        // 출력: 시작 상태에서 예측을 시작하는 객체.
        public LocalPlayerPredictor(int simHz, MoveState spawnState, PredictedDoors doors = null)
        {
            _stepSeconds = 1f / simHz;
            _doors = doors ?? new PredictedDoors();
            _state = spawnState;
            _previous = spawnState;
            RenderPosition = spawnState.Position.ToUnity();
            Vehicle = new VehiclePredictor(this, _stepSeconds);
        }

        // Phase 19 D9: the driver's vehicle prediction (active only while we drive).
        public VehiclePredictor Vehicle { get; }
        // Phase 19 D15: seated in a vehicle (from VehicleStates), and in which seat (-1 = none).
        public bool Seated { get; private set; }
        public int Seat { get; private set; } = -1;
        // The predicted doors' mask (the vehicle prediction gathers with it, like the movement).
        internal byte DoorMask => _doors.OpenMask;
        // Phase 19 review: the seq of the input that asked to get out, 0 when none is pending.
        public uint ExitSeq => _exitSeq;
        // Phase 19 D15: may our newest state act (not seated and an acting mode). The server takes no action from a seated input.
        public bool CanAct => !Seated && ActionsAllowed(_state.Mode);

        public uint LastSeq { get; private set; }
        public Vector3 RenderPosition { get; private set; }
        public float RenderYaw => _state.Yaw;
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
        public float HorizontalSpeed => _state.HorizontalVelocity.Length();
        public float VerticalSpeed => _state.VelocityY;
        // How far the newest correction moved the prediction (m), and how many corrections there were.
        public float LastCorrection { get; private set; }
        public int Corrections { get; private set; }
        // The server tick the newest input will be simulated at (valid once HasTickBase).
        public uint PredictedTick => (uint)(_tickBase + LastSeq);
        // The first snapshot that acks an input gives the tick base; before it the rider is not ride-predicted.
        public bool HasTickBase => _hasTickBase;
        // The (fractional) server tick RenderPosition is drawn at: between the two newest steps, as RenderPosition. For the
        // transport view while riding, so the transport and the rider (and its camera) move together. Valid once HasTickBase.
        public double RenderTick => (double)(_tickBase + LastSeq - 1) + (double)_accumulator / _stepSeconds;

        // Phase 13 D3, D6: the harvestables the server says are destroyed (HarvestStates) and the confirmed pieces
        // (BuildStore; never the predicted ones). Replays use the newest values, like the doors.
        public ulong DestroyedHarvestables { get; set; }
        public PieceGrid Pieces { get; set; }
        // Phase 16 D4: the loot containers and supply drops as the server said (null = none). A door is not predicted when
        // the server would open a nearer container or supply drop with the same E instead.
        public LootState Loot { get; set; }

        // D5: the match's transport route (TransportRoute). Kept until the next one or ClearRoute; it acts only in Transport
        // mode.
        public void SetRoute(in DropRoute route)
        {
            _route = route;
            _hasRoute = true;
        }

        // A new round's countdown (MatchState back to WaitingForPlayers or Starting): the old route is over.
        public void ClearRoute()
        {
            _hasRoute = false;
        }

        // 기능: 서버가 입력 seq로 행동하게 하는지 본다(D12: 그 이동 뒤 모드. Phase 19: 앉은 채 만든 입력은 행동하지 않는다).
        // 입력: seq - 최근 HistorySize개 안의 입력 번호.
        // 출력: 행동할 수 있으면 true.
        public bool ActionsAllowedAt(uint seq)
        {
            int slot = (int)(seq % HistorySize);
            return !_seatedAt[slot] && ActionsAllowed(_results[slot].Mode);
        }

        public static bool ActionsAllowed(MovementMode mode) =>
            mode == MovementMode.Ground || mode == MovementMode.Crouch || mode == MovementMode.Slide;

        // 기능: 프레임 시간만큼 고정 Tick 시뮬레이션을 돌리고 Step마다 입력 하나를 만든다(죽어 있으면 빈 입력).
        //   Phase 19: 앉아 있으면 이동·문 예측 없이 입력만 만들고(누르고 있는 Jump = 제동도 모든 Step에), 운전 중이면 차량을 예측하며
        //   위치를 좌석으로 둔다. 내리기 누름(Interact)을 실은 입력 뒤의 앉은 입력에서는 Jump를 뺀다(서버가 걸어서 처리해 점프가 된다).
        // 입력: deltaTime - 프레임 시간, move - 이동 입력, yaw - 카메라 방향, held - 누르고 있는 버튼(Sprint·Fire·Crouch·
        //   Phase 14 InteractHeld, 모든 Step에 들어간다. Phase 19: 앉아 있으면 Jump도), queued - 누른 순간 버튼(마지막 Step에만, 쓴 비트는
        //   지운다. Phase 17: ThrowGrenade 포함).
        // 출력: 돈 Step 수(만든 입력 수).
        // held: Sprint, Fire, Crouch and InteractHeld, applied to every step. queued: Jump, Reload, Slot1-3, Interact, Drop, UseMedkit,
        // UseShieldCell, the tool keys and (Phase 17) ThrowGrenade presses; they ride on
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
                else if (Seated)
                {
                    // Phase 19 D10: the vehicle reads Jump (brake) and Sprint (boost) as held on every tick. After an exit press the
                    // brake is dropped: the server walks those inputs, and the car without a driver brakes by itself.
                    var buttons = held & (HeldButtons | InputButtons.Jump);
                    if (_exitSeq != 0) buttons &= ~InputButtons.Jump;
                    if (lastStep)
                    {
                        buttons |= queued & QueuedButtons;
                        queued = InputButtons.None;
                    }
                    command = new InputCommand { Seq = ++LastSeq, MoveX = move.x, MoveY = move.y, Yaw = yaw, Buttons = buttons };
                    if (_exitSeq == 0 && (buttons & InputButtons.Interact) != 0) _exitSeq = command.Seq;
                    Sprinting = false;
                    if (Vehicle.Active)
                    {
                        Vehicle.Step(command);
                        _state.Position = Vehicle.SeatFeet(Seat);
                    }
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
                _seatedAt[slot] = Seated && !IsDead;
                steps++;
            }

            float alpha = _accumulator / _stepSeconds;
            if (Seated && !IsDead)
            {
                // Phase 19 D9: the seat of the vehicle as drawn (the driver's prediction; a passenger's seat comes from
                // SetSeatPosition).
                _previous = _state;
                _renderError = Vector3.zero;
                if (Vehicle.Active)
                {
                    Vehicle.UpdateRender(alpha, deltaTime);
                    RenderPosition = Vehicle.RenderSeat(Seat).ToUnity();
                }
                return steps;
            }
            _renderError = Vector3.Lerp(_renderError, Vector3.zero, 1f - Mathf.Exp(-ErrorDecayPerSecond * deltaTime));
            RenderPosition = Vector3.Lerp(_previous.Position.ToUnity(), _state.Position.ToUnity(), alpha) + _renderError;
            return steps;
        }

        // 기능: 차량에 앉았음을 알린다(Phase 19 D15: VehicleStates가 나를 Driver·Passenger로 적었다). 이동 예측이 멈추고 모드는 서버처럼
        //   Ground, 속도는 0이 된다. 운전석이 아니면 운전 예측을 멈춘다.
        // 입력: seat - 좌석 번호(0 운전석, 1 조수석).
        // 출력: 반환값 없음.
        public void SetSeated(int seat)
        {
            if (!Seated)
            {
                _state.Mode = MovementMode.Ground;
                _state.HorizontalVelocity = default;
                _state.VelocityY = 0f;
                _previous = _state;
                _renderError = Vector3.zero;
                Sprinting = false;
            }
            Seated = true;
            Seat = seat;
            _snapPending = false;
            if (seat != VehicleSettings.DriverSeat) Vehicle.Stop();
        }

        // 기능: 좌석 발 위치를 정한다(조수석: 그리는 차량의 좌석, Phase 19 D9). 앉아 있지 않거나 죽었으면 무시한다.
        // 입력: feet - 좌석 발 위치.
        // 출력: 반환값 없음. 예측 위치와 그리는 위치가 좌석이 된다.
        public void SetSeatPosition(Vector3 feet)
        {
            if (!Seated || IsDead) return;
            _state.Position = feet.ToNumerics();
            _previous = _state;
            RenderPosition = feet;
        }

        // 기능: 내리기 요청이 거절되었는지 본다(Phase 19 리뷰: 내릴 자리가 없으면 서버는 내리지 않는다). 그 입력까지 처리한 VehicleStates가
        //   여전히 나를 앉은 사람으로 적었으면 대기 중인 내리기를 지워 제동(Jump)이 다시 나가게 한다.
        // 입력: ackInputSeq - 나를 앉은 사람으로 적은 VehicleStates의 AckInputSeq.
        // 출력: 반환값 없음.
        public void ExitRefused(uint ackInputSeq)
        {
            if (_exitSeq != 0 && ackInputSeq >= _exitSeq) _exitSeq = 0;
        }

        // 기능: 차량에서 내렸음을 알린다(Phase 19 D15). 대기 중인 내리기를 지우고 운전 예측을 멈추고, fromTick 이후의 다음 Snapshot이 비교 없이 상태를 바꾸게 한다
        //   (내리기는 순간이동이고 앉은 동안의 이동 기록은 낡았다).
        // 입력: fromTick - 내림을 알린 VehicleStates의 ServerTick.
        // 출력: 반환값 없음.
        public void EndSeated(uint fromTick)
        {
            if (!Seated) return;
            Seated = false;
            Seat = -1;
            _exitSeq = 0;
            Vehicle.Stop();
            _snapPending = true;
            _snapFromTick = fromTick;
        }

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

        // 기능: E 누름의 문 열기·닫기를 예측한다(서버 Match.ToggleDoor와 같은 순서). Phase 16 D4: 같은 E로 서버가 더 가까운 Container나
        //   착지한 Supply Drop을 열 경우(ContainerRule.PreferContainer)에는 문을 예측하지 않는다.
        // 입력: 없음(이번 Step 뒤의 예측 상태).
        // 출력: 반환값 없음. 예측 문 상태가 바뀔 수 있다.
        private void PredictDoorToggle()
        {
            int door = DoorRule.FindTarget(_state.Position, _state.Yaw, GameMap.Doors);
            if (door < 0) return;
            if (Loot != null && Loot.FindTarget(_state.Position, _state.Yaw, out float lootSq) >= 0 &&
                ContainerRule.PreferContainer(_state.Position, door, GameMap.Doors, lootSq))
                return;
            if (!_doors.IsOpen(door)) _doors.Predict(door, true, _time);
            else if (!MovementSimulation.OverlapsAny(_state.Position, MovementSimulation.CollisionHeight(_state.Mode), GameMap.Doors.Slice(door, 1)))
                _doors.Predict(door, false, _time);
        }

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

        // One of the last HistorySize inputs (seq in LastSeq - 63 .. LastSeq).
        public InputCommand InputAt(uint seq) => _inputs[(int)(seq % HistorySize)];

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

        // PlayerDied for us (Reliable): stop predicting until the respawn.
        // 기능: 내 사망을 적용한다(예측 정지, Phase 19: 운전 예측도 멈춘다. 대기 중인 내리기도 지운다. 탄 상태는 서버가 먼저 내리므로 VehicleStates가 지운다).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void SetDead()
        {
            IsDead = true;
            _exitSeq = 0;
            Vehicle.Stop();
        }

        // PlayerRespawned for us: a teleport. State restarts at the spawn point (Phase 12: in the mode the server says),
        // but Seq continues: the server drops any seq it has already taken, so restarting at 1 would make every later
        // input ignored.
        // 기능: 부활 순간이동을 적용한다(Phase 19: 탄 상태, 대기 중인 Snap과 내리기도 지운다).
        // 입력: spawn - 부활 상태.
        // 출력: 반환값 없음.
        public void Respawn(MoveState spawn)
        {
            IsDead = false;
            Seated = false;
            Seat = -1;
            _snapPending = false;
            _exitSeq = 0;
            Vehicle.Stop();
            _state = spawn;
            _previous = spawn;
            _renderError = Vector3.zero;
            RenderPosition = spawn.Position.ToUnity();
            Sprinting = false;
            LastCorrection = 0f;
        }

        // Phase 12: the owner's movement state comes in two parts, the entity (position, VelocityY, mode and flags) and the
        // snapshot's self block (horizontal velocity, energy, tick counters). serverTick: the snapshot's tick; with ackSeq
        // it gives the tick every later input is simulated at (D5).
        // 기능: 내 Snapshot으로 이동 예측을 맞춘다. Phase 19 D15: 앉아 있으면 건너뛰고, 내린 뒤에는 내린 Tick 이후의 첫 Snapshot으로 비교 없이
        //   상태를 바꾸고 ack 뒤 입력을 재실행한다(교정 오프셋 없이 바로 옮김).
        // 입력: server - 내 엔티티 기록, self - Self 블록, ackSeq - 처리된 마지막 입력, serverTick - Snapshot Tick.
        // 출력: 반환값 없음. 예측 상태가 바뀔 수 있다.
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
            // Phase 19 D15: seated, the server places us on the seat and the vehicle prediction does the work. A snapshot older
            // than the exit still shows the seat.
            if (Seated || (_snapPending && serverTick < _snapFromTick)) return;

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
                _snapPending = false;
                Snap(authoritative);
                return;
            }

            MoveState predicted = _results[(int)(ackSeq % HistorySize)];
            bool exitSnap = _snapPending;
            _snapPending = false;
            if (!exitSnap && Matches(predicted, authoritative)) return;

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

            // Keep the rendered position continuous and let the difference decay, unless it is large. Phase 19: leaving a seat
            // always jumps (the seat and the exit spot are different places, not a misprediction).
            Vector3 correction = (oldPosition - _state.Position).ToUnity();
            _renderError = exitSnap || correction.sqrMagnitude > SnapDistance * SnapDistance ? Vector3.zero : _renderError + correction;
            if (exitSnap) return;
            if (firstTickBase && predicted.Mode == MovementMode.Transport) return;   // held, not mispredicted
            LastCorrection = correction.magnitude;
            Corrections++;
        }

        // The server's state as it is, without counting a correction (a held rider before the first ack).
        private void Hold(in MoveState authoritative)
        {
            _state = authoritative;
            _previous = authoritative;
            _renderError = Vector3.zero;
        }

        private void Snap(in MoveState authoritative)
        {
            LastCorrection = (_state.Position - authoritative.Position).Length();
            if (!IsDead && !Matches(_state, authoritative)) Corrections++;
            _state = authoritative;
            _previous = authoritative;
            _renderError = Vector3.zero;
        }

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

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
