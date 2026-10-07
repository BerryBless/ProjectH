using System.Numerics;
using NUnit.Framework;
using ProjectH.Client.Game.Map;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Tests
{
    // Phase 15 D10: TeamMarkers replaces the whole team marker state (idempotent), read from the real packet.
    public class TeamMarkerStateTests
    {
        // 기능: Ping 하나를 만든다.
        // 입력: id - 일련번호, owner - 주인, kind - 종류, endTick - 끝 Tick.
        // 출력: Ping.
        private static MarkerPing Ping(byte id, ushort owner, MapMarkerKind kind, uint endTick) =>
            new MarkerPing { Id = id, OwnerId = owner, Kind = kind, Position = new Vector3(id, 0f, -id), EndTick = endTick };

        // 기능: TeamMarkers 패킷을 쓰고 NetClient처럼 고정 배열에 읽어 상태에 적용한다.
        // 입력: state - 적용할 상태, pings·waypoints - 보낼 목록.
        // 출력: 반환값 없음. 읽기에 실패하면 테스트가 실패한다.
        private static void Receive(TeamMarkerState state, MarkerPing[] pings, MarkerWaypoint[] waypoints)
        {
            var buffer = new byte[TeamMarkersPacket.MaxSize];
            var writer = new PacketWriter(buffer);
            TeamMarkersPacket.Write(ref writer, pings, waypoints);
            var reader = new PacketReader(writer.WrittenSpan);
            Assert.IsTrue(reader.TryReadPacketId(out PacketId id));
            Assert.AreEqual(PacketId.TeamMarkers, id);
            var readPings = new MarkerPing[MapMarkerConstants.MaxTeamPings];
            var readWaypoints = new MarkerWaypoint[MapMarkerConstants.MaxWaypoints];
            Assert.IsTrue(TeamMarkersPacket.TryRead(ref reader, readPings, readWaypoints, out int pc, out int wc));
            state.Apply(readPings, pc, readWaypoints, wc);
        }

        [Test]
        public void Apply_ReplacesEverything()
        {
            var state = new TeamMarkerState();
            Receive(state, new[] { Ping(1, 3, MapMarkerKind.Location, 100), Ping(2, 4, MapMarkerKind.Enemy, 90) },
                new[] { new MarkerWaypoint { OwnerId = 3, Position = new Vector3(10f, 1f, 20f) } });
            Assert.AreEqual(2, state.PingCount);
            Assert.AreEqual(1, state.WaypointCount);
            Assert.AreEqual(MapMarkerKind.Enemy, state.Ping(1).Kind);
            Assert.AreEqual(10f, state.Waypoint(0).Position.X, 0.01f);

            int version = state.Version;
            Receive(state, new[] { Ping(5, 4, MapMarkerKind.Danger, 200) }, new MarkerWaypoint[0]);
            Assert.AreEqual(1, state.PingCount);
            Assert.AreEqual(0, state.WaypointCount);
            Assert.AreEqual(5, state.Ping(0).Id);
            Assert.Greater(state.Version, version);
        }

        [Test]
        public void Apply_SameListTwice_IsTheSameState()
        {
            var state = new TeamMarkerState();
            MarkerPing[] pings = { Ping(1, 3, MapMarkerKind.Item, 100) };
            Receive(state, pings, new MarkerWaypoint[0]);
            Receive(state, pings, new MarkerWaypoint[0]);
            Assert.AreEqual(1, state.PingCount);
            Assert.AreEqual(1, state.Ping(0).Id);
        }

        [Test]
        public void Apply_CountsAreClampedToTheArrays()
        {
            var state = new TeamMarkerState();
            var many = new MarkerPing[20];
            for (int i = 0; i < many.Length; i++) many[i] = Ping((byte)(i + 1), 2, MapMarkerKind.Location, 50);
            state.Apply(many, many.Length, null, 3);
            Assert.AreEqual(MapMarkerConstants.MaxTeamPings, state.PingCount);
            Assert.AreEqual(0, state.WaypointCount);
            state.Apply(many, -5, null, 0);
            Assert.AreEqual(0, state.PingCount);
        }

        [Test]
        public void Clear_EmptiesAndCountsOnlyARealChange()
        {
            var state = new TeamMarkerState();
            state.Clear();
            Assert.AreEqual(0, state.Version);
            Receive(state, new[] { Ping(1, 3, MapMarkerKind.Location, 100) }, new MarkerWaypoint[0]);
            int version = state.Version;
            state.Clear();
            Assert.AreEqual(0, state.PingCount);
            Assert.AreEqual(version + 1, state.Version);
        }

        [Test]
        public void IsLive_UntilTheEndTick()
        {
            var state = new TeamMarkerState();
            Receive(state, new[] { Ping(1, 3, MapMarkerKind.Location, 100) }, new MarkerWaypoint[0]);
            Assert.IsTrue(state.IsLive(0, 99.9));
            Assert.IsFalse(state.IsLive(0, 100));
            Assert.IsTrue(state.IsLive(0, 0));   // no clock yet: shown
        }
    }
}
