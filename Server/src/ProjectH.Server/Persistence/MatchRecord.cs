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
public sealed record PlayerRecord(
    string DevPlayerId,
    byte Placement,
    int Kills,
    int Damage,
    int SurvivalMs);
