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

        using var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestWeapons.Create(), NullLogger.Instance);
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
            _server = new NetManager(_listener, null) { MtuOverride = mtuOverride };
            _listener.ConnectionRequestEvent += request => request.Accept();
            _listener.PeerConnectedEvent += peer => _connected.Add(peer);
            _server.Start(0);
        }

        public NetPeer AcceptPeer()
        {
            var client = new NetManager(new EventBasedNetListener(), null);
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
