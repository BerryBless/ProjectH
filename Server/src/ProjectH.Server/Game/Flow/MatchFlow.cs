using System;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Flow;

// What Match must do after MatchFlow.Update (step 1 of Match.Tick).
public enum FlowEvent : byte
{
    None,
    MatchStarted,   // Starting -> Playing this tick: Match runs the start reset (D3)
    RoundClosed,    // Finished -> Closing this tick: Match resets the round (D13), then calls Reopen
}

// The match state machine (request §29, D1): WaitingForPlayers -> Starting -> Playing -> FinalPhase -> Finished
// -> Closing -> WaitingForPlayers or Starting. Every transition is decided in server ticks, so tests control time
// exactly. Owned by Match on the game loop thread; plain fields, no allocation.
//
// It also decides what Match.Tick allows (D2, D4): damage only during the match, respawn never. With
// DevRespawn (ServerOptions) the flow stays out of the way: no transitions, damage and respawn always on — the
// Phase 3/4 test sandbox.
public sealed class MatchFlow
{
    private readonly uint _countdownTicks;
    private readonly uint _resultTicks;

    public MatchFlow(int minPlayers, uint countdownTicks, uint resultTicks, bool devRespawn)
    {
        if (minPlayers < 1) throw new ArgumentOutOfRangeException(nameof(minPlayers));
        MinPlayers = minPlayers;
        _countdownTicks = countdownTicks;
        _resultTicks = resultTicks;
        DevRespawn = devRespawn;
    }

    public int MinPlayers { get; }
    public bool DevRespawn { get; }
    public MatchFlowState State { get; private set; } = MatchFlowState.WaitingForPlayers;
    // The tick the current state ends at (Starting, Finished); 0 when the state has no timer.
    public uint StateEndTick { get; private set; }
    // 1 for the first match; Closing moves to the next (D13). Loot and zone seeds add it (D3).
    public ushort Round { get; private set; } = 1;
    // Fixed when the match starts (D3): everyone connected at that tick.
    public int Participants { get; private set; }
    // Participants not yet eliminated (dead or left, D10). Phase 14 D6: players, so a knocked-down player counts.
    public int Alive { get; private set; }
    // Phase 14 D6: the teams of the match (MatchResult.Participants) and the ones not yet wiped out. Until Match sets the
    // teams at the start (SetTeams) every participant is its own team (Solo), so the flow alone behaves as before.
    public int Teams { get; private set; }
    public int TeamsAlive { get; private set; }

    public bool InMatch => !DevRespawn && (State == MatchFlowState.Playing || State == MatchFlowState.FinalPhase);
    // D2: shots before (and after) the match hurt nobody.
    public bool DamageAllowed => DevRespawn || InMatch;
    // D4: death is permanent; only the dev sandbox respawns.
    public bool RespawnAllowed => DevRespawn;

    // 기능: Match.Tick 1단계의 상태 전환. 시작 Tick에 참가자·생존 수를 정하고 팀 수는 Solo 값(참가자 수)으로 둔다(Phase 14: Match가 SetTeams로 바꾼다).
    // 입력: now - 마지막 Tick, playerCount - 연결된 플레이어 수.
    // 출력: Match가 할 일(None, MatchStarted, RoundClosed).
    // Step 1 of Match.Tick (now = the last completed tick). playerCount = connected players.
    public FlowEvent Update(uint now, int playerCount)
    {
        if (DevRespawn) return FlowEvent.None;
        switch (State)
        {
            case MatchFlowState.WaitingForPlayers:
                if (playerCount >= MinPlayers) Enter(MatchFlowState.Starting, now + _countdownTicks);
                return FlowEvent.None;

            case MatchFlowState.Starting:
                // Someone left during the countdown: wait again; the next countdown starts from the beginning.
                if (playerCount < MinPlayers)
                {
                    Enter(MatchFlowState.WaitingForPlayers, 0);
                    return FlowEvent.None;
                }
                if (now < StateEndTick) return FlowEvent.None;
                Enter(MatchFlowState.Playing, 0);
                Participants = playerCount;
                Alive = playerCount;
                Teams = playerCount;
                TeamsAlive = playerCount;
                return FlowEvent.MatchStarted;

            case MatchFlowState.Finished:
                if (now < StateEndTick) return FlowEvent.None;
                Enter(MatchFlowState.Closing, 0);
                return FlowEvent.RoundClosed;

            default:
                return FlowEvent.None;
        }
    }

