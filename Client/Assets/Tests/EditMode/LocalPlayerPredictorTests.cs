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

        private static void AdvanceSteps(LocalPlayerPredictor predictor, int steps, Vector2 move, InputButtons held = InputButtons.None)
        {
            InputButtons queued = InputButtons.None;
            // Slightly more than N steps of time so float rounding cannot drop a step.
            predictor.Advance(steps * Step + 0.0005f, move, 0f, held, ref queued);
        }

        // Snapshot entities of a living player (the alive flag is bit 0 of Flags).
        private static SnapshotEntity Alive(System.Numerics.Vector3 position, float velocityY = 0f, float yaw = 0f)
            => new SnapshotEntity { Position = position, VelocityY = velocityY, Yaw = yaw, Flags = SnapshotEntity.AliveFlag };

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
            for (int i = 0; i < 2; i++) MovementSimulation.Step(ref server, new InputCommand { MoveY = 1f }, Step, GameMap.Boxes, GameMap.Terrain);
            predictor.Reconcile(Alive(server.Position, server.VelocityY, server.Yaw), 2);

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
            MovementSimulation.Step(ref server, new InputCommand { MoveY = 1f }, Step, GameMap.Boxes, GameMap.Terrain);
            server.Position.X += 1f;
            predictor.Reconcile(Alive(server.Position, server.VelocityY, server.Yaw), 1);

            // Inputs 2..3 are replayed on top of the corrected state.
            Assert.AreEqual(before.x + 1f, predictor.PredictedPosition.x, 1e-4f);
            Assert.AreEqual(before.z, predictor.PredictedPosition.z, 1e-4f);
        }

        [Test]
        public void Reconcile_WithNoAckAndNoInputs_SnapsToServer()
        {
            var predictor = NewPredictor();
            predictor.Reconcile(Alive(new System.Numerics.Vector3(3f, 0f, 4f)), 0);
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

            predictor.Reconcile(Alive(new System.Numerics.Vector3(3f, 0f, 4f)), 0);

            Assert.AreEqual(before, predictor.PredictedPosition);
        }

        [Test]
        public void JumpQueued_IsConsumedByLastStepOnly()
        {
            var predictor = NewPredictor();
            InputButtons queued = InputButtons.Jump;
            predictor.Advance(2 * Step + 0.0005f, Vector2.zero, 0f, InputButtons.None, ref queued);
            Assert.AreEqual(InputButtons.None, queued);
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
            InputButtons queued = InputButtons.Jump;
            int steps = predictor.Advance(5 * Step + 0.0005f, Vector2.zero, 0f, InputButtons.None, ref queued);

            Assert.AreEqual(5, steps);
            Assert.AreEqual(InputButtons.None, queued);
            Assert.IsTrue(predictor.TryBuildInputPacket(out PlayerInputPacket packet));
            Assert.AreEqual(3, packet.Count);
            Assert.AreEqual(InputButtons.None, packet.Get(0).Buttons);
            Assert.AreEqual(InputButtons.None, packet.Get(1).Buttons);
            Assert.AreEqual(5u, packet.Get(2).Seq);
            Assert.AreEqual(InputButtons.Jump, packet.Get(2).Buttons);
        }

        [Test]
        public void HeldAndQueuedButtons_GoToEveryStepAndLastStep()
        {
            var predictor = NewPredictor();
            InputButtons queued = InputButtons.Reload | InputButtons.Slot2;
            predictor.Advance(3 * Step + 0.0005f, Vector2.zero, 0f, InputButtons.Fire | InputButtons.Sprint | InputButtons.Jump, ref queued);

            Assert.AreEqual(InputButtons.Fire | InputButtons.Sprint, predictor.InputAt(1).Buttons);   // Jump is never "held"
            Assert.AreEqual(InputButtons.Fire | InputButtons.Sprint, predictor.InputAt(2).Buttons);
            Assert.AreEqual(InputButtons.Fire | InputButtons.Sprint | InputButtons.Reload | InputButtons.Slot2, predictor.InputAt(3).Buttons);
            Assert.AreEqual(InputButtons.None, queued);
        }

        // Phase 4: the new presses (3, E, G, 4, 5) are queued presses too. A mask that forgot one would drop
        // the key silently.
        [Test]
        public void Phase4Presses_RideOnTheLastStep()
        {
            var predictor = NewPredictor();
            InputButtons presses = InputButtons.Slot3 | InputButtons.Interact | InputButtons.Drop |
                                   InputButtons.UseMedkit | InputButtons.UseShieldCell;
            InputButtons queued = presses;
            predictor.Advance(2 * Step + 0.0005f, Vector2.zero, 0f, presses, ref queued);

            Assert.AreEqual(InputButtons.None, predictor.InputAt(1).Buttons);   // never "held"
            Assert.AreEqual(presses, predictor.InputAt(2).Buttons);
            Assert.AreEqual(InputButtons.None, queued);
        }

        // PredictedPosition (and each step's result SetAim aims from) must be the state the server's Step
        // produces after the newest input, not the interpolated RenderPosition that trails it mid-step.
        [Test]
        public void PredictedPosition_IsTheSimulatedState_NotTheRenderPosition()
        {
            var predictor = NewPredictor();
            InputButtons queued = InputButtons.None;
            // 3.5 steps of time: three steps run and the render position sits halfway into the fourth.
            predictor.Advance(3.5f * Step, new Vector2(1f, 1f), 0f, InputButtons.Sprint, ref queued);

            var server = new MoveState();
            for (uint seq = 1; seq <= 3; seq++)
            {
                var input = new InputCommand { Seq = seq, MoveX = 1f, MoveY = 1f, Yaw = 0f, Buttons = InputButtons.Sprint };
                MovementSimulation.Step(ref server, input, Step, GameMap.Boxes, GameMap.Terrain);
            }

            Assert.AreEqual(3u, predictor.LastSeq);
            Assert.AreEqual(server.Position.X, predictor.PredictedPosition.x, 1e-6f);
            Assert.AreEqual(server.Position.Y, predictor.PredictedPosition.y, 1e-6f);
            Assert.AreEqual(server.Position.Z, predictor.PredictedPosition.z, 1e-6f);
            Assert.Greater(Vector3.Distance(predictor.PredictedPosition, predictor.RenderPosition), 0.05f);
        }

        [Test]
        public void SetAim_WritesAimIntoTheNewestSteps()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 2, Vector2.zero);
            // Standing at the origin: the eye is (0, 1.6, 0), so this point is straight ahead along +X.
            predictor.SetAim(2, new Vector3(10f, AimSolver.EyeHeight, 0f), 0f, 0f, 300f);
            AdvanceSteps(predictor, 3, Vector2.zero);
            predictor.SetAim(3, new Vector3(0f, AimSolver.EyeHeight, 10f), 0f, 0f, 303.5f);

            Assert.AreEqual(90f, predictor.InputAt(2).AimYaw, 1e-3f);
            Assert.AreEqual(300f, predictor.InputAt(2).ViewTick);
            Assert.AreEqual(0f, predictor.InputAt(3).AimYaw, 1e-3f);
            Assert.AreEqual(0f, predictor.InputAt(5).AimPitch, 1e-3f);
            Assert.AreEqual(303.5f, predictor.InputAt(5).ViewTick);
            Assert.IsTrue(predictor.TryBuildInputPacket(out PlayerInputPacket packet));
            Assert.AreEqual(303.5f, packet.Get(2).ViewTick);
        }

        // An aim point too close to the eye has no direction: the caller's fallback (camera) angles are sent.
        [Test]
        public void SetAim_PointAtTheEye_UsesTheFallback()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 1, Vector2.zero);
            predictor.SetAim(1, new Vector3(0f, AimSolver.EyeHeight, 0f), 33f, -7f, 1f);

            Assert.AreEqual(33f, predictor.InputAt(1).AimYaw);
            Assert.AreEqual(-7f, predictor.InputAt(1).AimPitch);
        }

        // The server fires each input from its own post-step position. On a multi-step frame the older steps
        // are behind the newest one, so each step's aim is solved from that step's own eye.
        [Test]
        public void SetAim_MultiStepFrame_SolvesEachStepFromItsOwnPosition()
        {
            var predictor = NewPredictor();
            InputButtons queued = InputButtons.None;
            Assert.AreEqual(3, predictor.Advance(3 * Step + 0.0005f, Vector2.up, 0f, InputButtons.Sprint, ref queued));
            // Close and to the side of a sprint along +Z, so the three eyes see it at clearly different yaws.
            var aimPoint = new Vector3(1f, AimSolver.EyeHeight, 1f);
            predictor.SetAim(3, aimPoint, 0f, 0f, 50f);

            var server = new MoveState();
            float firstYaw = 0f, lastYaw = 0f;
            for (uint seq = 1; seq <= 3; seq++)
            {
                MovementSimulation.Step(ref server, new InputCommand { Seq = seq, MoveY = 1f, Buttons = InputButtons.Sprint }, Step, GameMap.Boxes, GameMap.Terrain);
                var eye = new Vector3(server.Position.X, server.Position.Y + AimSolver.EyeHeight, server.Position.Z);
                Assert.IsTrue(AimSolver.TrySolve(eye, aimPoint, out float yaw, out float pitch));
                Assert.AreEqual(yaw, predictor.InputAt(seq).AimYaw, 1e-3f, $"seq {seq}");
                Assert.AreEqual(pitch, predictor.InputAt(seq).AimPitch, 1e-3f, $"seq {seq}");
                if (seq == 1) firstYaw = yaw;
                lastYaw = yaw;
            }
            Assert.Greater(Mathf.Abs(lastYaw - firstYaw), 1f);
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

            var entity = Alive(new System.Numerics.Vector3(x, 0f, 0f), velocityY, yaw);
            predictor.Reconcile(entity, 0);
            predictor.Reconcile(entity, 1);

            Assert.AreEqual(before, predictor.PredictedPosition);
            Assert.AreEqual(beforeYaw, predictor.RenderYaw);
        }

        // Spec §5: prediction stops while dead. Inputs keep their Seq but carry no move and no buttons.
        [Test]
        public void Dead_StopsMoving_AndSendsEmptyInputs()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 2, Vector2.up);
            predictor.SetDead();
            Vector3 body = predictor.PredictedPosition;

            InputButtons queued = InputButtons.Jump | InputButtons.Reload;
            predictor.Advance(3 * Step + 0.0005f, Vector2.up, 45f, InputButtons.Fire | InputButtons.Sprint, ref queued);

            Assert.IsTrue(predictor.IsDead);
            Assert.AreEqual(body, predictor.PredictedPosition);
            Assert.AreEqual(5u, predictor.LastSeq);
            Assert.AreEqual(InputButtons.None, queued);
            for (uint seq = 3; seq <= 5; seq++)
            {
                InputCommand c = predictor.InputAt(seq);
                Assert.AreEqual(0f, c.MoveX);
                Assert.AreEqual(0f, c.MoveY);
                Assert.AreEqual(InputButtons.None, c.Buttons);
            }
        }

        [Test]
        public void Dead_ReconcileSnapsToTheBody()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 3, Vector2.up);   // moved on after the server already killed us
            predictor.SetDead();

            var body = new System.Numerics.Vector3(0f, 0f, 0.1f);
            predictor.Reconcile(new SnapshotEntity { Position = body, Flags = 0 }, 1);

            Assert.AreEqual(new Vector3(0f, 0f, 0.1f), predictor.PredictedPosition);
        }

        // Review Focus: a respawn must not restart Seq (the server ignores seqs it already took), and
        // reconciling from the spawn point must land where the local prediction already is.
        [Test]
        public void Respawn_ResetsState_KeepsSeq_AndReconcilesFromSpawn()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 2, Vector2.up);            // seq 1-2 alive
            predictor.SetDead();
            AdvanceSteps(predictor, 3, Vector2.up);            // seq 3-5 dead: empty inputs
            var spawn = new MoveState { Position = new System.Numerics.Vector3(5f, 0f, 5f) };
            predictor.Respawn(spawn);
            Assert.IsFalse(predictor.IsDead);
            Assert.AreEqual(new Vector3(5f, 0f, 5f), predictor.PredictedPosition);
            Assert.AreEqual(new Vector3(5f, 0f, 5f), predictor.RenderPosition);

            AdvanceSteps(predictor, 3, Vector2.up);            // seq 6-8 alive again
            Assert.AreEqual(8u, predictor.LastSeq);
            Vector3 predicted = predictor.PredictedPosition;

            // The server respawned before taking seq 5, stepped seq 5 (empty) at the spawn point and acked it.
            var server = spawn;
            MovementSimulation.Step(ref server, new InputCommand { Seq = 5 }, Step, GameMap.Boxes, GameMap.Terrain);
            predictor.Reconcile(Alive(server.Position, server.VelocityY, server.Yaw), 5);

            Assert.AreEqual(predicted.x, predictor.PredictedPosition.x, 1e-4f);
            Assert.AreEqual(predicted.z, predictor.PredictedPosition.z, 1e-4f);
        }

        // Died and Respawned are Reliable, snapshots Sequenced: a snapshot can arrive from the other life.
        [Test]
        public void Reconcile_IgnoresSnapshotFromTheOtherLife()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 2, Vector2.up);
            Vector3 before = predictor.PredictedPosition;

            predictor.Reconcile(new SnapshotEntity { Position = new System.Numerics.Vector3(9f, 0f, 9f), Flags = 0 }, 1);   // dead, but no PlayerDied yet
            Assert.AreEqual(before, predictor.PredictedPosition);

            predictor.SetDead();
            predictor.Reconcile(Alive(new System.Numerics.Vector3(-9f, 0f, -9f)), 2);   // alive, but no PlayerRespawned yet
            Assert.AreEqual(before, predictor.PredictedPosition);

            // The main reorder: PlayerRespawned (Reliable) overtakes the last dead snapshot (Sequenced, other channel).
            predictor.Respawn(new MoveState { Position = new System.Numerics.Vector3(5f, 0f, 5f) });
            AdvanceSteps(predictor, 3, Vector2.up);                                        // seq 3-5 alive from the spawn
            Vector3 afterRespawn = predictor.PredictedPosition;
            predictor.Reconcile(new SnapshotEntity { Position = new System.Numerics.Vector3(9f, 0f, 9f), Flags = 0 }, 4);   // old life's body
            Assert.AreEqual(afterRespawn, predictor.PredictedPosition);
        }
    }
}
