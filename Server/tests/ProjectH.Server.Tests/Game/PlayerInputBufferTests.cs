using ProjectH.Server.Game;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

public class PlayerInputBufferTests
{
    private static InputCommand Cmd(uint seq) => new InputCommand { Seq = seq };

    [Fact]
    public void TakesInSeqOrder_RegardlessOfArrivalOrder()
    {
        var buffer = new PlayerInputBuffer(8);
        buffer.Add(Cmd(3));
        buffer.Add(Cmd(1));
        buffer.Add(Cmd(2));

        Assert.True(buffer.TryTake(out var a));
        Assert.True(buffer.TryTake(out var b));
        Assert.True(buffer.TryTake(out var c));
        Assert.Equal(new uint[] { 1, 2, 3 }, new[] { a.Seq, b.Seq, c.Seq });
        Assert.Equal(3u, buffer.LastTakenSeq);
    }

    [Fact]
    public void Duplicate_IsRejected()
    {
        var buffer = new PlayerInputBuffer(8);
        Assert.True(buffer.Add(Cmd(5)));
        Assert.False(buffer.Add(Cmd(5)));
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void AlreadyTakenOrOlder_IsRejected()
    {
        var buffer = new PlayerInputBuffer(8);
        buffer.Add(Cmd(5));
        buffer.TryTake(out _);
        Assert.False(buffer.Add(Cmd(5)));
        Assert.False(buffer.Add(Cmd(4)));
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void Full_DropsOldest()
    {
        var buffer = new PlayerInputBuffer(3);
        buffer.Add(Cmd(1));
        buffer.Add(Cmd(2));
        buffer.Add(Cmd(3));
        Assert.True(buffer.Add(Cmd(4)));

        Assert.Equal(3, buffer.Count);
        Assert.Equal(1, buffer.DroppedCount);
        buffer.TryTake(out var first);
        Assert.Equal(2u, first.Seq);
    }

    [Fact]
    public void Full_AndNewIsOldest_DropsNew()
    {
        var buffer = new PlayerInputBuffer(3);
        buffer.Add(Cmd(5));
        buffer.Add(Cmd(6));
        buffer.Add(Cmd(7));
        Assert.False(buffer.Add(Cmd(4)));
        Assert.Equal(1, buffer.DroppedCount);
        buffer.TryTake(out var first);
        Assert.Equal(5u, first.Seq);
    }

    // Review fix A4 (SEC-7): one input far ahead (injected or corrupt) no longer locks the player out: it is dropped and
    // counted, and the next normal Seq is still taken.
    [Fact]
    public void ASeqFarAhead_IsDropped_AndLaterNormalSeqsStillGoThrough()
    {
        var buffer = new PlayerInputBuffer(8);
        Assert.True(buffer.Add(Cmd(1)));
        Assert.True(buffer.TryTake(out _));
        Assert.False(buffer.Add(Cmd(0xFFFFFFF0)));
        Assert.Equal(1, buffer.SeqAheadDrops);
        Assert.Equal(0, buffer.Count);
        Assert.True(buffer.Add(Cmd(2)));
        Assert.True(buffer.TryTake(out var next));
        Assert.Equal(2u, next.Seq);
    }

    [Fact]
    public void TheWindowEdge_IsMaxInputSeqAheadPastTheLastTaken()
    {
        var buffer = new PlayerInputBuffer(8);
        buffer.Add(Cmd(10));
        buffer.TryTake(out _);
        Assert.True(buffer.Add(Cmd(10 + (uint)ProjectH.Shared.Protocol.ProtocolLimits.MaxInputSeqAhead)));
        Assert.False(buffer.Add(Cmd(11 + (uint)ProjectH.Shared.Protocol.ProtocolLimits.MaxInputSeqAhead)));
        Assert.Equal(1, buffer.SeqAheadDrops);
    }

    // The client keeps numbering inputs while its packets are lost, so the server's window covers an outage up to the
    // disconnect timeout: after it, the client's next input is still taken (a 64-tick window would drop it for good).
    [Fact]
    public void AnOutageUpToTheDisconnectTimeout_StaysWithinTheWindow()
    {
        var options = new ServerOptions();
        Assert.Equal(180, options.InputSeqWindow);   // 30 Hz * (5 s + 1 s)
        var buffer = new PlayerInputBuffer(8, options.InputSeqWindow);
        buffer.Add(Cmd(100));
        buffer.TryTake(out _);
        uint afterOutage = 100 + (uint)(options.SimHz * options.DisconnectTimeoutMs / 1000) + 3;
        Assert.True(buffer.Add(Cmd(afterOutage)));
        Assert.False(buffer.Add(Cmd(100 + (uint)options.InputSeqWindow + 1)));
        Assert.Equal(64, new ServerOptions { DisconnectTimeoutMs = 500, SimHz = 10, SnapshotEveryTicks = 1 }.InputSeqWindow);   // the floor
    }

    // Nothing taken yet (a new or resumed connection numbers from anywhere): the first input is not judged by the window.
    [Fact]
    public void AFirstInputWithAnySeq_IsAccepted()
    {
        var buffer = new PlayerInputBuffer(8);
        Assert.True(buffer.Add(Cmd(0x7FFFFFFF)));
        Assert.Equal(0, buffer.SeqAheadDrops);
    }

    [Fact]
    public void Empty_TryTake_ReturnsFalse()
    {
        var buffer = new PlayerInputBuffer(2);
        Assert.False(buffer.TryTake(out _));
        Assert.Equal(0u, buffer.LastTakenSeq);
    }
}
