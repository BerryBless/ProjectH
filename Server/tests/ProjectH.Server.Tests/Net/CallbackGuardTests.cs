using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Tests.Diagnostics;
using ProjectH.Server.Tests.Integration;
using ProjectH.Shared.Protocol;
using Xunit;
using static ProjectH.Server.Tests.Integration.HardeningIntegrationTests;

namespace ProjectH.Server.Tests.Net;

// Server review L9: an exception in a callback LiteNetLib or a timer calls must not escape into that thread (it serves
// every connection, or ends the process). It is counted (callbackErrors) and logged once per interval.
public class CallbackGuardTests
{
    [Fact]
    public void AThrowingNetworkErrorCallback_IsCounted_AndDoesNotThrow()
    {
        var log = new ListLogger();
        using var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestGameData.Create(), log);
        loop.Listener.CallbackFaultHook = _ => throw new InvalidOperationException("test fault");
        var from = new IPEndPoint(IPAddress.Loopback, 5000);
        loop.Listener.OnNetworkError(from, SocketError.ConnectionReset);
        loop.Listener.OnNetworkError(from, SocketError.ConnectionReset);
        Assert.Equal(2, loop.Health.CallbackErrors);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void AThrowingWatchdogCheck_IsCounted_AndLoggedOncePerInterval()
    {
        var time = new ManualTime();
        var log = new ListLogger();
        var health = new HealthCounters();
        using var watchdog = new StallWatchdog(() => throw new InvalidOperationException("test fault"), time, health, log);
        watchdog.Check();
        time.Advance(TimeSpan.FromSeconds(1));
        watchdog.Check();
        Assert.Equal(2, health.CallbackErrors);
        Assert.Single(log.Entries);
        time.Advance(TimeSpan.FromSeconds(10));   // the next interval
        watchdog.Check();
        Assert.Equal(3, health.CallbackErrors);
        Assert.Equal(2, log.Entries.Count);
    }

    // Thrown before Accept: the request is refused, and the server goes on taking connections.
    [Fact]
    public void AThrowingConnectionRequest_IsRefused_AndTheNextOneIsTaken()
    {
        using GameLoop server = StartServer();
        server.Listener.CallbackFaultHook = where => { if (where == "request") throw new InvalidOperationException("test fault"); };
        using var refused = new HeadlessClient();
        refused.Connect(server.LocalPort, "refused");
        Assert.True(Pump.Until(() => refused.Disconnected, 3000, refused), "refused");
        Assert.Equal(LiteNetLib.DisconnectReason.ConnectionRejected, refused.DisconnectReason);
        Assert.Equal(1, server.Health.CallbackErrors);

        server.Listener.CallbackFaultHook = null;
        using var a = Join(server, "a");
        Assert.False(a.Disconnected);
    }

    // Thrown after Accept: the accepted peer would hold a slot the game loop never hears of, so it is closed.
    [Fact]
    public void AConnectionThatThrowsAfterAccept_IsClosedWithServerError()
    {
        using GameLoop server = StartServer();
        server.Listener.CallbackFaultHook = where => { if (where == "accepted") throw new InvalidOperationException("test fault"); };
        using var c = new HeadlessClient();
        c.Connect(server.LocalPort, "c");
        Assert.True(Pump.Until(() => c.Disconnected, 3000, c), "closed");
        Assert.Equal(DisconnectCode.ServerError, c.DisconnectCode);
        Assert.Equal(1, server.Health.CallbackErrors);
        Assert.True(Pump.Until(() => server.Listener.Manager.ConnectedPeersCount == 0, 3000), "no orphan");
    }
}
