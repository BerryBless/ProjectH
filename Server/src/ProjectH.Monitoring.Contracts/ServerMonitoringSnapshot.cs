namespace ProjectH.Monitoring.Contracts;

// Monitoring D2: one game server's state at ObservedAt. Rates (PerSecond) and tick percentiles are over the last
// WindowSeconds; GcGen*, Exceptions, InvalidPackets and Disconnects are totals since the server started. Every field is a
// value the server already has; nothing is estimated. Adding a metric = a property here + one line in
// MonitoringCollector.Publish + (if it needs a range check) one line in SnapshotValidator + the UI.
public sealed record ServerMonitoringSnapshot
{
    // Server
    public required string ServerId { get; init; }
    public string Version { get; init; } = "";
    public int ProtocolVersion { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public double UptimeSeconds { get; init; }
    public double WindowSeconds { get; init; }

    // Gameplay (one match per server)
    public int ConnectedPeers { get; init; }
    public int Players { get; init; }
    public int Graced { get; init; }
    public string MatchState { get; init; } = "";
    public int Round { get; init; }

    // Tick (this window)
    public int TickSamples { get; init; }
    public double TickP50Ms { get; init; }
    public double TickP95Ms { get; init; }
    public double TickP99Ms { get; init; }
    public double TickMaxMs { get; init; }

    // Process. CpuPercent: this process, all logical cores = 100 %.
    public double CpuPercent { get; init; }
    public long ManagedMemoryBytes { get; init; }
    public long WorkingSetBytes { get; init; }
    public int GcGen0 { get; init; }
    public int GcGen1 { get; init; }
    public int GcGen2 { get; init; }

    // Network (this window, application-level LiteNetLib payloads)
    public double PacketsInPerSecond { get; init; }
    public double PacketsOutPerSecond { get; init; }
    public double BytesInPerSecond { get; init; }
    public double BytesOutPerSecond { get; init; }

    // Errors (totals). Exceptions = tick + loop + player + callback failures.
    public long Exceptions { get; init; }
    public long InvalidPackets { get; init; }
    public long Disconnects { get; init; }

    // Queue: the match history (database) queue.
    public int DbQueueCount { get; init; }
    public int DbQueueCapacity { get; init; }
}
