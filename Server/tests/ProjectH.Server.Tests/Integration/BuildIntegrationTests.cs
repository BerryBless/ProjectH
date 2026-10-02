using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Server.Diagnostics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;
using static ProjectH.Server.Tests.Integration.HardeningIntegrationTests;

namespace ProjectH.Server.Tests.Integration;

// Phase 13 D8, D13 over real UDP: building packets travel on channel 1 both ways, and a flood of build requests is
// counted as invalid packets.
public sealed class BuildIntegrationTests
{
    private static GameLoop StartServer()
    {
        var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4, DevRespawn = true, StatsIntervalSeconds = 60 },
            TestGameData.Create(), NullLogger.Instance, TestGameData.CombatLoadout);
        loop.Start();
        return loop;
    }

    [Fact]
    public void ABuildRequest_IsAnsweredOnTheBuildChannel()
    {
        using GameLoop server = StartServer();
        using HeadlessClient client = Join(server, "builder");
        // Not in build mode: refused, but answered, on channel 1.
        client.SendBuild(new BuildRequest { Sequence = 1, Piece = (byte)BuildPieceType.Wall, X = 16, Y = 0, Z = 16 });
        Assert.True(Pump.Until(() => client.BuildResults.Count == 1, 3000, client), "result");
        Assert.Equal(BuildResultCode.InvalidState, client.BuildResults[0].Code);
        Assert.True(Pump.Until(() => client.BuildPackets.Any(p => p.Id == PacketId.BuildInterest), 3000, client), "interest");
        Assert.All(client.BuildPackets.Where(p => p.Id != PacketId.BuildCatalog), p => Assert.Equal(ProtocolConstants.BuildChannel, p.Channel));
        Assert.Contains(client.BuildPackets, p => p.Id == PacketId.BuildCatalog && p.Channel == ProtocolConstants.ReliableChannel);
        Assert.Contains(client.BuildPackets, p => p.Id == PacketId.BuildSync);   // the join's reset
    }

    [Fact]
    public void AFullInboundBuildChannel_CountsTheDroppedRequests()
    {
        var health = new HealthCounters();
        var options = new ServerOptions { MaxPlayers = 1 };
        var channels = new ProjectH.Server.Net.InboundChannels(options, new ServerStats(), health.AddBuildInboxDrop);
        int capacity = options.MaxPlayers * ProjectH.Server.Game.Build.BuildRequestQueue.Capacity;
        for (int i = 0; i < capacity + 3; i++)
            Assert.True(channels.Build.Writer.TryWrite(default));
        Assert.Equal(3, health.BuildInboxDrops);
    }

    [Fact]
    public void MoreBuildRequestsThanTheLimit_AreInvalidPackets()
    {
        using GameLoop server = StartServer();
        using HeadlessClient client = Join(server, "spammer");
        for (int i = 0; i < 25; i++) client.SendBuild(new BuildRequest { Sequence = (ushort)(i + 1), Piece = 9 });
        Assert.True(Pump.Until(() => server.Health.BadPackets(BadPacketReason.BuildRate) >= 5, 3000, client), "rate");
        Assert.False(client.Disconnected);   // 5 is below the kick threshold
    }
}
