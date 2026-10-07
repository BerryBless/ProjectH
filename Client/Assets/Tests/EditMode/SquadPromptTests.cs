using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Vector3 = System.Numerics.Vector3;

namespace ProjectH.Client.Tests
{
    // Phase 14 D8, D10: which revive or reboot the hint names, and the bleed-out seconds shown from the downed health.
    public class SquadPromptTests
    {
        [Test]
        public void NearestDowned_WithinTheReviveRange()
        {
            var feet = new Vector3(10f, 0f, 10f);
            var downed = new[] { new Vector3(11.5f, 0f, 10f), new Vector3(10f, 0f, 10.8f), new Vector3(13f, 0f, 10f) };
            Assert.AreEqual(1, SquadPrompt.NearestDowned(feet, downed, 3));
            Assert.AreEqual(0, SquadPrompt.NearestDowned(feet, downed, 1));   // count limits the buffer
            Assert.AreEqual(-1, SquadPrompt.NearestDowned(feet, new[] { new Vector3(12.1f, 0f, 10f) }, 1));
            Assert.AreEqual(0, SquadPrompt.NearestDowned(feet, new[] { new Vector3(12f, 0f, 10f) }, 1));   // exactly at range
            Assert.AreEqual(-1, SquadPrompt.NearestDowned(feet, downed, 0));
        }

        [Test]
        public void NearestStation_HorizontalRange_AndHeight()
        {
            Vector3 station = RebootStations.All[2];
            Assert.AreEqual(2, SquadPrompt.NearestStation(station + new Vector3(2f, 0f, 2f)));
            Assert.AreEqual(-1, SquadPrompt.NearestStation(station + new Vector3(2.5f, 0f, 2.5f)));   // 3.5 m away
            Assert.AreEqual(2, SquadPrompt.NearestStation(station + new Vector3(0f, 1.9f, 0f)));
            Assert.AreEqual(-1, SquadPrompt.NearestStation(station + new Vector3(0f, 2.1f, 0f)));
            Assert.AreEqual(-1, SquadPrompt.NearestStation(Vector3.Zero));
        }

        [Test]
        public void Cooldown_FollowsTheMaskAndTheEndTick()
        {
            var state = new RebootStationsState { CooldownMask = 0b0100 };
            state.SetEndTick(2, 900);
            Assert.IsTrue(SquadPrompt.IsCoolingDown(state, 2, 899.5));
            Assert.IsFalse(SquadPrompt.IsCoolingDown(state, 2, 900));   // ended, even before the next packet clears the bit
            Assert.IsFalse(SquadPrompt.IsCoolingDown(state, 1, 10));
        }

        [Test]
        public void BleedSeconds_FromTheDownedHealth()
        {
            Assert.AreEqual(30, SquadPrompt.BleedSecondsLeft(100));
            Assert.AreEqual(15, SquadPrompt.BleedSecondsLeft(50));
            Assert.AreEqual(1, SquadPrompt.BleedSecondsLeft(1));
            Assert.AreEqual(0, SquadPrompt.BleedSecondsLeft(0));
            Assert.AreEqual(0, SquadPrompt.BleedSecondsLeft(-3));
        }
    }
}
