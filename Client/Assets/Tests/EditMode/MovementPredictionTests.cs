using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;
using Num = System.Numerics;

namespace ProjectH.Client.Tests
{
    // Phase 12 spec §2 예측: the same inputs give the same results on the server and in the prediction, in every mode. The
    // server replica below runs what Match.Tick runs (DropTransport.Ride at the tick, else Step against the map boxes and
    // the closed doors, a bash opens a door), one input per tick, and every second tick its state goes through a real
    // snapshot write and read (quantized entity, self block, DoorStates) into Reconcile. A prediction that agrees is
    // never corrected. Pure math only: these also run outside Unity (the EditTests tool).
    public class MovementPredictionTests
    {
        private const int SimHz = 30;
        private const float Step = 1f / SimHz;
        private const uint StartTick = 5000;

        private sealed class ServerReplica
        {
            public MoveState State;
            public bool Sprinting;
            public uint Tick = StartTick;
            public bool HasRoute;
            public DropRoute Route;
            public readonly PredictedDoors Doors = new PredictedDoors();   // only its server state is used

            public void Run(in InputCommand input)
            {
                Tick++;
                if (HasRoute && DropTransport.Ride(ref State, input, Route, Tick))
                {
                    Sprinting = false;
                    return;
                }
                MovementSimulation.Step(ref State, input, Step, Doors.World, GameMap.Terrain, out StepResult result);
                Sprinting = result.Sprinting;
                if (result.Charging && result.BlockedBy >= 0)
                {
                    int door = Doors.DoorBlocking(result);
                    if (door >= 0) Doors.ApplyServer((byte)(Doors.OpenMask | (1 << door)));
                }
            }
        }

        private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];
        private ServerReplica _server;
        private PredictedDoors _doors;
        private LocalPlayerPredictor _predictor;
        private byte _sentDoors;

        private void Start(MoveState spawn, DropRoute? route = null)
        {
            _server = new ServerReplica { State = spawn };
            _doors = new PredictedDoors();
            _predictor = new LocalPlayerPredictor(SimHz, spawn, _doors);
            _sentDoors = 0;
            if (route.HasValue)
            {
                _server.HasRoute = true;
                _server.Route = route.Value;
                _predictor.SetRoute(route.Value);
            }
            Snapshot(0);   // the join snapshot: nothing acked yet, it gives the tick
        }

        // One frame of exactly one step on both sides, then every second tick a snapshot.
        private void Frame(Vector2 move, float yaw, InputButtons held = InputButtons.None, InputButtons press = InputButtons.None)
        {
            InputButtons queued = press;
            Assert.AreEqual(1, _predictor.Advance(Step, move, yaw, held, ref queued));
            InputCommand sent = _predictor.InputAt(_predictor.LastSeq);
            _server.Run(sent);
            if (_server.Tick % 2 == 0) Snapshot(_predictor.LastSeq);
        }

        private void Frames(int count, Vector2 move, float yaw, InputButtons held = InputButtons.None)
        {
            for (int i = 0; i < count; i++) Frame(move, yaw, held);
        }

        private void Snapshot(uint ack)
        {
            MoveState s = _server.State;
            var writer = new PacketWriter(_buffer);
            SnapshotEntity.Write(ref writer, new SnapshotEntity
            {
                EntityId = 1, Position = s.Position, VelocityY = s.VelocityY, Yaw = s.Yaw,
                Flags = SnapshotEntity.MakeFlags(true, s.Mode, _server.Sprinting, s.Exhausted),
            });
            SnapshotSelf.Write(ref writer, new SnapshotSelf
            {
                Health = 100,
                Energy = (ushort)(MoveState.MaxEnergyHundredths - s.EnergySpent),
                HorizontalVelocity = s.HorizontalVelocity,
                ModeTicks = s.ModeTicks,
                EnergyDelayTicks = s.EnergyDelayTicks,
            });
            var reader = new PacketReader(new System.ReadOnlySpan<byte>(_buffer, 0, writer.Length));
            Assert.IsTrue(SnapshotEntity.TryRead(ref reader, out SnapshotEntity entity));
            Assert.IsTrue(SnapshotSelf.TryRead(ref reader, out SnapshotSelf self));
            // DoorStates (Reliable) when the server's doors changed.
            if (_server.Doors.OpenMask != _sentDoors)
            {
                _sentDoors = _server.Doors.OpenMask;
                _doors.ApplyServer(_sentDoors);
            }
            _predictor.Reconcile(entity, self, ack, _server.Tick);
        }

