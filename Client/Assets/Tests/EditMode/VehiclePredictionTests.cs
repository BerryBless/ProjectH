using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;
using Num = System.Numerics;

namespace ProjectH.Client.Tests
{
    // Phase 19 D9, D15: the driver's prediction (VehicleSimulation.Step against the client's CollisionWorld, reconciled with the
    // VehicleStates record and its ack) and LocalPlayerPredictor while seated (no movement, no action, no door, the brake held,
    // the snap on leaving the seat).
    public class VehiclePredictionTests
    {
        private const int SimHz = 30;
        private const float Step = 1f / SimHz;
        private const ushort Me = 5;

        private static Num.Vector3 SpawnPoint => VehicleSpawns.All[0];
        private static float SpawnHeading => VehicleSpawns.Heading(0);

        // 기능: 예측기를 한 번에 한 걸음씩 steps번 진행시킨다(Yaw 0). press는 마지막 걸음에만 실린다.
        // 입력: predictor - 대상 예측기, steps - 걸음 수(0이면 걸음 없이 Render 위치만 갱신), move - 이동 입력, held - 누르고 있는 버튼, press - 마지막 걸음에 넣는 버튼.
        // 출력: 반환값 없음. 예측기에 steps개의 입력이 쌓이고 상태가 갱신된다.
        private static void AdvanceSteps(LocalPlayerPredictor predictor, int steps, Vector2 move, InputButtons held = InputButtons.None,
            InputButtons press = InputButtons.None)
        {
            // One step per call: Advance caps a frame at 0.25 s (7 steps at 30 Hz). The press rides the last step.
            for (int i = 0; i < steps; i++)
            {
                InputButtons queued = i == steps - 1 ? press : InputButtons.None;
                predictor.Advance(Step, move, 0f, held, ref queued);
            }
            if (steps == 0)
            {
                InputButtons none = InputButtons.None;
                predictor.Advance(0.0005f, move, 0f, held, ref none);
            }
        }

        // 기능: 서버의 차량 Step을 fromSeq..toSeq 입력에 대해 돌린다(걸음마다 그 위치에서 Gather, 문 모두 닫힘, 조각 없음).
        // 입력: state - 시작 차량 상태, inputs - 입력을 꺼낼 예측기, fromSeq - 첫 입력 Seq, toSeq - 마지막 입력 Seq.
        // 출력: 입력을 모두 돌린 뒤의 VehicleMove.
        // The server's vehicle step for the same inputs: Gather at the position before the step, every door closed, nothing
        // destroyed, no pieces.
        private static VehicleMove ServerRun(VehicleMove state, LocalPlayerPredictor inputs, uint fromSeq, uint toSeq)
        {
            var world = new CollisionWorld();
            for (uint seq = fromSeq; seq <= toSeq; seq++)
            {
                world.Gather(state.Position, 0, 0, null);
                VehicleSimulation.Step(ref state, inputs.InputAt(seq), true, Step, world, GameMap.Terrain, out _);
            }
            return state;
        }

        // 기능: 차량 상태를 Me가 운전하는 Active 차량 레코드로 만든다(위치·방향·속도는 wire 양자화).
        // 입력: move - 차량 운동 상태, id - 차량 ID.
        // 출력: 체력이 가득한 VehicleRecord.
        private static VehicleRecord RecordOf(in VehicleMove move, byte id = 1) => new VehicleRecord
        {
            Id = id,
            State = VehicleState.Active,
            Driver = Me,
            Position = new Num.Vector3(VehicleRecord.QuantizeFixed(move.Position.X), VehicleRecord.QuantizeFixed(move.Position.Y),
                VehicleRecord.QuantizeFixed(move.Position.Z)),
            Heading = VehicleRecord.QuantizeHeading(move.Heading),
            Speed = VehicleRecord.QuantizeFixed(move.Speed),
            Steer = move.Steer,
            Health = VehiclePrompt.MaxHealth,
        };

        private static VehicleMove Start => new VehicleMove
        {
            Position = new Num.Vector3(SpawnPoint.X, VehicleSimulation.GroundHeight(SpawnPoint.X, SpawnPoint.Z, SpawnHeading, GameMap.Terrain),
                SpawnPoint.Z),
            Heading = SpawnHeading,
        };

