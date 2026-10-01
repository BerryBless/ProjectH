using NUnit.Framework;
using ProjectH.Client.Game;
using UnityEngine;

namespace ProjectH.Client.Tests
{
    public class RemotePlayerInterpolatorTests
    {
        [Test]
        public void Sample_BetweenTwoTicks_Interpolates()
        {
            var interp = new RemotePlayerInterpolator();
            interp.Push(10, new Vector3(0f, 0f, 0f), 0f);
            interp.Push(12, new Vector3(2f, 0f, 0f), 90f);

            Assert.IsTrue(interp.TrySample(11.0, out Vector3 position, out float yaw));
            Assert.AreEqual(1f, position.x, 1e-4f);
            Assert.AreEqual(45f, yaw, 1e-3f);
        }

        [Test]
        public void Sample_PastNewest_HoldsNewest()
        {
            var interp = new RemotePlayerInterpolator();
            interp.Push(10, Vector3.zero, 0f);
            interp.Push(12, new Vector3(2f, 0f, 0f), 0f);

            Assert.IsTrue(interp.TrySample(20.0, out Vector3 position, out _));
            Assert.AreEqual(2f, position.x, 1e-4f);
        }

        [Test]
        public void Sample_BeforeOldest_ReturnsOldest()
        {
            var interp = new RemotePlayerInterpolator();
            interp.Push(10, new Vector3(5f, 0f, 0f), 0f);
            interp.Push(12, new Vector3(7f, 0f, 0f), 0f);

            Assert.IsTrue(interp.TrySample(3.0, out Vector3 position, out _));
            Assert.AreEqual(5f, position.x, 1e-4f);
        }

        [Test]
        public void OlderOrDuplicateTick_IsIgnored()
        {
            var interp = new RemotePlayerInterpolator();
            interp.Push(10, Vector3.zero, 0f);
            interp.Push(12, new Vector3(2f, 0f, 0f), 0f);
            interp.Push(12, new Vector3(99f, 0f, 0f), 0f);
            interp.Push(11, new Vector3(99f, 0f, 0f), 0f);

            Assert.IsTrue(interp.TrySample(12.0, out Vector3 position, out _));
            Assert.AreEqual(2f, position.x, 1e-4f);
        }

        [Test]
        public void Ring_KeepsNewestEight()
        {
            var interp = new RemotePlayerInterpolator();
            for (uint t = 1; t <= 12; t++) interp.Push(t * 2, new Vector3(t, 0f, 0f), 0f);

            // Oldest kept sample is tick 10 (t = 5).
            Assert.IsTrue(interp.TrySample(0.0, out Vector3 position, out _));
            Assert.AreEqual(5f, position.x, 1e-4f);
        }

        [Test]
        public void Empty_ReturnsFalse()
        {
            Assert.IsFalse(new RemotePlayerInterpolator().TrySample(1.0, out _, out _));
        }

        [Test]
        public void NonFiniteSamples_AreDropped()
        {
            var interp = new RemotePlayerInterpolator();
            interp.Push(10, new Vector3(float.NaN, 0f, 0f), 0f);
            Assert.IsFalse(interp.TrySample(10.0, out _, out _));

            interp.Push(10, new Vector3(1f, 0f, 0f), 0f);
            interp.Push(12, new Vector3(0f, float.PositiveInfinity, 0f), 0f);
            interp.Push(13, new Vector3(0f, 0f, 0f), float.NaN);

            Assert.IsTrue(interp.TrySample(20.0, out Vector3 position, out float yaw));
            Assert.AreEqual(1f, position.x, 1e-4f);
            Assert.AreEqual(0f, yaw, 1e-4f);
        }

        [Test]
        public void Clear_DropsHistory_SoNextSampleIsTheNewPosition()
        {
            var interp = new RemotePlayerInterpolator();
            interp.Push(10, new Vector3(9f, 0f, 0f), 0f);
            interp.Push(12, new Vector3(9f, 0f, 0f), 0f);
            interp.Clear();
            Assert.IsFalse(interp.TrySample(11.0, out _, out _));

            interp.Push(20, new Vector3(-5f, 0f, 0f), 0f);
            // Rendering lags behind the newest tick: with one sample it is shown at once, no slide.
            Assert.IsTrue(interp.TrySample(16.0, out Vector3 position, out _));
            Assert.AreEqual(-5f, position.x, 1e-4f);
        }

