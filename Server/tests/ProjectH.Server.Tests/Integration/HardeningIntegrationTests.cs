using System;
using LiteNetLib;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Server.Diagnostics;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Integration;

// Phase 10 D1, D3-D5 over real UDP: the server says why it closes a connection, times out connections that never
// join and players that stop sending input, and counts invalid packets and rejects by reason.
public sealed class HardeningIntegrationTests
{
    internal static GameLoop StartServer(int maxPlayers = 4, int joinTimeout = 5, int inputTimeout = 10, int grace = 10,
        int minPlayers = 2, int countdown = 10)
    {
        var loop = new GameLoop(new ServerOptions
        {
            Port = 0,
            MaxPlayers = maxPlayers,
            MinPlayers = minPlayers,
            StartCountdownSeconds = countdown,
            ResultSeconds = 1,
            DisconnectTimeoutMs = 1000,
            StatsIntervalSeconds = 60,
            JoinTimeoutSeconds = joinTimeout,
            InputTimeoutSeconds = inputTimeout,
            ReconnectGraceSeconds = grace,
        }, TestGameData.Create(), NullLogger.Instance, TestGameData.CombatLoadout);
        loop.Start();
        return loop;
    }

    internal static HeadlessClient Join(GameLoop server, string devId, JoinResult expected = JoinResult.Ok)
    {
        var client = new HeadlessClient();
        client.Connect(server.LocalPort, devId);
        Assert.True(Pump.Until(() => client.Connected, 3000, client), "connect");
        client.SendJoin();
        Assert.True(Pump.Until(() => client.JoinResponse.HasValue, 3000, client), "join response");
        Assert.Equal(expected, client.JoinResponse?.Result);
        return client;
    }

    [Fact]
    public void AConnectionThatNeverJoins_IsClosedWithJoinTimeout()
    {
        using GameLoop server = StartServer(joinTimeout: 1);
        using var idle = new HeadlessClient();
        idle.Connect(server.LocalPort, "idle");
        Assert.True(Pump.Until(() => idle.Connected, 3000, idle), "connect");

        Assert.True(Pump.Until(() => idle.Disconnected, 4000, idle), "timed out");
        Assert.Equal(DisconnectReason.RemoteConnectionClose, idle.DisconnectReason);
        Assert.Equal(DisconnectCode.JoinTimeout, idle.DisconnectCode);
        Assert.Equal(1, server.Health.Kicks(DisconnectCode.JoinTimeout));
    }

    [Fact]
    public void APlayerThatStopsSendingInput_IsClosedWithInputTimeout_AndOneThatSendsIsNot()
    {
        using GameLoop server = StartServer(inputTimeout: 3);   // the smallest valid value with a 1 s disconnect timeout
        using var silent = Join(server, "silent");
        using var active = Join(server, "active");

        Assert.True(SendInputsUntil(active, () => silent.Disconnected, 5000, silent), "silent player timed out");
        Assert.Equal(DisconnectCode.InputTimeout, silent.DisconnectCode);

        // The active player keeps going well past the timeout.
        Assert.False(SendInputsUntil(active, () => active.Disconnected, 2500), "active player kept");
        Assert.Equal(1, server.Health.Kicks(DisconnectCode.InputTimeout));
    }

