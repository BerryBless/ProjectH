using ProjectH.Server.Diagnostics;
using ProjectH.Server.Net;
using Xunit;

namespace ProjectH.Server.Tests.Net;

// Review round 1: the Control channel holds a full per-IP connect burst on top of every player's messages, so one
// address's burst within one drain cannot get another player's Connected refused (and closed with ServerError).
public class ControlChannelTests
{
    [Theory]
    [InlineData(16, 20, 5, 108)]    // the defaults: 3 * (16 + 20)
    [InlineData(16, 0, 5, 48)]      // limit off: 3 per player as before
    [InlineData(16, 20, 0, 48)]
    [InlineData(100, 200, 5, 900)]  // the load test settings
    public void TheCapacity_IsThreePerPlayerAndPerBurstConnection(int maxPlayers, int burst, int perSecond, int capacity)
    {
        var options = new ServerOptions { MaxPlayers = maxPlayers, MinPlayers = 2, ConnectBurstPerIp = burst, ConnectsPerIpPerSecond = perSecond };
        Assert.Equal(capacity, options.ControlChannelCapacity);
    }

    // Every player connected, joined and left (3 each), and one address's whole burst did the same, all before one
    // drain: another peer's Connected still fits.
    [Fact]
    public void ABurstOfChurn_LeavesRoomForAnotherConnect()
    {
        var options = new ServerOptions();
        var channels = new InboundChannels(options, new ServerStats());
        var writer = channels.Control.Writer;
        int messages = 3 * (options.MaxPlayers - 1) + 3 * options.ConnectBurstPerIp;
        for (int i = 0; i < messages; i++)
            Assert.True(writer.TryWrite(new ControlMessage((ControlKind)(i % 3), i / 3, null!, null)), $"message {i + 1}");
        Assert.True(writer.TryWrite(new ControlMessage(ControlKind.Connected, 999, null!, "late")));
    }
}
