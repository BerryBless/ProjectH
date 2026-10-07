using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Tests
{
    // Phase 14 D14: the squad HUD strings are built only when a shown value changes, and say each member's state.
    public class SquadHudTextTests
    {
        [Test]
        public void Row_IsBuiltOnce_ForTheSameValues()
        {
            var text = new SquadHudText();
            string name = "alice";
            Assert.IsTrue(text.SetRow(0, 3, name, TeamMemberState.Up, TeamMemberFlags.None, false));
            Assert.AreEqual("alice", text.Row(0));
            int builds = text.Rebuilds;
            for (int i = 0; i < 5; i++) Assert.IsFalse(text.SetRow(0, 3, name, TeamMemberState.Up, TeamMemberFlags.None, false));
            Assert.AreEqual(builds, text.Rebuilds);
        }

        [Test]
        public void Row_SaysTheState()
        {
            var text = new SquadHudText();
            text.SetRow(0, 3, "alice", TeamMemberState.Downed, TeamMemberFlags.None, false);
            Assert.AreEqual("alice  기절", text.Row(0));
            text.SetRow(0, 3, "alice", TeamMemberState.Eliminated, TeamMemberFlags.None, false);
            Assert.AreEqual("alice  탈락", text.Row(0));
            text.SetRow(0, 3, "alice", TeamMemberState.Eliminated, TeamMemberFlags.CardDropped, false);
            Assert.AreEqual("alice  탈락 · 카드 떨어짐", text.Row(0));
            text.SetRow(0, 3, "alice", TeamMemberState.Eliminated, TeamMemberFlags.CardHeld, false);
            Assert.AreEqual("alice  탈락 · 카드 보유", text.Row(0));
            text.SetRow(0, 3, "alice", TeamMemberState.Rebooting, TeamMemberFlags.CardHeld, false);
            Assert.AreEqual("alice  재투입 중", text.Row(0));
            text.SetRow(1, 5, null, TeamMemberState.Up, TeamMemberFlags.None, true);
            Assert.AreEqual("플레이어 5 (나)", text.Row(1));
        }

        [Test]
        public void Cards_AndBleed_RebuildOnlyOnChange()
        {
            var text = new SquadHudText();
            Assert.IsTrue(text.SetCards(0));
            Assert.AreEqual(string.Empty, text.Cards);
            Assert.IsFalse(text.SetCards(0));
            Assert.IsTrue(text.SetCards(2));
            Assert.AreEqual("재투입 카드 2장", text.Cards);

            Assert.IsTrue(text.SetBleed(-1));
            Assert.AreEqual(string.Empty, text.Bleed);
            Assert.IsFalse(text.SetBleed(-5));   // every negative is "not downed"
            Assert.IsTrue(text.SetBleed(12));
            Assert.AreEqual("기절 · 출혈 12초", text.Bleed);
            Assert.IsFalse(text.SetBleed(12));
        }

        [Test]
        public void KillFeed_DownedLine_AndResultTexts()
        {
            Assert.AreEqual("bob ▸ alice 기절", UiText.DownedLine("bob", "alice", DeathCause.Zone));
            Assert.AreEqual("자기장 ▸ alice 기절", UiText.DownedLine(null, "alice", DeathCause.Zone));
            Assert.AreEqual("낙하 ▸ alice 기절", UiText.DownedLine(null, "alice", DeathCause.Fall));
            Assert.AreEqual("순위 2 / 4팀", UiText.Placement(2, 4, true));
            Assert.AreEqual(UiText.Placement(2, 8), UiText.Placement(2, 8, false));   // Solo unchanged
            Assert.AreEqual("우승 팀: alice", UiText.Winner("alice", true));
            Assert.AreEqual(UiText.Winner(null), UiText.Winner(null, true));
            Assert.AreEqual(UiText.Winner("alice"), UiText.Winner("alice", false));
        }
    }
}
