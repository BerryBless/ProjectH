using System;
using System.Net.Sockets;
using System.Numerics;
using LiteNetLib;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Integration;

public sealed class ServerIntegrationTests : IDisposable
{
    private readonly GameLoop _server = StartServer(maxPlayers: 4);

    private static GameLoop StartServer(int maxPlayers)
    {
        var loop = new GameLoop(new ServerOptions
        {
            Port = 0,                 // OS picks a free port: tests can run in parallel
            MaxPlayers = maxPlayers,
            MinPlayers = 2,   // Phase 5: Validate needs 2 <= MinPlayers <= MaxPlayers, so a test server holds at least 2
            DisconnectTimeoutMs = 1000,
            StatsIntervalSeconds = 60,
            // Phase 10 D3: FullMatch connects 100 clients before any of them joins; on a slow machine that can take
            // longer than the 5 s default.
            JoinTimeoutSeconds = 30,
        }, TestGameData.Create(), NullLogger.Instance);
        loop.Start();
        return loop;
    }

    public void Dispose() => _server.Dispose();

    private static HeadlessClient Join(GameLoop server, string devId)
    {
        var client = new HeadlessClient();
        client.Connect(server.LocalPort, devId);
        Assert.True(Pump.Until(() => client.Connected, 3000, client), "connect");
        client.SendJoin();
        Assert.True(Pump.Until(() => client.JoinResponse.HasValue, 3000, client), "join response");
        Assert.Equal(JoinResult.Ok, client.JoinResponse?.Result);
        return client;
    }

    [Fact]
    public void TwoClients_ReceiveEachOthersSpawn()
    {
        using var a = Join(_server, "a");
        using var b = Join(_server, "b");

        Assert.True(Pump.Until(() =>
            a.Spawned.Contains(a.MyEntityId) && a.Spawned.Contains(b.MyEntityId) &&
            b.Spawned.Contains(a.MyEntityId) && b.Spawned.Contains(b.MyEntityId), 3000, a, b));
    }

    [Fact]
    public void Movement_IsVisibleToOtherClient_AndAckAdvances()
    {
        using var a = Join(_server, "a");
        using var b = Join(_server, "b");
        Assert.True(Pump.Until(() => b.LastSnapshot.ContainsKey(a.MyEntityId), 3000, a, b));
        Vector3 start = b.LastSnapshot[a.MyEntityId].Position;

        for (int i = 0; i < 45; i++)
        {
            a.SendMove(0f, 1f, 0f);
            Pump.Until(() => false, 33, a, b);
        }

        Assert.True(Pump.Until(() => Vector3.Distance(b.LastSnapshot[a.MyEntityId].Position, start) > 1f, 3000, a, b));
        Assert.True(a.LastAckInputSeq > 0);
    }

    [Fact]
    public void Disconnect_DespawnsForOthers()
    {
        var a = Join(_server, "a");
        using var b = Join(_server, "b");
        ushort aId = a.MyEntityId;

        a.Dispose();
        Assert.True(Pump.Until(() => b.Despawned.Contains(aId), 3000, b));
    }

    [Fact]
    public void CrashedClient_IsDespawnedAfterTimeout()
    {
        var a = Join(_server, "a");
        using var b = Join(_server, "b");
        ushort aId = a.MyEntityId;

        a.Kill();
        Assert.True(Pump.Until(() => b.Despawned.Contains(aId), 5000, b));
    }

    [Fact]
    public void VersionMismatch_IsRejected()
    {
        using var c = new HeadlessClient();
        c.Connect(_server.LocalPort, "old", protocolVersion: 999);
        Assert.True(Pump.Until(() => c.Disconnected, 3000, c));
        Assert.Equal(DisconnectReason.ConnectionRejected, c.DisconnectReason);
        Assert.Equal(RejectReason.VersionMismatch, c.RejectReason);
    }

    [Fact]
    public void ServerFull_IsRejected()
    {
        using var server = StartServer(maxPlayers: 2);
        using var a = Join(server, "a");
        using var b = Join(server, "b");
        using var c = new HeadlessClient();
        c.Connect(server.LocalPort, "c");
        Assert.True(Pump.Until(() => c.Disconnected, 3000, c, a, b));
        Assert.Equal(RejectReason.ServerFull, c.RejectReason);
    }

