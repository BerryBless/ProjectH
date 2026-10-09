using System.Text.Json;

namespace ProjectH.QA;

public sealed record UiLogLine(long Seq, DateTimeOffset Time, string Category, string Text);

public sealed record UiEvent(long Seq, string Type, string Json);

public sealed record UiLogBatch(IReadOnlyList<UiLogLine> Lines, long Next, long Skipped);

// D20-D21: what the UI streams. Two rings: log lines (LogCapacity, oldest dropped) and run events (step, state, run
// start/end; EventCapacity). Writers: the run flow, the server's stdout reader threads, the actor pump's log. Readers:
// the SSE loops. Lock: _gate only, held for ring operations; JSON is serialized before taking it and nothing is
// called inside it (no nesting, no I/O: the hub is always the innermost, and nobody calls out of it).
public sealed class UiHub
{
    public const int LogCapacity = 2000;
    public const int EventCapacity = 512;
    public const int MaxLineLength = 2000;
    public static readonly string[] Categories = { "QA", "Server", "Actor", "Network", "Assertion" };

    private readonly object _gate = new();
    private readonly Queue<UiLogLine> _logs = new();
    private readonly Queue<UiEvent> _events = new();
    private long _nextLog = 1;
    private long _nextEvent = 1;

    public long LatestLogSeq
    {
        get { lock (_gate) return _nextLog - 1; }
    }

    public long LatestEventSeq
    {
        get { lock (_gate) return _nextEvent - 1; }
    }

    // 기능: 로그 한 줄을 Ring에 넣는다(MaxLineLength로 자르고, 모르는 category는 "QA", 가득 차면 가장 오래된 줄을 버린다).
    // 입력: category - 분류 이름, text - 로그 내용.
    // 출력: 반환값 없음. 줄이 seq를 받아 Ring에 들어간다.
    public void Log(string category, string text)
    {
        if (text.Length > MaxLineLength) text = text[..MaxLineLength] + "...";
        if (Array.IndexOf(Categories, category) < 0) category = "QA";
        lock (_gate)
        {
            if (_logs.Count >= LogCapacity) _logs.Dequeue();
            _logs.Enqueue(new UiLogLine(_nextLog++, DateTimeOffset.Now, category, text));
        }
    }

    // 기능: run 이벤트를 JSON으로 직렬화해 이벤트 Ring에 넣는다(가득 차면 가장 오래된 것을 버린다).
    // 입력: type - 이벤트 이름, payload - 직렬화할 객체.
    // 출력: 반환값 없음. 이벤트가 seq를 받아 Ring에 들어간다.
    public void Publish(string type, object payload)
    {
        string json = JsonSerializer.Serialize(payload, QaJson.Compact);
        lock (_gate)
        {
            if (_events.Count >= EventCapacity) _events.Dequeue();
            _events.Enqueue(new UiEvent(_nextEvent++, type, json));
        }
    }

    // 기능: after 이후의 로그 줄을 max개까지 읽되, 밀린 만큼 오래된 줄은 건너뛰고 센다.
    // 입력: after - 마지막으로 읽은 seq, max - 최대 줄 수.
    // 출력: 줄 목록·다음 커서(Next)·건너뛴 수(Skipped)를 담은 UiLogBatch.
    // Lines after `after`, at most `max`. When more are waiting (a flood, or lines the ring already dropped) the oldest
    // are skipped and counted, so a reader never falls further behind than one batch (D21).
    public UiLogBatch ReadLogs(long after, int max)
    {
        lock (_gate)
        {
            if (_logs.Count == 0) return new UiLogBatch(Array.Empty<UiLogLine>(), Math.Max(after, _nextLog - 1), 0);
            long oldest = _logs.Peek().Seq;
            long skipped = Math.Max(0, oldest - 1 - after);
            long from = Math.Max(after + 1, oldest);
            long available = _nextLog - from;
            if (available <= 0) return new UiLogBatch(Array.Empty<UiLogLine>(), _nextLog - 1, 0);
            if (available > max)
            {
                skipped += available - max;
                from = _nextLog - max;
            }
            var lines = new List<UiLogLine>((int)Math.Min(available, max));
            foreach (UiLogLine line in _logs)
            {
                if (line.Seq >= from) lines.Add(line);
            }
            return new UiLogBatch(lines, _nextLog - 1, skipped);
        }
    }

    // 기능: 최근 로그 줄을 복사한다.
    // 입력: count - 가져올 줄 수.
    // 출력: 마지막 count줄(오래된 순).
    public IReadOnlyList<UiLogLine> TailLogs(int count)
    {
        lock (_gate) return _logs.Skip(Math.Max(0, _logs.Count - count)).ToArray();
    }

    // 기능: after 이후의 이벤트를 모두 읽는다.
    // 입력: after - 마지막으로 읽은 seq.
    // 출력: 이벤트 목록, 다음 커서, 이미 버려진 이벤트가 있었으면 Lost=true.
    // Events after `after` (all of them: the ring is small and events are rare). lost = some were already dropped.
    public (IReadOnlyList<UiEvent> Events, long Next, bool Lost) ReadEvents(long after)
    {
        lock (_gate)
        {
            if (_events.Count == 0) return (Array.Empty<UiEvent>(), Math.Max(after, _nextEvent - 1), false);
            bool lost = _events.Peek().Seq > after + 1;
            UiEvent[] list = _events.Where(e => e.Seq > after).ToArray();
            return (list, _nextEvent - 1, lost);
        }
    }
}

// Lines the orchestrator writes to its TextWriter (run headers, summaries) into the hub as QA lines. Writes come from
// the run flow only; a lock still guards the buffer in case another writer appears.
internal sealed class HubWriter : TextWriter
{
    private readonly UiHub _hub;
    private readonly System.Text.StringBuilder _line = new();
    private readonly object _gate = new();

    // 기능: orchestrator 출력용 TextWriter를 만든다.
    // 입력: hub - 줄을 넘길 허브.
    // 출력: 빈 줄 버퍼의 HubWriter.
    public HubWriter(UiHub hub)
    {
        _hub = hub;
    }

    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

    // 기능: 문자 하나를 줄 버퍼에 모으고, 개행이면 한 줄을 QA 로그로 보낸다(MaxLineLength 초과분은 버린다).
    // 입력: value - 쓸 문자.
    // 출력: 반환값 없음. 개행에서 허브에 한 줄이 들어간다.
    public override void Write(char value)
    {
        string? done = null;
        lock (_gate)
        {
            if (value == '\n')
            {
                done = _line.ToString().TrimEnd('\r');
                _line.Clear();
            }
            else if (_line.Length < UiHub.MaxLineLength)
            {
                _line.Append(value);
            }
        }
        if (done != null) _hub.Log("QA", done);
    }

    // 기능: 버퍼에 남은 내용과 value를 한 줄로 QA 로그에 보낸다.
    // 입력: value - 쓸 문자열.
    // 출력: 반환값 없음. 버퍼가 비워진다.
    public override void WriteLine(string? value)
    {
        string done;
        lock (_gate)
        {
            done = _line.ToString() + value;
            _line.Clear();
        }
        _hub.Log("QA", done);
    }
}
