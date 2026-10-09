using System.Threading;
using System.Threading.Channels;

namespace ProjectH.Server.Persistence;

// Phase 9 D6 (§44): the only link between the game loop and the database writer.
// Maximum size: PersistenceOptions.QueueCapacity records. Producer: the game loop, once per finished match
// (TryEnqueue never blocks and never allocates beyond the channel slot). Consumer: MatchHistoryWriter, one reader.
// Overflow policy: Reject — a record that does not fit is dropped and counted (Dropped); the game never waits for
// the database. Complete() is called on shutdown so the writer can drain what is left.
public sealed class MatchHistoryQueue
{
    private readonly Channel<MatchRecord> _channel;
    private long _dropped;

    // 기능: 경기 기록용 유한 Channel(쓰는 쪽·읽는 쪽 하나씩)을 만든다.
    // 입력: capacity - 기다릴 수 있는 기록 수(PersistenceOptions.QueueCapacity).
    // 출력: 빈 MatchHistoryQueue.
    public MatchHistoryQueue(int capacity)
    {
        _channel = Channel.CreateBounded<MatchRecord>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,   // with TryWrite: returns false instead of waiting
            SingleReader = true,
            SingleWriter = true,
        });
    }

    public ChannelReader<MatchRecord> Reader => _channel.Reader;
    public long Dropped => Interlocked.Read(ref _dropped);
    // QA-3: records waiting for the writer (any thread; the bounded channel counts under its own lock).
    public int Count => _channel.Reader.CanCount ? _channel.Reader.Count : -1;

    // 기능: 끝난 경기 기록 하나를 넣는다(Game Loop, 기다리지 않는다). 못 넣으면 Dropped로 센다.
    // 입력: record - 저장할 경기 기록.
    // 출력: 들어갔으면 true, 가득 찼거나 완료된 큐면 false(기록은 버려진다).
    // Game loop thread. False = full or completed: the record is dropped and counted.
    public bool TryEnqueue(MatchRecord record)
    {
        if (_channel.Writer.TryWrite(record)) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    // 기능: 큐를 완료해 더는 받지 않게 한다(종료 때, Writer가 남은 것을 비울 수 있게).
    // 입력: 없음.
    // 출력: 반환값 없음. 이후 TryEnqueue는 false, Reader는 남은 것을 읽고 끝난다.
    public void Complete() => _channel.Writer.TryComplete();
}