    [Fact]
    public void JoinSpam_DoesNotFloodControlChannel()
    {
        // _server: MaxPlayers 4 -> Control capacity 12; default BadPacketDisconnectThreshold 20.
        using var spammer = Join(_server, "spammer");

        // Phase 1: more repeat Joins than the Control channel holds, but fewer than the kick threshold.
        // They must be rejected on the network thread, so they neither overflow the channel
        // (which would disconnect the spammer early) nor block other peers.
        for (int i = 0; i < 15; i++) spammer.SendJoin();
        using var other = Join(_server, "other");
        Assert.False(Pump.Until(() => spammer.Disconnected, 300, spammer, other), "kicked before threshold");

        // Phase 2: repeat Joins keep counting as bad packets until the threshold kicks the spammer.
        for (int i = 0; i < 10; i++) spammer.SendJoin();
        Assert.True(Pump.Until(() => spammer.Disconnected, 3000, spammer, other), "spammer kicked");

        int before = other.SnapshotsReceived;
        Assert.True(Pump.Until(() => other.SnapshotsReceived > before + 3, 3000, other), "server keeps ticking");
    }

    [Fact]
    public void InputFlood_IsCapped_AndFlooderKicked()
    {
        // _server: Input channel capacity 4 * 8 = 32, per-peer cap SimHz * 2 = 60 packets/s.
        using var flooder = Join(_server, "flooder");
        using var victim = Join(_server, "victim");

        // The victim sends at the tick rate; each round the flooder sends more than the whole
        // shared Input channel holds, which (uncapped) pushes the victim's inputs out.
        const int victimInputs = 30;
        for (int i = 0; i < victimInputs; i++)
        {
            victim.SendMove(0f, 1f, 0f);
            if (!flooder.Disconnected)
            {
                for (int j = 0; j < 50; j++) flooder.SendMove(1f, 0f, 0f);
            }
            Pump.Until(() => false, 33, flooder, victim);
        }

        Assert.True(Pump.Until(() => flooder.Disconnected, 3000, flooder, victim), "flooder kicked");
        Assert.True(Pump.Until(() => victim.LastAckInputSeq == victimInputs, 3000, victim), "victim's latest input processed");
        Assert.False(victim.Disconnected, "victim kicked");
    }

    [Fact]
    public void InputBeforeJoin_CountsAsBadPacket()
    {
        using var c = new HeadlessClient();
        c.Connect(_server.LocalPort, "nojoin");
        Assert.True(Pump.Until(() => c.Connected, 3000, c), "connect");

        // Below the rate cap, so only the "not joined" rule applies. Threshold is 20.
        for (int i = 0; i < 25; i++) c.SendMove(0f, 1f, 0f);
        Assert.True(Pump.Until(() => c.Disconnected, 3000, c), "kicked");
    }

    [Fact]
    public void FullMatch_SnapshotWithMaxEntities_IsDelivered()
    {
        // Phase 8: 100 players arrive in two Sequenced packets (90 + 10), each at most 19 + 13 * 90 = 1189 bytes, never
        // fragmented: the server's MTU must allow a single packet of ProtocolConstants.MaxPacketSize. Every client must
        // end up with all 100 (the parts of one tick add up).
        int max = ProtocolConstants.MaxSnapshotEntities;
        using var server = StartServer(maxPlayers: max);
        var clients = new HeadlessClient[max];
        try
        {
            for (int i = 0; i < max; i++)
            {
                clients[i] = new HeadlessClient();
                clients[i].Connect(server.LocalPort, "p" + i);
            }
            Assert.True(Pump.Until(() => clients.All(c => c.Connected), 10000, clients), "connect");
            foreach (var c in clients) c.SendJoin();
            Assert.True(Pump.Until(() => clients.All(c => c.JoinResponse?.Result == JoinResult.Ok), 10000, clients), "join");
            Assert.True(Pump.Until(() => clients.All(c => c.LastSnapshot.Count == max), 5000, clients), "full snapshot");
        }
        finally
        {
            foreach (var c in clients) c?.Dispose();
        }
    }

    [Fact]
    public void GarbagePackets_DoNotCrash_AndKickAfterThreshold()
    {
        using var a = Join(_server, "a");
        using var b = Join(_server, "b");

        for (int i = 0; i < 25; i++) a.SendRaw(new byte[] { 0xFF, 0x01 });
        Assert.True(Pump.Until(() => a.Disconnected, 3000, a, b));

        int before = b.SnapshotsReceived;
        Assert.True(Pump.Until(() => b.SnapshotsReceived > before + 3, 3000, b), "server keeps ticking");
    }

    [Fact]
    public void Stop_ReleasesUdpPort_AndThread()
    {
        var server = StartServer(maxPlayers: 2);
        int port = server.LocalPort;
        server.Dispose();

        Assert.False(server.IsRunning);
        using var socket = new UdpClient(port);   // throws if the port is still bound
    }
}
