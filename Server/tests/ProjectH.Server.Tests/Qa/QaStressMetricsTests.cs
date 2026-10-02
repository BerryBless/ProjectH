using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Qa;
using Xunit;

namespace ProjectH.Server.Tests.Qa;

// Stress runs (D36): byte rates, memory and GC numbers, and the match counters in /qa/metrics.
public sealed class QaStressMetricsTests
{
    [Fact]
    public void ServerStats_ByteTotals_SurviveTheStatsLine()
    {
        var stats = new ServerStats();
        stats.AddIn(100);
        stats.AddOut(40);
        stats.AddOut(60);
        Assert.Equal(100, stats.BytesInTotal);
        Assert.Equal(100, stats.BytesOutTotal);
        stats.TakeDelta();   // the periodic stats line resets the interval counters, not the totals
        stats.AddIn(5);
        Assert.Equal(105, stats.BytesInTotal);
        Assert.Equal(100, stats.BytesOutTotal);
        Assert.Equal(2, stats.PacketsInTotal);
        Assert.Equal(2, stats.PacketsOutTotal);
    }

    [Fact]
    public void ByteRates_UseTheSameWindowAsPacketRates()
    {
        var metrics = new QaMetrics(simHz: 30);
        metrics.SampleSecond(new QaNetTotals(0, 0, 0, 0));
        Thread.Sleep(100);
        QaTickMetrics m = metrics.Snapshot(10, new QaNetTotals(10, 20, 1000, 2000));
        Assert.True(m.RateSeconds > 0.05);
        Assert.Equal(1000 / m.RateSeconds, m.BytesInPerSec, 3);
        Assert.Equal(2000 / m.RateSeconds, m.BytesOutPerSec, 3);
        Assert.Equal(10 / m.RateSeconds, m.PktInPerSec, 3);
        Assert.Equal(0, new QaMetrics(30).Snapshot(10, default).BytesInPerSec);
    }

    [Fact]
    public async Task Metrics_HaveTheStressKeys()
    {
        using var h = new QaHarness();
        h.StartMatch();
        await h.Command("spawnBuildPiece", args: new { piece = "floor", material = "wood", cellX = 16, level = 0, cellZ = 16 });
        h.Ticks(2);
        var (status, body) = await h.Run(t => QaResult.Data(QaHttpService.Metrics(t, 10)));
        Assert.Equal(200, status);
        foreach (string key in new[] { "bytesInPerSec", "bytesOutPerSec", "managedMB", "allocatedMBTotal", "gcPauseMsTotal", "workingSetMB",
                     "pktInPerSec", "pktOutPerSec", "tickP99Ms", "dbQueueLength" })
            Assert.Equal(JsonValueKind.Number, body.GetProperty(key).ValueKind);
        Assert.True(body.GetProperty("managedMB").GetDouble() > 0);
        Assert.True(body.GetProperty("allocatedMBTotal").GetDouble() > 0);
        Assert.Equal(JsonValueKind.Number, body.GetProperty("gc").GetProperty("gen2").ValueKind);
        Assert.Equal(2, body.GetProperty("players").GetInt32());
        Assert.Equal(2, body.GetProperty("alive").GetInt32());
        Assert.Equal(1, body.GetProperty("buildPieces").GetInt32());
        Assert.Equal(0, body.GetProperty("stalls").GetInt64());
        Assert.Equal(0, body.GetProperty("health").GetProperty("stalls").GetInt64());
        Assert.Equal(JsonValueKind.Number, body.GetProperty("ticks").GetProperty("bytesInPerSec").ValueKind);
    }
}
