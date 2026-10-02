using System;
using System.Threading;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Net;

// Stored in NetPeer.Tag at accept time. Three groups of fields, each with one owner:
//   - DevPlayerId is immutable and may be read by any thread.
//   - BadPackets, Kicked, JoinRequested, the input token bucket (server review M5), the build request window and the
//     statistics request time (Phase 11) are touched
//     only on LiteNetLib's receive path, so
//     they need no synchronization: LiteNetLib runs one receive thread per socket and the server binds IPv4 only, so
//     all of a peer's packets arrive on that single thread.
//   - ConnectedTick, Joined, LastInputTick, JoinRefused, RefusedTick (Phase 10 D3, D4) and CongestedSinceTick (Phase 13
//     final review A4) belong to the game loop thread only. Joined is the one exception for reading: it is volatile and LiteNetLib's receive path reads it to
//     take statistics requests only from a joined player (Phase 11 B4). Only the game loop writes it.
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

    // Game loop only: the loop tick the Connected message was handled at, whether the Join succeeded (Ok or Resumed; set
    // once, never cleared), and the loop tick of the newest input (the Join counts as one, so the input timeout starts
    // at the join). Joined is volatile: the network thread reads it for statistics requests (see the class comment).
    public long ConnectedTick;
    public volatile bool Joined;
    public long LastInputTick;
    // Phase 10: Match refused the Join (MatchFull). The connection is closed a second later (GameLoop.SweepPeers), so
    // the ReliableOrdered JoinMatchResponse that says why goes out before the close.
    public bool JoinRefused;
    public long RefusedTick;
    // Phase 13 final review A4: the loop tick the peer's reliable queues went over GameLoop.MaxReliableBacklog (-1 = not
    // over now). Closed with Congested once it stays over for GameLoop.CongestedSeconds.
    public long CongestedSinceTick = -1;

    // Phase 10 D1, D2: the code the server closed this connection with (None = the server did not close it). Set
    // before peer.Disconnect is called, so the game loop always sees it when the Disconnected message arrives (with
    // UnsyncedEvents LiteNetLib may raise OnPeerDisconnected inside Disconnect itself). Decides whether the player
    // gets a reconnect grace: never after a server close.
    public DisconnectCode CloseCode => (DisconnectCode)Volatile.Read(ref _closeCode);

    // Returns false when another close already set the code (that one stays).
    public bool TrySetCloseCode(DisconnectCode code) =>
        Interlocked.CompareExchange(ref _closeCode, (int)code, (int)DisconnectCode.None) == (int)DisconnectCode.None;

    // Server review M5, L5: a token bucket in thousandths of a packet (integers: maxPerSecond per second is maxPerSecond
    // units per millisecond). Full at the first packet, whatever the clock reads.
    private bool _inputBucketStarted;
    private long _inputTokens;
    private long _lastInputMs;

    // Server review M5, L5: up to burst packets at once, then maxPerSecond. Returns false for a packet over the rate (the
    // caller drops it). Unlike the fixed one-second window before it, the bucket never lets 2 x maxPerSecond through
    // back to back across a window edge.
    public bool TryCountInputPacket(long nowMs, int maxPerSecond, int burst)
    {
        long capacity = burst * 1000L;
        if (!_inputBucketStarted)
        {
            _inputBucketStarted = true;
            _inputTokens = capacity;
        }
        else
        {
            long elapsed = Math.Clamp(nowMs - _lastInputMs, 0, capacity);   // capped, so the multiply cannot overflow
            _inputTokens = Math.Min(capacity, _inputTokens + elapsed * maxPerSecond);
        }
        _lastInputMs = nowMs;
        if (_inputTokens < 1000) return false;
        _inputTokens -= 1000;
        return true;
    }

    private long _buildWindowStartMs;
    private int _buildRequestsInWindow;

    // Phase 13 D8: the same fixed 1-second window for build requests (LiteNetLib's receive path only).
    public bool TryCountBuildRequest(long nowMs, int maxPerSecond)
    {
        if (nowMs - _buildWindowStartMs >= 1000)
        {
            _buildWindowStartMs = nowMs;
            _buildRequestsInWindow = 0;
        }
        return ++_buildRequestsInWindow <= maxPerSecond;
    }

    private bool _statsRequested;
    private long _lastStatsRequestMs;

    // Phase 11 D8: at most one statistics request per minIntervalMs. A refused request does not move the window, so
    // pressing the button again and again still gets one answer every minIntervalMs.
    public bool TryCountStatsRequest(long nowMs, int minIntervalMs)
    {
        if (_statsRequested && nowMs - _lastStatsRequestMs < minIntervalMs) return false;
        _statsRequested = true;
        _lastStatsRequestMs = nowMs;
        return true;
    }
}
