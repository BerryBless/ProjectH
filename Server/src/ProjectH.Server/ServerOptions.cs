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
    // looted spawn point stays empty. Phase 5 (D4): used only with DevRespawn; a match never refills loot.
    // Each match rolls its loot with LootSeed + round number (D3).
    public int LootSeed { get; set; } = 1;
    public int LootRespawnSeconds { get; set; } = 30;

    // Phase 5 (D1, D4, D6): the match flow. A countdown starts once MinPlayers are connected; the result
    // screen lasts ResultSeconds. DevRespawn = the Phase 3/4 sandbox (no match flow, respawn and loot refill
    // on, loot from the start); off in production. Each match rolls its zone with ZoneSeed + round number.
    public int MinPlayers { get; set; } = 2;
    public int StartCountdownSeconds { get; set; } = 10;
    public int ResultSeconds { get; set; } = 10;
    public bool DevRespawn { get; set; }
    public int ZoneSeed { get; set; } = 1;
    // Phase 6 D9: each match shuffles the drop points with SpawnSeed + round number.
    public int SpawnSeed { get; set; } = 1;
    // Phase 12 D4, D5: a match starts aboard the drop transport (its route is rolled with SpawnSeed + round number) and
    // the zone's clock starts when the route ends. Off = everyone starts on a drop point as in Phases 6-11 (most rule
    // tests, and a load run that compares with them). Never with DevRespawn.
    public bool AirDrop { get; set; } = true;
    // Phase 13 D17 (request §136): building costs nothing (load tests: --Server:BuildInfiniteResources=true). Off in
    // production; bots in a real match follow the normal resource rules.
    public bool BuildInfiniteResources { get; set; }

    // Phase 10 (D2-D4): a participant who drops during a match keeps its character this long (0 = off); a connection
    // must join within JoinTimeoutSeconds; a joined player that sends no input for InputTimeoutSeconds is disconnected
    // (0 = off).
    public int ReconnectGraceSeconds { get; set; } = 10;
    public int JoinTimeoutSeconds { get; set; } = 5;
    public int InputTimeoutSeconds { get; set; } = 10;

    // Server review M2: connection requests one remote IP may make, as a token bucket: ConnectBurstPerIp at once, then
    // ConnectsPerIpPerSecond. More are refused as ServerFull before Accept (counted as connectRate), so connect/disconnect
    // churn cannot fill the shared Control channel. 0 = off. A load test with many bots on one machine raises the burst
    // (--Server:ConnectBurstPerIp=200).
    public int ConnectBurstPerIp { get; set; } = 20;
    public int ConnectsPerIpPerSecond { get; set; } = 5;

    // Each connection produces at most Connected + JoinRequested + Disconnected.
    public int ControlChannelCapacity => MaxPlayers * 3;
    public int InputChannelCapacity => MaxPlayers * InputBufferPerPlayer;
    public byte SnapshotHz => (byte)(SimHz / SnapshotEveryTicks);
    // The client sends at most one input packet per simulation step; 2x leaves room for bursts
    // after network jitter. Server review M5, L5: a token bucket of InputBurst (one second of input) refilled at
    // MaxInputPacketsPerSecond; anything above is dropped and counted (inputRate), never kicked.
    public int MaxInputPacketsPerSecond => SimHz * 2;
    public int InputBurst => SimHz;

    public string? Validate()
    {
        if (Port < 0 || Port > 65535) return "Port must be 0-65535.";
        if (MaxPlayers < 1 || MaxPlayers > ProtocolConstants.MaxSnapshotEntities)
            return $"MaxPlayers must be 1-{ProtocolConstants.MaxSnapshotEntities}: a snapshot is at most {ProtocolConstants.MaxSnapshotParts} unfragmented datagrams.";
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
        // 1 is rejected: a lone player is the only one alive, so the match would finish on its first tick and the
        // flow would cycle Starting -> Finished forever.
        if (MinPlayers < 2 || MinPlayers > MaxPlayers) return "MinPlayers must be 2-MaxPlayers (a match needs at least two players).";
        if (StartCountdownSeconds < 1 || StartCountdownSeconds > 300) return "StartCountdownSeconds must be 1-300.";
        if (ResultSeconds < 1 || ResultSeconds > 300) return "ResultSeconds must be 1-300.";
        if (ZoneSeed < 0) return "ZoneSeed must be 0 or more.";
        if (SpawnSeed < 0) return "SpawnSeed must be 0 or more.";
        if (ReconnectGraceSeconds < 0 || ReconnectGraceSeconds > 60) return "ReconnectGraceSeconds must be 0-60 (0 = off).";
        if (JoinTimeoutSeconds < 1 || JoinTimeoutSeconds > 60) return "JoinTimeoutSeconds must be 1-60.";
        // Below 2 s a normal hitch (a scene load, a GC pause on a weak machine) would disconnect live players.
        if (InputTimeoutSeconds != 0 && (InputTimeoutSeconds < 2 || InputTimeoutSeconds > 300))
            return "InputTimeoutSeconds must be 0 (off) or 2-300.";
        // A lost network stops the input too. If the input sweep fired before LiteNetLib's own timeout, it would close
        // the connection as InputTimeout (a server close, so no reconnect grace) instead of letting the timeout find a
        // network loss. 2 s of margin covers the timeout's ping granularity. With the 500 ms minimum timeout this makes
        // 3 s the smallest usable value.
        if (InputTimeoutSeconds != 0 && (long)InputTimeoutSeconds * 1000 < (long)DisconnectTimeoutMs + 2000)
            return "InputTimeoutSeconds * 1000 must be at least DisconnectTimeoutMs + 2000 (or 0 = off), so a network loss keeps its reconnect grace.";
        if (ConnectBurstPerIp < 0 || ConnectBurstPerIp > 10000) return "ConnectBurstPerIp must be 0 (off) or 1-10000.";
        if (ConnectsPerIpPerSecond < 0 || ConnectsPerIpPerSecond > 1000) return "ConnectsPerIpPerSecond must be 0 (off) or 1-1000.";
        return null;
    }
}