        // 기능: 운전석에 앉아 차량 예측이 켜진 예측기를 만든다. 양쪽 다 양자화된 레코드에서 시작한다.
        // 입력: start - 결과: 서버 복제에 쓸 양자화된 시작 차량 상태.
        // 출력: 첫 레코드를 Reconcile한 LocalPlayerPredictor.
        private static LocalPlayerPredictor Driving(out VehicleMove start)
        {
            // Both sides start from the record as the wire carries it (quantized).
            start = RecordToMove(RecordOf(Start));
            var predictor = new LocalPlayerPredictor(SimHz, new MoveState { Position = start.Position });
            predictor.SetSeated(VehicleSettings.DriverSeat);
            predictor.Vehicle.Reconcile(RecordOf(start), 0);   // the first record naming us as Driver, nothing acked yet
            return predictor;
        }

        [Test]
        public void TheDriversPrediction_RunsTheServersStep()
        {
            LocalPlayerPredictor predictor = Driving(out VehicleMove start);
            Assert.IsTrue(predictor.Vehicle.Active);
            AdvanceSteps(predictor, 12, new Vector2(0.5f, 1f), InputButtons.Sprint);
            VehicleMove server = ServerRun(start, predictor, 1, predictor.LastSeq);
            Assert.AreEqual(server.Position.X, predictor.Vehicle.State.Position.X, 1e-4f);
            Assert.AreEqual(server.Position.Z, predictor.Vehicle.State.Position.Z, 1e-4f);
            Assert.AreEqual(server.Heading, predictor.Vehicle.State.Heading, 1e-3f);
            Assert.Greater(predictor.Vehicle.State.Speed, 0f);
            // The player sits on the predicted driver seat; the movement itself did not run.
            Num.Vector3 seat = VehicleSimulation.SeatPosition(server.Position, server.Heading, VehicleSettings.DriverSeat);
            Assert.AreEqual(seat.X, predictor.PredictedPosition.x, 1e-3f);
            Assert.AreEqual(seat.Z, predictor.PredictedPosition.z, 1e-3f);
        }

        [Test]
        public void AMatchingRecord_IsNoCorrection()
        {
            LocalPlayerPredictor predictor = Driving(out VehicleMove start);
            AdvanceSteps(predictor, 8, Vector2.up);
            VehicleMove server = ServerRun(start, predictor, 1, 5);
            predictor.Vehicle.Reconcile(RecordOf(server), 5);
            Assert.AreEqual(0, predictor.Vehicle.Corrections);
        }

        [Test]
        public void ADifferentRecord_ReplaysTheUnackedInputs_AndSmoothsASmallError()
        {
            LocalPlayerPredictor predictor = Driving(out VehicleMove start);
            AdvanceSteps(predictor, 8, Vector2.up);
            VehicleMove server = ServerRun(start, predictor, 1, 4);
            server.Speed += 1f;   // the server's car was faster after input 4 (e.g. a repeated input without the brake)
            predictor.Vehicle.Reconcile(RecordOf(server), 4);
            Assert.AreEqual(1, predictor.Vehicle.Corrections);
            VehicleMove expected = ServerRun(RecordToMove(RecordOf(server)), predictor, 5, 8);
            Assert.AreEqual(expected.Position.X, predictor.Vehicle.State.Position.X, 1e-4f);
            Assert.AreEqual(expected.Position.Z, predictor.Vehicle.State.Position.Z, 1e-4f);
            // Under SnapDistance: the drawn car keeps its place and eases over.
            AdvanceSteps(predictor, 0, Vector2.up);
            Assert.Less(predictor.Vehicle.LastCorrection, VehiclePredictor.SnapDistance);
            Assert.Greater(Num.Vector3.Distance(predictor.Vehicle.RenderPosition, predictor.Vehicle.State.Position), 0.05f);
        }

        [Test]
        public void ALargeError_Snaps()
        {
            LocalPlayerPredictor predictor = Driving(out VehicleMove start);
            AdvanceSteps(predictor, 4, Vector2.zero);   // standing still
            VehicleMove server = start;
            server.Position.X += 3f;
            predictor.Vehicle.Reconcile(RecordOf(server), 4);
            AdvanceSteps(predictor, 0, Vector2.zero);
            Assert.Greater(predictor.Vehicle.LastCorrection, VehiclePredictor.SnapDistance);
            Assert.AreEqual(predictor.Vehicle.State.Position.X, predictor.Vehicle.RenderPosition.X, 1e-3f);
        }

