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

        private static VehicleMove RecordToMove(in VehicleRecord r) =>
            new VehicleMove { Position = r.Position, Heading = r.Heading, Speed = r.Speed, Steer = r.Steer };
    }
}
