using System;
using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Server.Game.Map;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 15 (spec "검증 계획", Server): ping and waypoint checks, limits, lifetimes, team-only delivery, resends and clean-ups,
// through Match.HandleMarker and Match.Tick.
public class MapMatchTests
{
    private const string WideZone = """
        {
          "initialCenter": [0, 0],
          "initialRadius": 200,
          "arenaHalfSize": 80,
          "phases": [ {"waitSeconds":600,"shrinkSeconds":10,"targetRadius":0,"damagePerSecond":1} ]
        }
        """;

    // 기능: TeamSize 2, 4명(1·2 = 팀 1, 3·4 = 팀 2)으로 경기를 시작하고 광장에 세운다.
    // 입력: a1·a2·b1·b2 - 입장한 플레이어, map - 지도 수치(null = 기본값), record - 보낸 패킷을 기록할지.
    // 출력: 경기가 진행 중인 RoyaleHarness.
    private static RoyaleHarness Duo(out PlayerEntity a1, out PlayerEntity a2, out PlayerEntity b1, out PlayerEntity b2, MapCatalog? map = null,
        bool record = true)
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout, WideZone, teamSize: 2, map: map, record: record);
        a1 = h.Join(1);
        a2 = h.Join(2);
        b1 = h.Join(3);
        b2 = h.Join(4);
        h.RunToMatch();
        h.Place(a1, P(0f, 0f));
        h.Place(a2, P(3f, 0f));
        h.Place(b1, P(0f, 8f));
        h.Place(b2, P(-3f, 8f));
        h.Packets.Clear();
        return h;
    }

    private static Vector3 P(float x, float z) => SandboxHarness.Ground(x, z);

    // 기능: 기본값에서 한 줄을 바꾼 지도 수치를 만든다.
    // 입력: from - 바꿀 문자열, to - 새 문자열.
    // 출력: MapCatalog.
    private static MapCatalog Map(string from, string to)
    {
        Assert.True(MapCatalog.TryParse(MapCatalog.DefaultJson.Replace(from, to), 30, out var map, out string? error), error);
        return map!;
    }

    // 기능: 한 플레이어가 보낸 것처럼 지도 표시 요청을 처리한다.
    // 입력: h - 경기, p - 보내는 사람, kind - 종류, at - 위치, target - 대상 id.
    // 출력: 반환값 없음.
    private static void Mark(RoyaleHarness h, PlayerEntity p, MapMarkerKind kind, Vector3 at, ushort target = 0) =>
        h.Match.HandleMarker(p.PeerId, new MapMarker { Kind = kind, Position = at, TargetId = target });

    // 기능: 한 연결이 받은 마지막 TeamMarkers를 읽는다.
    // 입력: h - 경기, peer - 연결 id.
    // 출력: Ping 배열과 Waypoint 배열(받은 것이 없으면 실패).
    private static (MarkerPing[] Pings, MarkerWaypoint[] Waypoints) Last(RoyaleHarness h, int peer)
    {
        var sent = h.SentTo(peer, PacketId.TeamMarkers);
        Assert.NotEmpty(sent);
        var reader = RoyaleHarness.Reader(sent[^1]);
        var pings = new MarkerPing[MapMarkerConstants.MaxTeamPings];
        var waypoints = new MarkerWaypoint[MapMarkerConstants.MaxWaypoints];
        Assert.True(TeamMarkersPacket.TryRead(ref reader, pings, waypoints, out int pc, out int wc));
        return (pings[..pc], waypoints[..wc]);
    }

    // ---- delivery and common checks (D8, D10) ----

    [Fact]
    public void ALocationPing_GoesToTheTeamOnly_AtTheEndOfTheTick()
    {
        var h = Duo(out var a1, out _, out _, out _);
        Mark(h, a1, MapMarkerKind.Location, P(10f, 10f));
        Assert.Empty(h.SentTo(1, PacketId.TeamMarkers));   // not before the tick ends
        h.Match.Tick();
        var (pings, waypoints) = Last(h, 1);
        Assert.Single(pings);
        Assert.Empty(waypoints);
        Assert.Equal(MapMarkerKind.Location, pings[0].Kind);
        Assert.Equal(a1.EntityId, pings[0].OwnerId);
        Assert.Equal(10f, pings[0].Position.X, 2);
        Assert.Single(Last(h, 2).Pings);                  // the teammate too
        Assert.Empty(h.SentTo(3, PacketId.TeamMarkers));   // never the other team
        Assert.Empty(h.SentTo(4, PacketId.TeamMarkers));
        Assert.Equal(1, h.Match.MarkerPings);
    }

    [Fact]
    public void TheHeight_IsClampedToTheBuildRangeAboveTheTerrain()
    {
        var h = Duo(out var a1, out _, out _, out _);
        Vector3 ground = P(10f, 10f);
        Mark(h, a1, MapMarkerKind.Location, ground + new Vector3(0f, 300f, 0f));
        Mark(h, a1, MapMarkerKind.Danger, ground - new Vector3(0f, 50f, 0f));
        h.Match.Tick();
        var pings = Last(h, 1).Pings;
        float top = ground.Y + BuildGrid.Levels * BuildGrid.LevelHeight + 2f;
        Assert.Equal(top, pings.First(p => p.Kind == MapMarkerKind.Location).Position.Y, 1);
        Assert.Equal(ground.Y - 1f, pings.First(p => p.Kind == MapMarkerKind.Danger).Position.Y, 1);
    }

    [Fact]
    public void ADownedPlayer_CanPing()
    {
        // Spec D6: a knocked-down participant pings (ActionsAllowed is not the gate).
        var h = Duo(out var a1, out _, out _, out _);
        Assert.True(h.Match.DownPlayer(a1));
        Mark(h, a1, MapMarkerKind.Location, P(5f, 5f));
        h.Match.Tick();
        Assert.Single(Last(h, 1).Pings);
    }

    [Fact]
    public void AnEliminatedPlayer_ASpectator_AndAPointOutsideTheMap_AreRefused()
    {
        var h = Duo(out var a1, out _, out var b1, out _);
        h.Match.KillPlayer(b1);
        Mark(h, b1, MapMarkerKind.Location, P(5f, 5f));
        Mark(h, a1, MapMarkerKind.Location, new Vector3(80.5f, 0f, 0f));
        Mark(h, a1, MapMarkerKind.WaypointSet, new Vector3(0f, 0f, -81f));
        PlayerEntity late = h.Join(9);   // joins during the match: a spectator without a team
        Mark(h, late, MapMarkerKind.Location, P(5f, 5f));
        h.Match.Tick();
        Assert.Equal(0, h.Match.ActivePings);
        Assert.False(a1.HasWaypoint);
        Assert.Equal(4, h.Match.MarkersRefused);
        Assert.Empty(h.SentTo(3, PacketId.TeamMarkers));
    }

    [Fact]
    public void Nothing_IsTakenOutsideAMatch()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout, WideZone, teamSize: 2);
        PlayerEntity a = h.Join(1);
        h.Join(2);
        Mark(h, a, MapMarkerKind.Location, P(5f, 5f));   // the lobby: no team, not a match
        h.Match.Tick();
        Assert.Equal(0, h.Match.ActivePings);
        Assert.Equal(1, h.Match.MarkersRefused);
    }

    // ---- Enemy (D8) ----

    [Fact]
    public void AnEnemyInSight_IsConfirmed_AtItsFeet()
    {
        var h = Duo(out var a1, out _, out var b1, out _);
        Mark(h, a1, MapMarkerKind.Enemy, P(1f, 1f), b1.EntityId);   // the client's guess of the position does not matter
        h.Match.Tick();
        MarkerPing ping = Assert.Single(Last(h, 1).Pings);
        Assert.Equal(MapMarkerKind.Enemy, ping.Kind);
        Assert.Equal(b1.EntityId, ping.TargetId);
        Assert.Equal(b1.State.Position.Z, ping.Position.Z, 1);
        Assert.Equal(h.Match.MapData.EnemyPingTicks, ping.EndTick - (h.Match.ServerTick - 1));
        Assert.Equal(1, h.Match.EnemyPingsConfirmed);
    }

    [Fact]
    public void ATeammate_IsNotAnEnemy_ItBecomesALocationAtTheSentPoint()
    {
        var h = Duo(out var a1, out var a2, out _, out _);
        Mark(h, a1, MapMarkerKind.Enemy, P(1f, 1f), a2.EntityId);
        Mark(h, a1, MapMarkerKind.Enemy, P(2f, 2f), 999);   // nobody
        Mark(h, a1, MapMarkerKind.Enemy, P(3f, 3f), a1.EntityId);
        h.Match.Tick();
        var pings = Last(h, 1).Pings;
        Assert.Equal(3, pings.Length);
        Assert.All(pings, p => Assert.Equal(MapMarkerKind.Location, p.Kind));
        Assert.All(pings, p => Assert.Equal(0, p.TargetId));
        Assert.Contains(pings, p => Math.Abs(p.Position.X - 1f) < 0.01f);
        Assert.Equal(3, h.Match.EnemyPingsDemoted);
    }

    [Fact]
    public void AnEnemyBehindAMapWall_BecomesALocation()
    {
        // The open-ground wall at (26, 0) (3 m high, 5 m long): a1 west of it, b1 east of it.
        var h = Duo(out var a1, out _, out var b1, out _);
        h.Place(a1, P(22f, 0f));
        h.Place(b1, P(30f, 0f));
        Vector3 eye = a1.State.Position + new Vector3(0f, CombatRules.EyeHeight, 0f);
        Vector3 center = b1.State.Position + new Vector3(0f, 0.9f, 0f);
        float distance = Vector3.Distance(eye, center);
        Assert.True(HitScan.TraceWorld(eye, Vector3.Normalize(center - eye), distance, GameMap.Boxes, GameMap.Terrain) < distance, "the wall blocks");
        Mark(h, a1, MapMarkerKind.Enemy, P(29f, 0f), b1.EntityId);
        h.Match.Tick();
        MarkerPing ping = Assert.Single(Last(h, 1).Pings);
        Assert.Equal(MapMarkerKind.Location, ping.Kind);
        Assert.Equal(29f, ping.Position.X, 2);
        Assert.Equal(h.Match.MapData.PingTicks, ping.EndTick - (h.Match.ServerTick - 1));
    }

    [Fact]
    public void ABuildingPiece_DoesNotHideAnEnemy()
    {
        // D8: like the Phase 13.5 placement sight line, only map boxes, closed doors and terrain block.
        var h = Duo(out var a1, out _, out var b1, out _);
        h.Place(a1, P(2.5f, 0f));
        h.Place(b1, P(2.5f, 9f));
        // The south wall of the cell holding z 5-10 stands between them (BuildGrid cells are 5 m).
        var wall = new BuildPieceShape(BuildPieceType.Wall, BuildGrid.CellX(2.5f), 0, BuildGrid.CellZ(6f), 0);
        Assert.Equal(BuildResultCode.Ok, h.Match.PlacePiece(wall, BuildMaterialType.Wood, out _));
        Vector3 eye = a1.State.Position + new Vector3(0f, CombatRules.EyeHeight, 0f);
        Vector3 center = b1.State.Position + new Vector3(0f, 0.9f, 0f);
        float distance = Vector3.Distance(eye, center);
        Assert.True(ProjectH.Server.Game.Build.PieceTrace.Trace(eye, Vector3.Normalize(center - eye), distance, h.Match.Build, out _, out _, out _), "the wall is in the way");
        Mark(h, a1, MapMarkerKind.Enemy, P(2.5f, 9f), b1.EntityId);
        h.Match.Tick();
        Assert.Equal(MapMarkerKind.Enemy, Assert.Single(Last(h, 1).Pings).Kind);
    }

    [Fact]
    public void AnEnemyOutOfRange_BecomesALocation()
    {
        var h = Duo(out var a1, out _, out var b1, out _, Map("\"enemyPingRange\": 150", "\"enemyPingRange\": 5"));
        Mark(h, a1, MapMarkerKind.Enemy, P(0f, 8f), b1.EntityId);   // 8 m away
        h.Match.Tick();
        Assert.Equal(MapMarkerKind.Location, Assert.Single(Last(h, 1).Pings).Kind);
    }

    [Fact]
    public void AnEnemyOutsideTheMap_BecomesALocation_SoTheTeamCanStillReadTheList()
    {
        // A target outside +-80 (a transport rider) would make TeamMarkers unreadable on every client of the team.
        var h = Duo(out var a1, out _, out var b1, out _);
        b1.State.Position = new Vector3(0f, 40f, 95f);
        Mark(h, a1, MapMarkerKind.Enemy, P(0f, 70f), b1.EntityId);
        h.Match.Tick();
        MarkerPing ping = Assert.Single(Last(h, 1).Pings);
        Assert.Equal(MapMarkerKind.Location, ping.Kind);
        Assert.True(Math.Abs(ping.Position.Z) <= GameMap.HalfSize);
    }

    // ---- Item (D8) ----

    [Fact]
    public void AnItemPing_IsAtTheItem_AndAMissingOrFarItemIsDropped()
    {
        var h = Duo(out var a1, out _, out _, out _, Map("\"itemPingRange\": 60", "\"itemPingRange\": 10"));
        ushort near = h.Match.SpawnItem(new LootRoll(ItemKind.Consumable, (byte)ConsumableType.Medkit, 0, 1), P(4f, 2f), -1);
        ushort far = h.Match.SpawnItem(new LootRoll(ItemKind.Consumable, (byte)ConsumableType.Medkit, 0, 1), P(30f, 2f), -1);
        Mark(h, a1, MapMarkerKind.Item, P(0f, 0f), near);
        Mark(h, a1, MapMarkerKind.Item, P(0f, 0f), far);
        Mark(h, a1, MapMarkerKind.Item, P(0f, 0f), 60_000);
        h.Match.Tick();
        MarkerPing ping = Assert.Single(Last(h, 1).Pings);
        Assert.Equal(MapMarkerKind.Item, ping.Kind);
        Assert.Equal(near, ping.TargetId);
        Assert.Equal(4f, ping.Position.X, 2);
        Assert.Equal(2, h.Match.MarkersRefused);
    }

    // ---- limits and lifetimes (D9) ----

    [Fact]
    public void AFourthPing_ReplacesThePlayersOldest()
    {
        var h = Duo(out var a1, out _, out _, out _);
        for (int i = 0; i < 4; i++) Mark(h, a1, MapMarkerKind.Location, P(i, 10f));
        h.Match.Tick();
        var pings = Last(h, 1).Pings;
        Assert.Equal(3, pings.Length);
        Assert.DoesNotContain(pings, p => p.Position.X < 0.5f);   // the first one (x 0) went
        Assert.Equal(1, h.Match.PingsReplaced);
    }

    [Fact]
    public void ANinthTeamPing_ReplacesTheTeamsOldest()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout, WideZone, teamSize: 4, maxPlayers: 8);
        var team = new PlayerEntity[8];
        for (int i = 0; i < 8; i++) team[i] = h.Join(i + 1);
        h.RunToMatch();
        Assert.Equal(team[0].TeamId, team[2].TeamId);
        for (int p = 0; p < 3; p++)
        {
            for (int i = 0; i < 3; i++) Mark(h, team[p], MapMarkerKind.Location, P(p * 10 + i, 0f));   // 9 pings, 3 players
        }
        h.Match.Tick();
        var pings = Last(h, 1).Pings;
        Assert.Equal(8, pings.Length);
        Assert.DoesNotContain(pings, p => p.Position.X < 0.5f);   // the team's oldest (player 0's first) went
        Assert.Equal(1, h.Match.PingsReplaced);
    }

    [Fact]
    public void Pings_Expire_AtTheirLifetime_AndTheTeamIsTold()
    {
        var h = Duo(out var a1, out _, out var b1, out _);
        Mark(h, a1, MapMarkerKind.Enemy, P(0f, 8f), b1.EntityId);
        Mark(h, a1, MapMarkerKind.Location, P(5f, 5f));
        h.Match.Tick();
        Assert.Equal(2, Last(h, 1).Pings.Length);
        h.Ticks((int)h.Match.MapData.EnemyPingTicks - 2);
        Assert.Equal(2, h.Match.ActivePings);
        h.Match.Tick();
        Assert.Equal(1, h.Match.ActivePings);   // the Enemy ping (4 s) went first
        Assert.Equal(MapMarkerKind.Location, Assert.Single(Last(h, 1).Pings).Kind);
        h.Ticks((int)(h.Match.MapData.PingTicks - h.Match.MapData.EnemyPingTicks));
        Assert.Equal(0, h.Match.ActivePings);
        Assert.Empty(Last(h, 2).Pings);
        Assert.Equal(2, h.Match.PingsExpired);
    }

    // ---- waypoints (D5) ----

    [Fact]
    public void AWaypoint_IsSetMovedAndCleared_AndTheTeamSeesIt()
    {
        var h = Duo(out var a1, out _, out _, out _);
        Mark(h, a1, MapMarkerKind.WaypointSet, P(-20f, 30f));
        h.Match.Tick();
        MarkerWaypoint w = Assert.Single(Last(h, 2).Waypoints);
        Assert.Equal(a1.EntityId, w.OwnerId);
        Assert.Equal(-20f, w.Position.X, 2);
        Mark(h, a1, MapMarkerKind.WaypointSet, P(40f, -10f));   // one per player: moved
        h.Match.Tick();
        Assert.Equal(40f, Assert.Single(Last(h, 2).Waypoints).Position.X, 2);
        Mark(h, a1, MapMarkerKind.WaypointClear, Vector3.Zero);
        h.Match.Tick();
        Assert.Empty(Last(h, 2).Waypoints);
        Assert.Empty(h.SentTo(3, PacketId.TeamMarkers));
        Assert.Equal(3, h.Match.WaypointChanges);
    }

    [Fact]
    public void AnEliminatedPlayer_CanClearItsWaypoint_ButNotSetOne()
    {
        var h = Duo(out var a1, out _, out _, out _);
        Mark(h, a1, MapMarkerKind.WaypointSet, P(-20f, 30f));
        h.Match.KillPlayer(a1);
        Mark(h, a1, MapMarkerKind.WaypointSet, P(10f, 10f));
        Assert.Equal(-20f, a1.Waypoint.X, 2);
        Mark(h, a1, MapMarkerKind.WaypointClear, Vector3.Zero);
        Assert.False(a1.HasWaypoint);
    }

    // ---- resends and clean-ups (D10) ----

    [Fact]
    public void AResumedPlayer_GetsItsTeamsMarkers()
    {
        var h = Duo(out var a1, out var a2, out _, out _);
        Mark(h, a1, MapMarkerKind.Location, P(5f, 5f));
        Mark(h, a2, MapMarkerKind.WaypointSet, P(6f, 6f));
        h.Match.Tick();
        Assert.True(h.Match.Disconnect(2, allowGrace: true));
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(22, "p2"));
        var (pings, waypoints) = Last(h, 22);
        Assert.Single(pings);
        Assert.Single(waypoints);   // its own waypoint stayed while it was away
    }

    [Fact]
    public void APlayerWhoLeaves_TakesItsPingsAndWaypointAlong()
    {
        var h = Duo(out var a1, out _, out _, out _);
        Mark(h, a1, MapMarkerKind.Location, P(5f, 5f));
        Mark(h, a1, MapMarkerKind.WaypointSet, P(6f, 6f));
        h.Match.Tick();
        h.Match.Leave(1);
        h.Match.Tick();
        var (pings, waypoints) = Last(h, 2);
        Assert.Empty(pings);
        Assert.Empty(waypoints);
        Assert.Equal(0, h.Match.ActivePings);
    }

    [Fact]
    public void TheMatchEnd_ClearsEverything_AndTellsEachTeam()
    {
        var h = Duo(out var a1, out _, out var b1, out _);
        Mark(h, a1, MapMarkerKind.Location, P(5f, 5f));
        Mark(h, b1, MapMarkerKind.WaypointSet, P(6f, 6f));
        h.Match.Tick();
        h.Packets.Clear();
        Assert.True(h.Match.ForceFinish());
        h.Match.Tick();
        Assert.Equal(0, h.Match.ActivePings);
        Assert.False(b1.HasWaypoint);
        Assert.Empty(Last(h, 1).Pings);
        Assert.Empty(Last(h, 3).Waypoints);
        // Finished: nothing new is taken.
        Mark(h, a1, MapMarkerKind.Location, P(5f, 5f));
        Assert.Equal(0, h.Match.ActivePings);
    }

    [Fact]
    public void TheMatchStart_SendsEachNewTeamAnEmptyList()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout, WideZone, teamSize: 2);
        for (int i = 1; i <= 4; i++) h.Join(i);
        h.RunToMatch();
        for (int peer = 1; peer <= 4; peer++)
        {
            var (pings, waypoints) = Last(h, peer);
            Assert.Empty(pings);
            Assert.Empty(waypoints);
        }
    }

    [Fact]
    public void TheDevSandbox_TakesPings_AndANewTeammateGetsThem()
    {
        var h = new SandboxHarness(options: new ServerOptions { DevRespawn = true, MaxPlayers = 8, TeamSize = 2 });
        PlayerEntity a = h.Join(1, P(0f, 0f));
        h.Match.HandleMarker(1, new MapMarker { Kind = MapMarkerKind.Location, Position = P(5f, 5f) });
        h.Match.Tick();
        Assert.Equal(1, h.Match.ActivePings);
        PlayerEntity b = h.Join(2, P(2f, 0f));   // the dev team has room: same team
        Assert.Equal(a.TeamId, b.TeamId);
        Assert.Contains(h.Packets, s => s.PeerId == 2 && s.Id == PacketId.TeamMarkers);
    }

    // ---- hot path ----

    [Fact]
    public void MarkersAndTheirTicks_AllocateNothing()
    {
        var h = Duo(out var a1, out var a2, out var b1, out _, record: false);
        void Round()
        {
            Mark(h, a1, MapMarkerKind.Location, P(5f, 5f));
            Mark(h, a1, MapMarkerKind.Enemy, P(0f, 8f), b1.EntityId);
            Mark(h, a2, MapMarkerKind.Danger, P(6f, 6f));
            Mark(h, a2, MapMarkerKind.WaypointSet, P(7f, 7f));
            Mark(h, b1, MapMarkerKind.Enemy, P(0f, 0f), a1.EntityId);
            h.Ticks(10);
        }
        for (int i = 0; i < 30; i++) Round();   // warm up, then pings are replaced and expire all the time
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 30; i++) Round();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }
}
