using System.Threading;
using System.Threading.Channels;
using LiteNetLib;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Persistence;

// Phase 11 D8: one statistics request of a connection. Peer is carried for the same reason as in the inbound messages:
// LiteNetLib reuses peer ids, so the game loop answers only this very connection. EnqueuedMs: Environment.TickCount64
// when the network thread queued it; StatsQueryService answers a request older than MaxQueueAgeMs Unavailable without
// querying (the client gave up on it already).
public readonly record struct StatsQuery(int PeerId, NetPeer Peer, string DevPlayerId, long EnqueuedMs);

// The answer to one StatsQuery. Only the game loop sends it (it sends every packet).
public readonly record struct StatsReply(int PeerId, NetPeer Peer, StatsResponse Response);

// Phase 11 D8: what the statistics path did so far (any thread).
//   Requests: accepted into the request queue. Limited: dropped without an answer (before the join, or within
//   MinRequestIntervalMs of the connection's previous request). Busy / Unavailable: answers with that status.
//   Undelivered: answers that never went out (the reply queue was full, or the connection was gone).
public readonly record struct StatsQueryCounts(long Requests, long Limited, long Busy, long Unavailable, long Undelivered);

// Phase 11 D8 (§44): the two bounded queues of the statistics path and its counters. No lock: Channels and Interlocked.
//   Requests: LiteNetLib's threads -> StatsQueryService (one reader). Capacity requests. Full: Reject, and the network
//     thread answers Busy at once instead.
//   Replies: StatsQueryService (results) and LiteNetLib's threads (Busy) -> the game loop, which drains it every tick and
//     sends. Capacity replies. Full: Reject, the reply is dropped and counted (the client shows "no answer" after 5 s).
// Neither writer ever waits. Created once (Program, or GameLoop when none is given) and lives as long as the server.
public sealed class StatsQueryQueue
{
    public const int DefaultCapacity = 32;
    // D8: at most one request per connection per this many milliseconds; the rest are dropped and counted as Limited.
    public const int MinRequestIntervalMs = 2000;
    // A request that waited in the queue longer than this is answered Unavailable without a query: the client's stats
    // window gives up after 5 s (StatsWait.AnswerSeconds), so nobody waits for that answer any more.
    public const int MaxQueueAgeMs = 5000;

    private readonly Channel<StatsQuery> _requests;
    private readonly Channel<StatsReply> _replies;
    private long _accepted;
    private long _limited;
    private long _busy;
    private long _unavailable;
    private long _undelivered;

    // 기능: 요청 큐와 응답 큐(둘 다 유한, 읽는 쪽 하나·쓰는 쪽 여럿)를 만든다.
    // 입력: capacity - 각 큐가 기다릴 수 있는 항목 수.
    // 출력: 두 큐가 빈 StatsQueryQueue.
    public StatsQueryQueue(int capacity = DefaultCapacity)
    {
        Capacity = capacity;
        // SingleWriter is false: with UnsyncedEvents LiteNetLib may raise receive events for different peers from more
        // than one thread, and the reply queue is also written by StatsQueryService.
        _requests = Channel.CreateBounded<StatsQuery>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,   // with TryWrite: returns false instead of waiting
            SingleReader = true,
            SingleWriter = false,
        });
        _replies = Channel.CreateBounded<StatsReply>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public int Capacity { get; }
    public ChannelReader<StatsQuery> Requests => _requests.Reader;

    public StatsQueryCounts Counts => new(Interlocked.Read(ref _accepted), Interlocked.Read(ref _limited),
        Interlocked.Read(ref _busy), Interlocked.Read(ref _unavailable), Interlocked.Read(ref _undelivered));

    // 기능: 통계 요청 하나를 요청 큐에 넣는다(수신 스레드, 기다리지 않는다). 들어가면 Requests로 센다.
    // 입력: query - 연결·플레이어 id·넣은 시각이 든 요청.
    // 출력: 들어갔으면 true, 요청 큐가 가득 차면 false(호출자가 Busy로 답한다).
    // Network thread. False = the request queue is full: the caller answers Busy.
    public bool TryEnqueue(in StatsQuery query)
    {
        if (!_requests.Writer.TryWrite(query)) return false;
        Interlocked.Increment(ref _accepted);
        return true;
    }

    // 기능: 응답 하나를 응답 큐에 넣는다(StatsQueryService 또는 수신 스레드). Busy·Unavailable 상태는 그 합계로 센다.
    // 입력: reply - 연결과 보낼 StatsResponse.
    // 출력: 들어갔으면 true, 응답 큐가 가득 차면 false(응답은 버려지고 Undelivered로 센다).
    // StatsQueryService or a network thread. False = the reply queue is full: the reply is dropped and counted.
    public bool TryReply(in StatsReply reply)
    {
        if (reply.Response.Status == StatsStatus.Busy) Interlocked.Increment(ref _busy);
        else if (reply.Response.Status == StatsStatus.Unavailable) Interlocked.Increment(ref _unavailable);
        if (_replies.Writer.TryWrite(reply)) return true;
        Interlocked.Increment(ref _undelivered);
        return false;
    }

    // 기능: 응답 큐에서 응답 하나를 꺼낸다(Game Loop 전용, 기다리지 않는다).
    // 입력: reply - 꺼낸 응답을 받을 곳.
    // 출력: 꺼냈으면 true와 응답, 비어 있으면 false.
    // Game loop only.
    public bool TryTakeReply(out StatsReply reply) => _replies.Reader.TryRead(out reply);

    // 기능: 답 없이 버린 요청(Join 전, 또는 간격 안의 재요청) 하나를 센다(수신 스레드).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddLimited() => Interlocked.Increment(ref _limited);
    // 기능: 연결이 사라져 보내지 못한 응답 하나를 센다(Game Loop).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddUndelivered() => Interlocked.Increment(ref _undelivered);
}
