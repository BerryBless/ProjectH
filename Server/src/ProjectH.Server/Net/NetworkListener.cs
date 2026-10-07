using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LiteNetLib;
using Microsoft.Extensions.Logging;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Game.Build;
using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Net;

// Runs on LiteNetLib's threads (UnsyncedEvents). Validates and parses packets, then hands plain
// structs to the game loop through InboundChannels. Never touches Match or any game state.
public sealed class NetworkListener : INetEventListener
{
    private static readonly byte[] RejectVersionMismatch = { (byte)RejectReason.VersionMismatch };
    private static readonly byte[] RejectServerFull = { (byte)RejectReason.ServerFull };
    private static readonly byte[] RejectBadRequest = { (byte)RejectReason.BadRequest };
    // Phase 10 D1: one shared array per code; LiteNetLib copies the data into its own packet.
    private static readonly byte[][] CloseData =
    {
        new[] { (byte)DisconnectCode.None },
        new[] { (byte)DisconnectCode.ServerShutdown },
        new[] { (byte)DisconnectCode.Kicked },
        new[] { (byte)DisconnectCode.JoinTimeout },
        new[] { (byte)DisconnectCode.InputTimeout },
        new[] { (byte)DisconnectCode.ServerError },
        new[] { (byte)DisconnectCode.Congested },
    };

    private readonly ServerOptions _options;
    private readonly InboundChannels _channels;
    private readonly ServerStats _stats;
    private readonly HealthCounters _health;
    private readonly StatsQueryQueue _statsQueries;
    private readonly ILogger _logger;
    // 1 once a receive-handler exception was logged in the current stats interval (D5: the first one only).
    private int _handlerErrorLogged;
    // Server review M1: the same for socket errors (OnNetworkError). Interlocked: any LiteNetLib thread may report one.
    private int _networkErrorLogged;
    // Server review L9: the same for exceptions caught in the connection request, disconnect and socket-error callbacks.
    private int _callbackErrorLogged;
    // Set once by GameLoop (Stop, or the fatal path of repeated match resets) and never cleared: a stopping server
    // accepts no new connection. Written by the game loop or the host's thread, read on LiteNetLib's thread.
    private volatile bool _stopping;

    // Server review M2: connection requests per remote IP (fixed table; OnConnectionRequest only, so LiteNetLib's receive
    // thread only, like PeerState's receive fields).
    private readonly ConnectRateLimiter _connectRate;

    // Phase 13 D8: build requests a peer may send per second (building.json); more are invalid packets.
    private readonly int _maxBuildRequestsPerSecond;

    public NetworkListener(ServerOptions options, InboundChannels channels, ServerStats stats, HealthCounters health,
        StatsQueryQueue statsQueries, ILogger logger, int maxBuildRequestsPerSecond = 20)
    {
        _maxBuildRequestsPerSecond = maxBuildRequestsPerSecond;
        _options = options;
        _channels = channels;
        _stats = stats;
        _health = health;
        _statsQueries = statsQueries;
        _logger = logger;
        _connectRate = new ConnectRateLimiter(options.ConnectBurstPerIp, options.ConnectsPerIpPerSecond);
    }

    // Set once by GameLoop right after creating the NetManager (the two reference each other).
    public NetManager Manager { get; set; } = null!;

    // The disconnect data that carries code (D1).
    public static byte[] DataOf(DisconnectCode code) => CloseData[(int)code];

    // Phase 10 D1: every server-side close of one peer goes through here. The code is stored before Disconnect is
    // called (see PeerState.CloseCode). Safe from any thread: NetPeer.Disconnect is thread-safe.
    public static void Close(NetPeer peer, DisconnectCode code)
    {
        if (peer.Tag is PeerState state) state.TrySetCloseCode(code);
        peer.Disconnect(DataOf(code));
    }

    // Called by the game loop at each stats line: the next receive-handler exception is logged again.
    public void ResetLogLimits()
    {
        Volatile.Write(ref _handlerErrorLogged, 0);
        Volatile.Write(ref _networkErrorLogged, 0);
        Volatile.Write(ref _callbackErrorLogged, 0);
    }

    // Server review L9: an exception caught in a callback (a server bug) is counted and logged once per stats interval,
    // like a receive-handler exception. Never throws: the logger call is guarded too, it runs on LiteNetLib's thread.
    private void OnCallbackError(Exception ex, string callback)
    {
        _health.AddCallbackError();
        if (Interlocked.Exchange(ref _callbackErrorLogged, 1) != 0) return;
        try
        {
            _logger.LogError(ex, "Exception in the {Callback} callback (first of this stats interval)", callback);
        }
        catch
        {
            // Counted above; nothing left to report it with.
        }
    }

