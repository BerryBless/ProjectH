using NUnit.Framework;
using ProjectH.Client.Game;

namespace ProjectH.Client.Tests
{
    public class RingCursorTests
    {
        // The effect pools must not grow however long the trigger is held. LocalFireEffects allocates its
        // arrays once with the cursor capacity (16 tracers, 32 impacts) and only indexes them through Next().
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
