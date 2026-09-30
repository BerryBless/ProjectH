using NUnit.Framework;
using ProjectH.Client.Game;

namespace ProjectH.Client.Tests
{
    public class FireRateAccumulatorTests
    {
        private static FireRateAccumulator NewRate() => new FireRateAccumulator(10f, 3);

        // Held from rest for N frames of dt (cap not reached, dt * rate < 1): shot k (k = 0, 1, ...) fires on
        // the first frame whose accumulated time (f + 1) * dt reaches k / rate, so
        //   shots = floor(N * dt * rate) + 1.
        // N * dt * rate is kept away from an integer so float rounding cannot move a shot across a frame:
        // 90 frames * 0.017 s * 10/s = 15.3 -> 16 shots (rate 9/s would give 14, rate 11/s 17).
        [Test]
        public void HeldContinuously_FiresOnPressThenOncePerInterval()
        {
            var rate = NewRate();
            int shots = 0;
            for (int frame = 0; frame < 90; frame++) shots += rate.Consume(0.017f, true);
            Assert.AreEqual(16, shots);
        }

        [Test]
        public void FirstShot_FiresOnThePressFrame()
        {
            Assert.AreEqual(1, NewRate().Consume(0.016f, true));
        }

        [Test]
        public void Released_FiresNothing()
        {
            var rate = NewRate();
            for (int frame = 0; frame < 100; frame++) Assert.AreEqual(0, rate.Consume(0.01f, false));
        }

        // Review Focus: a hitch frame must not burst a pile of effects.
        [Test]
        public void HitchFrame_IsCapped_AndBacklogIsDropped()
        {
            var rate = NewRate();
            // A 1 s frame from rest is owed 11 shots (cooldown -1 s); the cap fires 3 and the remaining
            // -0.7 s backlog is dropped (cooldown clamped to 0). No value here is near a rounding boundary.
            Assert.AreEqual(3, rate.Consume(1f, true));
            // Next frame: cooldown 0 - 0.01 <= 0 -> exactly one shot, not a burst of the dropped backlog.
            Assert.AreEqual(1, rate.Consume(0.01f, true));
        }

        // Held only on even frames, dt = 0.017 s, interval 0.1 s. After a shot the cooldown is 0.083 s;
        // it crosses zero on the 5th frame after it (0.083 - 5 * 0.017 = -0.002 s), which is a released
        // frame, so the overshoot is dropped (clamped to 0) instead of banked, and the next held frame
        // fires. One shot per 6 frames: frames 0, 6, ..., 84 -> 15 shots in 90 frames, fewer than the
        // 16 of a continuous hold. Banking the release time would fire more; margins are >= 0.002 s.
        [Test]
        public void TappingEveryOtherFrame_CannotBeatTheRate()
        {
            var rate = NewRate();
            int shots = 0;
            for (int frame = 0; frame < 90; frame++) shots += rate.Consume(0.017f, frame % 2 == 0);
            Assert.AreEqual(15, shots);
        }

        [Test]
        public void LongRelease_DoesNotBankShots()
        {
            var rate = NewRate();
            Assert.AreEqual(0, rate.Consume(1f, false));      // 1 s released: would be 10 banked shots
            Assert.AreEqual(1, rate.Consume(0.01f, true));    // press: exactly one shot
        }

        // Review Focus: the effect pools must not grow however long the trigger is held. LocalFireEffects
        // allocates its arrays once with the cursor capacity (16 tracers, 32 impacts) and only indexes them
        // through RingCursor.Next().
        [Test]
        public void RingCursor_WrapsWithinCapacity()
        {
            var ring = new RingCursor(16);
            for (int i = 0; i < 1000; i++)
            {
                int slot = ring.Next();
                Assert.AreEqual(i % 16, slot);
            }
            Assert.AreEqual(16, ring.Capacity);
        }
    }
}
