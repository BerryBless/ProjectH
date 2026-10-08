using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Persistence;
using ProjectH.Server.Tests.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Tests.Persistence;

// Phase 9 D4: what the game loop records when a match finishes (no database involved).
public class MatchRecordTests
{
    private static (RoyaleHarness h, List<MatchRecord> records) Harness()
    {
        var records = new List<MatchRecord>();
        var h = new RoyaleHarness(TestGameData.CombatLoadout, matchSink: records.Add);
        return (h, records);
    }

    [Fact]
    public void AFinishedMatch_IsRecordedOnce_WithPlacementsKillsDamageAndSurvival()
    {
        (RoyaleHarness h, List<MatchRecord> records) = Harness();
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.Ticks(30);   // one second in
        h.ShootUntilDead(a, b);
        h.Ticks(2);
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);

        MatchRecord record = Assert.Single(records);
        Assert.Equal(1, record.Round);
        Assert.Equal("p1", record.WinnerDevPlayerId);
        Assert.True(record.StartedUtc <= record.EndedUtc);
        Assert.Equal(2, record.Players.Count);
        PlayerRecord winner = record.Players.Single(p => p.DevPlayerId == "p1");
        PlayerRecord loser = record.Players.Single(p => p.DevPlayerId == "p2");
        Assert.Equal(1, winner.Placement);
        Assert.Equal(1, winner.Kills);
        Assert.Equal(CombatRules.MaxHealth + TestGameData.LoadoutShield, winner.Damage);   // shield 50 + health 100, no overkill
        Assert.Equal(2, loser.Placement);
        Assert.Equal(0, loser.Kills);
        Assert.Equal(0, loser.Damage);
        Assert.InRange(loser.SurvivalMs, 1000, 10000);
        Assert.True(winner.SurvivalMs >= loser.SurvivalMs);