        private void AssertAgrees(string what)
        {
            Assert.AreEqual(0, _predictor.Corrections, what + ": corrections");
            Assert.AreEqual(_server.State.Mode, _predictor.Mode, what + ": mode");
            Assert.AreEqual(_server.State.Position.X, _predictor.PredictedPosition.x, 1e-5f, what);
            Assert.AreEqual(_server.State.Position.Y, _predictor.PredictedPosition.y, 1e-5f, what);
            Assert.AreEqual(_server.State.Position.Z, _predictor.PredictedPosition.z, 1e-5f, what);
        }

        private static MoveState At(float x, float z, float yaw = 0f) =>
            new MoveState { Position = new Num.Vector3(x, GameMap.Terrain.Height(x, z), z), Yaw = yaw };

        [Test]
        public void SprintingUntilExhausted_AndRecovering_Agrees()
        {
            Start(At(-40f, 0f));
            Frames(160, Vector2.up, 90f, InputButtons.Sprint);    // empties the energy (150 ticks)
            Assert.IsTrue(_predictor.Exhausted);
            Frames(80, Vector2.up, 90f);                          // walking: the delay, then back above 20
            Assert.IsFalse(_predictor.Exhausted);
            Frames(10, Vector2.up, 90f, InputButtons.Sprint);
            AssertAgrees("sprint");
        }

        [Test]
        public void CrouchSlideAndSlideJump_Agree()
        {
            Start(At(-40f, 0f));
            Frames(10, Vector2.up, 90f, InputButtons.Sprint);
            Frames(15, Vector2.up, 90f, InputButtons.Sprint | InputButtons.Crouch);   // a slide
            Assert.AreEqual(MovementMode.Slide, _predictor.Mode);
            Frame(Vector2.up, 90f, InputButtons.Crouch, InputButtons.Jump);          // a slide jump
            Frames(30, Vector2.up, 90f);
            Frames(20, Vector2.up, 90f, InputButtons.Crouch);                        // a crouch walk
            Assert.AreEqual(MovementMode.Crouch, _predictor.Mode);
            AssertAgrees("slide");
        }

        [Test]
        public void HurdlingTheGearworksCrate_Agrees()
        {
            // Low crate (36, 0.5, 40): x 35..37. Sprint along +X, jump half a metre before its face.
            Start(At(31f, 40f, 90f));
            while (35f - (_predictor.PredictedPosition.x + MoveSettings.HalfWidth) > 0.5f) Frame(Vector2.up, 90f, InputButtons.Sprint);
            Frame(Vector2.up, 90f, InputButtons.Sprint, InputButtons.Jump);
            Assert.AreEqual(MovementMode.Vault, _predictor.Mode);
            Frames(20, Vector2.up, 90f, InputButtons.Sprint);
            Assert.Greater(_predictor.PredictedPosition.x, 37f);
            AssertAgrees("hurdle");
        }

