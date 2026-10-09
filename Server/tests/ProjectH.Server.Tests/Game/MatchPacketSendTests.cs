using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 5 D11: when MatchState, ZoneState and MatchResult are sent, and to whom.
public class MatchPacketSendTests
{
    // 기능: 보낸 패킷을 MatchState로 읽는다. 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: s - 하네스가 기록한 송신 패킷.
    // 출력: 읽은 MatchState.
    private static MatchState ReadState(RoyaleHarness.Sent s) { var r = RoyaleHarness.Reader(s); Assert.True(MatchState.TryRead(ref r, out var v)); return v; }
    // 기능: 보낸 패킷을 ZoneState로 읽는다. 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: s - 하네스가 기록한 송신 패킷.
    // 출력: 읽은 ZoneState.
    private static ZoneState ReadZone(RoyaleHarness.Sent s) { var r = RoyaleHarness.Reader(s); Assert.True(ZoneState.TryRead(ref r, out var v)); return v; }
    // 기능: 보낸 패킷을 MatchResult로 읽는다. 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: s - 하네스가 기록한 송신 패킷.
    // 출력: 읽은 MatchResult.
    private static MatchResult ReadResult(RoyaleHarness.Sent s) { var r = RoyaleHarness.Reader(s); Assert.True(MatchResult.TryRead(ref r, out var v)); return v; }

    [Fact]
    public void Join_SendsMatchAndZoneState_AfterTheSpawns()
    {
        var h = new RoyaleHarness();
        h.Join(1);

        var toPeer = h.Packets.Where(s => s.PeerId == 1).Select(s => s.Id).ToList();
        int spawn = toPeer.LastIndexOf(PacketId.PlayerSpawned);
        Assert.Equal(PacketId.MatchState, toPeer[spawn + 1]);
        Assert.Equal(PacketId.ZoneState, toPeer[spawn + 2]);
        Assert.All(h.Packets.Where(s => s.Id is PacketId.MatchState or PacketId.ZoneState),
            s => Assert.Equal(DeliveryMethod.ReliableOrdered, s.Method));

        MatchState state = ReadState(h.SentTo(1, PacketId.MatchState)[0]);
        Assert.Equal(MatchFlowState.WaitingForPlayers, state.State);
        Assert.Equal(1, state.Participants);
        Assert.Equal(2, state.MinPlayers);
        Assert.Equal(1, state.Round);
        ZoneState zone = ReadZone(h.SentTo(1, PacketId.ZoneState)[0]);
        Assert.Equal(0, zone.Phase);
        Assert.Equal(30f, zone.ToRadius);
    }

    [Fact]
    public void DevRespawn_SendsNoMatchPackets()
    {
        var sent = new System.Collections.Generic.List<PacketId>();
        var match = new Match(new ServerOptions { MaxPlayers = 2, DevRespawn = true }, TestGameData.Create(),
            (_, data, _) => sent.Add((PacketId)data[0]));
        match.TryJoin(1, "a");
        match.TryJoin(2, "b");
        for (int i = 0; i < 400; i++) match.Tick();
        Assert.DoesNotContain(PacketId.MatchState, sent);
        Assert.DoesNotContain(PacketId.ZoneState, sent);
        Assert.DoesNotContain(PacketId.MatchResult, sent);
    }

    // Phase 11: a PlayerSpawned whose name does not fit (unreachable through the connect check, which Match does not
    // repeat) is not sent half-written; it is counted for GameLoop's log. The other spawns still go out.
    [Fact]
    public void ASpawnWhoseNameDoesNotFit_IsNotSent_AndIsCounted()
    {
        var sent = new System.Collections.Generic.List<(int Peer, PacketId Id)>();
        var match = new Match(new ServerOptions { MaxPlayers = 2, DevRespawn = true }, TestGameData.Create(),
            (peer, data, _) => sent.Add((peer, (PacketId)data[0])));
        match.TryJoin(1, "a");
        Assert.Equal(JoinResult.Ok, match.TryJoin(2, new string('x', ProtocolConstants.MaxDevPlayerIdBytes + 1)));

        // Peer 2 gets a's spawn only; peer 1 never gets the long-named spawn.
        Assert.Equal(1, sent.Count(s => s.Peer == 2 && s.Id == PacketId.PlayerSpawned));
        Assert.Equal(1, sent.Count(s => s.Peer == 1 && s.Id == PacketId.PlayerSpawned));
        Assert.Equal(2, match.SpawnEncodeFailures);
    }

