using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Server.Net;
using ProjectH.Server.Tests.Integration;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Net;

// Server review M2: connection requests per remote IP, a token bucket in a fixed table.
public class ConnectRateTests
{
    private static readonly IPAddress A = IPAddress.Parse("10.0.0.1");
    private static readonly IPAddress B = IPAddress.Parse("10.0.0.2");

    [Fact]
    public void TheTwentyFirstRequestAtOnce_IsRefused_AndARefillLetsOneIn()
    {
        var limiter = new ConnectRateLimiter(burst: 20, perSecond: 5);
        long now = 1000;
        for (int i = 0; i < 20; i++) Assert.True(limiter.TryAcquire(A, now), $"request {i + 1}");
        Assert.False(limiter.TryAcquire(A, now));
        Assert.False(limiter.TryAcquire(A, now + 199));   // 5 per second: one every 200 ms
        Assert.True(limiter.TryAcquire(A, now + 200));
        Assert.False(limiter.TryAcquire(A, now + 200));

        now += 200 + 10_000;                                // a long pause fills the bucket up to the burst, not past it
        for (int i = 0; i < 20; i++) Assert.True(limiter.TryAcquire(A, now), $"after the pause {i + 1}");
        Assert.False(limiter.TryAcquire(A, now));
    }

    [Fact]
    public void AnotherIp_HasItsOwnBucket()
    {
        Assert.NotEqual(ConnectRateLimiter.SlotOf(A), ConnectRateLimiter.SlotOf(B));   // else this would test the overwrite
        var limiter = new ConnectRateLimiter(burst: 20, perSecond: 5);
        for (int i = 0; i < 20; i++) limiter.TryAcquire(A, 0);
        Assert.False(limiter.TryAcquire(A, 0));
        for (int i = 0; i < 20; i++) Assert.True(limiter.TryAcquire(B, 0), $"B {i + 1}");
        Assert.False(limiter.TryAcquire(A, 0));
    }

    // Two IPs in one slot: the newer one takes the slot over with the tokens left in it (review round 1), so alternating
    // two colliding addresses gets no more than one burst. The table never grows.
    [Fact]
    public void ACollision_TakesTheSlotOver_WithTheTokensLeft()
    {
        IPAddress other = A;
        for (int last = 2; last < 255 * 255; last++)
        {
            var candidate = new IPAddress(new byte[] { 10, 1, (byte)(last / 255), (byte)(last % 255) });
            if (ConnectRateLimiter.SlotOf(candidate) == ConnectRateLimiter.SlotOf(A) && !candidate.Equals(A))
            {
                other = candidate;
                break;
            }
        }
        Assert.NotEqual(A, other);
        var limiter = new ConnectRateLimiter(burst: 2, perSecond: 1);
        Assert.True(limiter.TryAcquire(A, 0));
        Assert.True(limiter.TryAcquire(A, 0));
        Assert.False(limiter.TryAcquire(A, 0));
        Assert.False(limiter.TryAcquire(other, 0));   // the slot is empty: no fresh bucket for the other address
        Assert.False(limiter.TryAcquire(A, 0));

        int taken = 0;
        for (int i = 0; i < 100; i++)
        {
            if (limiter.TryAcquire(i % 2 == 0 ? A : other, 2000 + i)) taken++;   // 2 s: 2 tokens back
        }
        Assert.Equal(2, taken);
        Assert.True(limiter.TryAcquire(other, 3100));  // the refill still goes on, at the rate
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(20, 0)]
    public void Zero_TurnsItOff(int burst, int perSecond)
    {
        var limiter = new ConnectRateLimiter(burst, perSecond);
        Assert.False(limiter.Enabled);
        for (int i = 0; i < 1000; i++) Assert.True(limiter.TryAcquire(A, 0));
    }

    // Over UDP: the third request from 127.0.0.1 with a burst of 2 is refused as ServerFull and counted as connectRate.
    [Fact]
    public void TheListener_RefusesTheRequestOverTheRate_AsServerFull()
    {
        using var server = new GameLoop(new ServerOptions
        {
            Port = 0, MaxPlayers = 8, DisconnectTimeoutMs = 1000, StatsIntervalSeconds = 60,
            ConnectBurstPerIp = 2, ConnectsPerIpPerSecond = 1,
        }, TestGameData.Create(), NullLogger.Instance);
        server.Start();
        using var first = new HeadlessClient();
        first.Connect(server.LocalPort, "first");
        using var second = new HeadlessClient();
        second.Connect(server.LocalPort, "second");
        Assert.True(Pump.Until(() => first.Connected && second.Connected, 3000, first, second), "two in");
        using var third = new HeadlessClient();
        third.Connect(server.LocalPort, "third");
        Assert.True(Pump.Until(() => third.Disconnected, 3000, third, first, second), "third refused");

        Assert.Equal(LiteNetLib.DisconnectReason.ConnectionRejected, third.DisconnectReason);
        Assert.Equal(RejectReason.ServerFull, third.RejectReason);
        Assert.Equal(1, server.Health.ConnectRateRejects);
        Assert.Equal(0, server.Health.Rejects(RejectReason.ServerFull));
        Assert.Equal(2, server.Health.Connections);
    }
}
