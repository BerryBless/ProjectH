using System;
using System.Threading;
using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Diagnostics;

// Phase 10 D5: why a packet counted as invalid. Index into HealthCounters' bad-packet array; keep Count last.
public enum BadPacketReason
{
    UnknownId,        // empty, or a first byte that is no PacketId
    Malformed,        // a known id whose body does not parse
    InputBeforeJoin,
    DuplicateJoin,
    InputRate,        // above ServerOptions.MaxInputPacketsPerSecond
    WrongDirection,   // a server-to-client packet id
    HandlerException, // the receive handler threw (a server bug, counted against the peer)
    Count,
}

// Phase 10 D9: totals since the server started, for the Health line and the "ProjectH.Server" Meter. Written from
// LiteNetLib's threads and the game loop, read by the game loop (Health line) and by the Meter's observers on
// whatever thread polls them. Interlocked/Volatile only, no lock: each value is independent, so a slightly
// inconsistent view across values is acceptable for monitoring.
public sealed class HealthCounters
{
    private const int RejectSlots = (int)RejectReason.BadRequest + 1;
    private const int CodeSlots = (int)DisconnectCode.ServerError + 1;

    private readonly long[] _rejects = new long[RejectSlots];
    private readonly long[] _kicks = new long[CodeSlots];
    private readonly long[] _badPackets = new long[(int)BadPacketReason.Count];
    private long _connections;
    private long _joins;
    private long _resumes;
    private long _graceStarts;
    private long _graceExpiries;
    private long _disconnectTimeouts;
    private long _disconnectOthers;
    private long _tickFailures;
    private long _loopFailures;
    private long _matchResets;
    private long _stalls;
    private long _movementAnomalies;
    // Gauges, written by the game loop once per tick.
    private int _peers;
    private int _players;
    private int _graced;
    private int _matchState;

    // Phase 9 counters of the match history writer (null = no writer, e.g. tests). Set once before the loop starts.
    public Func<PersistenceCounts>? Persistence { get; set; }
    // Phase 11 D8 counters of the statistics path (StatsQueryQueue). Set once by GameLoop's constructor.
    public Func<StatsQueryCounts>? StatsQueries { get; set; }

    public void AddReject(RejectReason reason) => Interlocked.Increment(ref _rejects[(int)reason]);
    public void AddKick(DisconnectCode code) => Interlocked.Increment(ref _kicks[(int)code]);
    public void AddBadPacket(BadPacketReason reason) => Interlocked.Increment(ref _badPackets[(int)reason]);
    public void AddConnection() => Interlocked.Increment(ref _connections);
    public void AddJoin() => Interlocked.Increment(ref _joins);
    public void AddResume() => Interlocked.Increment(ref _resumes);
    public void AddGraceStart() => Interlocked.Increment(ref _graceStarts);
    // A graced player that left without resuming: its grace ran out, it died while away, or the round reset.
    public void AddGraceExpiry() => Interlocked.Increment(ref _graceExpiries);
    public void AddDisconnect(bool timeout) => Interlocked.Increment(ref timeout ? ref _disconnectTimeouts : ref _disconnectOthers);
    public void AddTickFailure() => Interlocked.Increment(ref _tickFailures);
    public void AddLoopFailure() => Interlocked.Increment(ref _loopFailures);
    public void AddMatchReset() => Interlocked.Increment(ref _matchResets);
    public void AddStall() => Interlocked.Increment(ref _stalls);
    // Phase 12 D12: a move faster than its mode allows (Match's self-check; should stay 0).
    public void AddMovementAnomaly() => Interlocked.Increment(ref _movementAnomalies);

    public void SetGauges(int peers, int players, int graced, MatchFlowState state)
    {
        Volatile.Write(ref _peers, peers);
        Volatile.Write(ref _players, players);
        Volatile.Write(ref _graced, graced);
        Volatile.Write(ref _matchState, (int)state);
    }

    public long Rejects(RejectReason reason) => Interlocked.Read(ref _rejects[(int)reason]);
    public long Kicks(DisconnectCode code) => Interlocked.Read(ref _kicks[(int)code]);
    public long BadPackets(BadPacketReason reason) => Interlocked.Read(ref _badPackets[(int)reason]);
    public long Connections => Interlocked.Read(ref _connections);
    public long Joins => Interlocked.Read(ref _joins);
    public long Resumes => Interlocked.Read(ref _resumes);
    public long GraceStarts => Interlocked.Read(ref _graceStarts);
    public long GraceExpiries => Interlocked.Read(ref _graceExpiries);
    public long DisconnectTimeouts => Interlocked.Read(ref _disconnectTimeouts);
    public long DisconnectOthers => Interlocked.Read(ref _disconnectOthers);
    public long TickFailures => Interlocked.Read(ref _tickFailures);
    public long LoopFailures => Interlocked.Read(ref _loopFailures);
    public long MatchResets => Interlocked.Read(ref _matchResets);
    public long Stalls => Interlocked.Read(ref _stalls);
    public long MovementAnomalies => Interlocked.Read(ref _movementAnomalies);
    public int Peers => Volatile.Read(ref _peers);
    public int Players => Volatile.Read(ref _players);
    public int Graced => Volatile.Read(ref _graced);
    public MatchFlowState MatchState => (MatchFlowState)Volatile.Read(ref _matchState);

    public long BadPacketsTotal
    {
        get
        {
            long total = 0;
            for (int i = 0; i < _badPackets.Length; i++) total += Interlocked.Read(ref _badPackets[i]);
            return total;
        }
    }
}