        [Test]
        public void RidingJumpingFallingAndGliding_Agree()
        {
            var route = new DropRoute
            {
                StartX = -100f, StartZ = 10f, EndX = 100f, EndZ = 10f, Altitude = 90f, StartTick = StartTick + 1, DurationTicks = 300,
            };
            Start(new MoveState { Position = route.PositionAt(route.StartTick), Mode = MovementMode.Transport }, route);
            Frames(60, Vector2.zero, 90f);
            Assert.AreEqual(MovementMode.Transport, _predictor.Mode);
            Assert.AreEqual(route.PositionAt(_server.Tick).X, _predictor.PredictedPosition.x, 1e-4f);

            Frame(Vector2.zero, 90f, InputButtons.None, InputButtons.Jump);
            Assert.AreEqual(MovementMode.Freefall, _predictor.Mode);
            Frames(60, Vector2.up, 45f);                       // steering the fall
            bool glided = false;
            for (int i = 0; i < 600 && _predictor.Mode != MovementMode.Ground; i++)
            {
                Frame(Vector2.up, 45f);
                if (_predictor.Mode == MovementMode.Glide) glided = true;
            }
            Assert.IsTrue(glided, "the glider opened on the way down");
            Assert.AreEqual(MovementMode.Ground, _predictor.Mode);
            AssertAgrees("deployment");
        }

        // Final review B6: before the first ack the tick an input runs at is unknown, so a rider is not ride-predicted from
        // a guess: it holds the server's position (taken from each ack-0 snapshot without counting a correction). The
        // first ack gives the tick base; the replay from it is no correction either.
        [Test]
        public void BeforeTheFirstAck_ARiderHoldsTheServersPosition()
        {
            var route = new DropRoute { StartX = -100f, EndX = 100f, Altitude = 90f, StartTick = 901, DurationTicks = 300 };
            var predictor = new LocalPlayerPredictor(SimHz, new MoveState { Position = route.PositionAt(901), Mode = MovementMode.Transport });
            predictor.SetRoute(route);
            InputButtons queued = InputButtons.None;
            predictor.Advance(3 * Step + 0.0005f, Vector2.zero, 0f, InputButtons.None, ref queued);
            Assert.IsFalse(predictor.HasTickBase);
            Assert.AreEqual(route.PositionAt(901).X, predictor.PredictedPosition.x, 1e-4f);   // held, not ridden

            var self = new SnapshotSelf { Energy = MoveState.MaxEnergyHundredths };
            SnapshotEntity At(uint tick) => new SnapshotEntity
            {
                Position = route.PositionAt(tick), Flags = SnapshotEntity.MakeFlags(true, MovementMode.Transport, false, false),
            };
            predictor.Reconcile(At(904), self, 0, 904);
            Assert.AreEqual(route.PositionAt(904).X, predictor.PredictedPosition.x, 1e-4f);
            Assert.AreEqual(0, predictor.Corrections);

            predictor.Reconcile(At(905), self, 2, 905);                                       // seq 2 ran at 905
            Assert.IsTrue(predictor.HasTickBase);
            Assert.AreEqual(906u, predictor.PredictedTick);                                     // seq 3 runs at 906
            Assert.AreEqual(route.PositionAt(906).X, predictor.PredictedPosition.x, 1e-3f);
            Assert.AreEqual(0, predictor.Corrections);
        }

        // Final review B2: a new round's countdown clears the route; a rider is then not placed on the old one.
        [Test]
        public void AClearedRoute_IsNotRidden()
        {
            var route = new DropRoute { StartX = -100f, StartZ = 10f, EndX = 100f, EndZ = 10f, Altitude = 90f, StartTick = StartTick + 1, DurationTicks = 300 };
            Start(new MoveState { Position = route.PositionAt(route.StartTick), Mode = MovementMode.Transport }, route);
            Frames(10, Vector2.zero, 90f);
            float x = _predictor.PredictedPosition.x;
            Assert.Greater(x, route.StartX);
            _predictor.ClearRoute();
            InputButtons queued = InputButtons.None;
            _predictor.Advance(3 * Step + 0.0005f, Vector2.zero, 90f, InputButtons.None, ref queued);
            Assert.AreEqual(x, _predictor.PredictedPosition.x, 1e-5f);
        }

        [Test]
        public void ShoulderingADoorOpen_Agrees()
        {
            // Door 0 (Rustvale, centre x -54, z 50.25), approached from the south at a sprint.
            Start(At(-54f, 45f));
            Frames(40, Vector2.up, 0f, InputButtons.Sprint);
            Assert.IsTrue(_doors.IsOpen(0));
            Assert.Greater(_predictor.PredictedPosition.z, 51f);
            AssertAgrees("bash");
        }

