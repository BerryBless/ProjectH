using System;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Net;
using Xunit;

namespace ProjectH.Server.Tests.Net;

// Review rounds 1 and 2, review fix A2: the Control channel holds every player's messages plus every connection the
// global accept bucket can let in before the next drain (its burst, AcceptBurst = MaxPlayers, and the tokens that refill
// within one tick). So churn from any number of addresses within one drain cannot get another player's Connected refused
// (and closed with ServerError). The per-IP bucket no longer sizes it: the global bucket bounds every address together.
public class ControlChannelTests
{
    [Theory]
    [InlineData(16, 20, 30, 99)]       // the defaults: 3 * (16 + 16 + ceil(20 / 30))
    [InlineData(100, 20, 30, 603)]     // 100 players: 3 * (100 + 100 + 1)
    [InlineData(16, 1000, 10, 396)]    // a high rate at a low tick rate: 3 * (16 + 16 + 100)
    [InlineData(2, 1, 30, 15)]         // the smallest: 3 * (2 + 2 + 1)
    public void TheCapacity_IsThreePerPlayer_PerAcceptBurst_AndPerTickRefill(int maxPlayers, int acceptsPerSecond, int simHz, int capacity)
    {
        var options = new ServerOptions
        {
            MaxPlayers = maxPlayers, MinPlayers = 2, SimHz = simHz, SnapshotEveryTicks = 1, AcceptsPerSecond = acceptsPerSecond,
        };
        Assert.Null(options.Validate());
        Assert.Equal(capacity, options.ControlChannelCapacity);
    }

    // Every player connected, joined and left (3 each), and the global accept burst and one tick's refill did the same,
    // all before one drain: another peer's Connected still fits.
    [Theory]
    [InlineData(20, 30)]      // the defaults
    [InlineData(1000, 10)]    // a high rate at a low tick rate: 100 tokens come back within one tick
    public void ABurstOfChurn_AndItsRefill_LeaveRoomForAnotherConnect(int acceptsPerSecond, int simHz)
    {
        var options = new ServerOptions { SimHz = simHz, SnapshotEveryTicks = 1, AcceptsPerSecond = acceptsPerSecond };
        Assert.Null(options.Validate());
        var channels = new InboundChannels(options, new ServerStats());
        var writer = channels.Control.Writer;
        int perTick = (acceptsPerSecond + simHz - 1) / simHz;
        int messages = 3 * (options.MaxPlayers - 1) + 3 * (options.AcceptBurst + perTick);
        for (int i = 0; i < messages; i++)
            Assert.True(writer.TryWrite(new ControlMessage((ControlKind)(i % 3), i / 3, null!, null)), $"message {i + 1}");
        Assert.True(writer.TryWrite(new ControlMessage(ControlKind.Connected, 999, null!, "late")));
    }

    // The accept bucket really bounds what the capacity assumes: AcceptBurst at once, then AcceptsPerSecond.
    [Fact]
    public void TheAcceptBucket_LetsTheBurstThenTheRate()
    {
        var accept = new AcceptRateLimiter(burst: 3, perSecond: 2);
        for (int i = 0; i < 3; i++) Assert.True(accept.TryAcquire(1000), $"accept {i + 1}");
        Assert.False(accept.TryAcquire(1000));
        Assert.False(accept.TryAcquire(1499));   // 2 per second: one every 500 ms
        Assert.True(accept.TryAcquire(1500));
        Assert.False(accept.TryAcquire(1500));
        int taken = 0;
        for (int i = 0; i < 100; i++) if (accept.TryAcquire(100_000)) taken++;   // a long pause refills up to the burst only
        Assert.Equal(3, taken);
    }
}
