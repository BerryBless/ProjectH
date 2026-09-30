using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Tests
{
    public class LocalPlayerPredictorTests
    {
        private const int SimHz = 30;
        private const float Step = 1f / SimHz;

        private static LocalPlayerPredictor NewPredictor() => new LocalPlayerPredictor(SimHz, new MoveState());

        private static void AdvanceSteps(LocalPlayerPredictor predictor, int steps, Vector2 move)
        {
            bool jump = false;
            // Slightly more than N steps of time so float rounding cannot drop a step.
            predictor.Advance(steps * Step + 0.0005f, move, 0f, false, ref jump);
        }

        [Test]
        public void Advance_ProducesOneSeqPerStep_AndPacketHoldsNewestThree()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 4, Vector2.up);

            Assert.AreEqual(4u, predictor.LastSeq);
            Assert.IsTrue(predictor.TryBuildInputPacket(out PlayerInputPacket packet));
            Assert.AreEqual(3, packet.Count);
            Assert.AreEqual(2u, packet.Get(0).Seq);
            Assert.AreEqual(4u, packet.Get(2).Seq);
        }

        [Test]
        public void Reconcile_WithMatchingServerState_KeepsPrediction()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 3, Vector2.up);
            Vector3 before = predictor.PredictedPosition;

            // Server processed inputs 1..2 exactly as the client did.
            var server = new MoveState();
            for (int i = 0; i < 2; i++) MovementSimulation.Step(ref server, new InputCommand { MoveY = 1f }, Step);
            predictor.Reconcile(new SnapshotEntity { Position = server.Position, VelocityY = server.VelocityY, Yaw = server.Yaw }, 2);

            Assert.AreEqual(before.z, predictor.PredictedPosition.z, 1e-4f);
        }

        [Test]
        public void Reconcile_WithDifferentServerState_ReplaysUnackedInputs()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 3, Vector2.up);
            Vector3 before = predictor.PredictedPosition;

            // Server says that after input 1 the player was 1 m further along +X (e.g. pushed).
            var server = new MoveState();
            MovementSimulation.Step(ref server, new InputCommand { MoveY = 1f }, Step);
            server.Position.X += 1f;
            predictor.Reconcile(new SnapshotEntity { Position = server.Position, VelocityY = server.VelocityY, Yaw = server.Yaw }, 1);

            // Inputs 2..3 are replayed on top of the corrected state.
            Assert.AreEqual(before.x + 1f, predictor.PredictedPosition.x, 1e-4f);
            Assert.AreEqual(before.z, predictor.PredictedPosition.z, 1e-4f);
        }

        [Test]
        public void Reconcile_WithNoAckAndNoInputs_SnapsToServer()
        {
            var predictor = NewPredictor();
            predictor.Reconcile(new SnapshotEntity { Position = new System.Numerics.Vector3(3f, 0f, 4f) }, 0);
            Assert.AreEqual(new Vector3(3f, 0f, 4f), predictor.PredictedPosition);
        }

        // Ack 0 only means the server has not processed any of our inputs yet. Once inputs are
        // predicted, snapping to the (older) server state would stutter on every join.
        [Test]
        public void Reconcile_WithNoAckButPredictedInputs_KeepsPrediction()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 2, Vector2.up);
            Vector3 before = predictor.PredictedPosition;

            predictor.Reconcile(new SnapshotEntity { Position = new System.Numerics.Vector3(3f, 0f, 4f) }, 0);

            Assert.AreEqual(before, predictor.PredictedPosition);
        }

        [Test]
        public void JumpQueued_IsConsumedByLastStepOnly()
        {
            var predictor = NewPredictor();
            bool jump = true;
            predictor.Advance(2 * Step + 0.0005f, Vector2.zero, 0f, false, ref jump);
            Assert.IsFalse(jump);
            Assert.IsTrue(predictor.TryBuildInputPacket(out PlayerInputPacket packet));
            Assert.AreEqual(2, packet.Count);
            Assert.AreEqual(InputButtons.None, packet.Get(0).Buttons);
            Assert.AreEqual(InputButtons.Jump, packet.Get(1).Buttons);
        }

        // A hitch frame runs more steps than one packet carries (3). The jump must ride on the newest
        // step, otherwise it is never sent and the server never applies it.
        [Test]
        public void JumpQueued_OnHitchFrame_LandsInSentPacket()
        {
            var predictor = NewPredictor();
            bool jump = true;
            int steps = predictor.Advance(5 * Step + 0.0005f, Vector2.zero, 0f, false, ref jump);

            Assert.AreEqual(5, steps);
            Assert.IsFalse(jump);
            Assert.IsTrue(predictor.TryBuildInputPacket(out PlayerInputPacket packet));
            Assert.AreEqual(3, packet.Count);
            Assert.AreEqual(InputButtons.None, packet.Get(0).Buttons);
            Assert.AreEqual(InputButtons.None, packet.Get(1).Buttons);
            Assert.AreEqual(5u, packet.Get(2).Seq);
            Assert.AreEqual(InputButtons.Jump, packet.Get(2).Buttons);
        }

        // Snapshot values come from the network and are not validated by NetClient: a non-finite
        // entity must be ignored instead of replacing (and poisoning) the predicted state.
        [TestCase(float.NaN, 0f, 0f)]
        [TestCase(0f, 0f, float.NaN)]
        [TestCase(0f, float.NaN, 0f)]
        public void Reconcile_WithNonFiniteServerState_IsIgnored(float x, float velocityY, float yaw)
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 2, Vector2.up);
            Vector3 before = predictor.PredictedPosition;
            float beforeYaw = predictor.RenderYaw;

            var entity = new SnapshotEntity { Position = new System.Numerics.Vector3(x, 0f, 0f), VelocityY = velocityY, Yaw = yaw };
            predictor.Reconcile(entity, 0);
            predictor.Reconcile(entity, 1);

            Assert.AreEqual(before, predictor.PredictedPosition);
            Assert.AreEqual(beforeYaw, predictor.RenderYaw);
        }
    }
}
