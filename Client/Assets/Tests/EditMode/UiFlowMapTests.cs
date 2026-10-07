using NUnit.Framework;
using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Tests
{
    // Phase 15 D4: the full map over InGame (UiFlow.MapOpen): it frees the cursor and blocks game input, Esc closes it before
    // the menu opens, and any screen change closes it.
    public class UiFlowMapTests
    {
        // 기능: 게임 화면까지 간 UiFlow를 만든다.
        // 입력: 없음.
        // 출력: InGame 화면의 UiFlow.
        private static UiFlow InGame()
        {
            var flow = new UiFlow();
            flow.ConnectRequested();
            flow.Update(UiConnection.Joined, false, 0, true, MatchFlowState.Playing);
            Assert.AreEqual(UiScreen.InGame, flow.Screen);
            return flow;
        }

        [Test]
        public void Toggle_OpensAndCloses_AndBlocksInput()
        {
            UiFlow flow = InGame();
            Assert.IsTrue(flow.AllowCursorLock);
            Assert.IsFalse(flow.BlocksGameInput);
            int version = flow.Version;
            flow.ToggleMap();
            Assert.IsTrue(flow.MapOpen);
            Assert.AreEqual(UiScreen.InGame, flow.Screen);
            Assert.IsFalse(flow.AllowCursorLock);
            Assert.IsTrue(flow.BlocksGameInput);
            Assert.Greater(flow.Version, version);
            flow.ToggleMap();
            Assert.IsFalse(flow.MapOpen);
            Assert.IsTrue(flow.AllowCursorLock);
            Assert.IsFalse(flow.BlocksGameInput);
        }

        [Test]
        public void Escape_ClosesTheMapFirst()
        {
            UiFlow flow = InGame();
            flow.OpenMap();
            flow.EscapePressed();
            Assert.IsFalse(flow.MapOpen);
            Assert.AreEqual(UiScreen.InGame, flow.Screen);   // not the menu
            flow.EscapePressed();
            Assert.AreEqual(UiScreen.Menu, flow.Screen);
        }

        [Test]
        public void OnlyInTheGame()
        {
            var title = new UiFlow();
            title.ToggleMap();
            Assert.IsFalse(title.MapOpen);

            UiFlow flow = InGame();
            flow.EscapePressed();   // menu
            flow.ToggleMap();
            Assert.IsFalse(flow.MapOpen);
            Assert.AreEqual(UiScreen.Menu, flow.Screen);
        }

        [Test]
        public void ScreenChange_ClosesTheMap()
        {
            UiFlow flow = InGame();
            flow.OpenMap();
            flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.Finished);   // a new result
            Assert.AreEqual(UiScreen.Result, flow.Screen);
            Assert.IsFalse(flow.MapOpen);

            flow = InGame();
            flow.OpenMap();
            flow.Update(UiConnection.Offline, false, 0, true, MatchFlowState.Playing);   // connection lost
            Assert.AreEqual(UiScreen.Disconnected, flow.Screen);
            Assert.IsFalse(flow.MapOpen);
        }

        [Test]
        public void StaysOpen_WhileTheGameGoesOn()
        {
            UiFlow flow = InGame();
            flow.OpenMap();
            int version = flow.Version;
            flow.Update(UiConnection.Joined, false, 0, true, MatchFlowState.FinalPhase);
            Assert.IsTrue(flow.MapOpen);
            Assert.AreEqual(version, flow.Version);
            flow.OpenMap();   // already open: nothing changes
            Assert.AreEqual(version, flow.Version);
        }
    }
}
