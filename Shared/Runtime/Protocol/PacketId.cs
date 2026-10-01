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
    }
}