    // 기능: Closing이 끝난 다음 라운드를 연다(라운드 +1, 참가자·생존·팀 수 0).
    // 입력: now - 마지막 Tick, playerCount - 연결된 플레이어 수.
    // 출력: 반환값 없음.
    // Closing is over (Match reset the round in the same tick): the next round waits or counts down (D13).
    public void Reopen(uint now, int playerCount)
    {
        if (State != MatchFlowState.Closing) return;
        unchecked { Round++; }
        Participants = 0;
        Alive = 0;
        Teams = 0;
        TeamsAlive = 0;
        if (playerCount >= MinPlayers) Enter(MatchFlowState.Starting, now + _countdownTicks);
        else Enter(MatchFlowState.WaitingForPlayers, 0);
    }

    // QA-1 (forceMatchState start): the countdown ends at `now` (the next Update starts the match). From
    // WaitingForPlayers the countdown starts already over when enough players are here. False otherwise.
    internal bool SkipCountdown(uint now, int playerCount)
    {
        if (DevRespawn || playerCount < MinPlayers) return false;
        if (State == MatchFlowState.WaitingForPlayers || State == MatchFlowState.Starting)
        {
            Enter(MatchFlowState.Starting, now);
            return true;
        }
        return false;
    }

    // The zone reached its last phase (D7).
    public void EnterFinalPhase()
    {
        if (State == MatchFlowState.Playing) State = MatchFlowState.FinalPhase;
    }

    // 기능: Solo 탈락(D9, D10): 참가자 한 명과 그 팀 하나가 함께 빠진다. 배치는 남은 팀 수(= Solo의 남은 사람 수)이므로 다섯 중
    //   처음 빠진 사람은 5등, 마지막 둘이 같은 Tick에 빠지면 나중에 처리된 쪽이 1등이다.
    // 입력: 없음.
    // 출력: 그 배치(경기 밖이면 0).
    public byte Eliminate()
    {
        if (!InMatch || Alive <= 0) return 0;
        Alive--;
        return EliminateTeam();
    }

    // 기능: Phase 14 D6: 참가자 한 명이 빠진다(사람 수만 줄고 팀 배치는 EliminateTeam이 정한다).
    // 입력: 없음.
    // 출력: 반환값 없음. Alive가 1 줄어든다(경기 밖이거나 0이면 그대로).
    public void EliminatePlayer()
    {
        if (InMatch && Alive > 0) Alive--;
    }

    // 기능: Phase 14 D6: 한 팀이 전멸했다. 배치 = 그 순간 남은 팀 수.
    // 입력: 없음.
    // 출력: 그 팀의 배치(경기 밖이거나 남은 팀이 없으면 0).
    public byte EliminateTeam()
    {
        if (!InMatch || TeamsAlive <= 0) return 0;
        byte placement = (byte)TeamsAlive;
        TeamsAlive--;
        return placement;
    }

    // 기능: Phase 14 D10: 재투입된 참가자 한 명이 다시 살아 있는 사람으로 센다(팀은 전멸하지 않은 상태였다).
    // 입력: 없음.
    // 출력: 반환값 없음. Alive가 1 늘어난다(참가자 수를 넘지 않는다).
    public void RestorePlayer()
    {
        if (InMatch && Alive < Participants) Alive++;
    }

    // 기능: Phase 14 D1: 경기 시작 Tick에 Match가 묶은 팀 수를 정한다(Update가 둔 Solo 값 대신).
    // 입력: teams - 팀 수(1..참가자 수).
    // 출력: 반환값 없음. Teams와 TeamsAlive가 바뀐다.
    public void SetTeams(int teams)
    {
        if (!InMatch) return;
        Teams = Math.Clamp(teams, 0, Participants);
        TeamsAlive = Teams;
    }

    // Step 5 of Match.Tick (D9): one or no participant left. Phase 14 D6: one or no team left.
    public bool ShouldFinish => InMatch && TeamsAlive <= 1;

    public void Finish(uint now)
    {
        if (!InMatch) return;
        Enter(MatchFlowState.Finished, now + _resultTicks);
    }

    // The MatchState packet (D11). Before the match Alive and Participants are the connected count.
    public MatchState ToWire(int playerCount)
    {
        bool counting = State == MatchFlowState.WaitingForPlayers || State == MatchFlowState.Starting || State == MatchFlowState.Closing;
        byte connected = (byte)Math.Min(playerCount, byte.MaxValue);
        return new MatchState
        {
            State = State,
            StateEndTick = StateEndTick,
            Alive = counting ? connected : (byte)Alive,
            Participants = counting ? connected : (byte)Participants,
            Round = Round,
            MinPlayers = (byte)Math.Min(MinPlayers, byte.MaxValue),
        };
    }

    private void Enter(MatchFlowState state, uint endTick)
    {
        State = state;
        StateEndTick = endTick;
    }
}
