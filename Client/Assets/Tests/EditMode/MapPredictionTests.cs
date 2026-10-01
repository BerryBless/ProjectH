using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;
using Num = System.Numerics;

namespace ProjectH.Client.Tests
{
    // Prediction against the Shared GameMap (boxes and terrain). The server replica below runs the same MovementSimulation.Step
    // with the same world, as Match.Tick does.
    public class MapPredictionTests
    {
        private const int SimHz = 30;
        private const float Step = 1f / SimHz;

        // Low crate (0, 0.5, 22) size 2 x 1 x 2: top at y = 1, x -1..1, z 21..23.
        private static readonly Num.Vector3 OnLowBox = new Num.Vector3(0f, 1f, 22f);

        // Exactly one step per call: from a zero accumulator, Step - Step leaves exactly 0, so long
        // loops never gain an extra step from rounding slop (Advance also caps one call at 0.25 s).
        private static void AdvanceOneStep(LocalPlayerPredictor predictor, Vector2 move)
        {
            InputButtons queued = InputButtons.None;
            Assert.AreEqual(1, predictor.Advance(Step, move, 0f, InputButtons.None, ref queued));
        }

        private static SnapshotEntity ToEntity(in MoveState s)
            => new SnapshotEntity { Position = s.Position, VelocityY = s.VelocityY, Yaw = s.Yaw, Flags = SnapshotEntity.AliveFlag };

        private static Num.Vector3 ToNumerics(Vector3 v) => new Num.Vector3(v.x, v.y, v.z);

        [Test]
        public void WalkingIntoLowBox_PredictionEqualsServer()
        {
            var spawn = new MoveState { Position = new Num.Vector3(0f, 0f, 19f) };
            var predictor = new LocalPlayerPredictor(SimHz, spawn);
            var server = spawn;
            for (int i = 0; i < 30; i++)
            {
                AdvanceOneStep(predictor, Vector2.up);
                MovementSimulation.Step(ref server, new InputCommand { MoveY = 1f }, Step, GameMap.Boxes, GameMap.Terrain);
            }

            Assert.AreEqual(server.Position.X, predictor.PredictedPosition.x, 1e-6f);
            Assert.AreEqual(server.Position.Y, predictor.PredictedPosition.y, 1e-6f);
            Assert.AreEqual(server.Position.Z, predictor.PredictedPosition.z, 1e-6f);
            // Stopped by the box side: z = 21 - 0.35 - 0.001.
            Assert.AreEqual(21f - MoveSettings.HalfWidth - MoveSettings.Skin, predictor.PredictedPosition.z, 1e-3f);
        }

        // Review Focus: over a hill the client's prediction must equal the server, step by step (Phase 6 D3: the
        // terrain is built with integer math, so both sides have bit-identical heights).
        [Test]
        public void WalkingOverTheNorthHill_PredictionEqualsServer()
        {
            // North hill: 4 m at (0, 46), 18 m out. Yaw 0 walks +Z from the plaza edge over the top.
            var spawn = new MoveState { Position = new Num.Vector3(0f, 0f, 26f) };
            var predictor = new LocalPlayerPredictor(SimHz, spawn);
            var server = spawn;
            float top = 0f;
            for (int i = 0; i < 250; i++)
            {
                AdvanceOneStep(predictor, Vector2.up);
                MovementSimulation.Step(ref server, new InputCommand { MoveY = 1f }, Step, GameMap.Boxes, GameMap.Terrain);
                Assert.AreEqual(server.Position.X, predictor.PredictedPosition.x, $"step {i}");
                Assert.AreEqual(server.Position.Y, predictor.PredictedPosition.y, $"step {i}");
                Assert.AreEqual(server.Position.Z, predictor.PredictedPosition.z, $"step {i}");
                top = Mathf.Max(top, server.Position.Y);
            }
            Assert.Greater(top, 3.9f);
        }

