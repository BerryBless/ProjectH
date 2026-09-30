using ProjectH.Shared.Protocol;

namespace ProjectH.Server;

// Bound from the "Server" section of appsettings.json. Validate() runs at startup so a bad
// config fails fast instead of producing oversized snapshots or unbounded queues at runtime.
public sealed class ServerOptions
{
    public int Port { get; set; } = 7777;
    public int MaxPlayers { get; set; } = 16;
    public int SimHz { get; set; } = 30;
    public int SnapshotEveryTicks { get; set; } = 2;
    public int InputBufferPerPlayer { get; set; } = 8;
    public int MaxInputMessagesPerTick { get; set; } = 512;
    public int BadPacketDisconnectThreshold { get; set; } = 20;
    public int DisconnectTimeoutMs { get; set; } = 5000;
    public int StatsIntervalSeconds { get; set; } = 10;
    // Phase 4 (D5, D7): seed of the game loop's loot Random (same seed = same loot), and how long a
    // looted spawn point stays empty. 0 turns respawning off (the battle royale rule, Match Flow phase).
    public int LootSeed { get; set; } = 1;
    public int LootRespawnSeconds { get; set; } = 30;

    // Each connection produces at most Connected + JoinRequested + Disconnected.
    public int ControlChannelCapacity => MaxPlayers * 3;
    public int InputChannelCapacity => MaxPlayers * InputBufferPerPlayer;
    public byte SnapshotHz => (byte)(SimHz / SnapshotEveryTicks);
    // The client sends at most one input packet per simulation step; 2x leaves room for bursts
    // after network jitter. Anything above is flooding and counts as bad packets.
    public int MaxInputPacketsPerSecond => SimHz * 2;

    public string? Validate()
    {
        if (Port < 0 || Port > 65535) return "Port must be 0-65535.";
        if (MaxPlayers < 1 || MaxPlayers > ProtocolConstants.MaxSnapshotEntities)
            return $"MaxPlayers must be 1-{ProtocolConstants.MaxSnapshotEntities}: a full snapshot must fit one unfragmented datagram.";
        if (SimHz < 10 || SimHz > 128) return "SimHz must be 10-128.";
        if (SnapshotEveryTicks < 1 || SnapshotEveryTicks > SimHz) return "SnapshotEveryTicks must be 1-SimHz.";
        if (SimHz % SnapshotEveryTicks != 0) return "SnapshotEveryTicks must divide SimHz: SnapshotHz is SimHz / SnapshotEveryTicks.";
        if (InputBufferPerPlayer < 2 || InputBufferPerPlayer > 64) return "InputBufferPerPlayer must be 2-64.";
        if (MaxInputMessagesPerTick < 1) return "MaxInputMessagesPerTick must be positive.";
        if (BadPacketDisconnectThreshold < 1) return "BadPacketDisconnectThreshold must be positive.";
        if (DisconnectTimeoutMs < 500) return "DisconnectTimeoutMs must be at least 500.";
        if (StatsIntervalSeconds < 1) return "StatsIntervalSeconds must be positive.";
        if (LootSeed < 0) return "LootSeed must be 0 or more.";
        if (LootRespawnSeconds < 0 || LootRespawnSeconds > 3600) return "LootRespawnSeconds must be 0-3600 (0 = off).";
        return null;
    }
}
