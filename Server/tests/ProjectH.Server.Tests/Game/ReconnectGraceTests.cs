using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 10 D2 through Match: a participant who drops during the match keeps its character for the grace and gets it
// back by joining again with the same DevPlayerId. Grace 10 s = 300 ticks at 30 Hz.
public class ReconnectGraceTests
{
    private const int GraceTicks = 300;

    private static (RoyaleHarness h, PlayerEntity a, PlayerEntity b, PlayerEntity c) InMatch(int grace = 10,
        List<MatchRecord>? records = null, List<string>? expired = null)
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout, reconnectGraceSeconds: grace, matchSink: records == null ? null : records.Add,
            graceExpired: expired == null ? null : expired.Add);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        PlayerEntity c = h.Join(3);
        h.RunToMatch();
        h.Ticks(5);
        return (h, a, b, c);
    }

    [Fact]
    public void ADroppedParticipant_StaysInTheWorld_WithoutADespawn()
    {
        var (h, a, b, _) = InMatch();
        h.Packets.Clear();

        Assert.True(h.Match.Disconnect(1, allowGrace: true));
        Assert.True(a.IsGraced);
        Assert.Equal(1, h.Match.GracedCount);
        Assert.Equal(3, h.Match.PlayerCount);
        Assert.False(h.Match.TryGetPlayer(1, out _));   // no connection maps to it any more

        h.Ticks(GraceTicks - 10);
        Assert.DoesNotContain(h.Packets, p => p.Id == PacketId.PlayerDespawned);
        Assert.DoesNotContain(h.Packets, p => p.PeerId == PlayerEntity.NoPeer || p.PeerId == 1);   // nothing to the dead connection
        Assert.True(a.Alive);
        Assert.Equal(3, h.Match.Flow.Alive);
        // Still drawn for the others.
        Assert.NotEmpty(h.SentTo(b.PeerId, PacketId.WorldSnapshot));
    }

    [Fact]
    public void JoiningAgain_ResumesTheSameCharacter_WithTheFullState()
    {
        var expired = new List<string>();
        var (h, a, b, _) = InMatch(expired: expired);
        h.Place(a, new Vector3(3f, 0f, 4f));
        a.Health = 60;
        ushort entity = a.EntityId;
        int weapons = a.Inventory.Slots.Count(s => !s.IsEmpty);
        h.Match.Disconnect(1, allowGrace: true);
        h.Ticks(30);
        h.Packets.Clear();

        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(11, "p1"));
        Assert.True(h.Match.TryGetPlayer(11, out PlayerEntity resumed));
        Assert.Same(a, resumed);
        Assert.Equal(11, a.PeerId);
        Assert.False(a.IsGraced);
        Assert.Equal(0, h.Match.GracedCount);
        Assert.Equal(60, a.Health);
        Assert.Equal(weapons, a.Inventory.Slots.Count(s => !s.IsEmpty));
        Assert.True(Vector3.Distance(new Vector3(3f, 0f, 4f), a.State.Position) < 0.5f);

        // What a late joiner gets, in the join order, to the new connection only.
        List<PacketId> toNew = h.Packets.Where(p => p.PeerId == 11).Select(p => p.Id).ToList();
        Assert.Equal(PacketId.JoinMatchResponse, toNew[0]);
        var r = RoyaleHarness.Reader(h.Packets.First(p => p.PeerId == 11));
        Assert.True(JoinMatchResponse.TryRead(ref r, out JoinMatchResponse response));
        Assert.Equal(JoinResult.Resumed, response.Result);
        Assert.Equal(entity, response.MyEntityId);
        Assert.Contains(PacketId.WeaponCatalog, toNew);
        Assert.Contains(PacketId.ItemCatalog, toNew);
        Assert.Contains(PacketId.InventoryState, toNew);
        Assert.Equal(3, toNew.Count(id => id == PacketId.PlayerSpawned));
        Assert.Contains(PacketId.MatchState, toNew);
        Assert.Contains(PacketId.ZoneState, toNew);
        // The others never saw it leave: no spawn and no despawn for them.
        Assert.DoesNotContain(h.Packets, p => p.PeerId != 11 && (p.Id == PacketId.PlayerSpawned || p.Id == PacketId.PlayerDespawned));
        // The match is still on: no result, and a resume is no expiry.
        Assert.DoesNotContain(PacketId.MatchResult, toNew);
        Assert.Empty(expired);

        // The new connection numbers its inputs from 1: they are taken, they move the player and the ack follows.
        Vector3 before = a.State.Position;
        for (int i = 0; i < 10; i++)
        {
            h.Send(a, new InputCommand { MoveY = 1f });
            h.Match.Tick();
        }
        Assert.Equal(10u, a.LastProcessedSeq);
        Assert.True(Vector3.Distance(before, a.State.Position) > 0.5f);
        _ = b;
    }

    [Fact]
    public void WhenTheGraceRunsOut_ThePlayerIsEliminated_DropsItsLoot_AndIsRecordedAsLeft()
    {
        var records = new List<MatchRecord>();
        var expired = new List<string>();
        var (h, a, b, c) = InMatch(records: records, expired: expired);
        h.Match.Disconnect(1, allowGrace: true);
        h.Packets.Clear();

        h.Ticks(GraceTicks - 1);
        Assert.True(a.Alive);
        Assert.Empty(expired);
        Assert.DoesNotContain(h.Packets, p => p.Id == PacketId.PlayerDespawned);
        h.Ticks(2);

        Assert.Equal(new[] { "p1" }, expired);
        Assert.False(a.Alive);
        Assert.Equal(3, a.Placement);   // the first one out of three
        Assert.Equal(0, h.Match.GracedCount);
        Assert.Equal(2, h.Match.PlayerCount);
        Assert.NotEmpty(h.SentTo(b.PeerId, PacketId.PlayerDespawned));
        Assert.NotEmpty(h.SentTo(b.PeerId, PacketId.ItemSpawned));   // its weapons and ammo on the ground

        h.Place(b, new Vector3(0f, 0f, -3f));
        h.Place(c, new Vector3(0f, 0f, 3f));
        h.ShootUntilDead(b, c);
        h.Ticks(2);
        MatchRecord record = Assert.Single(records);
        PlayerRecord left = record.Players.Single(p => p.DevPlayerId == "p1");
        Assert.Equal(3, left.Placement);
    }

    [Fact]
    public void KilledWhileAway_ComesBackAsASpectator()
    {
        var expired = new List<string>();
        var (h, a, b, _) = InMatch(expired: expired);
        h.Place(a, new Vector3(0f, 0f, 3f));
        h.Place(b, new Vector3(0f, 0f, -3f));
        h.Match.Disconnect(1, allowGrace: true);

        h.ShootUntilDead(b, a);   // a graced character can be shot
        h.Ticks(1);
        Assert.Equal(new[] { "p1" }, expired);   // died while away counts as an expiry
        Assert.Equal(0, h.Match.GracedCount);
        Assert.Equal(3, a.Placement);
        Assert.NotEmpty(h.SentTo(b.PeerId, PacketId.PlayerDespawned));

        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(11, "p1"));
        Assert.True(h.Match.TryGetPlayer(11, out PlayerEntity again));
        Assert.NotSame(a, again);
        Assert.False(again.Alive);   // spectates (Phase 5 D10)
        Assert.False(again.Participant);
    }

    [Theory]
    [InlineData(false)]   // the server closed the connection (Kicked, InputTimeout)
    [InlineData(true)]
    public void OutsideTheGraceRules_TheDisconnectIsALeave(bool allowGrace)
    {
        var (h, a, b, _) = InMatch();
        if (allowGrace)
        {
            // A dead participant has nothing to come back to.
            h.Place(a, new Vector3(0f, 0f, 3f));
            h.Place(b, new Vector3(0f, 0f, -3f));
            h.ShootUntilDead(b, a);
        }
        h.Packets.Clear();
        Assert.False(h.Match.Disconnect(1, allowGrace));
        Assert.Equal(0, h.Match.GracedCount);
        Assert.Equal(2, h.Match.PlayerCount);
        Assert.NotEmpty(h.SentTo(b.PeerId, PacketId.PlayerDespawned));
    }

    [Fact]
    public void BeforeTheMatch_ADisconnectIsALeave()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        h.Join(1);
        h.Join(2);
        h.Ticks(3);   // counting down
        Assert.Equal(MatchFlowState.Starting, h.Match.Flow.State);
        Assert.False(h.Match.Disconnect(1, allowGrace: true));
        Assert.Equal(1, h.Match.PlayerCount);
    }

    [Fact]
    public void AConnectedPlayerWithTheSameId_IsNotTakenOver()
    {
        var (h, a, _, _) = InMatch();
        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(11, "p2"));   // p2 is connected: a new spectator, not p2's character
        Assert.True(h.Match.TryGetPlayer(11, out PlayerEntity newcomer));
        Assert.False(newcomer.Alive);

        // A second p1 joins while p1 plays (a spectator copy). p1 drops: with that copy connected, a third p1 is a new
        // player as well, and the graced character stays where it is.
        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(12, "p1"));
        Assert.True(h.Match.Disconnect(1, allowGrace: true));
        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(13, "p1"));
        Assert.True(a.IsGraced);
    }

    [Fact]
    public void TwoGracedCopies_TheOldestIsResumedFirst()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(1, "same"));
        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(2, "same"));
        h.Join(3);
        h.RunToMatch();
        h.Match.TryGetPlayer(1, out PlayerEntity first);
        h.Match.TryGetPlayer(2, out PlayerEntity second);
        h.Match.Disconnect(2, allowGrace: true);
        h.Match.Disconnect(1, allowGrace: true);

        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(11, "same"));
        Assert.Equal(11, second.PeerId);   // dropped first
        Assert.True(first.IsGraced);
        // Now a "same" is connected, so the other copy is not handed out.
        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(12, "same"));
        Assert.True(first.IsGraced);
    }

    // D2: a match that ends while a participant is away ranks it with the others still in; the round reset then
    // removes it (the grace list is empty for the next round).
    [Fact]
    public void AMatchThatEndsDuringTheGrace_RanksTheGracedPlayer_AndTheRoundResetDropsIt()
    {
        var records = new List<MatchRecord>();
        var expired = new List<string>();
        var (h, _, _, c) = InMatch(records: records, expired: expired);
        Assert.True(h.Match.Disconnect(3, allowGrace: true));
        h.Match.Leave(1);
        h.Match.Leave(2);
        h.Ticks(1);

        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        Assert.Equal(1, c.Placement);   // the last one in, though away
        Assert.Equal(c.EntityId, h.Match.WinnerId);
        Assert.Equal("p3", Assert.Single(records).WinnerDevPlayerId);
        Assert.Equal(1, h.Match.GracedCount);

        h.TickUntil(() => h.Match.Flow.Round == 2, RoyaleHarness.ResultTicks + 5);
        Assert.Equal(new[] { "p3" }, expired);   // the round reset ended its grace
        Assert.Equal(0, h.Match.GracedCount);
        Assert.Equal(0, h.Match.PlayerCount);
        Assert.Equal(MatchFlowState.WaitingForPlayers, h.Match.Flow.State);
    }

    // B3: the result FinishMatch sent to nobody (NoPeer) reaches the player when it resumes before the round reset.
    [Fact]
    public void ResumingAfterTheMatchEnded_SendsTheResultAgain()
    {
        var (h, _, _, c) = InMatch();
        Assert.True(h.Match.Disconnect(3, allowGrace: true));
        h.Match.Leave(1);
        h.Match.Leave(2);
        h.Ticks(1);
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        h.Packets.Clear();

        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(13, "p3"));
        RoyaleHarness.Sent sent = Assert.Single(h.SentTo(13, PacketId.MatchResult));
        var r = RoyaleHarness.Reader(sent);
        Assert.True(MatchResult.TryRead(ref r, out MatchResult result));
        Assert.Equal(c.EntityId, result.WinnerId);
        Assert.Equal(1, result.Placement);
        Assert.Equal(3, result.Participants);
        // After the full state, so the client already knows the match is Finished.
        List<PacketId> toNew = h.Packets.Where(p => p.PeerId == 13).Select(p => p.Id).ToList();
        Assert.True(toNew.IndexOf(PacketId.MatchState) < toNew.IndexOf(PacketId.MatchResult));
    }

    // B17: a participant killed while away is in the record exactly once, whether the match goes on after the kill
    // (it leaves through ExpireGrace while InMatch: a left participant) or the kill ends it (it is still in the player
    // list when the record is built, and its later removal is outside the match).
    [Fact]
    public void KilledWhileAway_MidMatch_IsRecordedOnce()
    {
        var records = new List<MatchRecord>();
        var (h, a, b, c) = InMatch(records: records);
        h.Place(a, new Vector3(0f, 0f, 3f));
        h.Place(b, new Vector3(0f, 0f, -3f));
        h.Place(c, new Vector3(6f, 0f, 0f));
        h.Match.Disconnect(1, allowGrace: true);
        h.ShootUntilDead(b, a);
        h.Ticks(1);
        Assert.True(h.Match.Flow.InMatch);
        Assert.Equal(2, h.Match.PlayerCount);

        h.Place(c, new Vector3(0f, 0f, 3f));
        h.ShootUntilDead(b, c);
        h.Ticks(2);
        MatchRecord record = Assert.Single(records);
        Assert.Equal(3, record.Players.Count);
        Assert.Equal(3, Assert.Single(record.Players, p => p.DevPlayerId == "p1").Placement);
    }

    [Fact]
    public void KilledWhileAway_EndingTheMatch_IsRecordedOnce()
    {
        var records = new List<MatchRecord>();
        var (h, a, b, _) = InMatch(records: records);
        h.Match.Leave(3);   // third out
        h.Place(a, new Vector3(0f, 0f, 3f));
        h.Place(b, new Vector3(0f, 0f, -3f));
        h.Match.Disconnect(1, allowGrace: true);
        h.ShootUntilDead(b, a);   // the last two: this kill ends the match
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        h.Ticks(2);   // the dead graced player leaves now, after the match
        Assert.Equal(0, h.Match.GracedCount);

        MatchRecord record = Assert.Single(records);
        Assert.Equal(3, record.Players.Count);
        Assert.Equal(2, Assert.Single(record.Players, p => p.DevPlayerId == "p1").Placement);
        Assert.Equal("p2", record.WinnerDevPlayerId);
    }

    [Fact]
    public void GraceZero_IsTheOldLeave()
    {
        var (h, _, b, _) = InMatch(grace: 0);
        Assert.False(h.Match.Disconnect(1, allowGrace: true));
        Assert.Equal(2, h.Match.PlayerCount);
        Assert.NotEmpty(h.SentTo(b.PeerId, PacketId.PlayerDespawned));
        Assert.Equal(2, h.Match.Flow.Alive);
    }
}