        // Phase 8 D4: snapshots arrive quantized (1/256 m). Reconciling every tick against the quantized server state over a
        // hill must never correct the prediction: the error stays inside the 0.01 m match tolerance, so the predicted
        // position stays bit-identical to the server's exact one.
        [Test]
        public void QuantizedSnapshots_OverAHill_NeverCorrectThePrediction()
        {
            var spawn = new MoveState { Position = new Num.Vector3(0f, 0f, 26f) };
            var predictor = new LocalPlayerPredictor(SimHz, spawn);
            var server = spawn;
            var buffer = new byte[SnapshotEntity.Size];
            for (uint seq = 1; seq <= 250; seq++)
            {
                AdvanceOneStep(predictor, Vector2.up);
                MovementSimulation.Step(ref server, new InputCommand { MoveY = 1f }, Step, GameMap.Boxes, GameMap.Terrain);

                var writer = new PacketWriter(buffer);
                SnapshotEntity.Write(ref writer, ToEntity(server));
                var reader = new PacketReader(buffer);
                Assert.IsTrue(SnapshotEntity.TryRead(ref reader, out SnapshotEntity wire));
                predictor.Reconcile(wire, seq);

                Assert.AreEqual(server.Position.X, predictor.PredictedPosition.x, $"seq {seq}");
                Assert.AreEqual(server.Position.Y, predictor.PredictedPosition.y, $"seq {seq}");
                Assert.AreEqual(server.Position.Z, predictor.PredictedPosition.z, $"seq {seq}");
            }
        }

        // Review Focus: standing on a box top must not jitter when snapshots reconcile it.
        // Every 5th tick the server entity is perturbed (VelocityY + 0.02, above the match epsilon) and
        // acked two steps late, so Reconcile takes the replay path (restart from the server state, replay 2 inputs).
        [Test]
        public void StandingOnBox_ReconcileEveryStep_KeepsExactHeight()
        {
            var spawn = new MoveState { Position = OnLowBox };
            var predictor = new LocalPlayerPredictor(SimHz, spawn);
            var server = spawn;
            var serverHistory = new MoveState[91];   // serverHistory[seq] = server state after input seq
            serverHistory[0] = spawn;
            for (uint seq = 1; seq <= 90; seq++)
            {
                AdvanceOneStep(predictor, Vector2.zero);
                MovementSimulation.Step(ref server, new InputCommand { Seq = seq }, Step, GameMap.Boxes, GameMap.Terrain);
                serverHistory[seq] = server;

                if (seq % 5 == 0)
                {
                    SnapshotEntity stale = ToEntity(serverHistory[seq - 2]);
                    stale.VelocityY += 0.02f;
                    predictor.Reconcile(stale, seq - 2);
                }
                else
                {
                    predictor.Reconcile(ToEntity(server), seq);
                }

                Assert.AreEqual(1f, predictor.PredictedPosition.y);
                Assert.AreEqual(1f, predictor.RenderPosition.y, 1e-6f);
            }
        }

        // Review Focus: a server correction may place the player inside a box (for example a stale
        // snapshot). Replaying the unacked inputs must push it out, not leave it stuck inside.
        [Test]
        public void ReconcileIntoPillar_ReplayEndsOutsideEveryBox()
        {
            var predictor = new LocalPlayerPredictor(SimHz, new MoveState { Position = new Num.Vector3(-44f, 0f, -44f) });
            for (int i = 0; i < 3; i++) AdvanceOneStep(predictor, Vector2.zero);

            // Ruins pillar (-46, 1.5, -46) size 1 x 3 x 1: this position is its centre.
            predictor.Reconcile(new SnapshotEntity { Position = new Num.Vector3(-46f, 0f, -46f), Flags = SnapshotEntity.AliveFlag }, 1);

            Assert.IsFalse(MovementSimulation.OverlapsAny(ToNumerics(predictor.PredictedPosition), GameMap.Boxes));
        }
    }
}
