using System;
using System.Collections.Generic;

namespace ProjectH.Server.Persistence;

// Phase 9 D4: everything saved about one finished match, built once by the game loop when the match ends and handed
// to the writer. Immutable, so the writer thread can read it while the game loop goes on (§37).
public sealed record MatchRecord(
    int Round,
    DateTime StartedUtc,
    DateTime EndedUtc,
    string? WinnerDevPlayerId,
    IReadOnlyList<PlayerRecord> Players);

// One participant of the match, including players who left mid-match (an elimination, Phase 5 D10).
// Review fix C7 (SEC-10, SEC-19): plus the anti-cheat counters (PlayerEntity, match_player schema v2): shots, rays, rays that
// hit a player, rewound ticks (sum), rewinds cut to the RTT allowance, the farthest player hit (cm), movement anomalies and
// the largest aim turn between two inputs (tenths of a degree). 0 when not given.
public sealed record PlayerRecord(
    string DevPlayerId,
    byte Placement,
    int Kills,
    int Damage,
    int SurvivalMs,
    int Shots = 0,
    int Pellets = 0,
    int Hits = 0,
    int RewindTicks = 0,
    int RewindClamped = 0,
    int MaxHitDistanceCm = 0,
    int MovementAnomalies = 0,
    int MaxAimTurn = 0);
