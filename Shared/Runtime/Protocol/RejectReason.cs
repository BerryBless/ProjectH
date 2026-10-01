namespace ProjectH.Shared.Protocol
{
    // Sent as the single byte of LiteNetLib's reject data when a connection request is refused.
    public enum RejectReason : byte
    {
        None = 0,
        VersionMismatch = 1,
        ServerFull = 2,
        BadRequest = 3,
    }

    public enum JoinResult : byte
    {
        Ok = 0,
        AlreadyJoined = 1,
        MatchFull = 2,
        // Phase 10 D2: the same DevPlayerId came back within the reconnect grace and took over its character
        // (same entity id). The client handles it like Ok; the server sends the full state again.
        Resumed = 3,
    }
}
