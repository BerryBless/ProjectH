using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 6 D9: the match start spreads the participants over the map's drop points, shuffled by SpawnSeed + round.
public class DropAssignmentTests
{
    // 기능: 맵의 모든 투입 지점을 쓰는 경기를 만들고 플레이어들을 들여보내 경기 시작까지 돌린다.
    // 입력: players - 들여보낼 인원(peer 1..players), maxPlayers - 최대 인원(기본 6).
    // 출력: 경기가 시작된 RoyaleHarness.
    private static RoyaleHarness Start(int players, int maxPlayers = 6)
    {
        var h = new RoyaleHarness(maxPlayers: maxPlayers, dropPoints: DropPoints.All.ToArray());
        for (int peer = 1; peer <= players; peer++) h.Join(peer);
        h.RunToMatch();
        return h;
    }

    // 기능: peer 1..players의 현재 위치를 순서대로 모은다.
    // 입력: h - 경기, players - 인원.
    // 출력: 위치 목록. 없는 peer가 있으면 테스트가 실패한다.
    private static List<Vector3> Positions(RoyaleHarness h, int players)
    {
        var list = new List<Vector3>();
        for (int peer = 1; peer <= players; peer++)
        {
            Assert.True(h.Match.TryGetPlayer(peer, out var p));
            list.Add(p.State.Position);
        }
        return list;
    }

    [Fact]
    public void MatchStart_PutsEveryParticipantOnADifferentDropPoint()
    {
        RoyaleHarness h = Start(6);
        List<Vector3> at = Positions(h, 6);
        foreach (Vector3 p in at) Assert.Contains(p, DropPoints.All.ToArray());
        Assert.Equal(6, at.Distinct().Count());
    }

    [Fact]
    public void TheSameSeedAndRound_GiveTheSameDrops()
    {
        Assert.Equal(Positions(Start(6), 6), Positions(Start(6), 6));
    }

    [Fact]
    public void TheNextRound_ShufflesAgain()
    {
        RoyaleHarness h = Start(6);
        List<Vector3> round1 = Positions(h, 6);
        h.Match.Flow.Eliminate();
        h.Match.Flow.Finish(h.Match.ServerTick);
        h.TickUntil(() => h.Match.Flow.Round == 2 && h.Match.Flow.InMatch, RoyaleHarness.ResultTicks + 2 * RoyaleHarness.CountdownTicks + 5);
        List<Vector3> round2 = Positions(h, 6);
        Assert.NotEqual(round1, round2);
        Assert.Equal(6, round2.Distinct().Count());
    }

    // Review Focus: more participants than drop points. Every spot is clear of the boxes, on the terrain, inside the
    // walls, and at least the lap offset (3 m) from every other spot.
    [Fact]
    public void FiftyParticipants_AllStandClear_OnTheTerrain_AndApart()
    {
        const int players = ProtocolConstants.MaxSnapshotEntities;
        RoyaleHarness h = Start(players, maxPlayers: players);
        List<Vector3> at = Positions(h, players);
        foreach (Vector3 p in at)
        {
            Assert.False(MovementSimulation.OverlapsAny(p, GameMap.Boxes), $"{p} overlaps a box");
            Assert.Equal(GameMap.Terrain.Height(p.X, p.Z), p.Y);
            Assert.True(MathF.Abs(p.X) < GameMap.HalfSize && MathF.Abs(p.Z) < GameMap.HalfSize, $"{p} is outside");
        }
        for (int i = 0; i < at.Count; i++)
        {
            for (int j = i + 1; j < at.Count; j++)
            {
                float dx = at[i].X - at[j].X;
                float dz = at[i].Z - at[j].Z;
                Assert.True(MathF.Sqrt(dx * dx + dz * dz) >= 3f - 1e-4f, $"players {i} and {j}: {at[i]} and {at[j]}");
            }
        }
    }

    // The lobby, the result screen and the next countdown stay on the 5 m ring in the plaza.
    [Fact]
    public void AfterTheRound_EveryoneIsBackOnTheLobbyRing()
    {
        RoyaleHarness h = Start(3);
        h.Match.Flow.Eliminate();
        h.Match.Flow.Eliminate();
        h.Match.Flow.Finish(h.Match.ServerTick);
        h.TickUntil(() => h.Match.Flow.Round == 2, RoyaleHarness.ResultTicks + 5);
        for (int peer = 1; peer <= 3; peer++)
        {
            Assert.True(h.Match.TryGetPlayer(peer, out var p));
            Assert.Equal(Match.SpawnPosition(p.EntityId), p.State.Position);
        }
    }

    [Fact]
    public void TheSecondLap_StandsThreeMetresEastOfItsPoint()
    {
        Vector3[] points = { DropPoints.All[0], DropPoints.All[1] };
        var h = new RoyaleHarness(maxPlayers: 3, dropPoints: points);
        for (int peer = 1; peer <= 3; peer++) h.Join(peer);
        h.RunToMatch();
        List<Vector3> at = Positions(h, 3);
        Assert.Contains(at[0], points);
        Assert.Contains(at[1], points);
        Assert.NotEqual(at[0], at[1]);
        Vector3 third = at[2];
        Assert.Contains(points, p => p.X + 3f == third.X && p.Z == third.Z);
        Assert.Equal(GameMap.Terrain.Height(third.X, third.Z), third.Y);
    }

    [Fact]
    public void AMatchWithoutDropPoints_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new Match(new ServerOptions(), TestGameData.Create(), static (_, _, _) => { },
            dropPoints: Array.Empty<Vector3>()));
    }
}
