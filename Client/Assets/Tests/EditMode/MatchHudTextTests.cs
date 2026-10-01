using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Tests
{
    // D14 / Client Hot Path: MatchHud calls every Set* each frame; a string is built only when a shown value changed.
    public class MatchHudTextTests
    {
        [Test]
        public void Status_Texts()
        {
            var text = new MatchHudText();
            text.SetStatus(MatchFlowState.WaitingForPlayers, 0, 1, 1, 2);
            Assert.AreEqual("Waiting for players 1/2", text.Status);
            text.SetStatus(MatchFlowState.Starting, 7, 2, 2, 2);
            Assert.AreEqual("Starting in 7", text.Status);
            text.SetStatus(MatchFlowState.Playing, 0, 3, 5, 2);
            Assert.AreEqual("Alive 3/5", text.Status);
            text.SetStatus(MatchFlowState.FinalPhase, 0, 2, 5, 2);
            Assert.AreEqual("Alive 2/5", text.Status);
            text.SetStatus(MatchFlowState.Finished, 0, 1, 5, 2);
            Assert.AreEqual("Match over", text.Status);
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
            Assert.AreEqual("Zone shrinking in 12s", text.Zone);
            Assert.IsFalse(text.SetZone(ZoneHint.ShrinksIn, 12));
            Assert.IsTrue(text.SetZone(ZoneHint.Closing, 0));
            Assert.AreEqual("Zone closing", text.Zone);
            Assert.IsFalse(text.SetZone(ZoneHint.Closing, 5));   // seconds do not matter while closing
            Assert.IsTrue(text.SetZone(ZoneHint.None, 0));
            Assert.AreEqual(string.Empty, text.Zone);
        }

        [Test]
        public void Result_Texts()
        {
            var text = new MatchHudText();
            text.SetResult(true, 1, 4);
            Assert.AreEqual("#1 VICTORY", text.Result);
            text.SetResult(false, 3, 2);
            Assert.AreEqual("ELIMINATED #3 — 2 kills", text.Result);
            text.SetResult(false, 2, 1);
            Assert.AreEqual("ELIMINATED #2 — 1 kill", text.Result);
            Assert.IsFalse(text.SetResult(false, 2, 1));
            text.SetResult(false, 0, 0);
            Assert.AreEqual(string.Empty, text.Result);
        }

        [Test]
        public void Spectating_Texts_AndBuildsOnlyOnChange()
        {
            var text = new MatchHudText();
            Assert.IsFalse(text.SetSpectating(0));
            Assert.IsTrue(text.SetSpectating(3));
            Assert.AreEqual("Spectating Player 3", text.Spectating);
            Assert.IsFalse(text.SetSpectating(3));
            Assert.IsTrue(text.SetSpectating(0));
            Assert.AreEqual(string.Empty, text.Spectating);
        }
    }
}
