namespace ProjectH.Monitoring.Tests;

public class MonitoringServerOptionsTests
{
    [Fact]
    public void Defaults_AreValid_AndHold120Samples()
    {
        var o = new MonitoringServerOptions();
        Assert.Null(o.Validate());
        Assert.Equal(120, o.HistoryCapacity);   // 10 min × 60 / 5 s
        Assert.Equal(0, o.MemoryWarningBytes);  // off until measured (D11)
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void Validate_RejectsHistoryMinutesOutOfRange(int minutes) => Assert.NotNull(new MonitoringServerOptions { HistoryMinutes = minutes }.Validate());

    [Fact]
    public void Validate_RequiresOfflineThresholdOfAtLeastTwoIntervals()
    {
        Assert.NotNull(new MonitoringServerOptions { ExpectedIntervalSeconds = 5, OfflineThresholdSeconds = 9 }.Validate());
        Assert.Null(new MonitoringServerOptions { ExpectedIntervalSeconds = 5, OfflineThresholdSeconds = 10 }.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void Validate_RejectsMaxServersOutOfRange(int max) => Assert.NotNull(new MonitoringServerOptions { MaxServers = max }.Validate());

    [Fact]
    public void Validate_RejectsNegativeThresholds()
    {
        Assert.NotNull(new MonitoringServerOptions { TickP95WarningMs = -1 }.Validate());
        Assert.NotNull(new MonitoringServerOptions { MemoryWarningBytes = -1 }.Validate());
        Assert.NotNull(new MonitoringServerOptions { MaxClockSkewSeconds = 0 }.Validate());
        Assert.NotNull(new MonitoringServerOptions { SweepIntervalSeconds = 0 }.Validate());
    }

    [Fact]
    public void HistoryCapacity_IsAtLeastOne()
    {
        Assert.Equal(2, new MonitoringServerOptions { HistoryMinutes = 1, ExpectedIntervalSeconds = 30, OfflineThresholdSeconds = 60 }.HistoryCapacity);
        Assert.Equal(1, new MonitoringServerOptions { HistoryMinutes = 1, ExpectedIntervalSeconds = 60, OfflineThresholdSeconds = 120 }.HistoryCapacity);
    }
}
