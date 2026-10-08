using System;
using System.Threading;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Net;

// Stored in NetPeer.Tag at accept time. Three groups of fields, each with one owner:
//   - DevPlayerId and ConnectSlot are immutable and may be read by any thread.
//   - BadPackets, Kicked, JoinRequested, the input token bucket (server review M5), the build request window, the
//     statistics request time (Phase 11) and the map marker window and token bucket (Phase 15 D7) are touched
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

    // 기능: 수락된 연결의 상태를 만든다.
    // 입력: devPlayerId - 연결 요청의 이름, connectSlot - 이 연결을 센 ConnectRateLimiter 칸(리뷰 수정 A2·A6, -1 = 세지 않음·모름).
    // 출력: 아무 Join도 하지 않은 PeerState.
    public PeerState(string devPlayerId, int connectSlot = -1)
    {
        DevPlayerId = devPlayerId;
        ConnectSlot = connectSlot;
    }

    public string DevPlayerId { get; }
    // Review fixes A2, A6: the ConnectRateLimiter slot this connection was counted in (Acquired at accept, Released at the
    // disconnect), and the source the game loop charges this player's failures to. -1 = not counted (tests).
    public int ConnectSlot { get; }
    // Review fixes B3, B4 (set once at accept, then read-only, any thread): this connection's session keys (registered in
    // AuthPacketLayer), the raw session key (the resume proof is bound to it), and the resume claim of the request
    // (ResumeProof null = no resume attempted). The game loop hands them to Match.TryJoin.
    public SessionKeys? Keys { get; init; }
    public byte[]? SessionKey { get; init; }
    public uint ResumeNonce { get; init; }
    public byte[]? ResumeProof { get; init; }
    // This connection's resume key (HMAC(K, "resume")): the key a later connection must prove to resume this character.
    public byte[]? ResumeKey => Keys?.ResumeKey;
    public int BadPackets;
    public bool Kicked;
    // Set when the first JoinMatchRequest is enqueued. Later Joins are bad packets and are not
    // enqueued, which keeps each connection at <= 3 control messages (see ControlChannelCapacity).
    public bool JoinRequested;

    // Game loop only: the loop tick the Connected message was handled at, whether the Join succeeded (Ok or Resumed; set
    // once, never cleared), and the loop tick of the newest input (the Join counts as one, so the input timeout starts
    // at the join). Joined is volatile: the network thread reads it for statistics requests (see the class comment).
    public long ConnectedTick;
    // Review fix C3 (game loop only): LiteNetLib's RoundTripTime of this connection, copied by SweepPeers every tick; Match
    // sizes the shooter's rewind allowance from it (0 until the first sweep: the narrowest allowance).
    public int RttMs;
    public volatile bool Joined;
    public long LastInputTick;
    // Phase 10: Match refused the Join (MatchFull). The connection is closed a second later (GameLoop.SweepPeers), so
    // the ReliableOrdered JoinMatchResponse that says why goes out before the close. Review fix B4: also set on a connection
    // whose character a new connection took over with its resume proof (closed the same way, without a code or a grace).
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

    // Phase 15 D7 (LiteNetLib's receive path only): every MapMarker packet in a fixed 1-second window (above the limit they
    // are invalid packets), and the token bucket that lets pingsPerSecond through (pingBurst at once).
    private long _markerWindowStartMs;
    private int _markersInWindow;
    private bool _markerBucketStarted;
    private long _markerTokens;
    private long _lastMarkerMs;

    // 기능: MapMarker 패킷을 고정 1초 창으로 센다(Phase 15 D7, 끊기 기준용).
    // 입력: nowMs - 지금 시각(ms), maxPerSecond - 1초에 허용하는 수.
    // 출력: 이 창에서 maxPerSecond 이하면 true, 넘으면 false(호출자가 잘못된 패킷으로 센다).
    public bool TryCountMarkerPacket(long nowMs, int maxPerSecond)
    {
        if (nowMs - _markerWindowStartMs >= 1000)
        {
            _markerWindowStartMs = nowMs;
            _markersInWindow = 0;
        }
        return ++_markersInWindow <= maxPerSecond;
    }

    // 기능: MapMarker 토큰 버킷(Phase 15 D7): 처음에는 가득 차 있고, 초당 perSecond개씩 burst개까지 찬다. 정수 1/1000 단위.
    // 입력: nowMs - 지금 시각(ms), perSecond - 초당 통과 수, burst - 한 번에 통과할 수 있는 수.
    // 출력: 통과하면 true, 버킷이 비었으면 false(호출자가 버리고 센다, 끊지 않는다).
    public bool TryTakeMarkerToken(long nowMs, int perSecond, int burst)
    {
        long capacity = burst * 1000L;
        if (!_markerBucketStarted)
        {
            _markerBucketStarted = true;
            _markerTokens = capacity;
        }
        else
        {
            long elapsed = Math.Clamp(nowMs - _lastMarkerMs, 0, capacity);   // capped, so the multiply cannot overflow
            _markerTokens = Math.Min(capacity, _markerTokens + elapsed * perSecond);
        }
        _lastMarkerMs = nowMs;
        if (_markerTokens < 1000) return false;
        _markerTokens -= 1000;
        return true;
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
