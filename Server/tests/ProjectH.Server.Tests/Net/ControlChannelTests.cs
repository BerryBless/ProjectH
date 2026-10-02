using System;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Net;
using Xunit;

namespace ProjectH.Server.Tests.Net;

// Review rounds 1 and 2: the Control channel holds a full per-IP connect burst, plus the tokens that refill within one
// drain (one tick), on top of every player's messages. So one address's churn within one drain cannot get another
// player's Connected refused (and closed with ServerError).
public class ControlChannelTests
{
    [Theory]
    [InlineData(16, 20, 5, 30, 111)]       // the defaults: 3 * (16 + 20 + ceil(5 / 30))
    [InlineData(16, 0, 5, 30, 48)]         // limit off: 3 per player as before
    [InlineData(16, 20, 0, 30, 48)]
    [InlineData(100, 200, 5, 30, 903)]     // the load test settings
    [InlineData(16, 20, 1000, 10, 408)]    // a high rate at a low tick rate: 3 * (16 + 20 + 100)
    public void TheCapacity_IsThreePerPlayer_PerBurstConnection_AndPerTickRefill(int maxPlayers, int burst, int perSecond, int simHz, int capacity)
    {
        var options = new ServerOptions
        {
            MaxPlayers = maxPlayers, MinPlayers = 2, SimHz = simHz, SnapshotEveryTicks = 1,
            ConnectBurstPerIp = burst, ConnectsPerIpPerSecond = perSecond,
        };
        Assert.Equal(capacity, options.ControlChannelCapacity);
    }

    // Every player connected, joined and left (3 each), and one address's whole burst and one tick's refill did the
    // same, all before one drain: another peer's Connected still fits.
    [Theory]
    [InlineData(5, 30)]       // the defaults
    [InlineData(1000, 10)]    // a high rate at a low tick rate: 100 tokens come back within one tick
    public void ABurstOfChurn_AndItsRefill_LeaveRoomForAnotherConnect(int perSecond, int simHz)
    {
        var options = new ServerOptions { SimHz = simHz, SnapshotEveryTicks = 1, ConnectsPerIpPerSecond = perSecond };
        Assert.Null(options.Validate());
        var channels = new InboundChannels(options, new ServerStats());
        var writer = channels.Control.Writer;
        int perTick = (perSecond + simHz - 1) / simHz;
        int messages = 3 * (options.MaxPlayers - 1) + 3 * (options.ConnectBurstPerIp + perTick);
        for (int i = 0; i < messages; i++)
            Assert.True(writer.TryWrite(new ControlMessage((ControlKind)(i % 3), i / 3, null!, null)), $"message {i + 1}");
        Assert.True(writer.TryWrite(new ControlMessage(ControlKind.Connected, 999, null!, "late")));
    }
}
