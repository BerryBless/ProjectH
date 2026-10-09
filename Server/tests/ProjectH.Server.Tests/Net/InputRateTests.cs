using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Net;
using ProjectH.Server.Tests.Integration;
using ProjectH.Shared.Protocol;
using Xunit;
using static ProjectH.Server.Tests.Integration.HardeningIntegrationTests;

namespace ProjectH.Server.Tests.Net;

// Server review M5, L5: inputs over the rate are dropped and counted (inputRate) but never kick, and the rate is a token
// bucket (SimHz * 2 per second, burst SimHz) instead of a fixed one-second window.
public class InputRateTests
{
    [Fact]
    public void TheBucket_StartsFull_AtAnyClock()
    {
        var state = new PeerState("p");
        for (int i = 0; i < 30; i++) Assert.True(state.TryCountInputPacket(0, 60, 30), $"input {i + 1}");
        Assert.False(state.TryCountInputPacket(0, 60, 30));
    }

    [Fact]
    public void TheBucket_RefillsAtTheRate_UpToTheBurst()
    {
        var state = new PeerState("p");
        for (int i = 0; i < 30; i++) state.TryCountInputPacket(5000, 60, 30);
        Assert.False(state.TryCountInputPacket(5000, 60, 30));
        Assert.False(state.TryCountInputPacket(5016, 60, 30));   // 60 per second: one every 16.7 ms
        Assert.True(state.TryCountInputPacket(5017, 60, 30));
        Assert.False(state.TryCountInputPacket(5017, 60, 30));

        // A fixed window allowed 2 x 60 back to back across its edge; the bucket allows the burst, then the rate.
        int taken = 0;
        for (int i = 0; i < 200; i++) if (state.TryCountInputPacket(10_000, 60, 30)) taken++;
        Assert.Equal(30, taken);
    }

    [Fact]
    public void ANormalFlow_WithTwoBurstsOfSeventy_IsNeverKicked()
    {
        using GameLoop server = StartServer();
        using var a = Join(server, "a");
        Flow(a, 30);
        for (int i = 0; i < 70; i++) a.SendMove(0f, 0f, 0f);   // a link that held two seconds of input
        Flow(a, 30);
        for (int i = 0; i < 70; i++) a.SendMove(0f, 0f, 0f);
        Flow(a, 30);

        Pump.Until(() => false, 200, a);
        Assert.Equal(0, server.Health.Kicks(DisconnectCode.Kicked));
        Assert.False(a.Disconnected);
        Assert.True(Pump.Until(() => server.Health.BadPackets(BadPacketReason.InputRate) >= 40, 3000, a), "dropped and counted");
    }

    [Fact]
    public void ASustainedFlood_IsDropped_AndCounted_ButNotKicked()
    {
        using GameLoop server = StartServer();
        using var a = Join(server, "a");
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 2000)
        {
            for (int i = 0; i < 4; i++) a.SendMove(0f, 0f, 0f);   // about 120 per second
            Pump.Until(() => false, 33, a);
        }
        Pump.Until(() => false, 200, a);
        Assert.Equal(0, server.Health.Kicks(DisconnectCode.Kicked));
        Assert.False(a.Disconnected);
        Assert.True(Pump.Until(() => server.Health.BadPackets(BadPacketReason.InputRate) >= 60, 3000, a), "dropped and counted");
    }

    [Fact]
    public void TwentyMalformedInputs_StillKick()
    {
        using GameLoop server = StartServer();
        using var a = Join(server, "a");
        for (int i = 0; i < 20; i++) a.SendRaw(new byte[] { (byte)PacketId.PlayerInput, 0 });
        Assert.True(Pump.Until(() => a.Disconnected, 3000, a), "kicked");
        Assert.Equal(DisconnectCode.Kicked, a.DisconnectCode);
        Assert.Equal(20, server.Health.BadPackets(BadPacketReason.Malformed));
    }

    // Review fix A5 (SEC-18): the shared Input channel holds every player's whole burst at once, so connections spending
    // their bursts together never push another player's input out (DropOldest).
    [Fact]
    public void TheInputChannel_HoldsEveryPlayersBurst()
    {
        var options = new ServerOptions();
        Assert.Equal(options.MaxPlayers * options.InputBurst, options.InputChannelCapacity);
        var stats = new ServerStats();
        var channels = new InboundChannels(options, stats);
        for (int i = 0; i < options.MaxPlayers * options.InputBurst; i++)
            Assert.True(channels.Input.Writer.TryWrite(new InputMessage(i % options.MaxPlayers, null!, default)));
        Assert.Equal(0, stats.TakeDelta().InputDrops);
    }

    // Review fix A5: the same for the Build channel: every player's requests of two back-to-back one-second windows
    // (the listener counts building requests in fixed windows, so 2 x maxRequestsPerSecond can arrive at once).
    [Fact]
    public void TheBuildChannel_HoldsTwoWindowsOfEveryPlayersRequests()
    {
        var options = new ServerOptions();
        int dropped = 0;
        const int perSecond = 20;
        var channels = new InboundChannels(options, new ServerStats(), () => dropped++, buildRequestsPerSecond: perSecond);
        for (int i = 0; i < options.MaxPlayers * 2 * perSecond; i++)
            Assert.True(channels.Build.Writer.TryWrite(new BuildMessage(i % options.MaxPlayers, null!, new ProjectH.Server.Game.Build.BuildQueueItem(new BuildRequest()))));
        Assert.Equal(0, dropped);
        channels.Build.Writer.TryWrite(new BuildMessage(0, null!, new ProjectH.Server.Game.Build.BuildQueueItem(new BuildRequest())));
        Assert.Equal(1, dropped);   // still bounded: one more pushes the oldest out
    }

    // 기능: 주어진 Tick 수만큼 약 33ms마다 정지 입력 하나를 보내며 Poll한다.
    // 입력: client - 입력을 보낼 Client, ticks - 보낼 입력 수.
    // 출력: 반환값 없음. 입력 패킷 ticks개가 나간다.
    // One input per tick for the given number of ticks.
    private static void Flow(HeadlessClient client, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            client.SendMove(0f, 0f, 0f);
            Pump.Until(() => false, 33, client);
        }
    }
}
