using ProjectH.Server.Diagnostics;
using Xunit;

namespace ProjectH.Server.Tests.Diagnostics;

public class DiagnosticsTests
{
    [Fact]
    public void Percentiles_UseNearestRank()
    {
        var metrics = new TickMetrics(128);
        for (int i = 1; i <= 100; i++) metrics.Record(i);

        TickStats stats = metrics.Compute();
        Assert.Equal(50, stats.P50);
        Assert.Equal(95, stats.P95);
        Assert.Equal(99, stats.P99);
        Assert.Equal(100, stats.Max);
        Assert.Equal(100, stats.SampleCount);
    }

    [Fact]
    public void Ring_KeepsOnlyNewestSamples()
    {
        var metrics = new TickMetrics(4);
        for (int i = 1; i <= 6; i++) metrics.Record(i);   // keeps 3, 4, 5, 6

        TickStats stats = metrics.Compute();
        Assert.Equal(4, stats.SampleCount);
        Assert.Equal(6, stats.Max);
        Assert.Equal(4, stats.P50);
    }

    [Fact]
    public void Empty_ReturnsZeroes_AndResetClears()
    {
        var metrics = new TickMetrics(4);
        Assert.Equal(0, metrics.Compute().SampleCount);
        metrics.Record(3);
        metrics.Reset();
        Assert.Equal(0, metrics.Compute().SampleCount);
    }

    [Fact]
    public void ServerStats_TakeDelta_ResetsCounters()
    {
        var stats = new ServerStats();
        stats.AddIn(10);
        stats.AddIn(5);
        stats.AddOut(7);
        stats.AddBadPacket();
        stats.AddInputDrop();

        StatsCounters first = stats.TakeDelta();
        Assert.Equal(2, first.PacketsIn);
        Assert.Equal(15, first.BytesIn);
        Assert.Equal(1, first.PacketsOut);
        Assert.Equal(7, first.BytesOut);
        Assert.Equal(1, first.BadPackets);
        Assert.Equal(1, first.InputDrops);

        StatsCounters second = stats.TakeDelta();
        Assert.Equal(0, second.PacketsIn);
        Assert.Equal(0, second.BytesIn);
    }
}
