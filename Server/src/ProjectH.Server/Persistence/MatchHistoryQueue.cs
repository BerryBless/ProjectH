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

    // Game loop thread. False = full or completed: the record is dropped and counted.
    public bool TryEnqueue(MatchRecord record)
    {
        if (_channel.Writer.TryWrite(record)) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    public void Complete() => _channel.Writer.TryComplete();
}