        [Test]
        public void ARecordOfAnotherVehicle_StartsOverFromIt()
        {
            LocalPlayerPredictor predictor = Driving(out VehicleMove start);
            AdvanceSteps(predictor, 3, Vector2.up);
            VehicleMove other = start;
            other.Position.X += 10f;
            predictor.Vehicle.Reconcile(RecordOf(other, id: 2), 3);
            Assert.AreEqual(2, predictor.Vehicle.VehicleId);
            Assert.AreEqual(0, predictor.Vehicle.Corrections);   // a start, not a misprediction
            Assert.AreEqual(VehicleRecord.QuantizeFixed(other.Position.X), predictor.Vehicle.State.Position.X, 1e-4f);
        }

        [Test]
        public void Seated_MakesInputsWithTheBrakeHeld_ButNoMoveNoActionNoDoor()
        {
            // Right at a door, facing it (MovementPredictionTests.E_OnADoor_IsPredicted...): seated, E is the exit, not the door.
            var doors = new PredictedDoors();
            var at = new Num.Vector3(-54f, GameMap.Terrain.Height(-54f, 48.5f), 48.5f);
            var predictor = new LocalPlayerPredictor(SimHz, new MoveState { Position = at }, doors);
            predictor.SetSeated(VehicleSettings.PassengerSeat);
            Assert.IsFalse(predictor.Vehicle.Active);
            AdvanceSteps(predictor, 3, Vector2.up, InputButtons.Jump | InputButtons.Sprint, InputButtons.Interact);
            Assert.AreEqual(at.Z, predictor.PredictedPosition.z, 1e-5f);
            Assert.IsFalse(doors.IsOpen(0));
            Assert.IsFalse(predictor.CanAct);
            Assert.IsFalse(predictor.ActionsAllowedAt(predictor.LastSeq));
            InputCommand sent = predictor.InputAt(predictor.LastSeq);
            Assert.AreEqual(1f, sent.MoveY);
            Assert.AreNotEqual(InputButtons.None, sent.Buttons & InputButtons.Jump);       // held brake on every step
            Assert.AreNotEqual(InputButtons.None, predictor.InputAt(predictor.LastSeq - 1).Buttons & InputButtons.Jump);
            Assert.AreNotEqual(InputButtons.None, sent.Buttons & InputButtons.Interact);   // the press rides the last step

            // A passenger sits where the vehicle is drawn.
            predictor.SetSeatPosition(new Vector3(1f, 2f, 3f));
            Assert.AreEqual(new Vector3(1f, 2f, 3f), predictor.RenderPosition);

            // On foot a held Space is not a jump on every step (only the press is).
            var walker = new LocalPlayerPredictor(SimHz, new MoveState { Position = at });
            AdvanceSteps(walker, 2, Vector2.zero, InputButtons.Jump);
            Assert.AreEqual(InputButtons.None, walker.InputAt(walker.LastSeq).Buttons & InputButtons.Jump);
            Assert.IsTrue(walker.ActionsAllowedAt(walker.LastSeq));
        }

        [Test]
        public void LeavingTheSeat_SnapsToTheFirstSnapshotFromTheExitTick()
        {
            LocalPlayerPredictor predictor = Driving(out VehicleMove start);
            AdvanceSteps(predictor, 6, Vector2.up);
            var exitSpot = new Num.Vector3(start.Position.X + 2f, start.Position.Y, start.Position.Z);
            var entity = new SnapshotEntity { Position = exitSpot, Flags = SnapshotEntity.AliveFlag };
            var self = new SnapshotSelf { Energy = MoveState.MaxEnergyHundredths };

            // Seated, snapshots are ignored.
            predictor.Reconcile(entity, self, 6, 100);
            Assert.AreNotEqual(exitSpot.X, predictor.PredictedPosition.x);

            predictor.EndSeated(110);
            Assert.IsFalse(predictor.Seated);
            Assert.IsFalse(predictor.Vehicle.Active);
            predictor.Reconcile(entity, self, 6, 105);          // older than the exit: still the seat
            Assert.AreNotEqual(exitSpot.X, predictor.PredictedPosition.x);
            predictor.Reconcile(entity, self, 6, 110);          // the exit tick: taken as it is, no smoothing
            Assert.AreEqual(exitSpot.X, predictor.PredictedPosition.x, 1e-4f);
            AdvanceSteps(predictor, 0, Vector2.zero);
            Assert.AreEqual(exitSpot.X, predictor.RenderPosition.x, 1e-3f);
            Assert.IsTrue(predictor.CanAct);
        }

