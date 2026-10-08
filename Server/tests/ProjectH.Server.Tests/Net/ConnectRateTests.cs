using System;
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

    // Review fix A2 (SEC-3): at most maxActive connections per address at once. TryAcquire only asks; the listener counts a
    // connection (Acquired) once Accept went through and gives it back (Release) when it disconnects.
    [Fact]
    public void AFifthConnectionFromOneAddress_IsRefused_AndAnotherAddressIsAccepted()
    {
        var limiter = new ConnectRateLimiter(burst: 20, perSecond: 5, maxActivePerAddress: 4, hashSalt: 0);
        for (int i = 0; i < 4; i++)
        {
            Assert.True(limiter.TryAcquire(A, 0, out ConnectRefusal ok), $"connection {i + 1}");
            Assert.Equal(ConnectRefusal.None, ok);
            limiter.Acquired(limiter.SlotOf(A));
        }
        Assert.False(limiter.TryAcquire(A, 0, out ConnectRefusal refusal));
        Assert.Equal(ConnectRefusal.PerIp, refusal);
        Assert.True(limiter.TryAcquire(B, 0, out _));

        limiter.Release(limiter.SlotOf(A));
        Assert.True(limiter.TryAcquire(A, 0, out _));
    }

    // TryAcquire alone never counts a connection: a request refused later (version, full) must not use up the address.
    [Fact]
    public void Asking_WithoutAcquired_DoesNotCountAConnection()
    {
        var limiter = new ConnectRateLimiter(burst: 20, perSecond: 5, maxActivePerAddress: 1, hashSalt: 0);
        for (int i = 0; i < 10; i++) Assert.True(limiter.TryAcquire(A, 0), $"request {i + 1}");
    }

    // Review A round 1 (confirmed): a connection is counted on the receive thread (Accept) and given back on whatever thread
    // ran OnPeerDisconnected (the game loop's Close, a LiteNetLib thread). Two threads counting and giving back on one slot
    // 10,000 times each lose no update: the count ends at 0, and the slot takes connections again.
    [Fact]
    public void AcquiredAndRelease_FromTwoThreads_LoseNoUpdate()
    {
        var limiter = new ConnectRateLimiter(burst: 0, perSecond: 0, maxActivePerAddress: 1, hashSalt: 0);
        int slot = limiter.SlotOf(A);
        const int Rounds = 10_000;
        using var start = new System.Threading.Barrier(2);
        void Run()
        {
            start.SignalAndWait();
            for (int i = 0; i < Rounds; i++)
            {
                limiter.Acquired(slot);
                limiter.Release(slot);
            }
        }
        var other = new System.Threading.Thread(Run);
        other.Start();
        Run();
        other.Join();

        Assert.Equal(0, limiter.ActiveOf(slot));
        Assert.True(limiter.TryAcquire(A, 0));
    }

    // The connection limit and the penalty work with the rate limit off (the table is always there).
    [Fact]
    public void TheConnectionLimit_WorksWithTheRateOff()
    {
        var limiter = new ConnectRateLimiter(burst: 0, perSecond: 0, maxActivePerAddress: 2, hashSalt: 0);
        Assert.False(limiter.Enabled);
        for (int i = 0; i < 2; i++)
        {
            Assert.True(limiter.TryAcquire(A, 0));
            limiter.Acquired(limiter.SlotOf(A));
        }
        Assert.False(limiter.TryAcquire(A, 0));
        limiter.Release(limiter.SlotOf(A));
        Assert.True(limiter.TryAcquire(A, 0));
        limiter.Release(limiter.SlotOf(B));   // nothing to give back: stays at zero, never negative
        limiter.Penalize(limiter.SlotOf(B), 100);
        Assert.False(limiter.TryAcquire(B, 50));
    }

    // Review fix A6: a penalized slot refuses every request until the deadline (whatever its tokens and connections).
    [Fact]
    public void APenalizedSlot_RefusesUntilTheDeadline()
    {
        var limiter = new ConnectRateLimiter(burst: 20, perSecond: 5, maxActivePerAddress: 4, hashSalt: 0);
        limiter.Penalize(limiter.SlotOf(A), 5000);
        Assert.False(limiter.TryAcquire(A, 4999, out ConnectRefusal refusal));
        Assert.Equal(ConnectRefusal.Penalty, refusal);
        Assert.True(limiter.TryAcquire(B, 4999));
        Assert.True(limiter.TryAcquire(A, 5000));
    }

    // Review fix A2 (SEC-4): the slot of an address depends on a salt made at startup, so which addresses share a slot
    // cannot be worked out from the code.
    [Fact]
    public void TheSalt_ChangesWhichSlotAnAddressGets()
    {
        var plain = new ConnectRateLimiter(20, 5, 4, hashSalt: 0);
        var salted = new ConnectRateLimiter(20, 5, 4, hashSalt: 0x9E3779B9);
        Assert.Equal(ConnectRateLimiter.SlotOf(A), plain.SlotOf(A));
        int moved = 0;
        for (int last = 1; last < 255; last++)
        {
            var address = new IPAddress(new byte[] { 10, 2, 3, (byte)last });
            if (plain.SlotOf(address) != salted.SlotOf(address)) moved++;
        }
        Assert.True(moved > 200, $"{moved} of 254 moved");
    }

    // Over UDP: a fifth connection from 127.0.0.1 is refused as ServerFull (counted as perIp); once one leaves, another gets in.
    [Fact]
    public void TheListener_RefusesAFifthConnectionFromOneAddress_UntilOneLeaves()
    {
        using var server = new GameLoop(new ServerOptions
        {
            Port = 0, MaxPlayers = 8, DisconnectTimeoutMs = 1000, StatsIntervalSeconds = 60, MaxConnectionsPerIp = 4,
        }, TestGameData.Create(), NullLogger.Instance);
        server.Start();
        var clients = new HeadlessClient[4];
        for (int i = 0; i < clients.Length; i++)
        {
            clients[i] = new HeadlessClient();
            clients[i].Connect(server.LocalPort, "c" + i);
        }
        try
        {
            Assert.True(Pump.Until(() => Array.TrueForAll(clients, c => c.Connected), 3000, clients), "four in");
            using var fifth = new HeadlessClient();
            fifth.Connect(server.LocalPort, "fifth");
            Assert.True(Pump.Until(() => fifth.Disconnected, 3000, fifth), "fifth refused");
            Assert.Equal(LiteNetLib.DisconnectReason.ConnectionRejected, fifth.DisconnectReason);
            Assert.Equal(RejectReason.ServerFull, fifth.RejectReason);
            Assert.Equal(1, server.Health.PerIpRejects);
            Assert.Equal(0, server.Health.Rejects(RejectReason.ServerFull));

            clients[0].Dispose();   // a clean disconnect: the server releases the address's connection
            Assert.True(SpinUntil(() => server.Health.DisconnectOthers == 1, 3000), "left");
            using var sixth = new HeadlessClient();
            sixth.Connect(server.LocalPort, "sixth");
            Assert.True(Pump.Until(() => sixth.Connected, 3000, sixth), "sixth in");
            Assert.Equal(5, server.Health.Connections);
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
        }
    }

    // 기능: 조건이 참이 될 때까지 기다린다.
    // 입력: condition - 조건, timeoutMs - 최대 대기 ms.
    // 출력: 시간 안에 참이 되면 true.
    private static bool SpinUntil(Func<bool> condition, int timeoutMs) => System.Threading.SpinWait.SpinUntil(condition, timeoutMs);

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
