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