        [Test]
        public void AfterTheExitPress_NoInputCarriesTheBrake()
        {
            LocalPlayerPredictor predictor = Driving(out _);
            AdvanceSteps(predictor, 3, Vector2.up, InputButtons.Jump);                          // braking
            AdvanceSteps(predictor, 1, Vector2.up, InputButtons.Jump, InputButtons.Interact);   // E while Space is held
            uint exitSeq = predictor.LastSeq;
            Assert.AreEqual(exitSeq, predictor.ExitSeq);
            Assert.AreNotEqual(InputButtons.None, predictor.InputAt(exitSeq).Buttons & InputButtons.Jump);   // up to the exit: brake
            AdvanceSteps(predictor, 5, Vector2.up, InputButtons.Jump);                          // the server walks these
            for (uint seq = exitSeq + 1; seq <= predictor.LastSeq; seq++)
                Assert.AreEqual(InputButtons.None, predictor.InputAt(seq).Buttons & InputButtons.Jump, "seq " + seq);

            predictor.EndSeated(100);
            Assert.AreEqual(0u, predictor.ExitSeq);
        }

        [Test]
        public void ARefusedExit_BringsTheBrakeBack()
        {
            LocalPlayerPredictor predictor = Driving(out _);
            AdvanceSteps(predictor, 1, Vector2.zero, InputButtons.Jump, InputButtons.Interact);
            uint exitSeq = predictor.LastSeq;
            AdvanceSteps(predictor, 2, Vector2.zero, InputButtons.Jump);
            predictor.ExitRefused(exitSeq - 1);                 // not processed yet: still waiting
            Assert.AreEqual(exitSeq, predictor.ExitSeq);
            predictor.ExitRefused(exitSeq);                     // processed and still seated: refused
            Assert.AreEqual(0u, predictor.ExitSeq);
            AdvanceSteps(predictor, 1, Vector2.zero, InputButtons.Jump);
            Assert.AreNotEqual(InputButtons.None, predictor.InputAt(predictor.LastSeq).Buttons & InputButtons.Jump);

            // Death and a respawn forget a pending exit too.
            AdvanceSteps(predictor, 1, Vector2.zero, InputButtons.None, InputButtons.Interact);
            Assert.AreNotEqual(0u, predictor.ExitSeq);
            predictor.SetDead();
            Assert.AreEqual(0u, predictor.ExitSeq);
        }

        // Phase 19 review (S8): after any exit the server ignores Jump on the inputs it walks until one comes without Jump.
        // 기능: 내린 뒤의 첫 Snapshot(차 옆 2 m 지면에 선 서버 상태)으로 예측을 맞춘다.
        // 입력: predictor - 내린 예측기, start - 차량 시작 상태, ack - Snapshot의 ack, tick - Snapshot Tick, exitSpot - 결과(내린 자리).
        // 출력: 반환값 없음. 예측기가 ack 뒤 입력을 재실행한다.
        private static void SnapAfterExit(LocalPlayerPredictor predictor, VehicleMove start, uint ack, uint tick, out Num.Vector3 exitSpot)
        {
            float x = start.Position.X + 2f, z = start.Position.Z;
            exitSpot = new Num.Vector3(x, GameMap.Terrain.Height(x, z), z);
            var entity = new SnapshotEntity { Position = exitSpot, Flags = SnapshotEntity.AliveFlag };
            var self = new SnapshotSelf { Energy = MoveState.MaxEnergyHundredths };
            predictor.Reconcile(entity, self, ack, tick);
        }

        [Test]
        public void AForcedExit_WithSpaceHeld_IsNoJump_UntilAnInputWithoutJump()
        {
            LocalPlayerPredictor predictor = Driving(out VehicleMove start);
            AdvanceSteps(predictor, 3, Vector2.zero, InputButtons.Jump);   // braking
            predictor.ExitRefused(3);                                        // a VehicleStates still names us seated through input 3
            AdvanceSteps(predictor, 4, Vector2.zero, InputButtons.Jump);   // the car is wrecked: the server walks 4..7, Space held
            Assert.AreNotEqual(InputButtons.None, predictor.InputAt(7).Buttons & InputButtons.Jump);   // sent as made
            predictor.EndSeated(110);
            SnapAfterExit(predictor, start, 5, 110, out Num.Vector3 exitSpot);   // replays 6 and 7 with their Jump ignored
            Assert.AreEqual(MovementMode.Ground, predictor.Mode);
            Assert.LessOrEqual(predictor.VerticalSpeed, 0f);
            Assert.AreEqual(exitSpot.Y, predictor.PredictedPosition.y, 0.01f);

            // Still latched: a press on the first input on foot is ignored, like on the server.
            AdvanceSteps(predictor, 1, Vector2.zero, InputButtons.None, InputButtons.Jump);
            Assert.LessOrEqual(predictor.VerticalSpeed, 0f);
            // One input without Jump ends it: the next press jumps.
            AdvanceSteps(predictor, 1, Vector2.zero);
            AdvanceSteps(predictor, 1, Vector2.zero, InputButtons.None, InputButtons.Jump);
            Assert.Greater(predictor.VerticalSpeed, 0f);
        }