    // From now on every connection request is refused (as ServerFull: no protocol change, and the client does not
    // retry a reject). A request already accepted is closed by Stop's DisconnectAll like every other connection.
    public void BeginStopping() => _stopping = true;
    internal bool IsStopping => _stopping;

    // Server review L9: nothing may escape into LiteNetLib's thread, which serves every connection. A request that throws
    // before Accept is refused (as ServerFull; not counted as a reject, callbackErrors counts it). One that throws after
    // Accept is closed with ServerError: the game loop may never have heard of it, and nothing else would free its slot.
    public void OnConnectionRequest(ConnectionRequest request)
    {
        NetPeer? accepted = null;
        try
        {
            HandleConnectionRequest(request, ref accepted);
        }
        catch (Exception ex)
        {
            OnCallbackError(ex, "connection request");
            try
            {
                if (accepted != null)
                {
                    _health.AddKick(DisconnectCode.ServerError);
                    Close(accepted, DisconnectCode.ServerError);
                }
                else
                {
                    request.Reject(RejectServerFull);
                }
            }
            catch
            {
                // The request or the peer is beyond help; LiteNetLib's own timeout ends it.
            }
        }
    }

    private void HandleConnectionRequest(ConnectionRequest request, ref NetPeer? accepted)
    {
        CallbackFaultHook?.Invoke("request");
        if (_stopping || Manager.ConnectedPeersCount >= _options.MaxPlayers)
        {
            Reject(request, RejectReason.ServerFull, RejectServerFull);
            return;
        }
        // Server review M2: after the full check (a refused request takes no token), before anything is read. The client
        // is told ServerFull (no protocol change; it does not retry a reject); the Health line counts it as connectRate.
        if (!_connectRate.TryAcquire(request.RemoteEndPoint.Address, Environment.TickCount64))
        {
            _health.AddConnectRateReject();
            _logger.LogDebug("Rejected connection from {EndPoint}: over the per-IP connect rate", request.RemoteEndPoint);
            request.Reject(RejectServerFull);
            return;
        }

        var data = request.Data;
        if (data == null || data.AvailableBytes == 0)
        {
            Reject(request, RejectReason.BadRequest, RejectBadRequest);
            return;
        }

        var reader = new PacketReader(new ReadOnlySpan<byte>(data.RawData, data.Position, data.AvailableBytes));
        if (!ConnectRequestData.TryRead(ref reader, out var connect))
        {
            Reject(request, RejectReason.BadRequest, RejectBadRequest);
            return;
        }
        if (connect.ProtocolVersion != ProtocolConstants.ProtocolVersion)
        {
            Reject(request, RejectReason.VersionMismatch, RejectVersionMismatch);
            return;
        }

        NetPeer peer = request.Accept();
        accepted = peer;
        CallbackFaultHook?.Invoke("accepted");
        peer.Tag = new PeerState(connect.DevPlayerId);
        _health.AddConnection();
        // Server review M1: Debug, like every per-connection event (the Health line counts them).
        _logger.LogDebug("Peer {PeerId} ({DevPlayerId}) connected from {EndPoint}", peer.Id, connect.DevPlayerId, request.RemoteEndPoint);

        // Connected is announced here, not in OnPeerConnected: with UnsyncedEvents LiteNetLib raises
        // OnPeerConnected synchronously inside Accept(), before Tag is assigned above. Writing after
        // the Tag assignment guarantees the game loop never sees a peer without its PeerState.
        // A full channel is a server fault, so the close says ServerError (D1): the client may retry, and the code is
        // set before the disconnect, so the game loop never gives this connection a grace.
        if (!_channels.Control.Writer.TryWrite(new ControlMessage(ControlKind.Connected, peer.Id, peer, connect.DevPlayerId)))
        {
            _logger.LogCritical("Control channel full; disconnecting peer {PeerId}", peer.Id);
            _health.AddKick(DisconnectCode.ServerError);
            Close(peer, DisconnectCode.ServerError);
        }
    }

