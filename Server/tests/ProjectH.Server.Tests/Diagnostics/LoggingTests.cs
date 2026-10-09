using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using ProjectH.Server.Tests.Integration;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Diagnostics;

// Server review M1: a console that does not take output (a selected QuickEdit window, a pipe nobody reads) must not stop
// the game loop or LiteNetLib's thread, and events that repeat with every connection are not logged above Debug.
public class LoggingTests
{
    // The host Program runs, built but never started (it would bind the configured port).
    [Fact]
    public void TheServerHost_DropsConsoleLogsWhenTheQueueIsFull()
    {
        using IHost host = ServerHost.CreateBuilder(Array.Empty<string>()).Build();
        ConsoleLoggerOptions console = host.Services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value;
        Assert.Equal(ConsoleLoggerQueueFullMode.DropWrite, console.QueueFullMode);
    }

    [Fact]
    public void TheShippedSettings_SayDropWrite()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile(System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build();
        Assert.Equal("DropWrite", config["Logging:Console:QueueFullMode"]);
    }

    // Connect, join, a join timeout and a disconnect: counted in the Health line, logged at Debug only.
    [Fact]
    public void ConnectionEvents_LogNothingAboveDebug()
    {
        var log = new ListLogger();
        using var server = new GameLoop(new ServerOptions
        {
            Port = 0,
            MaxPlayers = 4,
            DisconnectTimeoutMs = 1000,
            StatsIntervalSeconds = 60,
            JoinTimeoutSeconds = 1,
        }, TestGameData.Create(), log);
        server.Start();

        var a = HardeningIntegrationTests.Join(server, "a");
        using var idle = new HeadlessClient();
        idle.Connect(server.LocalPort, "idle");
        Assert.True(Pump.Until(() => idle.Disconnected, 4000, idle, a), "join timeout");
        Assert.Equal(DisconnectCode.JoinTimeout, idle.DisconnectCode);
        a.Dispose();
        Assert.True(SpinWaitUntil(() => server.Health.DisconnectOthers + server.Health.DisconnectTimeouts >= 2, 3000), "disconnects seen");

        Assert.Equal(1, server.Health.Joins);
        Assert.Equal(2, server.Health.Connections);
        var loud = log.Entries.Where(e => e.Level >= LogLevel.Information && !e.Message.StartsWith("Server listening")).ToList();
        Assert.True(loud.Count == 0, string.Join("\n", loud.Select(e => $"{e.Level}: {e.Message}")));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("join: Ok"));
    }

    // A socket error is counted every time and logged once per stats interval, like a receive-handler exception.
    [Fact]
    public void NetworkErrors_AreCounted_AndLoggedOncePerInterval()
    {
        var log = new ListLogger();
        using var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestGameData.Create(), log);
        var from = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 5000);
        int Logged() => log.Entries.Count(e => e.Message.StartsWith("Network error"));

        for (int i = 0; i < 5; i++) loop.Listener.OnNetworkError(from, System.Net.Sockets.SocketError.ConnectionReset);
        Assert.Equal(5, loop.Health.NetworkErrors);
        Assert.Equal(1, Logged());

        loop.LogPeriodic();   // a new interval
        loop.Listener.OnNetworkError(from, System.Net.Sockets.SocketError.ConnectionReset);
        Assert.Equal(6, loop.Health.NetworkErrors);
        Assert.Equal(2, Logged());
    }

    // 기능: 조건이 참이 될 때까지 제한 시간 안에서 Spin 대기한다.
    // 입력: condition - 기다릴 조건, timeoutMs - 최대 대기 시간.
    // 출력: 제한 시간 안에 조건이 참이 되면 true.
    private static bool SpinWaitUntil(Func<bool> condition, int timeoutMs) => System.Threading.SpinWait.SpinUntil(condition, timeoutMs);
}
