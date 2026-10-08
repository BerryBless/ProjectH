using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using Num = System.Numerics;

namespace ProjectH.Client.Tests
{
    // Review fix D3 (SEC-24): the remote player table is capped at ProtocolConstants.MaxSnapshotEntities (an honest server
    // sends at most MaxPlayers - 1); a spawn over the cap makes no view and is counted. Builds real views (Unity objects),
    // cleaned up with UnityObjects.Destroy, which works outside Play mode.
    public class RemotePlayersTests
    {
        // 기능: 시험용 PlayerSpawned를 만든다.
        // 입력: id - Entity id.
        // 출력: 원점 근처 위치·이름을 가진 등장 정보.
        private static PlayerSpawned Spawned(int id) =>
            new PlayerSpawned { EntityId = (ushort)id, Position = new Num.Vector3(id, 1f, 0f), Yaw = 0f, Name = "p" + id };

        [Test]
        public void TheHundredAndFirstSpawn_IsIgnored()
        {
            var players = new RemotePlayers();
            try
            {
                for (int id = 1; id <= ProtocolConstants.MaxSnapshotEntities; id++) players.Spawn(Spawned(id), 0);
                Assert.AreEqual(ProtocolConstants.MaxSnapshotEntities, players.Count);
                Assert.AreEqual(0, players.SpawnRejects);

                int over = ProtocolConstants.MaxSnapshotEntities + 1;
                players.Spawn(Spawned(over), 0);
                Assert.AreEqual(ProtocolConstants.MaxSnapshotEntities, players.Count);
                Assert.AreEqual(1, players.SpawnRejects);
                Assert.IsFalse(players.Contains((ushort)over));

                players.Spawn(Spawned(1), 0);                       // a repeat of a known player is not a reject
                Assert.AreEqual(1, players.SpawnRejects);

                players.Despawn(1);                                  // room again
                players.Spawn(Spawned(over), 0);
                Assert.IsTrue(players.Contains((ushort)over));
                Assert.AreEqual(ProtocolConstants.MaxSnapshotEntities, players.Count);
            }
            finally
            {
                players.Clear();
                PlayerViewFactory.ReleaseMaterials();
            }
        }
    }
}