    // D5: rejects are counted by reason and logged at Debug only, because a flood of connection requests must not
    // flood the log.
    private void Reject(ConnectionRequest request, RejectReason reason, byte[] data)
    {
        _health.AddReject(reason);
        _logger.LogDebug("Rejected connection from {EndPoint}: {Reason}", request.RemoteEndPoint, reason);
        request.Reject(data);
    }

    public void OnPeerConnected(NetPeer peer)
    {
        // Runs inside request.Accept() (see OnConnectionRequest); peer.Tag is not set yet here.
        // The server never connects out, so there is nothing else to handle.
    }

    // Server review L9: guarded like OnConnectionRequest. A Disconnected message that is not written because of a throw is
    // covered by the game loop's stale-peer sweep (the peer is no longer Connected).
    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        try
        {
            HandleDisconnect(peer, disconnectInfo);
        }
        catch (Exception ex)
        {
            OnCallbackError(ex, "disconnect");
        }
    }

    private void HandleDisconnect(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        CallbackFaultHook?.Invoke("disconnected");
        _health.AddDisconnect(disconnectInfo.Reason == DisconnectReason.Timeout);
        if (peer.Tag is PeerState state)
        {
            _logger.LogDebug("Peer {PeerId} ({DevPlayerId}) disconnected: {Reason} (server code {Code})",
                peer.Id, state.DevPlayerId, disconnectInfo.Reason, state.CloseCode);
        }
        // If this message is lost the session is still removed: the game loop also drops
        // sessions whose peer is no longer Connected.
        if (!_channels.Control.Writer.TryWrite(new ControlMessage(ControlKind.Disconnected, peer.Id, peer, null)))
            _logger.LogWarning("Control channel full; disconnect of peer {PeerId} will be detected by the stale-peer sweep", peer.Id);
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        // D5: nothing a packet does may escape into LiteNetLib's thread, which serves every connection. A throw is a
        // server bug; it is counted against this peer like an invalid packet and logged once per stats interval.
        try
        {
            Receive(peer, reader);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _handlerErrorLogged, 1) == 0)
                _logger.LogError(ex, "Exception while handling a packet from peer {PeerId}", peer.Id);
            OnBadPacket(peer, BadPacketReason.HandlerException);
        }
    }

    // Test seam (server review L9): runs inside the other callbacks with where it is ("request" at the start of a connection
    // request, "accepted" right after Accept, "disconnected", "networkError"), so a test can make one throw.
    internal Action<string>? CallbackFaultHook { get; set; }

    // Test seam (D5): a hook that runs at the start of every receive, so a test can make the handler throw.
    internal Action? ReceiveFaultHook { get; set; }

    private void Receive(NetPeer peer, NetPacketReader reader)
    {
        ReceiveFaultHook?.Invoke();
        // AutoRecycle is on: the reader's buffer is reused after this returns, so everything the
        // game loop needs is copied into value-type messages here.
        ReadOnlySpan<byte> data = reader.GetRemainingBytesSpan();
        _stats.AddIn(data.Length);

        var packet = new PacketReader(data);
        if (!packet.TryReadPacketId(out PacketId id))
        {
            OnBadPacket(peer, BadPacketReason.UnknownId);
            return;
        }

        switch (id)
        {
            case PacketId.JoinMatchRequest:
                // Only the first Join per connection reaches the game loop; repeats would let one
                // client fill the Control channel and get other peers disconnected.
                if (peer.Tag is not PeerState joinState || joinState.JoinRequested)
                {
                    OnBadPacket(peer, BadPacketReason.DuplicateJoin);
                    break;
                }
                joinState.JoinRequested = true;
                if (!_channels.Control.Writer.TryWrite(new ControlMessage(ControlKind.JoinRequested, peer.Id, peer, null)))
                {
                    _logger.LogCritical("Control channel full; disconnecting peer {PeerId}", peer.Id);
                    _health.AddKick(DisconnectCode.ServerError);
                    Close(peer, DisconnectCode.ServerError);
                }
                break;

            case PacketId.PlayerInput:
                // The Input channel is shared by all peers and drops the oldest message when full,
                // so one peer must not be able to fill it: inputs before Join and inputs above the
                // per-peer rate are rejected here. Before Join counts toward the bad-packet kick.
                if (peer.Tag is not PeerState inputState || !inputState.JoinRequested)
                {
                    OnBadPacket(peer, BadPacketReason.InputBeforeJoin);
                    break;
                }
                // Server review M5: over the rate is dropped and counted (Health inputRate) but never kicks: a bad link
                // delivers seconds of a normal player's input at once, and the bucket already keeps the channel fair.
                if (!inputState.TryCountInputPacket(Environment.TickCount64, _options.MaxInputPacketsPerSecond, _options.InputBurst))
                {
                    _health.AddBadPacket(BadPacketReason.InputRate);
                    break;
                }
                if (PlayerInputPacket.TryRead(ref packet, out var input))
                    _channels.Input.Writer.TryWrite(new InputMessage(peer.Id, peer, input));
                else
                    OnBadPacket(peer, BadPacketReason.Malformed);
                break;

            case PacketId.BuildRequest:
            case PacketId.BuildEditRequest:
                // Phase 13 D8: only from a joined connection (like input). The game loop queues it per player.
                // Phase 13.5 D4: an edit goes the same way: the same per-second count (one budget for both kinds) and
                // the same channel and queue.
                if (peer.Tag is not PeerState buildState || !buildState.JoinRequested)
                {
                    OnBadPacket(peer, BadPacketReason.InputBeforeJoin);
                    break;
                }
                if (!buildState.TryCountBuildRequest(Environment.TickCount64, _maxBuildRequestsPerSecond))
                {
                    OnBadPacket(peer, BadPacketReason.BuildRate);
                    break;
                }
                if (id == PacketId.BuildRequest && BuildRequest.TryRead(ref packet, out var build))
                    _channels.Build.Writer.TryWrite(new BuildMessage(peer.Id, peer, new BuildQueueItem(build)));
                else if (id == PacketId.BuildEditRequest && BuildEditRequest.TryRead(ref packet, out var edit))
                    _channels.Build.Writer.TryWrite(new BuildMessage(peer.Id, peer, new BuildQueueItem(edit)));
                else
                    OnBadPacket(peer, BadPacketReason.Malformed);
                break;

            case PacketId.StatsRequest:
                // Phase 11 D8: the request has no body. It is taken only from a connection whose join succeeded
                // (PeerState.Joined, published by the game loop for Ok and Resumed only, so a refused or still pending
                // join gets nothing) and at most once per MinRequestIntervalMs; otherwise it is dropped without an
                // answer and counted as Limited, not as an invalid packet, so pressing the button repeatedly never gets
                // a player kicked. A full request queue is answered Busy at once.
                if (packet.Remaining != 0)
                {
                    OnBadPacket(peer, BadPacketReason.Malformed);
                    break;
                }
                if (peer.Tag is not PeerState statsState || !statsState.Joined ||
                    !statsState.TryCountStatsRequest(Environment.TickCount64, StatsQueryQueue.MinRequestIntervalMs))
                {
                    _statsQueries.AddLimited();
                    break;
                }
                if (!_statsQueries.TryEnqueue(new StatsQuery(peer.Id, peer, statsState.DevPlayerId, Environment.TickCount64)))
                    _statsQueries.TryReply(new StatsReply(peer.Id, peer, StatsResponse.Of(StatsStatus.Busy)));
                break;

            default:
                // Server-to-client packet ids are never valid from a client.
                OnBadPacket(peer, BadPacketReason.WrongDirection);
                break;
        }
    }

    // Server review M1: counted (networkErrors) and logged once per stats interval, so a burst of socket errors cannot
    // flood the log. Server review L9: guarded like the other callbacks.
    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        try
        {
            CallbackFaultHook?.Invoke("networkError");
            _health.AddNetworkError();
            if (Interlocked.Exchange(ref _networkErrorLogged, 1) == 0)
                _logger.LogWarning("Network error {SocketError} from {EndPoint} (first of this stats interval)", socketError, endPoint);
        }
        catch (Exception ex)
        {
            OnCallbackError(ex, "network error");
        }
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        // Unconnected messages are disabled on the NetManager; nothing to do.
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
    }

    private void OnBadPacket(NetPeer peer, BadPacketReason reason)
    {
        _stats.AddBadPacket();
        _health.AddBadPacket(reason);
        if (peer.Tag is not PeerState state) return;
        state.BadPackets++;
        if (state.BadPackets >= _options.BadPacketDisconnectThreshold && !state.Kicked)
        {
            state.Kicked = true;
            _health.AddKick(DisconnectCode.Kicked);
            _logger.LogWarning("Kicking peer {PeerId} ({DevPlayerId}) after {Count} invalid packets (last: {Reason})",
                peer.Id, state.DevPlayerId, state.BadPackets, reason);
            Close(peer, DisconnectCode.Kicked);
        }
    }
}
