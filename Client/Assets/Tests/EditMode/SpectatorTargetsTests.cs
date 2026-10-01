using NUnit.Framework;
using ProjectH.Client.Game;

namespace ProjectH.Client.Tests
{
    // D5: a dead player first watches its killer, a click moves to the next living player (ascending id, wrapping),
    // and a target that dies is replaced by the next one.
    public class SpectatorTargetsTests
    {
        private static readonly ushort[] Alive = { 7, 2, 5 };   // any order, as RemotePlayers lists them

        [Test]
        public void Next_GoesUpInIdOrder_AndWraps()
        {
            Assert.AreEqual(5, SpectatorTargets.Next(Alive, 3, 2));
            Assert.AreEqual(7, SpectatorTargets.Next(Alive, 3, 5));
            Assert.AreEqual(2, SpectatorTargets.Next(Alive, 3, 7));
            Assert.AreEqual(2, SpectatorTargets.Next(Alive, 3, 0));   // no target yet: the smallest
            Assert.AreEqual(7, SpectatorTargets.Next(Alive, 3, 6));   // current died: the next one after it
        }

        [Test]
        public void Next_WithOneOrNobody()
        {
            Assert.AreEqual(5, SpectatorTargets.Next(new ushort[] { 5 }, 1, 5));
            Assert.AreEqual(0, SpectatorTargets.Next(new ushort[0], 0, 5));
            Assert.AreEqual(0, SpectatorTargets.Next(Alive, 0, 5));   // count limits the buffer
        }

        [Test]
        public void Resolve_FollowsTheKillerFirst()
        {
            Assert.AreEqual(7, SpectatorTargets.Resolve(Alive, 3, 0, preferred: 7));
        }

        [Test]
        public void Resolve_KeepsALivingTarget()
        {
            Assert.AreEqual(2, SpectatorTargets.Resolve(Alive, 3, 2, preferred: 7));
        }

        [Test]
        public void Resolve_ATargetThatDied_MovesToTheNext()
        {
            ushort[] after = { 7, 5 };   // 2 died
            Assert.AreEqual(5, SpectatorTargets.Resolve(after, 2, 2, preferred: 0));
            // The killer is dead too (zone death, or killed later): the next one after the old target.
            Assert.AreEqual(5, SpectatorTargets.Resolve(after, 2, 2, preferred: 9));
        }

        [Test]
        public void Resolve_NobodyLeft_IsZero()
        {
            Assert.AreEqual(0, SpectatorTargets.Resolve(new ushort[4], 0, 3, preferred: 3));
        }

        [Test]
        public void Follow_TakesTheKillerOnce_ThenForgetsIt()
        {
            ushort preferred = 7;
            Assert.AreEqual(7, SpectatorTargets.Follow(Alive, 3, 0, ref preferred));
            Assert.AreEqual(0, preferred);
        }

        [Test]
        public void Follow_AfterCycling_ATargetThatDies_MovesOn_NotBackToTheKiller()
        {
            ushort preferred = 7;
            ushort target = SpectatorTargets.Follow(Alive, 3, 0, ref preferred);   // the killer, 7
            target = SpectatorTargets.Next(Alive, 3, target);                      // a click: wraps to 2
            Assert.AreEqual(2, target);

            ushort[] after = { 7, 5 };   // 2 died; the killer 7 still lives
            Assert.AreEqual(5, SpectatorTargets.Follow(after, 2, target, ref preferred));
        }

        [Test]
        public void Follow_NobodyAlive_KeepsThePreferenceForLater()
        {
            ushort preferred = 7;
            Assert.AreEqual(0, SpectatorTargets.Follow(new ushort[4], 0, 0, ref preferred));
            Assert.AreEqual(7, preferred);   // the killer's snapshot may not have arrived yet
        }
    }
}
