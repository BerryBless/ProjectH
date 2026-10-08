using ProjectH.Monitoring.Contracts;

namespace ProjectH.Monitoring.Storage;

// Monitoring D9: one stored snapshot. ReceivedAt is this server's clock (request §21); Snapshot.ObservedAt is the game
// server's.
public readonly record struct MetricSample(DateTimeOffset ReceivedAt, ServerMonitoringSnapshot Snapshot);

public enum IngestOutcome
{
    Stored,          // a known, online server
    FirstSeen,       // a new ServerId (logged as "first seen")
    Returned,        // a known server that had been offline for OfflineThresholdSeconds (logged as "online again")
    TooManyServers,  // a new ServerId when MaxServers are already known (refused)
}

public enum ServerState
{
    Online,
    Warning,
    Offline,
}

// What the API shows for one server. Latest is the snapshot as posted (opaque to the store and the API, D12).
public sealed record ServerSummary(string ServerId, ServerState Status, DateTimeOffset LastReceivedAt, DateTimeOffset LastObservedAt,
    IReadOnlyList<string> Warnings, ServerMonitoringSnapshot Latest);