    // Sends one input about every tick (well below the 60/s cap) while polling, until the condition holds.
    internal static bool SendInputsUntil(HeadlessClient sender, Func<bool> condition, int timeoutMs, params HeadlessClient[] others)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            sender.SendMove(0f, 0f, 0f);
            if (Pump.Until(condition, 33, [sender, .. others])) return true;
        }
        return condition();
    }

    [Fact]
    public void InputTimeoutZero_NeverClosesASilentPlayer()
    {
        using GameLoop server = StartServer(inputTimeout: 0);
        using var silent = Join(server, "silent");
        Assert.False(Pump.Until(() => silent.Disconnected, 3000, silent), "kept");
        Assert.Equal(0, server.Health.Kicks(DisconnectCode.InputTimeout));
    }

    [Fact]
    public void AKick_CarriesTheKickedCode_AndCountsTheReason()
    {
        using GameLoop server = StartServer();
        using var a = Join(server, "a");
        using var b = Join(server, "b");

        for (int i = 0; i < 25; i++) a.SendRaw(new byte[] { 0xFF, 0x01 });
        Assert.True(Pump.Until(() => a.Disconnected, 3000, a, b), "kicked");
        Assert.Equal(DisconnectCode.Kicked, a.DisconnectCode);
        Assert.Equal(1, server.Health.Kicks(DisconnectCode.Kicked));
        Assert.True(server.Health.BadPackets(BadPacketReason.UnknownId) >= 20);
        Assert.False(b.Disconnected);
    }

    [Fact]
    public void InvalidPackets_AreCountedByReason()
    {
        using GameLoop server = StartServer();
        HealthCounters health = server.Health;
        using var c = new HeadlessClient();
        c.Connect(server.LocalPort, "c");
        Assert.True(Pump.Until(() => c.Connected, 3000, c), "connect");

        c.SendMove(0f, 1f, 0f);
        Assert.True(Pump.Until(() => health.BadPackets(BadPacketReason.InputBeforeJoin) == 1, 3000, c), "input before join");

        c.SendJoin();
        Assert.True(Pump.Until(() => c.JoinResponse.HasValue, 3000, c), "join");
        c.SendJoin();
        c.SendRaw(new byte[] { (byte)PacketId.WorldSnapshot });
        c.SendRaw(new byte[] { (byte)PacketId.PlayerSpawned, 1, 2 });
        c.SendRaw(new byte[] { 0 });
        c.SendRaw(new byte[] { (byte)PacketId.PlayerInput, 0 });   // zero inputs: malformed
        Assert.True(Pump.Until(() =>
            health.BadPackets(BadPacketReason.DuplicateJoin) == 1 &&
            health.BadPackets(BadPacketReason.WrongDirection) == 2 &&
            health.BadPackets(BadPacketReason.UnknownId) == 1 &&
            health.BadPackets(BadPacketReason.Malformed) == 1, 3000, c), "reasons");

        // Above SimHz * 2 = 60 inputs in one second: the rest count as InputRate (below the kick threshold of 20).
        for (int i = 0; i < 70; i++) c.SendMove(0f, 0f, 0f);
        Assert.True(Pump.Until(() => health.BadPackets(BadPacketReason.InputRate) >= 5, 3000, c), "rate");
        Assert.False(c.Disconnected);
    }

    [Fact]
    public void AThrowingReceiveHandler_CountsAgainstThePeer_AndTheServerGoesOn()
    {
        using GameLoop server = StartServer();
        using var a = Join(server, "a");
        server.Listener.ReceiveFaultHook = () => throw new InvalidOperationException("test fault");
        a.SendMove(0f, 0f, 0f);
        Assert.True(Pump.Until(() => server.Health.BadPackets(BadPacketReason.HandlerException) >= 1, 3000, a), "counted");
        server.Listener.ReceiveFaultHook = null;

        int before = a.SnapshotsReceived;
        Assert.True(Pump.Until(() => a.SnapshotsReceived > before + 3, 3000, a), "server keeps ticking");
        Assert.False(a.Disconnected);
    }

    [Fact]
    public void Rejects_AreCountedByReason()
    {
        using GameLoop server = StartServer(maxPlayers: 2);
        using var a = Join(server, "a");
        using var b = Join(server, "b");

        using var full = new HeadlessClient();
        full.Connect(server.LocalPort, "full");
        using var old = new HeadlessClient();
        old.Connect(server.LocalPort, "old", protocolVersion: 7);
        using var bad = new HeadlessClient();
        bad.ConnectRaw(server.LocalPort, Array.Empty<byte>());
        Assert.True(Pump.Until(() => full.Disconnected && old.Disconnected && bad.Disconnected, 3000, full, old, bad, a, b), "rejected");

        // The server is full, so all three are refused as ServerFull before their data is read.
        Assert.Equal(RejectReason.ServerFull, old.RejectReason);
        Assert.Equal(3, server.Health.Rejects(RejectReason.ServerFull));
        Assert.Equal(0, server.Health.Rejects(RejectReason.VersionMismatch));
    }

    [Fact]
    public void Rejects_ByVersionAndBadRequest_AreCountedApart()
    {
        using GameLoop server = StartServer();
        using var old = new HeadlessClient();
        old.Connect(server.LocalPort, "old", protocolVersion: 7);
        using var bad = new HeadlessClient();
        bad.ConnectRaw(server.LocalPort, Array.Empty<byte>());
        Assert.True(Pump.Until(() => old.Disconnected && bad.Disconnected, 3000, old, bad), "rejected");
        Assert.Equal(RejectReason.VersionMismatch, old.RejectReason);
        Assert.Equal(1, server.Health.Rejects(RejectReason.VersionMismatch));
        Assert.Equal(1, server.Health.Rejects(RejectReason.BadRequest));
    }
}
