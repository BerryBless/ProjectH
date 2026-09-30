namespace ProjectH.Server.Net;

// Stored in NetPeer.Tag at accept time. DevPlayerId is immutable and may be read by the game loop;
// BadPackets, Kicked, JoinRequested and the input rate window are touched only on LiteNetLib's
// receive path, so they need no synchronization: LiteNetLib runs one receive thread per socket and
// the server binds IPv4 only, so all of a peer's packets arrive on that single thread.
public sealed class PeerState
{
    public PeerState(string devPlayerId)
    {
        DevPlayerId = devPlayerId;
    }

    public string DevPlayerId { get; }
    public int BadPackets;
    public bool Kicked;
    // Set when the first JoinMatchRequest is enqueued. Later Joins are bad packets and are not
    // enqueued, which keeps each connection at <= 3 control messages (see ControlChannelCapacity).
    public bool JoinRequested;

    private long _inputWindowStartMs;
    private int _inputPacketsInWindow;

    // Fixed 1-second window. Returns false once the peer exceeds maxPerSecond in the current window.
    public bool TryCountInputPacket(long nowMs, int maxPerSecond)
    {
        if (nowMs - _inputWindowStartMs >= 1000)
        {
            _inputWindowStartMs = nowMs;
            _inputPacketsInWindow = 0;
        }
        return ++_inputPacketsInWindow <= maxPerSecond;
    }
}
