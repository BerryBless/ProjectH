using System.Text.Json;

namespace ProjectH.QA;

// Reads the server's QA event ring (GET /qa/events?after=) only when a step needs events (waitForEvent, event.*
// assertions) and once at the end for the report — request §148-149: no constant polling. Keeps the last
// RingCapacity events of the run; older ones are dropped (counted). Owned by the run flow; no threads.
public sealed class EventCursor
{
    public const int RingCapacity = 200;
    public const int FetchMax = 500;
    private const int MaxPagesPerFetch = 10;   // bounded catch-up: 5,000 events per call at most

    private readonly IQaServerClient _client;
    private readonly Queue<QaEvent> _ring = new();
    private long _next;
    private long _consumedSeq;
    private long _baselineSeq;

    // 기능: 서버 QA 이벤트 Ring을 읽을 커서를 만든다.
    // 입력: client - 이벤트를 조회할 QA Control API Client.
    // 출력: seq 0부터 읽는 빈 Ring 상태의 EventCursor.
    public EventCursor(IQaServerClient client)
    {
        _client = client;
    }

    public long ServerDropped { get; private set; }
    public long RingDropped { get; private set; }
    public IReadOnlyCollection<QaEvent> Recent => _ring;

    // 기능: 서버에 이미 쌓인 이벤트를 보관하지 않고 끝까지(최대 MaxPagesPerFetch 페이지) 읽어 넘기고 그 지점을 기준선으로 잡는다.
    // 입력: token - 취소 토큰.
    // 출력: 반환값 없음. _next·_baselineSeq·_consumedSeq가 서버의 마지막 seq 다음으로 맞춰진다.
    // Attach mode: events from before this run belong to someone else; skip them.
    public async Task SkipExistingAsync(CancellationToken token)
    {
        for (int page = 0; page < MaxPagesPerFetch; page++)
        {
            JsonElement batch = await _client.GetEventsAsync(_next, FetchMax, token).ConfigureAwait(false);
            int count = ReadBatch(batch, keep: false);
            if (count < FetchMax) break;
        }
        _baselineSeq = _next;
        _consumedSeq = _next;
    }

    // 기능: 마지막으로 읽은 seq 이후의 서버 이벤트를 페이지 단위(최대 MaxPagesPerFetch)로 받아 Ring에 쌓는다.
    // 입력: token - 취소 토큰.
    // 출력: 반환값 없음. Ring에 새 이벤트가 들어가고 넘친 만큼 RingDropped가 늘며 _next가 전진한다.
    public async Task FetchAsync(CancellationToken token)
    {
        for (int page = 0; page < MaxPagesPerFetch; page++)
        {
            JsonElement batch = await _client.GetEventsAsync(_next, FetchMax, token).ConfigureAwait(false);
            if (ReadBatch(batch, keep: true) < FetchMax) break;
        }
    }

    // 기능: 아직 소비하지 않은 이벤트 중 조건에 맞는 첫 이벤트를 찾아 그 seq까지를 소비 처리한다.
    // 입력: match - 이벤트 선택 조건.
    // 출력: 찾은 QaEvent, 없으면 null. 찾으면 _consumedSeq가 그 seq로 올라간다.
    // The first not yet consumed event matching the filter; it and everything before it count as consumed, so the
    // next waitForEvent looks after it (waits read events in order).
    public QaEvent? TakeFirst(Func<QaEvent, bool> match)
    {
        foreach (QaEvent e in _ring)
        {
            if (e.Seq <= _consumedSeq || !match(e)) continue;
            _consumedSeq = e.Seq;
            return e;
        }
        return null;
    }

    // 기능: 기준선 이후 Ring에 있는 이벤트 중 조건에 맞는 수를 센다(소비 여부와 무관).
    // 입력: match - 이벤트 선택 조건.
    // 출력: 조건에 맞는 이벤트 수.
    public int Count(Func<QaEvent, bool> match) => _ring.Count(e => e.Seq > _baselineSeq && match(e));

    // 기능: /qa/events 응답 한 페이지를 해석해 ServerDropped와 다음 조회 시작 seq(_next)를 갱신하고, keep이면 Ring에 넣는다.
    // 입력: batch - 응답 JSON(dropped·events·next), keep - true면 Ring에 보관, false면 세기만 한다.
    // 출력: 이 페이지의 이벤트 수. keep이면 Ring이 바뀌고 넘친 만큼 RingDropped가 는다.
    private int ReadBatch(JsonElement batch, bool keep)
    {
        if (JsonPath.Child(batch, "dropped") is { ValueKind: JsonValueKind.Number } d && d.TryGetInt64(out long dropped)) ServerDropped = Math.Max(ServerDropped, dropped);
        int count = 0;
        long maxSeq = _next;
        if (JsonPath.Child(batch, "events") is { ValueKind: JsonValueKind.Array } events)
        {
            foreach (JsonElement e in events.EnumerateArray())
            {
                count++;
                QaEvent ev = QaEvent.From(e);
                if (ev.Seq > maxSeq) maxSeq = ev.Seq;
                if (!keep) continue;
                if (_ring.Count >= RingCapacity)
                {
                    _ring.Dequeue();
                    RingDropped++;
                }
                _ring.Enqueue(ev);
            }
        }
        // `next` is what to pass back as `after` (exclusive: the server returns seq > after). Without it, the
        // highest seq seen.
        _next = JsonPath.Child(batch, "next") is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out long next) && next >= _next
            ? next
            : maxSeq;
        return count;
    }
}

public sealed record QaEvent(long Seq, long Tick, string Utc, string Type, string? Player, JsonElement Raw)
{
    // 기능: 서버 이벤트 JSON 한 건을 QaEvent로 바꾼다(없거나 타입이 다른 필드는 0·빈 문자열·null).
    // 입력: e - 이벤트 JSON 요소.
    // 출력: 필드를 채우고 원본 JSON 복제본(Raw)을 가진 QaEvent.
    public static QaEvent From(JsonElement e)
    {
        long seq = JsonPath.Child(e, "seq") is { ValueKind: JsonValueKind.Number } s && s.TryGetInt64(out long sv) ? sv : 0;
        long tick = JsonPath.Child(e, "tick") is { ValueKind: JsonValueKind.Number } t && t.TryGetInt64(out long tv) ? tv : 0;
        string utc = JsonPath.Child(e, "utc") is { ValueKind: JsonValueKind.String } u ? u.GetString()! : string.Empty;
        string type = JsonPath.Child(e, "type") is { ValueKind: JsonValueKind.String } ty ? ty.GetString()! : string.Empty;
        string? player = JsonPath.Child(e, "player") is { ValueKind: JsonValueKind.String } p ? p.GetString() : null;
        return new QaEvent(seq, tick, utc, type, player, e.Clone());
    }
}