        [Test]
        public void AForcedExit_LatchStartsAfterTheLastSeatedAck()
        {
            // Space released on input 3 (after the last seated ack 2) and held again: the server's latch ended on input 3, so
            // input 4 is a jump on both sides.
            LocalPlayerPredictor predictor = Driving(out VehicleMove start);
            AdvanceSteps(predictor, 2, Vector2.zero);
            predictor.ExitRefused(2);
            AdvanceSteps(predictor, 1, Vector2.zero);
            AdvanceSteps(predictor, 3, Vector2.zero, InputButtons.Jump);
            predictor.EndSeated(110);
            SnapAfterExit(predictor, start, 3, 110, out _);
            Assert.Greater(predictor.VerticalSpeed, 0f);

            // Released before the last seated ack (input 1) does not count: those inputs were seated.
            LocalPlayerPredictor held = Driving(out VehicleMove start2);
            AdvanceSteps(held, 1, Vector2.zero);
            AdvanceSteps(held, 3, Vector2.zero, InputButtons.Jump);
            held.ExitRefused(2);
            AdvanceSteps(held, 3, Vector2.zero, InputButtons.Jump);
            held.EndSeated(110);
            SnapAfterExit(held, start2, 3, 110, out _);
            Assert.LessOrEqual(held.VerticalSpeed, 0f);
        }

        [Test]
        public void AForcedExit_BeforeAnEPress_KeepsTheLatchFromTheLastSeatedAck()
        {
            // The car is wrecked after the seated ack 2 while Space is held, and E comes (Space still held) before the client
            // knows: the server walked 3..6 with the latch on, so the E input's seq is not where the latch starts.
            LocalPlayerPredictor predictor = Driving(out VehicleMove start);
            AdvanceSteps(predictor, 2, Vector2.zero);
            predictor.ExitRefused(2);
            AdvanceSteps(predictor, 3, Vector2.zero, InputButtons.Jump);
            AdvanceSteps(predictor, 1, Vector2.zero, InputButtons.Jump, InputButtons.Interact);
            Assert.AreEqual(predictor.LastSeq, predictor.ExitSeq);
            predictor.EndSeated(110);
            SnapAfterExit(predictor, start, 3, 110, out _);   // replays 4..6, Space held on all
            Assert.LessOrEqual(predictor.VerticalSpeed, 0f);
        }

        [Test]
        public void AnExitWithE_EndsTheLatchOnTheNextInput()
        {
            // The seated inputs after the exit press drop the held brake, so the server's latch ends on the first one.
            LocalPlayerPredictor predictor = Driving(out VehicleMove start);
            AdvanceSteps(predictor, 2, Vector2.zero, InputButtons.Jump, InputButtons.Interact);
            AdvanceSteps(predictor, 2, Vector2.zero, InputButtons.Jump);
            predictor.EndSeated(110);
            SnapAfterExit(predictor, start, 3, 110, out _);
            Assert.LessOrEqual(predictor.VerticalSpeed, 0f);
            AdvanceSteps(predictor, 1, Vector2.zero, InputButtons.None, InputButtons.Jump);
            Assert.Greater(predictor.VerticalSpeed, 0f);
        }

        // 기능: 차량 레코드의 위치·방향·속도·조향을 운동 상태로 옮긴다.
        // 입력: r - 변환할 차량 레코드.
        // 출력: 그 값을 가진 VehicleMove.
        private static VehicleMove RecordToMove(in VehicleRecord r) =>
            new VehicleMove { Position = r.Position, Heading = r.Heading, Speed = r.Speed, Steer = r.Steer };
    }
}
