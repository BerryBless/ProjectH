using System;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Flow;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Spec §6 MatchFlow (D1, D4, D9): transitions in server ticks, placements, the dev sandbox.
public class MatchFlowTests
{
    private const uint Countdown = 300;   // 10 s at 30 Hz
    private const uint Result = 300;

    // 기능: 10초 카운트다운·10초 결과 화면의 MatchFlow를 만든다.
    // 입력: minPlayers - 시작 최소 인원(기본 2), dev - true면 개발 모드.
    // 출력: WaitingForPlayers 상태의 MatchFlow.
    private static MatchFlow Flow(int minPlayers = 2, bool dev = false) => new(minPlayers, Countdown, Result, dev);

    // 기능: [from, to) 구간의 Tick마다 Update를 돌려 None이 아닌 첫 이벤트를 찾는다.
    // 입력: flow - 흐름, from·to - Tick 구간, players - 접속 인원.
    // 출력: (첫 이벤트, 그 Tick). 구간 안에 이벤트가 없으면 (None, to).
    // Runs Update for every tick in [from, to) and returns the first event that is not None (and its tick).
    private static (FlowEvent Event, uint Tick) RunUntilEvent(MatchFlow flow, uint from, uint to, int players)
    {
        for (uint tick = from; tick < to; tick++)
        {
            FlowEvent e = flow.Update(tick, players);
            if (e != FlowEvent.None) return (e, tick);
        }
        return (FlowEvent.None, to);
    }

    [Fact]
    public void BelowMinPlayers_StaysWaiting()
    {
        MatchFlow flow = Flow();
        Assert.Equal((FlowEvent.None, 1000u), RunUntilEvent(flow, 0, 1000, players: 1));
        Assert.Equal(MatchFlowState.WaitingForPlayers, flow.State);
        Assert.Equal(0u, flow.StateEndTick);
        Assert.False(flow.InMatch);
        Assert.False(flow.DamageAllowed);
        Assert.False(flow.RespawnAllowed);
    }

    [Fact]
    public void TwoPlayers_CountDown10Seconds_ThenPlaying()
    {
        MatchFlow flow = Flow();
        flow.Update(50, 2);
        Assert.Equal(MatchFlowState.Starting, flow.State);
        Assert.Equal(50u + Countdown, flow.StateEndTick);

        Assert.Equal((FlowEvent.MatchStarted, 50u + Countdown), RunUntilEvent(flow, 51, 1000, players: 2));
        Assert.Equal(MatchFlowState.Playing, flow.State);
        Assert.Equal(0u, flow.StateEndTick);
        Assert.Equal(2, flow.Participants);
        Assert.Equal(2, flow.Alive);
        Assert.True(flow.InMatch);
        Assert.True(flow.DamageAllowed);
        Assert.False(flow.RespawnAllowed);   // D4: permanent death
        Assert.Equal(1, flow.Round);
    }

    // Review Focus: a countdown never finishes below MinPlayers.
    [Fact]
    public void PlayerLeavesDuringCountdown_BackToWaiting_AndTheNextCountdownStartsOver()
    {
        MatchFlow flow = Flow();
        flow.Update(0, 2);
        RunUntilEvent(flow, 1, Countdown - 1, players: 2);
        flow.Update(Countdown - 1, 1);   // one tick before the start
        Assert.Equal(MatchFlowState.WaitingForPlayers, flow.State);
        Assert.Equal((FlowEvent.None, 2000u), RunUntilEvent(flow, Countdown, 2000, players: 1));

        flow.Update(2000, 2);
        Assert.Equal(MatchFlowState.Starting, flow.State);
        Assert.Equal(2000u + Countdown, flow.StateEndTick);
    }

    [Fact]
    public void Eliminations_GivePlacementsInReverseOrder_AndOneLeftFinishes()
    {
        MatchFlow flow = Flow();
        flow.Update(0, 4);
        RunUntilEvent(flow, 1, 1000, players: 4);

        Assert.Equal(4, flow.Eliminate());
        Assert.False(flow.ShouldFinish);
        Assert.Equal(3, flow.Eliminate());
        Assert.False(flow.ShouldFinish);
        Assert.Equal(2, flow.Eliminate());
        Assert.Equal(1, flow.Alive);
        Assert.True(flow.ShouldFinish);
        Assert.Equal(4, flow.Participants);
    }

    // D9: the last two die in the same tick: the one processed last gets 1st, and nobody is left.
    [Fact]
    public void LastTwoEliminatedTogether_TheLastOneIsFirst()
    {
        MatchFlow flow = Flow();
        flow.Update(0, 2);
        RunUntilEvent(flow, 1, 1000, players: 2);
        Assert.Equal(2, flow.Eliminate());
        Assert.Equal(1, flow.Eliminate());
        Assert.Equal(0, flow.Alive);
        Assert.True(flow.ShouldFinish);
        Assert.Equal(0, flow.Eliminate());   // nobody left to eliminate
    }

