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
            // Phase 13 D3: the server gathers its world around the character before each step (Match.Move); so do these.
            public readonly CollisionWorld World = new CollisionWorld();
            public PieceGrid Pieces;

            // 기능: 서버의 한 Tick을 흉내 낸다. 수송기 경로를 타고 있으면 Ride, 아니면 문·조각을 모은 월드에서 Step을 돌리고 돌진으로 막힌 문을 연다.
            // 입력: input - 이 Tick에 처리할 입력.
            // 출력: 반환값 없음. Tick이 1 늘고 State·Sprinting·Doors가 갱신된다.
            public void Run(in InputCommand input)
            {
                Tick++;
                if (HasRoute && DropTransport.Ride(ref State, input, Route, Tick))
                {
                    Sprinting = false;
                    return;
                }
                World.Gather(State.Position, Doors.OpenMask, 0UL, Pieces);
                MovementSimulation.Step(ref State, input, Step, World, GameMap.Terrain, out StepResult result);
                Sprinting = result.Sprinting;
                if (result.Charging && !result.BlockedBy.IsNone)
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

        // 기능: 서버 복제본·예측 문·예측기를 같은 시작 상태로 만들고 ack 0의 합류 Snapshot을 보낸다.
        // 입력: spawn - 양쪽의 시작 이동 상태, route - 수송기 경로(null이면 경로 없음).
        // 출력: 반환값 없음. _server·_doors·_predictor·_sentDoors가 새로 만들어진다.
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

        // 기능: 양쪽에서 정확히 한 걸음을 진행한다. 예측기가 만든 입력을 서버 복제본이 그대로 돌리고, 짝수 Tick마다 Snapshot을 보낸다.
        // 입력: move - 이동 입력, yaw - 시선 Yaw(도), held - 누르고 있는 버튼, press - 이 걸음에만 넣는 버튼.
        // 출력: 반환값 없음. 예측기와 서버 복제본이 한 Tick 진행된다.
        // One frame of exactly one step on both sides, then every second tick a snapshot.
        private void Frame(Vector2 move, float yaw, InputButtons held = InputButtons.None, InputButtons press = InputButtons.None)
        {
            InputButtons queued = press;
            Assert.AreEqual(1, _predictor.Advance(Step, move, yaw, held, ref queued));
            InputCommand sent = _predictor.InputAt(_predictor.LastSeq);
            _server.Run(sent);
            if (_server.Tick % 2 == 0) Snapshot(_predictor.LastSeq);
        }

        // 기능: 같은 입력으로 Frame을 count번 반복한다.
        // 입력: count - 반복할 걸음 수, move - 이동 입력, yaw - 시선 Yaw(도), held - 누르고 있는 버튼.
        // 출력: 반환값 없음. 예측기와 서버 복제본이 count Tick 진행된다.
        private void Frames(int count, Vector2 move, float yaw, InputButtons held = InputButtons.None)
        {
            for (int i = 0; i < count; i++) Frame(move, yaw, held);
        }

        // 기능: 서버 복제본의 상태를 실제 Snapshot 쓰기·읽기(양자화)로 통과시켜 예측기에 Reconcile하고, 문 상태가 바뀌었으면 DoorStates를 먼저 적용한다.
        // 입력: ack - Snapshot이 확인하는 마지막 입력 Seq.
        // 출력: 반환값 없음. 예측기가 서버 상태와 대조되고 _sentDoors가 갱신된다.
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

        // 기능: 예측이 한 번도 교정되지 않았고 모드·위치가 서버 복제본과 같은지 단언한다.
        // 입력: what - 실패 메시지에 붙일 장면 이름.
        // 출력: 반환값 없음. 다르면 테스트가 실패한다.
        private void AssertAgrees(string what)
        {
            Assert.AreEqual(0, _predictor.Corrections, what + ": corrections");
            Assert.AreEqual(_server.State.Mode, _predictor.Mode, what + ": mode");
            Assert.AreEqual(_server.State.Position.X, _predictor.PredictedPosition.x, 1e-5f, what);
            Assert.AreEqual(_server.State.Position.Y, _predictor.PredictedPosition.y, 1e-5f, what);
            Assert.AreEqual(_server.State.Position.Z, _predictor.PredictedPosition.z, 1e-5f, what);
        }

        // 기능: 지형 높이에 발을 붙인 시작 이동 상태를 만든다.
        // 입력: x - 월드 x, z - 월드 z, yaw - 시작 Yaw(도).
        // 출력: 지형 위 (x, z)에 선 MoveState.
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
            predictor.SetAim(1, feet + new Vector3(0f, AimSolver.CrouchEyeHeight, 10f), 0f, 0f, 0u);
            Assert.AreEqual(0f, predictor.InputAt(2).AimPitch, 1e-3f);
        }

        // ---- Phase 13 D2, D3: moving on building pieces ----

        // 기능: 건설 조각들을 한 PieceGrid에 넣어 서버 복제본과 예측기가 같은 격자를 보게 한다.
        // 입력: shapes - 넣을 조각 모양들(ID는 1부터 차례로).
        // 출력: 반환값 없음. _server.Pieces와 _predictor.Pieces가 같은 격자를 가리킨다.
        // The server and the prediction both know these pieces (the predictor's Pieces is the client's store of confirmed
        // pieces; here the same grid), so they gather the same world and agree with no correction. Plaza cell 16:
        // x 0..5, z 0..5.
        private void Pieces(params BuildPieceShape[] shapes)
        {
            var grid = new PieceGrid(64);
            for (int i = 0; i < shapes.Length; i++) Assert.IsTrue(grid.TryAdd((uint)(i + 1), shapes[i], out _));
            _server.Pieces = grid;
            _predictor.Pieces = grid;
        }

        // 기능: 격자 좌표를 BuildGrid.TryNormalize로 정규화한 조각 모양을 만들고 성공을 단언한다.
        // 입력: type - 조각 종류, x - 격자 x 칸, y - 층, z - 격자 z 칸, rotation - 회전(0..3).
        // 출력: 정규화된 BuildPieceShape.
        private static BuildPieceShape Shape(BuildPieceType type, int x, int y, int z, int rotation = 0)
        {
            Assert.IsTrue(BuildGrid.TryNormalize(type, x, y, z, rotation, out BuildPieceShape shape));
            return shape;
        }

        [Test]
        public void UpARampOntoAFloor_AndBackDown_Agrees()
        {
            Start(At(2.5f, -3f));
            Pieces(Shape(BuildPieceType.Ramp, 16, 0, 16, 0), Shape(BuildPieceType.Floor, 16, 1, 17));
            Frames(55, Vector2.up, 0f);
            Assert.AreEqual(3f, _predictor.PredictedPosition.y, 1e-4f);
            AssertAgrees("up the ramp");
            Frames(40, Vector2.up, 180f, InputButtons.Sprint);
            Assert.AreEqual(0f, _predictor.PredictedPosition.y, 1e-4f);
            AssertAgrees("down the ramp");
        }

        [Test]
        public void SprintingUpARampOntoARoof_AndJumpingOnIt_Agrees()
        {
            Start(At(2.5f, -8f));
            Pieces(Shape(BuildPieceType.Ramp, 16, 0, 15, 0), Shape(BuildPieceType.Wall, 16, 0, 16, 0), Shape(BuildPieceType.Wall, 16, 0, 16, 1),
                Shape(BuildPieceType.Wall, 16, 0, 16, 2), Shape(BuildPieceType.Wall, 16, 0, 16, 3), Shape(BuildPieceType.Roof, 16, 0, 16));
            Frames(46, Vector2.up, 0f, InputButtons.Sprint);
            Assert.Greater(_predictor.PredictedPosition.y, 4f);
            Frame(Vector2.up, 0f, InputButtons.None, InputButtons.Jump);
            Frames(30, Vector2.up, 20f);
            AssertAgrees("roof");
        }

        [Test]
        public void PressingAlongAWallLine_AndInsideABox_Agrees()
        {
            Start(At(-13f, -0.6f, 90f));
            Pieces(Shape(BuildPieceType.Wall, 13, 0, 16, 0), Shape(BuildPieceType.Wall, 14, 0, 16, 0), Shape(BuildPieceType.Wall, 15, 0, 16, 0),
                Shape(BuildPieceType.Wall, 16, 0, 16, 0), Shape(BuildPieceType.Wall, 16, 0, 16, 1), Shape(BuildPieceType.Wall, 16, 0, 16, 2),
                Shape(BuildPieceType.Wall, 16, 0, 16, 3), Shape(BuildPieceType.Floor, 16, 1, 16));
            Frames(140, new Vector2(-1f, 1f), 90f);   // forward +X, strafing into the wall line
            Assert.Greater(_predictor.PredictedPosition.x, 0f);
            AssertAgrees("wall line");
        }
    }
}
