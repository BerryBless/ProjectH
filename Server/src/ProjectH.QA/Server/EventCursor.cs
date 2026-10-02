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

    public EventCursor(IQaServerClient client)
    {
        _client = client;
    }

    public long ServerDropped { get; private set; }
    public long RingDropped { get; private set; }
    public IReadOnlyCollection<QaEvent> Recent => _ring;

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

    public async Task FetchAsync(CancellationToken token)
    {
        for (int page = 0; page < MaxPagesPerFetch; page++)
        {
            JsonElement batch = await _client.GetEventsAsync(_next, FetchMax, token).ConfigureAwait(false);
            if (ReadBatch(batch, keep: true) < FetchMax) break;
        }
    }

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

    public int Count(Func<QaEvent, bool> match) => _ring.Count(e => e.Seq > _baselineSeq && match(e));

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
