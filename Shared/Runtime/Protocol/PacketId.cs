namespace ProjectH.Shared.Protocol
{
    // First byte of every packet. Keep values stable: they are the wire format.
    public enum PacketId : byte
    {
        None = 0,
        JoinMatchRequest = 1,
        JoinMatchResponse = 2,
        PlayerSpawned = 3,
        PlayerDespawned = 4,
        PlayerInput = 5,
        WorldSnapshot = 6,
        WeaponCatalog = 7,
        ShotFired = 8,
        HitConfirmed = 9,
        DamageTaken = 10,
        PlayerDied = 11,
        PlayerRespawned = 12,
        ItemCatalog = 13,
        WorldItems = 14,
        ItemSpawned = 15,
        ItemRemoved = 16,
        InventoryState = 17,
        PickupResult = 18,
        MatchState = 19,
        ZoneState = 20,
        MatchResult = 21,
        // Phase 11 D8: statistics on request.
        StatsRequest = 22,
        StatsResponse = 23,
        // Phase 12 D11: deployment and doors.
        TransportRoute = 24,
        DoorStates = 25,
        // Phase 13 D19: building and harvesting. BuildRequest is the only one a client sends.
        BuildCatalog = 26,
        BuildRequest = 27,
        BuildResult = 28,
        BuildEvents = 29,
        BuildSync = 30,
        BuildInterest = 31,
        ResourcesState = 32,
        HarvestHit = 33,
        HarvestStates = 34,
        // Phase 13.5 D4: editing a piece (C->S, the building channel).
        BuildEditRequest = 35,
        // Phase 14 D2, D5, D8, D10: squads, knock-downs, revives and reboots (all S->C, channel 0).
        TeamState = 36,
        PlayerDowned = 37,
        ChannelState = 38,
        RebootStations = 39,
        // Phase 15 D7, D10: map pings and waypoints. MapMarker is C->S (channel 0); TeamMarkers is S->C to one team.
        MapMarker = 40,
        TeamMarkers = 41,
        // Phase 16 D3, D7: loot containers and supply drops (both S->C, channel 0).
        ContainerStates = 42,
        SupplyDrops = 43,
    }
}
