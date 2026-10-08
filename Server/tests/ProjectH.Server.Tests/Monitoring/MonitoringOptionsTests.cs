using ProjectH.Server.Monitoring;

namespace ProjectH.Server.Tests.Monitoring;

// Monitoring D6: the game server's "Monitoring" section, checked at start like ServerOptions.
public class MonitoringOptionsTests
{
    [Fact]
    public void Defaults_AreOff_AndValid()
    {
        var o = new MonitoringOptions();
        Assert.False(o.Enabled);
        Assert.Null(o.Validate());
        Assert.Equal(new Uri("http://127.0.0.1:5080/api/ingest/metrics"), o.IngestUri);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://127.0.0.1:5080")]
    [InlineData("")]
    public void Enabled_RequiresAnAbsoluteHttpEndpoint(string endpoint) =>
        Assert.NotNull(new MonitoringOptions { Enabled = true, Endpoint = endpoint }.Validate());

    [Fact]
    public void Disabled_DoesNotCheckTheEndpoint() => Assert.Null(new MonitoringOptions { Enabled = false, Endpoint = "not a url" }.Validate());

    [Theory]
    [InlineData("has space")]
    [InlineData("")]
    public void Enabled_RequiresAValidServerId(string id) => Assert.NotNull(new MonitoringOptions { Enabled = true, ServerId = id }.Validate());

    [Theory]
    [InlineData(0, 2)]
    [InlineData(61, 2)]
    [InlineData(5, 0)]
    [InlineData(5, 6)]    // timeout above the interval
    [InlineData(30, 31)]
    public void IntervalAndTimeout_AreBounded(int interval, int timeout) =>
        Assert.NotNull(new MonitoringOptions { IntervalSeconds = interval, TimeoutSeconds = timeout }.Validate());

    [Fact]
    public void TimeoutEqualToInterval_IsAllowed() => Assert.Null(new MonitoringOptions { IntervalSeconds = 2, TimeoutSeconds = 2 }.Validate());

    [Fact]
    public void IngestUri_AppendsThePath_WithOrWithoutATrailingSlash()
    {
        Assert.Equal("http://127.0.0.1:5080/api/ingest/metrics", new MonitoringOptions { Endpoint = "http://127.0.0.1:5080/" }.IngestUri.ToString());
        Assert.Equal("http://host:1/api/ingest/metrics", new MonitoringOptions { Endpoint = "http://host:1" }.IngestUri.ToString());
    }
}
