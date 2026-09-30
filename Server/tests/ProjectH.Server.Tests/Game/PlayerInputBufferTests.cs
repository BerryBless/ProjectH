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

    [Fact]
    public void Empty_TryTake_ReturnsFalse()
    {
        var buffer = new PlayerInputBuffer(2);
        Assert.False(buffer.TryTake(out _));
        Assert.Equal(0u, buffer.LastTakenSeq);
    }
}
