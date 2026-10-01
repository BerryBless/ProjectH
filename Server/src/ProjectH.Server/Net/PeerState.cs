using System.Threading;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Net;

// Stored in NetPeer.Tag at accept time. Three groups of fields, each with one owner:
//   - DevPlayerId is immutable and may be read by any thread.
//   - BadPackets, Kicked, JoinRequested and the input rate window are touched only on LiteNetLib's receive path, so
//     they need no synchronization: LiteNetLib runs one receive thread per socket and the server binds IPv4 only, so
//     all of a peer's packets arrive on that single thread.
//   - ConnectedTick, Joined, LastInputTick, JoinRefused and RefusedTick (Phase 10 D3, D4) belong to the game loop
//     thread only.
// CloseCode is the one field both threads write; it is first-writer-wins through Interlocked.
public sealed class PeerState
{
    private int _closeCode;

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

    // Game loop only: the loop tick the Connected message was handled at, whether the Join was handled, and the loop
    // tick of the newest input (the Join counts as one, so the input timeout starts at the join).
    public long ConnectedTick;
    public bool Joined;
    public long LastInputTick;
    // Phase 10: Match refused the Join (MatchFull). The connection is closed a second later (GameLoop.SweepPeers), so
    // the ReliableOrdered JoinMatchResponse that says why goes out before the close.
    public bool JoinRefused;
    public long RefusedTick;

    // Phase 10 D1, D2: the code the server closed this connection with (None = the server did not close it). Set
    // before peer.Disconnect is called, so the game loop always sees it when the Disconnected message arrives (with
    // UnsyncedEvents LiteNetLib may raise OnPeerDisconnected inside Disconnect itself). Decides whether the player
    // gets a reconnect grace: never after a server close.
    public DisconnectCode CloseCode => (DisconnectCode)Volatile.Read(ref _closeCode);

    // Returns false when another close already set the code (that one stays).
    public bool TrySetCloseCode(DisconnectCode code) =>
        Interlocked.CompareExchange(ref _closeCode, (int)code, (int)DisconnectCode.None) == (int)DisconnectCode.None;

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
