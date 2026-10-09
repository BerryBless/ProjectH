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

    // 기능: 재접속 유예가 설정된 CombatLoadout 경기를 만들어 세 명(Peer 1·2·3)을 넣고 경기를 시작한 뒤 5 Tick 돌린다.
    // 입력: grace - 재접속 유예 초, records - 경기 기록을 받을 목록(null이면 기록하지 않음), expired - 유예 만료 플레이어 이름을 받을 목록(null이면 받지 않음).
    // 출력: 진행 중인 하네스와 참가한 세 PlayerEntity(a = Peer 1, b = Peer 2, c = Peer 3).
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

        // A second p1 joins while p1 plays (a spectator copy). p1 drops: a third p1 without the resume proof is a new player
        // as well, and the graced character stays where it is. Review fix B4 (SEC-2): the proof, not the name, decides, so
        // the copy that holds the name cannot keep the owner out: p1 with its proof gets its character back.
        Assert.Equal(JoinResult.Ok, h.Match.JoinWithoutProof(12, "p1"));
        Assert.True(h.Match.Disconnect(1, allowGrace: true));
        Assert.Equal(JoinResult.Ok, h.Match.JoinWithoutProof(13, "p1"));
        Assert.True(a.IsGraced);
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(14, "p1"));
        Assert.Equal(14, a.PeerId);
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
        // A "same" without a proof is a new player: the other copy is not handed out. Review fix B4: with a "same" connected,
        // the other copy's owner still resumes it with its own proof.
        Assert.Equal(JoinResult.Ok, h.Match.JoinWithoutProof(12, "same"));
        Assert.True(first.IsGraced);
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(13, "same"));
        Assert.Equal(13, first.PeerId);
    }

    // Review fix B4 (SEC-2): the same name alone no longer resumes; a graced character comes back only to a proof made with
    // the resume key of the connection it last joined with, bound to the new connection's session key, with a fresh nonce.
    [Fact]
    public void AJoin_WithTheSameName_ButNoProof_BecomesANewPlayer_AndTheGracedCharacterStays()
    {
        var (h, a, _, _) = InMatch();
        h.Match.Disconnect(1, allowGrace: true);
        Assert.Equal(JoinResult.Ok, h.Match.JoinWithoutProof(11, "p1"));
        Assert.True(a.IsGraced);
        Assert.True(h.Match.TryGetPlayer(11, out PlayerEntity newcomer));
        Assert.NotSame(a, newcomer);
        Assert.False(newcomer.Alive);   // a spectator, as any newcomer during the match
    }

    [Fact]
    public void AJoin_WithAValidProof_Resumes_AndTakesTheNewConnectionsKey()
    {
        var (h, a, _, _) = InMatch();
        byte[] oldKey = a.ResumeKey!;
        h.Match.Disconnect(1, allowGrace: true);
        byte[] session = NewSession(1);
        byte[] proof = Proof(oldKey, 1, session, "p1");
        byte[] newKey = SessionAuth.DeriveResumeKey(session);
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(11, "p1", newKey, session, 1, proof, out _));
        Assert.Same(newKey, a.ResumeKey);
        Assert.Equal(0u, a.LastResumeNonce);
    }

    [Fact]
    public void AJoin_WithAProofFromAnotherKey_OrForAnotherSession_BecomesANewPlayer()
    {
        var (h, a, _, _) = InMatch();
        h.Match.Disconnect(1, allowGrace: true);
        byte[] session = NewSession(2);
        byte[] wrongKey = Proof(SessionAuth.DeriveResumeKey(NewSession(3)), 1, session, "p1");
        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(11, "p1", SessionAuth.DeriveResumeKey(session), session, 1, wrongKey, out _));
        // A proof seen on the wire, sent with another session key (another connection), fails too.
        byte[] seen = Proof(a.ResumeKey!, 1, NewSession(4), "p1");
        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(12, "p1", SessionAuth.DeriveResumeKey(session), session, 1, seen, out _));
        Assert.True(a.IsGraced);
    }

    [Fact]
    public void AResumeProof_WithAReusedNonce_IsRefused()
    {
        var (h, a, _, _) = InMatch();
        h.Match.Disconnect(1, allowGrace: true);
        a.LastResumeNonce = 3;   // the client already tried nonces up to 3 with this key
        byte[] session = NewSession(5);
        byte[] key = SessionAuth.DeriveResumeKey(session);
        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(11, "p1", key, session, 3, Proof(a.ResumeKey!, 3, session, "p1"), out _));
        Assert.True(a.IsGraced);
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(12, "p1", key, session, 4, Proof(a.ResumeKey!, 4, session, "p1"), out _));
    }

    // Review fix B4: the owner came back before the server noticed its old connection drop (still connected here). Its proof
    // takes the character over from the old connection, which the match no longer maps; no grace starts.
    [Fact]
    public void AValidProof_TakesOverACharacterStillConnected_AndNamesTheOldConnection()
    {
        var (h, a, _, _) = InMatch();
        ushort entity = a.EntityId;
        byte[] session = NewSession(6);
        int before = h.Match.PlayerCount;
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(11, "p1", SessionAuth.DeriveResumeKey(session), session, 1,
            Proof(a.ResumeKey!, 1, session, "p1"), out int takenOver));
        Assert.Equal(1, takenOver);
        Assert.Equal(11, a.PeerId);
        Assert.Equal(entity, a.EntityId);
        Assert.False(h.Match.TryGetPlayer(1, out _));
        Assert.True(h.Match.TryGetPlayer(11, out PlayerEntity now));
        Assert.Same(a, now);
        Assert.Equal(before, h.Match.PlayerCount);
        Assert.Equal(0, h.Match.GracedCount);
        Assert.False(h.Match.Disconnect(1, allowGrace: true));   // the old connection's close later finds nothing to grace
        Assert.Equal(0, h.Match.GracedCount);
        Assert.Empty(h.SentTo(11, PacketId.PlayerDied));   // alive: nothing tells it otherwise
    }

    // Review B round 1: a dead (spectating) character taken over from a connection the server still holds is told it is
    // dead after its own spawn, as a spectating newcomer is. The client learns its own death only from PlayerDied.
    [Fact]
    public void TakingOverADeadCharacter_SendsPlayerDied_AfterItsSpawn()
    {
        var (h, a, b, _) = InMatch();
        h.Place(a, new Vector3(0f, 0f, 3f));
        h.Place(b, new Vector3(0f, 0f, -3f));
        h.ShootUntilDead(b, a);
        h.Packets.Clear();
        byte[] session = NewSession(30);
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(11, "p1", SessionAuth.DeriveResumeKey(session), session, 1,
            Proof(a.ResumeKey!, 1, session, "p1"), out int takenOver));
        Assert.Equal(1, takenOver);
        int died = h.Packets.FindIndex(p => p.PeerId == 11 && p.Id == PacketId.PlayerDied);
        Assert.True(died >= 0, "PlayerDied to the new connection");
        Assert.Equal(a.EntityId, RoyaleHarness.ReadDied(h.Packets[died]).VictimId);
        Assert.True(died > h.Packets.FindLastIndex(p => p.PeerId == 11 && p.Id == PacketId.PlayerSpawned), "after the spawns");
    }

    // Review B round 1: the server rotates the resume key at the Join, but the client takes the new key only when
    // JoinMatchResponse arrives. Until the new connection's first input is accepted, the key the client proved with keeps
    // resuming (with its own nonce), so a response lost to another drop, even twice, does not strand the character.
    [Fact]
    public void AResponseLostBeforeTheFirstInput_TheProvingKeyStillResumes_EvenTwice()
    {
        var (h, a, _, _) = InMatch();
        byte[] k0 = a.ResumeKey!;
        h.Match.Disconnect(1, allowGrace: true);
        byte[] s1 = NewSession(10);
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(11, "p1", SessionAuth.DeriveResumeKey(s1), s1, 1, Proof(k0, 1, s1, "p1"), out _));

        // Lost: the link drops before the response, so the client still holds k0 (its nonce at 1).
        Assert.True(h.Match.Disconnect(11, allowGrace: true));
        byte[] s2 = NewSession(11);
        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(12, "p1", SessionAuth.DeriveResumeKey(s2), s2, 1, Proof(k0, 1, s2, "p1"), out _));   // nonce 1 taken
        Assert.True(a.IsGraced);
        byte[] s3 = NewSession(12);
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(13, "p1", SessionAuth.DeriveResumeKey(s3), s3, 2, Proof(k0, 2, s3, "p1"), out _));
        Assert.Equal(13, a.PeerId);

        // Lost again: k0 is still the client's key and still resumes.
        Assert.True(h.Match.Disconnect(13, allowGrace: true));
        byte[] s4 = NewSession(13);
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(14, "p1", SessionAuth.DeriveResumeKey(s4), s4, 3, Proof(k0, 3, s4, "p1"), out _));
        Assert.Equal(14, a.PeerId);

        // And before the server notices that drop (still connected): the takeover takes k0 as well.
        byte[] s5 = NewSession(14);
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(15, "p1", SessionAuth.DeriveResumeKey(s5), s5, 4, Proof(k0, 4, s5, "p1"),
            out int takenOver));
        Assert.Equal(14, takenOver);
        Assert.Equal(15, a.PeerId);
    }

    // The first accepted input proves the client had the response (it sends input only once joined): from then on only the
    // new key resumes.
    [Fact]
    public void AfterTheFirstAcceptedInput_TheOldKeyNoLongerResumes_AndTheNewOneDoes()
    {
        var (h, a, _, _) = InMatch();
        byte[] k0 = a.ResumeKey!;
        h.Match.Disconnect(1, allowGrace: true);
        byte[] s1 = NewSession(20);
        byte[] k1 = SessionAuth.DeriveResumeKey(s1);
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(11, "p1", k1, s1, 1, Proof(k0, 1, s1, "p1"), out _));
        Assert.NotNull(a.PrevResumeKey);
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = 1 });
        Assert.True(h.Match.EnqueueInput(11, packet));
        Assert.Null(a.PrevResumeKey);

        Assert.True(h.Match.Disconnect(11, allowGrace: true));
        byte[] s2 = NewSession(21);
        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(12, "p1", SessionAuth.DeriveResumeKey(s2), s2, 2, Proof(k0, 2, s2, "p1"), out _));
        Assert.True(a.IsGraced);
        byte[] s3 = NewSession(22);
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(13, "p1", SessionAuth.DeriveResumeKey(s3), s3, 1, Proof(k1, 1, s3, "p1"), out _));
        Assert.Equal(13, a.PeerId);
    }

    [Fact]
    public void AWrongProof_ForACharacterStillConnected_BecomesANewPlayer()
    {
        var (h, a, _, _) = InMatch();
        byte[] session = NewSession(7);
        byte[] wrong = Proof(SessionAuth.DeriveResumeKey(NewSession(8)), 1, session, "p1");
        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(11, "p1", SessionAuth.DeriveResumeKey(session), session, 1, wrong, out int takenOver));
        Assert.Equal(PlayerEntity.NoPeer, takenOver);
        Assert.Equal(1, a.PeerId);
    }

    // 기능: 시험용 세션 키 32 B를 만든다.
    // 입력: seed - 값 시작.
    // 출력: 32 B.
    private static byte[] NewSession(byte seed)
    {
        var key = new byte[ProtocolLimits.SessionKeyBytes];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(seed * 31 + i);
        return key;
    }

    // 기능: Resume 증명 16 B를 만든다.
    // 입력: resumeKey·nonce·session·name - 증명 입력.
    // 출력: 16 B 증명.
    private static byte[] Proof(byte[] resumeKey, uint nonce, byte[] session, string name)
    {
        var proof = new byte[ProtocolLimits.ResumeProofBytes];
        SessionAuth.ComputeResumeProof(resumeKey, nonce, session, name, proof);
        return proof;
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
