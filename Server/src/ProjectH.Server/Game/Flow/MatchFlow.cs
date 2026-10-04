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

    // 기능: Match 진행 상태 머신을 만든다.
    // 입력: minPlayers - 카운트다운을 시작할 최소 인원(1 이상), countdownTicks - 시작 카운트다운 Tick 수, resultTicks - 결과 표시 Tick 수, devRespawn - 개발용 부활 Sandbox 모드 여부.
    // 출력: WaitingForPlayers 상태, Round 1로 초기화된 MatchFlow.
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
    // Participants not yet eliminated (dead or left, D10).
    public int Alive { get; private set; }

    public bool InMatch => !DevRespawn && (State == MatchFlowState.Playing || State == MatchFlowState.FinalPhase);
    // D2: shots before (and after) the match hurt nobody.
    public bool DamageAllowed => DevRespawn || InMatch;
    // D4: death is permanent; only the dev sandbox respawns.
    public bool RespawnAllowed => DevRespawn;

    // 기능: 인원과 Tick에 따라 대기·카운트다운·시작·결과 종료 전이를 처리한다. DevRespawn이면 아무것도 하지 않는다.
    // 입력: now - 마지막으로 완료된 Tick, playerCount - 접속 중인 플레이어 수.
    // 출력: 이번 Tick에 Match가 처리할 이벤트(MatchStarted, RoundClosed, 없으면 None). State와 StateEndTick이 갱신될 수 있다.
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
                return FlowEvent.MatchStarted;

            case MatchFlowState.Finished:
                if (now < StateEndTick) return FlowEvent.None;
                Enter(MatchFlowState.Closing, 0);
                return FlowEvent.RoundClosed;

            default:
                return FlowEvent.None;
        }
    }

    // 기능: Closing 상태를 끝내고 다음 Round를 연다.
    // 입력: now - 현재 Tick, playerCount - 접속 중인 플레이어 수.
    // 출력: 반환값 없음. Closing일 때만 Round가 증가하고 참가자 수가 0이 되며, 인원이 충분하면 Starting, 아니면 WaitingForPlayers로 바뀐다.
    // Closing is over (Match reset the round in the same tick): the next round waits or counts down (D13).
    public void Reopen(uint now, int playerCount)
    {
        if (State != MatchFlowState.Closing) return;
        unchecked { Round++; }
        Participants = 0;
        Alive = 0;
        if (playerCount >= MinPlayers) Enter(MatchFlowState.Starting, now + _countdownTicks);
        else Enter(MatchFlowState.WaitingForPlayers, 0);
    }

    // 기능: 안전지대가 마지막 단계에 도달했을 때 FinalPhase로 전환한다.
    // 입력: 없음.
    // 출력: 반환값 없음. Playing 상태일 때만 State가 FinalPhase로 바뀐다.
    // The zone reached its last phase (D7).
    public void EnterFinalPhase()
    {
        if (State == MatchFlowState.Playing) State = MatchFlowState.FinalPhase;
    }

    // 기능: 경기 중 참가자 한 명의 탈락(사망·이탈)을 기록하고 순위를 정한다.
    // 입력: 없음.
    // 출력: 탈락한 참가자의 순위(탈락 직전 생존 인원). 경기 중이 아니거나 생존자가 없으면 0. Alive가 1 감소한다.
    // A participant died or left during the match (D9, D10). Returns its placement: the living participants
    // left after it + 1, so the first of five to go is 5th and, when the last two go in the same tick, the one
    // processed last is 1st.
    public byte Eliminate()
    {
        if (!InMatch || Alive <= 0) return 0;
        byte placement = (byte)Alive;
        Alive--;
        return placement;
    }

    // Step 5 of Match.Tick (D9): one or no participant left.
    public bool ShouldFinish => InMatch && Alive <= 1;

    // 기능: 경기를 끝내고 결과 표시 단계로 전환한다.
    // 입력: now - 현재 Tick.
    // 출력: 반환값 없음. 경기 중일 때만 State가 Finished가 되고 결과 표시 종료 Tick이 정해진다.
    public void Finish(uint now)
    {
        if (!InMatch) return;
        Enter(MatchFlowState.Finished, now + _resultTicks);
    }

    // 기능: 현재 진행 상태를 Client에 보낼 MatchState Packet 값으로 만든다.
    // 입력: playerCount - 접속 중인 플레이어 수.
    // 출력: MatchState. 대기·카운트다운·Closing 중에는 Alive와 Participants에 접속 인원(최대 255)을 넣는다.
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

    // 기능: 상태를 전환하고 그 상태의 종료 Tick을 기록한다.
    // 입력: state - 새 상태, endTick - 상태가 끝나는 Tick(타이머 없는 상태는 0).
    // 출력: 반환값 없음. State와 StateEndTick이 바뀐다.
    private void Enter(MatchFlowState state, uint endTick)
    {
        State = state;
        StateEndTick = endTick;
    }
}
