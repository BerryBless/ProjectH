using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Tests
{
    // D14 / Client Hot Path: MatchHud calls every Set* each frame; a string is built only when a shown value changed.
    // Phase 11: Korean texts, the spectated player's name, and no result line (the result screen shows it).
    public class MatchHudTextTests
    {
        [Test]
        public void Status_Texts()
        {
            var text = new MatchHudText();
            text.SetStatus(MatchFlowState.WaitingForPlayers, 0, 1, 1, 2);
            Assert.AreEqual("플레이어를 기다리는 중 1/2", text.Status);
            text.SetStatus(MatchFlowState.Starting, 7, 2, 2, 2);
            Assert.AreEqual("시작까지 7초", text.Status);
            text.SetStatus(MatchFlowState.Playing, 0, 3, 5, 2);
            Assert.AreEqual("생존 3/5", text.Status);
            text.SetStatus(MatchFlowState.FinalPhase, 0, 2, 5, 2);
            Assert.AreEqual("생존 2/5", text.Status);
            text.SetStatus(MatchFlowState.Finished, 0, 1, 5, 2);
            Assert.AreEqual("경기 종료", text.Status);
        }

        [Test]
        public void Status_BuildsOnlyOnChange()
        {
            var text = new MatchHudText();
            Assert.IsTrue(text.SetStatus(MatchFlowState.Playing, 0, 3, 5, 2));
            string built = text.Status;
            int rebuilds = text.Rebuilds;
            // Values the current state does not show (the countdown while playing) change nothing.
            for (int frame = 0; frame < 100; frame++) Assert.IsFalse(text.SetStatus(MatchFlowState.Playing, frame, 3, 5, 2));
            Assert.AreEqual(rebuilds, text.Rebuilds);
            Assert.AreSame(built, text.Status);
            Assert.IsTrue(text.SetStatus(MatchFlowState.Playing, 0, 2, 5, 2));   // someone died
        }

        [Test]
        public void Countdown_RebuildsOncePerSecond()
        {
            var text = new MatchHudText();
            int before = text.Rebuilds;
            for (int frame = 0; frame < 600; frame++)
                text.SetStatus(MatchFlowState.Starting, 10 - frame / 60, 2, 2, 2);   // 60 frames per second
            Assert.AreEqual(before + 10, text.Rebuilds);
        }

        [Test]
        public void Zone_Texts_AndBuildsOnlyOnChange()
        {
            var text = new MatchHudText();
            Assert.IsTrue(text.SetZone(ZoneHint.ShrinksIn, 12));
            Assert.AreEqual("자기장 축소까지 12초", text.Zone);
            Assert.IsFalse(text.SetZone(ZoneHint.ShrinksIn, 12));
            Assert.IsTrue(text.SetZone(ZoneHint.Closing, 0));
            Assert.AreEqual("자기장 축소 중", text.Zone);
            Assert.IsFalse(text.SetZone(ZoneHint.Closing, 5));   // seconds do not matter while closing
            Assert.IsTrue(text.SetZone(ZoneHint.None, 0));
            Assert.AreEqual(string.Empty, text.Zone);
        }

        [Test]
        public void Spectating_Texts_AndBuildsOnlyOnChange()
        {
            var text = new MatchHudText();
            const string alice = "alice";
            Assert.IsFalse(text.SetSpectating(0, null));
            Assert.IsTrue(text.SetSpectating(3, alice));
            Assert.AreEqual("관전 중: alice", text.Spectating);
            Assert.IsFalse(text.SetSpectating(3, alice));
            Assert.IsTrue(text.SetSpectating(4, null));
            Assert.AreEqual("관전 중: 플레이어 4", text.Spectating);
            Assert.IsTrue(text.SetSpectating(0, null));
            Assert.AreEqual(string.Empty, text.Spectating);
        }

        // The watched player's PlayerSpawned may arrive after the camera started following it: the same id with its
        // name rebuilds the line once, then nothing more.
        [Test]
        public void Spectating_NameArrivingLater_ForTheSameId_RebuildsOnce()
        {
            var text = new MatchHudText();
            Assert.IsTrue(text.SetSpectating(5, null));
            Assert.AreEqual("관전 중: 플레이어 5", text.Spectating);
            Assert.IsFalse(text.SetSpectating(5, null));

            const string bob = "bob";
            Assert.IsTrue(text.SetSpectating(5, bob));
            Assert.AreEqual("관전 중: bob", text.Spectating);
            int rebuilds = text.Rebuilds;
            Assert.IsFalse(text.SetSpectating(5, bob));
            Assert.AreEqual(rebuilds, text.Rebuilds);
        }
    }
}
