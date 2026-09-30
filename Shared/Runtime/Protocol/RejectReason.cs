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
    }
}
