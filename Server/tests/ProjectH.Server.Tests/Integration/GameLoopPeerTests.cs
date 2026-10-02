using System;
using System.Collections.Generic;
using LiteNetLib;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Server.Net;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Integration;

// Drives GameLoop's tick directly (its thread is never started), so control-message races that
// cannot be produced on demand over UDP are replayed deterministically.
public sealed class GameLoopPeerTests
{
    [Fact]
    public void ReusedPeerId_ReplacesStaleSession()
    {
        // LiteNetLib reuses peer ids. If the old peer's Disconnected message was lost (or has not
        // arrived yet), a new peer can show up under the same id while the old session still exists.
        using var host = new PeerHost();
        NetPeer oldPeer = host.AcceptPeer();
        NetPeer newPeer = host.AcceptPeer();
        oldPeer.Tag = new PeerState("old");
        newPeer.Tag = new PeerState("new");
        const int reusedId = 7;

        using var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestGameData.Create(), NullLogger.Instance);
        var control = loop.Channels.Control.Writer;

        Assert.True(control.TryWrite(new ControlMessage(ControlKind.Connected, reusedId, oldPeer, "old")));
        Assert.True(control.TryWrite(new ControlMessage(ControlKind.JoinRequested, reusedId, oldPeer, null)));
        loop.RunTick();
        Assert.True(loop.Match.TryGetPlayer(reusedId, out var first));
        Assert.Equal("old", first.DevPlayerId);

        Assert.True(control.TryWrite(new ControlMessage(ControlKind.Connected, reusedId, newPeer, "new")));
        Assert.True(control.TryWrite(new ControlMessage(ControlKind.JoinRequested, reusedId, newPeer, null)));
        loop.RunTick();
        Assert.True(loop.Match.TryGetPlayer(reusedId, out var second));
        Assert.Equal("new", second.DevPlayerId);
        Assert.Equal(1, loop.Match.PlayerCount);

