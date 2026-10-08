using System;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Tests.Integration;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;
using static ProjectH.Server.Tests.Integration.HardeningIntegrationTests;

namespace ProjectH.Server.Tests.Net;

// Review fix A1 (SEC-1): the server takes no client packet larger than ProtocolLimits.MaxClientPacketBytes (Malformed,
// counted toward the kick, never parsed), and a Join carries no body.
public class PacketSizeLimitTests
{
    [Fact]
    public void AClientPacketOver128Bytes_IsMalformed()
    {
        using GameLoop server = StartServer();
        using var client = Join(server, "big");

        var packet = new PlayerInputPacket { Count = 3 };
        for (int i = 0; i < 3; i++) packet.Set(i, new InputCommand { Seq = (uint)(1 + i) });
        var bytes = new byte[PlayerInputPacket.MaxSize + 200];
        var writer = new PacketWriter(bytes);
        PlayerInputPacket.Write(ref writer, packet);
        Assert.Equal(PlayerInputPacket.MaxSize, writer.Length);
        client.SendRaw(bytes);

        Assert.True(Pump.Until(() => server.Health.BadPackets(BadPacketReason.Malformed) == 1, 3000, client), "malformed");
        Pump.Until(() => false, 200, client);
        Assert.False(client.Disconnected);
        Assert.Equal(0, server.Health.BadPackets(BadPacketReason.UnknownId));
    }

    [Fact]
    public void TheServer_ReassemblesAtMostTwoFragments()
    {
        using var server = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestGameData.Create(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.Equal(ProtocolLimits.MaxFragments, server.Listener.Manager.MaxFragmentsCount);
    }

    [Fact]
    public void AJoinWithABody_IsMalformed_AndDoesNotJoin()
    {
        using GameLoop server = StartServer();
        using var client = new HeadlessClient();
        client.Connect(server.LocalPort, "joinbody");
        Assert.True(Pump.Until(() => client.Connected, 3000, client), "connect");
        client.SendRaw(new byte[] { (byte)PacketId.JoinMatchRequest, 0 });

        Assert.True(Pump.Until(() => server.Health.BadPackets(BadPacketReason.Malformed) == 1, 3000, client), "malformed");
        Pump.Until(() => false, 200, client);
        Assert.Null(client.JoinResponse);
        Assert.Equal(0, server.Health.Joins);

        client.SendJoin();   // the join was not taken, so a correct one still goes through
        Assert.True(Pump.Until(() => client.JoinResponse.HasValue, 3000, client), "join");
        Assert.Equal(JoinResult.Ok, client.JoinResponse?.Result);
    }
}