        [Test]
        public void E_OnADoor_IsPredicted_AndTheServersDoorStatesDecide()
        {
            Start(At(-54f, 48.5f));
            Frame(Vector2.zero, 0f, InputButtons.None, InputButtons.Interact);
            Assert.IsTrue(_doors.IsOpen(0));                     // predicted at once
            _doors.ApplyServer(0);                              // the server says closed (someone else closed it again)
            Assert.IsFalse(_doors.IsOpen(0));

            _doors.Predict(1, true, 10f);
            _doors.Expire(10.5f);
            Assert.IsTrue(_doors.IsOpen(1));
            _doors.Expire(10f + PredictedDoors.PredictionSeconds);   // never confirmed: it goes back
            Assert.IsFalse(_doors.IsOpen(1));
        }

        [Test]
        public void AResumeInTheAir_TakesTheServersMode_BeforeAnyInputIsAcked()
        {
            // The predictor starts from PlayerSpawned (no mode): Ground, mid-air.
            var predictor = new LocalPlayerPredictor(SimHz, new MoveState { Position = new Num.Vector3(0f, 60f, 0f) });
            InputButtons queued = InputButtons.None;
            predictor.Advance(3 * Step + 0.0005f, Vector2.zero, 0f, InputButtons.None, ref queued);
            var entity = new SnapshotEntity
            {
                Position = new Num.Vector3(0f, 61f, 0f), Flags = SnapshotEntity.MakeFlags(true, MovementMode.Freefall, false, false),
            };
            predictor.Reconcile(entity, new SnapshotSelf { Energy = MoveState.MaxEnergyHundredths }, 0, 900);
            Assert.AreEqual(MovementMode.Freefall, predictor.Mode);
            Assert.AreEqual(61f, predictor.PredictedPosition.y, 1e-4f);
        }

        [Test]
        public void NoActionIsPredicted_AboardOrInTheAir()
        {
            var route = new DropRoute { StartX = -100f, EndX = 100f, Altitude = 90f, StartTick = StartTick + 1, DurationTicks = 300 };
            Start(new MoveState { Position = route.PositionAt(route.StartTick), Mode = MovementMode.Transport }, route);
            Frame(Vector2.zero, 0f, InputButtons.Fire);
            Assert.IsFalse(_predictor.ActionsAllowedAt(_predictor.LastSeq));
            Assert.IsTrue(LocalPlayerPredictor.ActionsAllowed(MovementMode.Slide));
            Assert.IsFalse(LocalPlayerPredictor.ActionsAllowed(MovementMode.Glide));
            Assert.IsFalse(LocalPlayerPredictor.ActionsAllowed(MovementMode.Vault));
        }

        [Test]
        public void Crouch_IsHeld_InEveryStep_AndLowersTheAimEye()
        {
            var predictor = new LocalPlayerPredictor(SimHz, At(-40f, 0f));
            InputButtons queued = InputButtons.None;
            predictor.Advance(2 * Step + 0.0005f, Vector2.zero, 0f, InputButtons.Crouch, ref queued);
            Assert.AreEqual(InputButtons.Crouch, predictor.InputAt(1).Buttons);
            Assert.AreEqual(InputButtons.Crouch, predictor.InputAt(2).Buttons);
            Assert.AreEqual(MovementMode.Crouch, predictor.Mode);

            // Aiming level at a point 10 m ahead at 1 m height: from the crouched eye (1.0 m) the pitch is 0.
            Vector3 feet = predictor.PredictedPosition;
            predictor.SetAim(1, feet + new Vector3(0f, AimSolver.CrouchEyeHeight, 10f), 0f, 0f, 0f);
            Assert.AreEqual(0f, predictor.InputAt(2).AimPitch, 1e-3f);
        }
    }
}
