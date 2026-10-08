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

    // 기능: Health 수치를 읽는 관찰형 계측기를 모두 등록한다(Phase 15: 지도 표시 사건과 MapMarker 드롭, Phase 16: Loot 사건,
    //   리뷰 수정 A2–A4·A6: 새 거절 이유, 쿠키 응답, 벌점, Seq 창 드롭 포함).
    // 입력: h - 시작부터의 합계.
    // 출력: 등록이 끝난 ServerMeter(Dispose가 해제한다).
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
            // Server review M2: sent as ServerFull, counted apart (no RejectReason value: that enum is the protocol's).
            new Measurement<long>(h.ConnectRateRejects, Tag("reason", "ConnectRate")),
            // Review fixes A2, A3, A6: sent as ServerFull (the cookie refusal as a fresh cookie), counted apart.
            new Measurement<long>(h.PerIpRejects, Tag("reason", "PerIp")),
            new Measurement<long>(h.PenaltyRejects, Tag("reason", "Penalized")),
            new Measurement<long>(h.AcceptRateRejects, Tag("reason", "AcceptRate")),
            new Measurement<long>(h.CookieRejects, Tag("reason", "Cookie")),
        });
        // Review fix A3: first requests answered with a cookie (every normal connect makes one; not a refusal).
        _meter.CreateObservableCounter("projecth.cookie_challenges", () => h.CookieChallenges, description: "Connection requests without a cookie, answered with one");
        // Review fix A6.
        _meter.CreateObservableCounter("projecth.penalties", () => h.Penalties, description: "Addresses penalized for repeated player failures");
        // Review fix A4.
        _meter.CreateObservableCounter("projecth.input_seq_drops", () => h.InputSeqDrops, description: "Inputs dropped for a Seq too far ahead of the last one taken");
        _meter.CreateObservableCounter("projecth.kicks", () => ByCode(h));
        _meter.CreateObservableCounter("projecth.bad_packets", () => ByReason(h));
        _meter.CreateObservableCounter("projecth.tick_failures", () => h.TickFailures);
        _meter.CreateObservableCounter("projecth.loop_failures", () => h.LoopFailures);
        _meter.CreateObservableCounter("projecth.match_resets", () => h.MatchResets);
        _meter.CreateObservableCounter("projecth.stalls", () => h.Stalls);
        _meter.CreateObservableCounter("projecth.movement_anomalies", () => h.MovementAnomalies,
            description: "Moves faster than their movement mode allows (a simulation bug; should stay 0)");
        _meter.CreateObservableCounter("projecth.network_errors", () => h.NetworkErrors, description: "Socket errors LiteNetLib reported");
        // Server review M7.
        _meter.CreateObservableCounter("projecth.player_failures", () => h.PlayerFailures, description: "Players whose own tick threw; each left the match and was closed with ServerError");
        // Server review M8.
        _meter.CreateObservableCounter("projecth.stall_exits", () => h.StallExits, description: "Stalls that lasted FatalStallSeconds and stopped the server");
        // Server review L9.
        _meter.CreateObservableCounter("projecth.callback_errors", () => h.CallbackErrors, description: "Exceptions caught in LiteNetLib callbacks and the stall watchdog (server bugs; should stay 0)");
        // Phase 13 D18: building and harvesting (since the match object was made).
        _meter.CreateObservableGauge("projecth.build.pieces", () => h.Build.Pieces, description: "Building pieces standing (game.build.count)");
        _meter.CreateObservableGauge("projecth.build.cells", () => h.Build.Cells, description: "Build cells holding a piece (the spatial index)");
        _meter.CreateObservableCounter("projecth.build.requests", () => BuildRequests(h));
        _meter.CreateObservableCounter("projecth.build.destroyed", () => new[]
        {
            new Measurement<long>(h.Build.DamageDestroyed, Tag("cause", "damage")),
            new Measurement<long>(h.Build.Collapsed, Tag("cause", "collapse")),
        });
        _meter.CreateObservableCounter("projecth.build.sync_deferred", () => h.Build.SyncDeferred,
            description: "Ticks a client's building sync waited for its backed-up building channel");
        _meter.CreateObservableCounter("projecth.build.inbox_drops", () => h.BuildInboxDrops, description: "Build requests dropped by the full inbound channel");
        _meter.CreateObservableCounter("projecth.build.edits", () => h.Build.Edits, description: "Edits that changed a building piece (Phase 13.5)");
        _meter.CreateObservableCounter("projecth.harvest.hits", () => h.Build.HarvestHits);
        _meter.CreateObservableCounter("projecth.harvest.destroyed", () => h.Build.EnvironmentDestroyed);
        // Phase 14: squads (since the server started).
        _meter.CreateObservableCounter("projecth.squad.events", () => SquadEvents(h), description: "Knock-downs, revives, reboots, bleed-outs, cards and wipes");
        // Phase 15: map pings and waypoints (since the server started).
        _meter.CreateObservableCounter("projecth.map.events", () => MapEvents(h), description: "Pings, Enemy checks, refusals, replacements, expiries, waypoints and TeamMarkers packets");
        _meter.CreateObservableCounter("projecth.map.marker_drops", () => new[]
        {
            new Measurement<long>(h.MarkerDrops, Tag("where", "rate")),
            new Measurement<long>(h.MarkerInboxDrops, Tag("where", "inbox")),
        }, description: "MapMarker packets dropped by the per-connection rate and by the full inbound channel");
        // Phase 16: loot containers and supply drops (since the server started).
        _meter.CreateObservableCounter("projecth.loot.events", () => LootEvents(h), description: "Containers opened, supply drops spawned, landed and opened, loot items, blocked opens and state packets");
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
        Kick(h, DisconnectCode.Congested),
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
        var result = new Measurement<long>[(int)BuildResultCode.NotFound + 2];
        result[0] = new Measurement<long>(h.Build.Accepted, Tag("result", "Ok"));
        for (int code = 1; code <= (int)BuildResultCode.NotFound; code++)
            result[code] = new Measurement<long>(h.BuildRejects((BuildResultCode)code), Tag("result", ((BuildResultCode)code).ToString()));
        result[^1] = new Measurement<long>(h.Build.Duplicates, Tag("result", "Duplicate"));
        return result;
    }

    // 기능: Phase 14 분대 사건 수를 종류 Tag로 나눠 돌려준다.
    // 입력: h - 합계.
    // 출력: Measurement 배열(관찰자가 읽을 때마다 만든다, Tick 경로 아님).
    private static Measurement<long>[] SquadEvents(HealthCounters h)
    {
        SquadCounts c = h.Squad;
        return new[]
        {
            new Measurement<long>(c.Downs, Tag("event", "down")),
            new Measurement<long>(c.Revives, Tag("event", "revive")),
            new Measurement<long>(c.Reboots, Tag("event", "reboot")),
            new Measurement<long>(c.BleedOuts, Tag("event", "bleed_out")),
            new Measurement<long>(c.CardsDropped, Tag("event", "card_dropped")),
            new Measurement<long>(c.CardsExpired, Tag("event", "card_expired")),
            new Measurement<long>(c.Wipes, Tag("event", "wipe")),
            new Measurement<long>(c.ChannelsCancelled, Tag("event", "channel_cancelled")),
        };
    }

    // 기능: Phase 15 지도 표시 사건 수를 종류 Tag로 나눠 돌려준다.
    // 입력: h - 합계.
    // 출력: Measurement 배열(관찰자가 읽을 때마다 만든다, Tick 경로 아님).
    private static Measurement<long>[] MapEvents(HealthCounters h)
    {
        MapCounts c = h.Map;
        return new[]
        {
            new Measurement<long>(c.Pings, Tag("event", "ping")),
            new Measurement<long>(c.EnemyConfirmed, Tag("event", "enemy_confirmed")),
            new Measurement<long>(c.EnemyDemoted, Tag("event", "enemy_demoted")),
            new Measurement<long>(c.Refused, Tag("event", "refused")),
            new Measurement<long>(c.Replaced, Tag("event", "replaced")),
            new Measurement<long>(c.Expired, Tag("event", "expired")),
            new Measurement<long>(c.Waypoints, Tag("event", "waypoint")),
            new Measurement<long>(c.Packets, Tag("event", "packet")),
        };
    }

    // 기능: Phase 16 Loot 사건 수를 종류 Tag로 나눠 돌려준다.
    // 입력: h - 합계.
    // 출력: Measurement 배열(관찰자가 읽을 때마다 만든다, Tick 경로 아님).
    private static Measurement<long>[] LootEvents(HealthCounters h)
    {
        LootCounts c = h.Loot;
        return new[]
        {
            new Measurement<long>(c.ContainersOpened, Tag("event", "container_opened")),
            new Measurement<long>(c.DropsSpawned, Tag("event", "drop_spawned")),
            new Measurement<long>(c.DropsLanded, Tag("event", "drop_landed")),
            new Measurement<long>(c.DropsOpened, Tag("event", "drop_opened")),
            new Measurement<long>(c.LootItems, Tag("event", "loot_item")),
            new Measurement<long>(c.OpensBlocked, Tag("event", "open_blocked")),
            new Measurement<long>(c.Packets, Tag("event", "packet")),
        };
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
