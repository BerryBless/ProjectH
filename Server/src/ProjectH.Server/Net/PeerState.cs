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

    // 기능: 연결 수락 시 NetPeer.Tag에 붙일 연결별 상태를 만든다.
    // 입력: devPlayerId - 연결 요청에 들어 있던 개발용 Player Id.
    // 출력: Join 전이고 잘못된 Packet 수·종료 코드가 초기값인 PeerState 객체.
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

    // 기능: 서버가 이 연결을 끊는 사유 코드를 처음 한 번만 기록한다. 어느 Thread에서 불러도 된다.
    // 입력: code - 기록할 연결 종료 코드.
    // 출력: 이번 호출이 코드를 기록했으면 true, 다른 종료가 이미 코드를 기록했으면 false.
    // Returns false when another close already set the code (that one stays).
    public bool TrySetCloseCode(DisconnectCode code) =>
        Interlocked.CompareExchange(ref _closeCode, (int)code, (int)DisconnectCode.None) == (int)DisconnectCode.None;

    // Server review M5, L5: a token bucket in thousandths of a packet (integers: maxPerSecond per second is maxPerSecond
    // units per millisecond). Full at the first packet, whatever the clock reads.
    private bool _inputBucketStarted;
    private long _inputTokens;
    private long _lastInputMs;

    // 기능: 입력 Packet 하나를 Token Bucket으로 비율 검사한다. LiteNetLib 수신 경로에서만 호출한다.
    // 입력: nowMs - 단조 증가 Millisecond 시각, maxPerSecond - 초당 허용 입력 Packet 수, burst - 한 번에 허용할 Packet 수.
    // 출력: 허용되면 true(Token 하나 소비), 비율을 넘었으면 false.
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

    // 기능: 건설 요청 하나를 고정 1초 Window로 세어 비율 검사한다.
    // 입력: nowMs - 단조 증가 Millisecond 시각, maxPerSecond - 1초 Window당 허용 건설 요청 수.
    // 출력: 현재 Window 안에서 허용 수 이하면 true, 넘었으면 false.
    // Phase 13 D8: a fixed 1-second window for build requests (LiteNetLib's receive path only). Input uses a token
    // bucket since server review M5; build requests keep the window.
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

    // 기능: 전적 조회 요청이 최소 간격을 지켰는지 검사하고, 허용하면 요청 시각을 기록한다.
    // 입력: nowMs - 단조 증가 Millisecond 시각, minIntervalMs - 요청 사이 최소 간격(ms).
    // 출력: 허용되면 true, 지난 허용 요청 뒤 minIntervalMs가 지나지 않았으면 false.
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