        h.TickUntil(() => h.Match.Flow.Round == 2, RoyaleHarness.ResultTicks + 5);
        Assert.Single(records);   // the result screen and the next countdown record nothing
    }

    // Review fix C7 (SEC-10, SEC-19): each participant's shots, rays, hits, farthest hit, rewind and its clamps, movement
    // anomalies and largest aim turn go into the record (match_player), counted from the match start.
    [Fact]
    public void AFinishedMatch_RecordsTheAntiCheatCounters()
    {
        (RoyaleHarness h, List<MatchRecord> records) = Harness();
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.ShootUntilDead(a, b);   // the test auto weapon: one ray, 30 damage, so five hits (shield 50 + health 100)
        h.Ticks(2);

        PlayerRecord shooter = Assert.Single(records).Players.Single(p => p.DevPlayerId == "p1");
        Assert.Equal(5, shooter.Shots);
        Assert.Equal(5, shooter.Pellets);
        Assert.Equal(5, shooter.Hits);
        Assert.InRange(shooter.MaxHitDistanceCm, 550, 600);   // 6 m apart, to the front of the hit box (0.35 m nearer)
        Assert.Equal(0, shooter.RewindTicks);                // the harness claims the latest tick
        Assert.Equal(0, shooter.RewindClamped);
        Assert.Equal(0, shooter.MovementAnomalies);
        Assert.Equal(0, shooter.MaxAimTurn);                 // the same aim every shot
        PlayerRecord target = records[0].Players.Single(p => p.DevPlayerId == "p2");
        Assert.Equal(0, target.Shots);
        Assert.Equal(0, target.Hits);
    }

    // The aim turn is the largest |change of AimYaw| between two real inputs, wrapped to 180 degrees, in tenths of a degree.
    [Fact]
    public void TheLargestAimTurn_IsWrappedAndInTenthsOfADegree()
    {
        (RoyaleHarness h, _) = Harness();
        PlayerEntity a = h.Join(1);
        h.Join(2);
        h.Send(a, new InputCommand { AimYaw = 10f });
        h.Match.Tick();
        h.Send(a, new InputCommand { AimYaw = 350f });   // 20 degrees the short way
        h.Match.Tick();
        Assert.Equal(200, a.MaxAimTurnDeg10);
        h.Send(a, new InputCommand { AimYaw = 175.5f });   // 174.5 degrees
        h.Match.Tick();
        Assert.Equal(1745, a.MaxAimTurnDeg10);
        h.Send(a, new InputCommand { AimYaw = float.NaN });   // not a turn
        h.Match.Tick();
        Assert.Equal(1745, a.MaxAimTurnDeg10);
    }

    // Review C round 1: a respawn (every match start is one) resets the movement repeat, not the aim. The first input of the
    // match only sets the aim baseline; the turns after it are measured from there.
    [Fact]
    public void TheFirstInputAfterTheMatchStart_IsNotATurn()
    {
        (RoyaleHarness h, _) = Harness();
        PlayerEntity a = h.Join(1);
        h.Join(2);
        h.Send(a, new InputCommand { AimYaw = 0f });   // a countdown input: the seq is not 0 any more
        h.Match.Tick();
        h.RunToMatch();
        Assert.Equal(0, a.MaxAimTurnDeg10);
        h.Send(a, new InputCommand { AimYaw = 120f });
        h.Match.Tick();
        Assert.Equal(0, a.MaxAimTurnDeg10);
        h.Send(a, new InputCommand { AimYaw = 130f });
        h.Match.Tick();
        Assert.Equal(100, a.MaxAimTurnDeg10);
    }

    // After more than half a second without input (a paused client) the first input only sets the baseline again.
    [Fact]
    public void TheFirstInputAfterAnInputGap_IsNotATurn()
    {
        (RoyaleHarness h, _) = Harness();
        PlayerEntity a = h.Join(1);
        h.Join(2);
        h.Send(a, new InputCommand { AimYaw = 10f });
        h.Match.Tick();
        h.Ticks(20);   // more than SimHz / 2 = 15 ticks without input
        h.Send(a, new InputCommand { AimYaw = 100f });
        h.Match.Tick();
        Assert.Equal(0, a.MaxAimTurnDeg10);
    }

    // A non-finite aim is skipped, not taken as the baseline: A -> NaN -> B measures A -> B.
    [Fact]
    public void ANonFiniteAim_InBetween_StillMeasuresTheTurnAroundIt()
    {
        (RoyaleHarness h, _) = Harness();
        PlayerEntity a = h.Join(1);
        h.Join(2);
        h.Send(a, new InputCommand { AimYaw = 10f });
        h.Match.Tick();
        h.Send(a, new InputCommand { AimYaw = float.NaN });
        h.Match.Tick();
        h.Send(a, new InputCommand { AimYaw = 40f });
        h.Match.Tick();
        Assert.Equal(300, a.MaxAimTurnDeg10);
    }

    [Fact]
    public void AParticipantWhoLeaves_IsStillInTheRecord()
    {
        (RoyaleHarness h, List<MatchRecord> records) = Harness();
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.Join(3);
        h.RunToMatch();
        h.Ticks(15);
        h.Match.Leave(3);
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.ShootUntilDead(a, b);
        h.Ticks(2);

        MatchRecord record = Assert.Single(records);
        Assert.Equal(3, record.Players.Count);
        PlayerRecord left = record.Players.Single(p => p.DevPlayerId == "p3");
        Assert.Equal(3, left.Placement);   // the first one out
        Assert.InRange(left.SurvivalMs, 400, 700);   // 15 ticks at 30 Hz after the start, plus the leave tick
    }

    [Fact]
    public void ASpectatorWhoJoinedMidMatch_IsNotInTheRecord()
    {
        (RoyaleHarness h, List<MatchRecord> records) = Harness();
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        h.Join(3);   // spectates (Phase 5 D10)
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.ShootUntilDead(a, b);
        h.Ticks(2);
        Assert.DoesNotContain(Assert.Single(records).Players, p => p.DevPlayerId == "p3");
    }

    [Fact]
    public void TheDevSandbox_RecordsNothing()
    {
        var records = new List<MatchRecord>();
        var match = new Match(new ServerOptions { MaxPlayers = 4, DevRespawn = true }, TestGameData.Create(), static (_, _, _) => { },
            TestGameData.CombatLoadout, matchSink: records.Add);
        match.TryJoin(1, "p1");
        match.TryJoin(2, "p2");
        for (int i = 0; i < 300; i++) match.Tick();
        Assert.Empty(records);
    }

    // A participant killed and then gone before the end: one entry, with the placement and survival of its death.
    [Fact]
    public void AParticipantWhoDiesAndThenLeaves_IsRecordedOnce_AsOfItsDeath()
    {
        (RoyaleHarness h, List<MatchRecord> records) = Harness();
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        PlayerEntity c = h.Join(3);
        h.RunToMatch();
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(3f, 0f, 0f));
        h.Place(c, new Vector3(0f, 0f, 3f));
        h.Ticks(30);                     // one second in
        h.ShootUntilDead(a, c);          // dies after about 1.4 s
        h.Ticks(60);
        h.Match.Leave(3);                // leaves after about 3.4 s
        h.ShootUntilDead(a, b);
        h.Ticks(2);

        MatchRecord record = Assert.Single(records);
        Assert.Equal(3, record.Players.Count);
        PlayerRecord dead = Assert.Single(record.Players, p => p.DevPlayerId == "p3");
        Assert.Equal(3, dead.Placement);
        Assert.InRange(dead.SurvivalMs, 1000, 2500);   // the death, not the leave
        Assert.Equal(2, record.Players.Single(p => p.DevPlayerId == "p2").Placement);
    }

    // Damage the shield absorbed is damage dealt, even when the target's health is untouched.
    [Fact]
    public void DamageThatOnlyRemovesShield_IsCounted()
    {
        (RoyaleHarness h, List<MatchRecord> records) = Harness();
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.Ticks(30);
        h.ShootOnce(a, b);
        Assert.Equal(CombatRules.MaxHealth, b.Health);
        Assert.Equal(TestGameData.LoadoutShield - 30, b.Shield);   // one Test Auto hit, all on the shield
        h.Match.Leave(2);
        h.Ticks(1);

        MatchRecord record = Assert.Single(records);
        Assert.Equal(30, record.Players.Single(p => p.DevPlayerId == "p1").Damage);
    }

    // D9: the last two leave before the same tick; the one processed last has placement 1. It gets no MatchResult
    // (WinnerId 0), but the record names it the winner.
    [Fact]
    public void AWinnerWhoLeft_IsStillTheRecordedWinner()
    {
        (RoyaleHarness h, List<MatchRecord> records) = Harness();
        h.Join(1);
        h.Join(2);
        h.RunToMatch();
        h.Match.Leave(1);
        h.Match.Leave(2);
        h.Ticks(1);

        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        Assert.Equal(0, h.Match.WinnerId);
        MatchRecord record = Assert.Single(records);
        Assert.Equal("p2", record.WinnerDevPlayerId);
        Assert.Equal(1, record.Players.Single(p => p.DevPlayerId == "p2").Placement);
    }

    // The sink runs only after every participant's MatchResult is sent.
    [Fact]
    public void TheSink_RunsAfterTheResultsAreSent()
    {
        RoyaleHarness? h = null;
        int resultsBeforeSink = -1;
        h = new RoyaleHarness(TestGameData.CombatLoadout, matchSink: _ => resultsBeforeSink = h!.Packets.Count(s => s.Id == PacketId.MatchResult));
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.ShootUntilDead(a, b);
        h.Ticks(2);
        Assert.Equal(2, resultsBeforeSink);
    }

    // A sink that throws must not take the tick down: the results are out, the failure is counted, the rounds go on.
    [Fact]
    public void AThrowingSink_IsCounted_AndTheGameGoesOn()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout, matchSink: _ => throw new InvalidOperationException("database queue broke"));
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.ShootUntilDead(a, b);   // the finishing tick runs inside; it must not throw
        h.Ticks(2);

        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        Assert.Equal(1, h.Match.MatchSinkFailures);
        Assert.Single(h.SentTo(1, PacketId.MatchResult));
        Assert.Single(h.SentTo(2, PacketId.MatchResult));
        h.TickUntil(() => h.Match.Flow.Round == 2, RoyaleHarness.ResultTicks + 5);
    }

    [Fact]
    public void WithoutASink_TheMatchStillFinishes()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);   // no sink: the default for tests and tools
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.ShootUntilDead(a, b);
        h.Ticks(2);
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);   // finished without a sink, nothing thrown
    }
}
