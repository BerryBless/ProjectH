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

    // 기능: 게임 루프(Writer 1개)와 DB 저장 작업(Reader 1개) 사이의 크기 제한 Channel을 만든다.
    // 입력: capacity - 대기할 수 있는 최대 매치 기록 수.
    // 출력: 비어 있고 Dropped가 0인 MatchHistoryQueue 객체.
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

    // 기능: 끝난 매치 기록을 기다리지 않고 저장 Queue에 넣는다.
    // 입력: record - 저장할 매치 기록.
    // 출력: 넣었으면 true, Queue가 가득 찼거나 완료되었으면 false(기록은 버려지고 Dropped가 1 증가).
    // Game loop thread. False = full or completed: the record is dropped and counted.
    public bool TryEnqueue(MatchRecord record)
    {
        if (_channel.Writer.TryWrite(record)) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    // 기능: 저장 Queue를 완료해 더 이상 기록을 받지 않게 한다. 여러 번 불러도 된다.
    // 입력: 없음.
    // 출력: 반환값 없음. 이후 TryEnqueue는 false를 돌려주고, Reader는 남은 기록을 다 읽으면 끝난다.
    public void Complete() => _channel.Writer.TryComplete();
}
