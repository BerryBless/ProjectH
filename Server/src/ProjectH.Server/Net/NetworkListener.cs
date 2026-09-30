using System;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using Microsoft.Extensions.Logging;
using ProjectH.Server.Diagnostics;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Net;

// Runs on LiteNetLib's threads (UnsyncedEvents). Validates and parses packets, then hands plain
// structs to the game loop through InboundChannels. Never touches Match or any game state.
public sealed class NetworkListener : INetEventListener
{
    private static readonly byte[] RejectVersionMismatch = { (byte)RejectReason.VersionMismatch };
    private static readonly byte[] RejectServerFull = { (byte)RejectReason.ServerFull };
    private static readonly byte[] RejectBadRequest = { (byte)RejectReason.BadRequest };

    private readonly ServerOptions _options;
    private readonly InboundChannels _channels;
    private readonly ServerStats _stats;
    private readonly ILogger _logger;

    public NetworkListener(ServerOptions options, InboundChannels channels, ServerStats stats, ILogger logger)
    {
        _options = options;
        _channels = channels;
        _stats = stats;
        _logger = logger;
    }

    // Set once by GameLoop right after creating the NetManager (the two reference each other).
    public NetManager Manager { get; set; } = null!;

    public void OnConnectionRequest(ConnectionRequest request)
    {
        if (Manager.ConnectedPeersCount >= _options.MaxPlayers)
        {
            request.Reject(RejectServerFull);
            return;
        }

        var data = request.Data;
        if (data == null || data.AvailableBytes == 0)
        {
            request.Reject(RejectBadRequest);
            return;
        }

        var reader = new PacketReader(new ReadOnlySpan<byte>(data.RawData, data.Position, data.AvailableBytes));
        if (!ConnectRequestData.TryRead(ref reader, out var connect))
        {
            request.Reject(RejectBadRequest);
            return;
        }
        if (connect.ProtocolVersion != ProtocolConstants.ProtocolVersion)
        {
            request.Reject(RejectVersionMismatch);
            return;
        }

        NetPeer peer = request.Accept();
        peer.Tag = new PeerState(connect.DevPlayerId);

        // Connected is announced here, not in OnPeerConnected: with UnsyncedEvents LiteNetLib raises
        // OnPeerConnected synchronously inside Accept(), before Tag is assigned above. Writing after
        // the Tag assignment guarantees the game loop never sees a peer without its PeerState.
        if (!_channels.Control.Writer.TryWrite(new ControlMessage(ControlKind.Connected, peer.Id, peer, connect.DevPlayerId)))
        {
            _logger.LogCritical("Control channel full; disconnecting peer {PeerId}", peer.Id);
            peer.Disconnect();
        }
    }

    public void OnPeerConnected(NetPeer peer)
    {
        // Runs inside request.Accept() (see OnConnectionRequest); peer.Tag is not set yet here.
        // The server never connects out, so there is nothing else to handle.
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        // If this message is lost the session is still removed: the game loop also drops
        // sessions whose peer is no longer Connected.
        if (!_channels.Control.Writer.TryWrite(new ControlMessage(ControlKind.Disconnected, peer.Id, peer, null)))
            _logger.LogWarning("Control channel full; disconnect of peer {PeerId} will be detected by the stale-peer sweep", peer.Id);
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        // AutoRecycle is on: the reader's buffer is reused after this returns, so everything the
        // game loop needs is copied into value-type messages here.
        ReadOnlySpan<byte> data = reader.GetRemainingBytesSpan();
        _stats.AddIn(data.Length);

        var packet = new PacketReader(data);
        if (!packet.TryReadPacketId(out PacketId id))
        {
            OnBadPacket(peer);
            return;
        }

        switch (id)
        {
            case PacketId.JoinMatchRequest:
                // Only the first Join per connection reaches the game loop; repeats would let one
                // client fill the Control channel and get other peers disconnected.
                if (peer.Tag is not PeerState joinState || joinState.JoinRequested)
                {
                    OnBadPacket(peer);
                    break;
                }
                joinState.JoinRequested = true;
                if (!_channels.Control.Writer.TryWrite(new ControlMessage(ControlKind.JoinRequested, peer.Id, peer, null)))
                {
                    _logger.LogCritical("Control channel full; disconnecting peer {PeerId}", peer.Id);
                    peer.Disconnect();
                }
                break;

            case PacketId.PlayerInput:
                // The Input channel is shared by all peers and drops the oldest message when full,
                // so one peer must not be able to fill it: inputs before Join and inputs above the
                // per-peer rate are rejected here and count toward the bad-packet kick.
                if (peer.Tag is not PeerState inputState || !inputState.JoinRequested ||
                    !inputState.TryCountInputPacket(Environment.TickCount64, _options.MaxInputPacketsPerSecond))
                {
                    OnBadPacket(peer);
                    break;
                }
                if (PlayerInputPacket.TryRead(ref packet, out var input))
                    _channels.Input.Writer.TryWrite(new InputMessage(peer.Id, peer, input));
                else
                    OnBadPacket(peer);
                break;

            default:
                // Server-to-client packet ids are never valid from a client.
                OnBadPacket(peer);
                break;
        }
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        _logger.LogWarning("Network error {SocketError} from {EndPoint}", socketError, endPoint);
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        // Unconnected messages are disabled on the NetManager; nothing to do.
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
    }

    private void OnBadPacket(NetPeer peer)
    {
        _stats.AddBadPacket();
        if (peer.Tag is not PeerState state) return;
        state.BadPackets++;
        if (state.BadPackets >= _options.BadPacketDisconnectThreshold && !state.Kicked)
        {
            state.Kicked = true;
            _logger.LogWarning("Disconnecting peer {PeerId} ({DevPlayerId}) after {Count} invalid packets", peer.Id, state.DevPlayerId, state.BadPackets);
            peer.Disconnect();
        }
    }
}
