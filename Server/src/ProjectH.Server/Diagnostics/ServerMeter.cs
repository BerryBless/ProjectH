using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Diagnostics;

// Phase 10 D9: the Health numbers as .NET metrics, for `dotnet-counters monitor -n ProjectH.Server --counters
// ProjectH.Server`. Observable instruments only: nothing is recorded on the game loop; the values are read from
// HealthCounters when a listener polls (allocating a few small arrays then, never on the tick path). No new package
// and no open port: dotnet-counters attaches through the runtime's diagnostics channel.
// Owned by GameServerService; Dispose unregisters every instrument.
public sealed class ServerMeter : IDisposable
{
    public const string Name = "ProjectH.Server";

    private readonly Meter _meter = new(Name);

    public ServerMeter(HealthCounters h)
    {
        _meter.CreateObservableGauge("projecth.peers", () => h.Peers, description: "Open connections");
        _meter.CreateObservableGauge("projecth.players", () => h.Players, description: "Players in the match, graced included");
        _meter.CreateObservableGauge("projecth.graced", () => h.Graced, description: "Players waiting for a reconnect");
        _meter.CreateObservableGauge("projecth.match_state", () => (int)h.MatchState,
            description: "MatchFlowState: 0 WaitingForPlayers, 1 Starting, 2 Playing, 3 FinalPhase, 4 Finished, 5 Closing");
        _meter.CreateObservableCounter("projecth.connections", () => h.Connections);
        _meter.CreateObservableCounter("projecth.joins", () => h.Joins);
        _meter.CreateObservableCounter("projecth.resumes", () => h.Resumes);
        _meter.CreateObservableCounter("projecth.grace_starts", () => h.GraceStarts);
        _meter.CreateObservableCounter("projecth.grace_expiries", () => h.GraceExpiries);
        _meter.CreateObservableCounter("projecth.disconnects", () => new[]
        {
            new Measurement<long>(h.DisconnectTimeouts, Tag("reason", "timeout")),
            new Measurement<long>(h.DisconnectOthers, Tag("reason", "other")),
        });
        _meter.CreateObservableCounter("projecth.rejects", () => new[]
        {
            new Measurement<long>(h.Rejects(RejectReason.ServerFull), Tag("reason", nameof(RejectReason.ServerFull))),
            new Measurement<long>(h.Rejects(RejectReason.BadRequest), Tag("reason", nameof(RejectReason.BadRequest))),
            new Measurement<long>(h.Rejects(RejectReason.VersionMismatch), Tag("reason", nameof(RejectReason.VersionMismatch))),
        });
        _meter.CreateObservableCounter("projecth.kicks", () => ByCode(h));
        _meter.CreateObservableCounter("projecth.bad_packets", () => ByReason(h));
        _meter.CreateObservableCounter("projecth.tick_failures", () => h.TickFailures);
        _meter.CreateObservableCounter("projecth.loop_failures", () => h.LoopFailures);
        _meter.CreateObservableCounter("projecth.match_resets", () => h.MatchResets);
        _meter.CreateObservableCounter("projecth.stalls", () => h.Stalls);
        _meter.CreateObservableCounter("projecth.movement_anomalies", () => h.MovementAnomalies,
            description: "Moves faster than their movement mode allows (a simulation bug; should stay 0)");
        // Phase 13 D18: building and harvesting (since the match object was made).
        _meter.CreateObservableGauge("projecth.build.pieces", () => h.Build.Pieces, description: "Building pieces standing (game.build.count)");
        _meter.CreateObservableGauge("projecth.build.cells", () => h.Build.Cells, description: "Build cells holding a piece (the spatial index)");
        _meter.CreateObservableCounter("projecth.build.requests", () => BuildRequests(h));
        _meter.CreateObservableCounter("projecth.build.destroyed", () => new[]
        {
            new Measurement<long>(h.Build.DamageDestroyed, Tag("cause", "damage")),
            new Measurement<long>(h.Build.Collapsed, Tag("cause", "collapse")),
        });
        _meter.CreateObservableCounter("projecth.build.inbox_drops", () => h.BuildInboxDrops, description: "Build requests dropped by the full inbound channel");
        _meter.CreateObservableCounter("projecth.harvest.hits", () => h.Build.HarvestHits);
        _meter.CreateObservableCounter("projecth.harvest.destroyed", () => h.Build.EnvironmentDestroyed);
        _meter.CreateObservableCounter("projecth.db_records", () => DbRecords(h));
        _meter.CreateObservableCounter("projecth.stats_queries", () => StatsQueries(h));
    }

    public void Dispose() => _meter.Dispose();

    private static KeyValuePair<string, object?> Tag(string key, string value) => new(key, value);

    // Every code the server closes one connection with. None and ServerShutdown are left out: a shutdown closes everyone
    // at once and is not a kick (it is never counted).
    private static Measurement<long>[] ByCode(HealthCounters h) => new[]
    {
        Kick(h, DisconnectCode.Kicked),
        Kick(h, DisconnectCode.JoinTimeout),
        Kick(h, DisconnectCode.InputTimeout),
        Kick(h, DisconnectCode.ServerError),
    };

    private static Measurement<long> Kick(HealthCounters h, DisconnectCode code) => new(h.Kicks(code), Tag("code", code.ToString()));

    private static Measurement<long>[] ByReason(HealthCounters h)
    {
        var result = new Measurement<long>[(int)BadPacketReason.Count];
        for (int reason = 0; reason < result.Length; reason++)
            result[reason] = new Measurement<long>(h.BadPackets((BadPacketReason)reason), Tag("reason", ((BadPacketReason)reason).ToString()));
        return result;
    }

    // Accepted, each refusal reason, and duplicates.
    private static Measurement<long>[] BuildRequests(HealthCounters h)
    {
        var result = new Measurement<long>[(int)BuildResultCode.BudgetFull + 2];
        result[0] = new Measurement<long>(h.Build.Accepted, Tag("result", "Ok"));
        for (int code = 1; code <= (int)BuildResultCode.BudgetFull; code++)
            result[code] = new Measurement<long>(h.BuildRejects((BuildResultCode)code), Tag("result", ((BuildResultCode)code).ToString()));
        result[^1] = new Measurement<long>(h.Build.Duplicates, Tag("result", "Duplicate"));
        return result;
    }

    private static Measurement<long>[] DbRecords(HealthCounters h)
    {
        if (h.Persistence is not { } source) return Array.Empty<Measurement<long>>();
        PersistenceCounts c = source();
        return new[]
        {
            new Measurement<long>(c.Saved, Tag("result", "saved")),
            new Measurement<long>(c.Failed, Tag("result", "failed")),
            new Measurement<long>(c.Discarded, Tag("result", "discarded")),
            new Measurement<long>(c.Dropped, Tag("result", "dropped")),
        };
    }

    // Phase 11 D8: the statistics path.
    private static Measurement<long>[] StatsQueries(HealthCounters h)
    {
        if (h.StatsQueries is not { } source) return Array.Empty<Measurement<long>>();
        StatsQueryCounts c = source();
        return new[]
        {
            new Measurement<long>(c.Requests, Tag("result", "requests")),
            new Measurement<long>(c.Limited, Tag("result", "limited")),
            new Measurement<long>(c.Busy, Tag("result", "busy")),
            new Measurement<long>(c.Unavailable, Tag("result", "unavailable")),
            new Measurement<long>(c.Undelivered, Tag("result", "undelivered")),
        };
    }
}
