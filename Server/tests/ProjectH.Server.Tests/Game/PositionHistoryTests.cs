using System.Numerics;
using ProjectH.Server.Game.Combat;
using Xunit;

namespace ProjectH.Server.Tests.Game;

public class PositionHistoryTests
{
    private static Vector3 X(float x) => new(x, 0f, 0f);

    [Fact]
    public void Sample_BetweenTicks_Interpolates()
    {
        var history = new PositionHistory();
        history.Reset(10, X(0f));
        history.Record(11, X(2f));

        Assert.Equal(1f, history.Sample(10.5).X, 5);
        Assert.Equal(2f, history.Sample(11).X, 5);
    }

    [Fact]
    public void Sample_PastNewest_HoldsNewest()
    {
        var history = new PositionHistory();
        history.Reset(10, X(0f));
        history.Record(11, X(2f));
        Assert.Equal(2f, history.Sample(50).X);
    }

    // Spec §5: not enough history -> the oldest record.
    [Fact]
    public void Sample_BeforeOldest_UsesOldest()
    {
        var history = new PositionHistory();
        history.Reset(10, X(5f));
        history.Record(11, X(6f));
        Assert.Equal(5f, history.Sample(3).X);
    }

    [Fact]
    public void Ring_KeepsNewest32_AndNeverGrows()
    {
        var history = new PositionHistory();
        history.Reset(1, X(1f));
        for (uint t = 2; t <= 100; t++) history.Record(t, X(t));

        Assert.Equal(PositionHistory.Capacity, history.Count);
        Assert.Equal(69f, history.Sample(0).X);    // oldest kept: tick 100 - 31
        Assert.Equal(100f, history.Sample(100).X);
    }

    [Fact]
    public void Reset_ForgetsOlderPositions()
    {
        var history = new PositionHistory();
        history.Reset(1, X(1f));
        for (uint t = 2; t <= 10; t++) history.Record(t, X(t));
        history.Reset(10, X(-7f));   // respawn: teleport

        Assert.Equal(1, history.Count);
        Assert.Equal(-7f, history.Sample(5).X);
    }

    [Fact]
    public void RepeatedOrOlderTick_IsIgnored()
    {
        var history = new PositionHistory();
        history.Reset(10, X(1f));
        history.Record(10, X(99f));
        history.Record(9, X(99f));
        Assert.Equal(1, history.Count);
        Assert.Equal(1f, history.Sample(10).X);
    }
}