        // Phase 5: PlayerRespawned (ReliableOrdered) and snapshots (Sequenced) are on different channels, so the
        // event can come before or after the first snapshots from the spawn point. Either way the view snaps.
        private static readonly Vector3 Far = new Vector3(40f, 0f, 0f);
        private static readonly Vector3 Spawn = new Vector3(-20f, 0f, 0f);

        [Test]
        public void Teleport_EventFirst_DropsTheOldHistory()
        {
            var interp = new RemotePlayerInterpolator();
            interp.Push(10, Far, 0f);
            interp.Push(12, Far, 0f);
            interp.Teleport(Spawn);
            interp.Push(14, Spawn, 0f);

            Assert.IsTrue(interp.TrySample(11.0, out Vector3 position, out _));
            Assert.AreEqual(Spawn.x, position.x, 1e-4f);   // no slide from Far
        }

        [Test]
        public void Teleport_EventLate_KeepsTheSamplesFromTheSpawn()
        {
            var interp = new RemotePlayerInterpolator();
            interp.Push(10, Far, 0f);
            interp.Push(12, Far, 0f);
            interp.Push(14, Spawn, 0f);
            interp.Push(16, Spawn + new Vector3(1f, 0f, 0f), 0f);
            interp.Teleport(Spawn);

            Assert.IsTrue(interp.TrySample(15.0, out Vector3 between, out _));
            Assert.AreEqual(Spawn.x + 0.5f, between.x, 1e-4f);   // both post-respawn samples kept
            Assert.IsTrue(interp.TrySample(11.0, out Vector3 early, out _));
            Assert.AreEqual(Spawn.x, early.x, 1e-4f);            // the old ones are gone
        }

        [Test]
        public void Teleport_AfterTheDeadToAliveClear_IsIdempotent()
        {
            var interp = new RemotePlayerInterpolator();
            interp.Push(10, Far, 0f);
            interp.Clear();   // what RemotePlayers.Push does on the alive flag flip
            interp.Push(14, Spawn, 0f);
            interp.Push(16, Spawn + new Vector3(1f, 0f, 0f), 0f);
            interp.Teleport(Spawn);
            interp.Teleport(Spawn);

            Assert.IsTrue(interp.TrySample(15.0, out Vector3 position, out _));
            Assert.AreEqual(Spawn.x + 0.5f, position.x, 1e-4f);
        }

        // Accepted residual: an old sample within the keep radius (5 m) of the spawn point cannot be told apart from
        // a post-respawn one, so it is kept (the view then slides less than 5 m). Farther ones are dropped.
        [Test]
        public void Teleport_KeepsOldSamplesWithin5m_DropsFartherOnes()
        {
            var interp = new RemotePlayerInterpolator();
            interp.Push(8, Spawn + new Vector3(6f, 0f, 0f), 0f);     // 6 m: dropped
            interp.Push(10, Spawn + new Vector3(4.9f, 0f, 0f), 0f);  // 4.9 m: kept
            interp.Push(12, Spawn + new Vector3(4f, 0f, 0f), 0f);    // 4 m: kept
            interp.Teleport(Spawn);

            Assert.IsTrue(interp.TrySample(0.0, out Vector3 oldest, out _));
            Assert.AreEqual(Spawn.x + 4.9f, oldest.x, 1e-4f);
            Assert.IsTrue(interp.TrySample(11.0, out Vector3 between, out _));
            Assert.AreEqual(Spawn.x + 4.45f, between.x, 1e-4f);
        }

        [Test]
        public void Teleport_NonFiniteTarget_IsIgnored()
        {
            var interp = new RemotePlayerInterpolator();
            interp.Push(10, Far, 0f);
            interp.Teleport(new Vector3(float.NaN, 0f, 0f));

            Assert.IsTrue(interp.TrySample(10.0, out Vector3 position, out _));
            Assert.AreEqual(Far.x, position.x, 1e-4f);
        }

        [Test]
        public void ServerClock_RenderTick_NeverGoesBackwards()
        {
            var clock = new ServerClock(30);
            clock.OnSnapshot(300, 10.0);
            double first = clock.RenderTick(10.0, 0.1);
            clock.OnSnapshot(290, 10.0);   // late/odd sample pulls the offset down
            double second = clock.RenderTick(10.0, 0.1);
            Assert.GreaterOrEqual(second, first);
        }
    }
}