        // The late Disconnected of the old peer must not remove the new session.
        Assert.True(control.TryWrite(new ControlMessage(ControlKind.Disconnected, reusedId, oldPeer, null)));
        loop.RunTick();
        Assert.True(loop.Match.TryGetPlayer(reusedId, out var still));
        Assert.Equal("new", still.DevPlayerId);
    }

    // Server review L12: when dropping the old session of a reused peer id throws, the new connection is still registered
    // (it was registered first), so the join timeout closes it instead of leaving it an orphan nothing ever sweeps.
    [Fact]
    public void AThrowWhileDroppingTheOldSession_StillRegistersTheNewConnection()
    {
        using var host = new PeerHost();
        NetPeer oldPeer = host.AcceptPeer();
        NetPeer newPeer = host.AcceptPeer();
        oldPeer.Tag = new PeerState("old");
        var newState = new PeerState("new");
        newPeer.Tag = newState;
        const int reusedId = 7;
        var options = new ServerOptions { Port = 0, MaxPlayers = 4, JoinTimeoutSeconds = 1 };
        using var loop = new GameLoop(options, TestGameData.Create(), NullLogger.Instance);
        var control = loop.Channels.Control.Writer;
        Assert.True(control.TryWrite(new ControlMessage(ControlKind.Connected, reusedId, oldPeer, "old")));
        Assert.True(control.TryWrite(new ControlMessage(ControlKind.JoinRequested, reusedId, oldPeer, null)));
        loop.RunTickGuarded();

        int thrown = 0;
        loop.RemoveFaultHook = () =>
        {
            if (thrown++ == 0) throw new InvalidOperationException("test fault");
        };
        Assert.True(control.TryWrite(new ControlMessage(ControlKind.Connected, reusedId, newPeer, "new")));
        loop.RunTickGuarded();
        Assert.Equal(1, loop.Health.TickFailures);

        for (int i = 0; i <= options.JoinTimeoutSeconds * options.SimHz; i++) loop.RunTickGuarded();
        Assert.Equal(DisconnectCode.JoinTimeout, newState.CloseCode);
        Assert.Equal(1, loop.Health.Kicks(DisconnectCode.JoinTimeout));
        Assert.Equal(0, loop.Health.Peers);
    }

    // Phase 13 final review A4: a joined peer whose reliable queues stay over MaxReliableBacklog for CongestedSeconds is
    // closed with Congested (counted); a dip below the limit starts the count over.
    [Fact]
    public void APeerBackedUpForTenSeconds_IsClosedAsCongested()
    {
        using var host = new PeerHost();
        NetPeer peer = host.AcceptPeer();
        var state = new PeerState("slow");
        peer.Tag = state;
        var options = new ServerOptions { Port = 0, MaxPlayers = 4, InputTimeoutSeconds = 0 };
        using var loop = new GameLoop(options, TestGameData.Create(), NullLogger.Instance);
        var control = loop.Channels.Control.Writer;
        Assert.True(control.TryWrite(new ControlMessage(ControlKind.Connected, 3, peer, "slow")));
        Assert.True(control.TryWrite(new ControlMessage(ControlKind.JoinRequested, 3, peer, null)));
        loop.RunTick();
        Assert.True(loop.Match.TryGetPlayer(3, out _));   // that tick read the real queues of both channels

        int queued = GameLoop.MaxReliableBacklog / 2 + 1;   // per channel: both together are over the limit
        loop.QueueProbe = (_, _) => queued;
        long limit = (long)GameLoop.CongestedSeconds * options.SimHz;
        for (long i = 0; i < limit - 1; i++) loop.RunTick();
        queued = 0;                                         // one tick below: the count starts over
        loop.RunTick();
        queued = GameLoop.MaxReliableBacklog / 2 + 1;
        for (long i = 0; i < limit; i++) loop.RunTick();
        Assert.True(loop.Match.TryGetPlayer(3, out _));
        loop.RunTick();
        Assert.False(loop.Match.TryGetPlayer(3, out _));
        Assert.Equal(DisconnectCode.Congested, state.CloseCode);
        Assert.Equal(1, loop.Health.Kicks(DisconnectCode.Congested));
    }

    // Server review M7: a player whose own tick throws is closed with ServerError and counted; the match is not reset and
    // the other player's ticks go on.
    [Fact]
    public void APlayerWhoseTickThrows_IsClosedWithServerError_WithoutAMatchReset()
    {
        using var host = new PeerHost();
        NetPeer badPeer = host.AcceptPeer();
        NetPeer goodPeer = host.AcceptPeer();
        var bad = new PeerState("bad");
        var good = new PeerState("good");
        badPeer.Tag = bad;
        goodPeer.Tag = good;
        using var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4, InputTimeoutSeconds = 0 }, TestGameData.Create(), NullLogger.Instance);
        var control = loop.Channels.Control.Writer;
        Assert.True(control.TryWrite(new ControlMessage(ControlKind.Connected, 1, badPeer, "bad")));
        Assert.True(control.TryWrite(new ControlMessage(ControlKind.JoinRequested, 1, badPeer, null)));
        Assert.True(control.TryWrite(new ControlMessage(ControlKind.Connected, 2, goodPeer, "good")));
        Assert.True(control.TryWrite(new ControlMessage(ControlKind.JoinRequested, 2, goodPeer, null)));
        loop.RunTickGuarded();
        Assert.True(loop.Match.TryGetPlayer(1, out var player));

        loop.Match.FaultEntityId = player.EntityId;
        uint tick = loop.Match.ServerTick;
        for (int i = 0; i < 10; i++) loop.RunTickGuarded();

        Assert.Equal(DisconnectCode.ServerError, bad.CloseCode);
        Assert.Equal(DisconnectCode.None, good.CloseCode);
        Assert.False(loop.Match.TryGetPlayer(1, out _));
        Assert.True(loop.Match.TryGetPlayer(2, out _));
        Assert.Equal(tick + 10, loop.Match.ServerTick);
        Assert.Equal(1, loop.Health.PlayerFailures);
        Assert.Equal(1, loop.Health.Kicks(DisconnectCode.ServerError));
        Assert.Equal(0, loop.Health.TickFailures);
        Assert.Equal(0, loop.Health.MatchResets);
        Assert.Equal(1, loop.Health.Peers);
    }

    // Server review L9: a throwing disconnect callback is counted and does not escape into LiteNetLib's thread.
    [Fact]
    public void AThrowingDisconnectCallback_IsCounted_AndDoesNotThrow()
    {
        using var host = new PeerHost();
        NetPeer peer = host.AcceptPeer();
        peer.Tag = new PeerState("p");
        using var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestGameData.Create(), NullLogger.Instance);
        loop.Listener.CallbackFaultHook = _ => throw new InvalidOperationException("test fault");
        loop.Listener.OnPeerDisconnected(peer, default);
        Assert.Equal(1, loop.Health.CallbackErrors);
    }

    [Fact]
    public void ProtocolMtu_FitsMaxPacketSizeInOneSequencedPacket()
    {
        // Snapshots are Sequenced, which LiteNetLib never fragments: sending more than
        // GetMaxSinglePacketSize throws TooBigPacketException.
        using var host = new PeerHost(ProtocolConstants.Mtu);
        NetPeer peer = host.AcceptPeer();
        Assert.True(peer.GetMaxSinglePacketSize(DeliveryMethod.Sequenced) >= ProtocolConstants.MaxPacketSize);
        Assert.True(peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable) >= ProtocolConstants.MaxPacketSize);
    }

    // A plain LiteNetLib host that hands out real, connected server-side NetPeer objects.
    private sealed class PeerHost : IDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _server;
        private readonly List<NetManager> _clients = new();
        private readonly List<NetPeer> _connected = new();

        public PeerHost(int mtuOverride = 0)
        {
            // The game's channels, like GameLoop's own NetManager (the loop asks the peers for their channel-1 queue).
            _server = new NetManager(_listener, null) { MtuOverride = mtuOverride, ChannelsCount = ProtocolConstants.ChannelCount };
            _listener.ConnectionRequestEvent += request => request.Accept();
            _listener.PeerConnectedEvent += peer => _connected.Add(peer);
            _server.Start(0);
        }

        public NetPeer AcceptPeer()
        {
            var client = new NetManager(new EventBasedNetListener(), null) { ChannelsCount = ProtocolConstants.ChannelCount };
            client.Start();
            _clients.Add(client);
            int expected = _connected.Count + 1;
            client.Connect("127.0.0.1", _server.LocalPort, string.Empty);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (_connected.Count < expected && clock.ElapsedMilliseconds < 3000)
            {
                _server.PollEvents();
                client.PollEvents();
                System.Threading.Thread.Sleep(5);
            }
            Assert.Equal(expected, _connected.Count);
            return _connected[^1];
        }

        public void Dispose()
        {
            foreach (var client in _clients) client.Stop();
            _server.Stop();
        }
    }
}
