using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Client.Net;

namespace ProjectH.Client.Tests
{
    // Review fix D3 (SEC-25): only a spawn after the join answer, with our non-zero id, is our own character. Before the
    // answer (or with id 0) a spawn made a predictor with SimHz 0 that never sent an input.
    public class GameClientSpawnTests
    {
        [Test]
        public void JoinedWithOurId_IsMySpawn()
        {
            Assert.IsTrue(GameClient.IsMySpawn(ClientState.Joined, 5, 5));
        }

        [Test]
        public void BeforeTheJoinAnswer_IsNotMine()
        {
            Assert.IsFalse(GameClient.IsMySpawn(ClientState.Connected, 5, 5));
            Assert.IsFalse(GameClient.IsMySpawn(ClientState.Connecting, 5, 5));
            Assert.IsFalse(GameClient.IsMySpawn(ClientState.Disconnected, 5, 5));
        }

        [Test]
        public void IdZero_IsNotMine()
        {
            Assert.IsFalse(GameClient.IsMySpawn(ClientState.Joined, 0, 0));
            Assert.IsFalse(GameClient.IsMySpawn(ClientState.Connected, 0, 0));
        }

        [Test]
        public void AnotherId_IsNotMine()
        {
            Assert.IsFalse(GameClient.IsMySpawn(ClientState.Joined, 5, 6));
        }
    }
}
