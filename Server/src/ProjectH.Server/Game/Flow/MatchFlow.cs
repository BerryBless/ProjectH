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
    // Participants not yet eliminated (dead or left, D10).
    public int Alive { get; private set; }

    public bool InMatch => !DevRespawn && (State == MatchFlowState.Playing || State == MatchFlowState.FinalPhase);
    // D2: shots before (and after) the match hurt nobody.
    public bool DamageAllowed => DevRespawn || InMatch;
    // D4: death is permanent; only the dev sandbox respawns.
    public bool RespawnAllowed => DevRespawn;

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

    // The zone reached its last phase (D7).
    public void EnterFinalPhase()
    {
        if (State == MatchFlowState.Playing) State = MatchFlowState.FinalPhase;
    }

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