    // Only changes are broadcast: an idle wait sends nothing; a join, the countdown, the start and a death do.
    [Fact]
    public void MatchState_IsBroadcastOnlyWhenItChanges()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        h.Ticks(1);
        h.Packets.Clear();
        h.Ticks(100);
        Assert.Empty(h.SentTo(1, PacketId.MatchState));

        PlayerEntity b = h.Join(2);
        h.Ticks(1);
        MatchState starting = ReadState(h.SentTo(1, PacketId.MatchState).Single());
        Assert.Equal(MatchFlowState.Starting, starting.State);
        Assert.Equal(h.Match.Flow.StateEndTick, starting.StateEndTick);
        Assert.Equal(2, starting.Participants);

        h.Packets.Clear();
        h.RunToMatch();
        MatchState playing = ReadState(h.SentTo(1, PacketId.MatchState).Single());
        Assert.Equal(MatchFlowState.Playing, playing.State);
        Assert.Equal(0u, playing.StateEndTick);
        Assert.Equal(2, playing.Alive);
        ZoneState zone = ReadZone(h.SentTo(2, PacketId.ZoneState).Single());
        Assert.Equal(1, zone.Phase);
        Assert.Equal(h.Match.Zone.ShrinkStartTick, zone.ShrinkStartTick);

        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.Packets.Clear();
        h.ShootUntilDead(a, b);
        MatchState finished = ReadState(h.SentTo(2, PacketId.MatchState).Single());
        Assert.Equal(MatchFlowState.Finished, finished.State);
        Assert.Equal(1, finished.Alive);
        Assert.Equal(2, finished.Participants);
        Assert.Equal(h.Match.Flow.StateEndTick, finished.StateEndTick);
    }

    // A ZoneState for every phase change, each matching the server's zone, and phase 0 again after the reset.
    [Fact]
    public void ZoneState_IsBroadcastOnEveryPhaseChange()
    {
        var h = new RoyaleHarness(zonesJson: TestGameData.ShortZonesJson);
        h.Join(1);
        h.Join(2);
        h.Packets.Clear();
        h.RunToMatch();
        h.TickUntil(() => h.Match.Zone.IsFinalPhase, 200);
        h.Ticks(1);

        var zones = h.SentTo(1, PacketId.ZoneState).Select(ReadZone).ToList();
        Assert.Equal(new byte[] { 1, 2 }, zones.Select(z => z.Phase));
        Assert.True(zones[1].SameAs(h.Match.Zone.ToWire()));
        Assert.Equal(TestGameData.ShortPhase2Damage, zones[1].DamagePerSecond);
        // Phase 2 starts where phase 1 ended.
        Assert.Equal(zones[0].ToX, zones[1].FromX);
        Assert.Equal(zones[0].ToRadius, zones[1].FromRadius);
        Assert.Equal(zones[0].ShrinkEndTick + 30u, zones[1].ShrinkStartTick);
    }

    // D9, D10, D11: each participant gets its own result; a spectator and a leaver get none.
    [Fact]
    public void MatchResult_GoesToEachParticipant_WithItsOwnPlacementAndKills()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        PlayerEntity c = h.Join(3);
        h.RunToMatch();
        h.Join(4);   // spectator
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.Match.Leave(3);
        h.ShootUntilDead(a, b);

        MatchResult winner = ReadResult(h.SentTo(1, PacketId.MatchResult).Single());
        Assert.Equal(a.EntityId, winner.WinnerId);
        Assert.Equal(1, winner.Placement);
        Assert.Equal(1, winner.Kills);
        Assert.Equal(3, winner.Participants);

        MatchResult loser = ReadResult(h.SentTo(2, PacketId.MatchResult).Single());
        Assert.Equal(a.EntityId, loser.WinnerId);
        Assert.Equal(2, loser.Placement);
        Assert.Equal(0, loser.Kills);

        Assert.Empty(h.SentTo(3, PacketId.MatchResult));
        Assert.Empty(h.SentTo(4, PacketId.MatchResult));
        Assert.Equal(3, c.Placement);   // the leaver was eliminated first
    }
}
