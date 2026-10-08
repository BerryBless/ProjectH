using NUnit.Framework;
using ProjectH.Client.Game;

namespace ProjectH.Client.Tests
{
    // Review fixes D2, D3 (STB-1, SEC-26): a snapshot tick far ahead of the newest one is dropped and counted, so one forged or
    // corrupt tick cannot pin the render tick; and the uint ViewTick the inputs carry ("now" = uint.MaxValue).
    public class ServerClockTests
    {
        private const int SimHz = 30;

        [Test]
        public void AHugeTick_IsRejected()
        {
            var clock = new ServerClock(SimHz);
            Assert.IsTrue(clock.OnSnapshot(100, 0.0));
            Assert.IsFalse(clock.OnSnapshot(100 + SimHz * 10 + 1, 0.0));   // same moment: the window is 10 s
            Assert.AreEqual(1, clock.TickRejects);
            Assert.AreEqual(100u, clock.LatestTick);
            Assert.IsFalse(clock.OnSnapshot(0xFFFFFF00u, 0.1));
            Assert.AreEqual(2, clock.TickRejects);

            Assert.IsTrue(clock.OnSnapshot(102, 0.2));                  // the next normal tick still counts
            Assert.AreEqual(102u, clock.LatestTick);
            Assert.IsTrue(clock.OnSnapshot(102 + SimHz * 10, 0.3));     // exactly the window: accepted
            Assert.Less(clock.RenderTick(0.3, 0.1), 1000.0);            // the render tick did not jump with the rejected ones
        }

        [Test]
        public void ARealGapInTime_WidensTheWindow()
        {
            var clock = new ServerClock(SimHz);
            Assert.IsTrue(clock.OnSnapshot(100, 0.0));
            // 20 s without a snapshot (the main thread stalled): the server moved on by 20 s worth of ticks.
            Assert.IsTrue(clock.OnSnapshot(100 + SimHz * 20, 20.0));
            Assert.AreEqual(0, clock.TickRejects);
        }

        [Test]
        public void TheFirstSample_IsTaken()
        {
            var clock = new ServerClock(SimHz);
            Assert.IsTrue(clock.OnSnapshot(5_000_000, 0.0));
            Assert.IsTrue(clock.IsReady);
            Assert.AreEqual(5_000_000u, clock.LatestTick);
        }

        [Test]
        public void ToViewTick_TruncatesAndSaturates_AndMeansNowForNoValue()
        {
            Assert.AreEqual(300u, ServerClock.ToViewTick(300.0));
            Assert.AreEqual(303u, ServerClock.ToViewTick(303.9));
            Assert.AreEqual(0u, ServerClock.ToViewTick(0.0));
            Assert.AreEqual(33_554_433u, ServerClock.ToViewTick(33_554_433.25));   // 2^25 + 1: exact, a float could not hold it
            Assert.AreEqual(uint.MaxValue, ServerClock.ToViewTick(double.NaN));
            Assert.AreEqual(uint.MaxValue, ServerClock.ToViewTick(-1.0));
            Assert.AreEqual(uint.MaxValue, ServerClock.ToViewTick(double.PositiveInfinity));
            Assert.AreEqual(uint.MaxValue, ServerClock.ToViewTick(5e12));
        }
    }
}