    [Fact]
    public void Finished_Lasts10Seconds_ThenClosing_ThenTheNextRoundCountsDown()
    {
        MatchFlow flow = Flow();
        flow.Update(0, 2);
        RunUntilEvent(flow, 1, 1000, players: 2);
        flow.Eliminate();
        flow.Finish(500);
        Assert.Equal(MatchFlowState.Finished, flow.State);
        Assert.Equal(500u + Result, flow.StateEndTick);
        Assert.False(flow.InMatch);
        Assert.False(flow.DamageAllowed);
        Assert.Equal(1, flow.Alive);   // the result still shows the field

        Assert.Equal((FlowEvent.RoundClosed, 500u + Result), RunUntilEvent(flow, 501, 2000, players: 2));
        Assert.Equal(MatchFlowState.Closing, flow.State);

        flow.Reopen(500 + Result, 2);
        Assert.Equal(MatchFlowState.Starting, flow.State);
        Assert.Equal(500u + Result + Countdown, flow.StateEndTick);
        Assert.Equal(2, flow.Round);
        Assert.Equal(0, flow.Participants);
    }

    [Fact]
    public void Closing_WithTooFewPlayers_GoesBackToWaiting()
    {
        MatchFlow flow = Flow();
        flow.Update(0, 2);
        RunUntilEvent(flow, 1, 1000, players: 2);
        flow.Eliminate();
        flow.Finish(400);
        RunUntilEvent(flow, 401, 2000, players: 1);
        flow.Reopen(700, 1);
        Assert.Equal(MatchFlowState.WaitingForPlayers, flow.State);
        Assert.Equal(2, flow.Round);
    }

    [Fact]
    public void FinalPhase_OnlyFromPlaying_AndStillInMatch()
    {
        MatchFlow flow = Flow();
        flow.EnterFinalPhase();
        Assert.Equal(MatchFlowState.WaitingForPlayers, flow.State);

        flow.Update(0, 3);
        RunUntilEvent(flow, 1, 1000, players: 3);
        flow.EnterFinalPhase();
        Assert.Equal(MatchFlowState.FinalPhase, flow.State);
        Assert.True(flow.InMatch);
        Assert.Equal(3, flow.Eliminate());
        flow.Eliminate();
        Assert.True(flow.ShouldFinish);
        flow.Finish(900);
        Assert.Equal(MatchFlowState.Finished, flow.State);
    }

    [Fact]
    public void FinishAndEliminate_OutsideAMatch_DoNothing()
    {
        MatchFlow flow = Flow();
        flow.Finish(10);
        Assert.Equal(MatchFlowState.WaitingForPlayers, flow.State);
        Assert.Equal(0, flow.Eliminate());
        Assert.False(flow.ShouldFinish);
    }

    // D4: the Phase 3/4 sandbox. No transitions; damage and respawn always on.
    [Fact]
    public void DevRespawn_NeverTransitions_AndAllowsDamageAndRespawn()
    {
        MatchFlow flow = Flow(dev: true);
        Assert.Equal((FlowEvent.None, 5000u), RunUntilEvent(flow, 0, 5000, players: 10));
        Assert.Equal(MatchFlowState.WaitingForPlayers, flow.State);
        Assert.True(flow.DamageAllowed);
        Assert.True(flow.RespawnAllowed);
        Assert.False(flow.InMatch);
        Assert.Equal(0, flow.Eliminate());
    }

    [Fact]
    public void ToWire_BeforeTheMatch_ShowsTheConnectedCount_AndInTheMatchTheField()
    {
        MatchFlow flow = Flow(minPlayers: 3);
        MatchState waiting = flow.ToWire(playerCount: 2);
        Assert.Equal(MatchFlowState.WaitingForPlayers, waiting.State);
        Assert.Equal(2, waiting.Alive);
        Assert.Equal(2, waiting.Participants);
        Assert.Equal(3, waiting.MinPlayers);
        Assert.Equal(1, waiting.Round);

        flow.Update(0, 3);
        Assert.Equal(Countdown, flow.ToWire(3).StateEndTick);
        RunUntilEvent(flow, 1, 1000, players: 3);
        flow.Eliminate();
        MatchState playing = flow.ToWire(playerCount: 4);   // a spectator joined: not a participant
        Assert.Equal(MatchFlowState.Playing, playing.State);
        Assert.Equal(2, playing.Alive);
        Assert.Equal(3, playing.Participants);
    }

    [Fact]
    public void Update_DoesNotAllocate()
    {
        MatchFlow flow = Flow();
        flow.Update(0, 2);
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (uint tick = 1; tick < 2000; tick++)
        {
            if (flow.Update(tick, 2) == FlowEvent.MatchStarted) flow.Eliminate();
            if (flow.ShouldFinish) flow.Finish(tick);
            if (flow.State == MatchFlowState.Closing) flow.Reopen(tick, 2);
            flow.ToWire(2);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
        Assert.True(flow.Round > 1);
    }
}
