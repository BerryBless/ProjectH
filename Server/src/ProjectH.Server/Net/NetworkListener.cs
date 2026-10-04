using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LiteNetLib;
using Microsoft.Extensions.Logging;
using ProjectH.Server.Diagnostics;
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
    // accepts no new connection. Written by the game loop, the host's thread or the stall watchdog's timer thread (fatal stall), read on
    // LiteNetLib's thread.
    private volatile bool _stopping;

    // Server review M2: connection requests per remote IP (fixed table; OnConnectionRequest only, so LiteNetLib's receive
    // thread only, like PeerState's receive fields).
    private readonly ConnectRateLimiter _connectRate;

    // Phase 13 D8: build requests a peer may send per second (building.json); more are invalid packets.
    private readonly int _maxBuildRequestsPerSecond;

    // 기능: 수신 검증·파싱을 맡는 Network Listener를 만든다. IP별 연결 비율 제한기도 여기서 만든다.
    // 입력: options - 서버 설정, channels - Game Loop로 넘길 Inbound Channel, stats - Packet 통계, health - Health Counter, statsQueries - 전적 조회 Queue, logger - 로그 출력 대상, maxBuildRequestsPerSecond - Peer당 초당 허용 건설 요청 수.
    // 출력: 연결 수락 중(정지 전) 상태로 초기화된 NetworkListener 객체. Manager는 Game Loop가 따로 설정한다.
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

    // 기능: 종료 코드를 담은 연결 종료 Data를 돌려준다.
    // 입력: code - 보낼 연결 종료 코드.
    // 출력: 미리 만들어 둔 해당 코드의 종료 Data 배열(공유하므로 수정하지 않는다).
    // The disconnect data that carries code (D1).
    public static byte[] DataOf(DisconnectCode code) => CloseData[(int)code];

    // 기능: 서버 쪽에서 Peer 하나의 연결을 종료 코드와 함께 끊는다. 어느 Thread에서 불러도 된다.
    // 입력: peer - 끊을 연결, code - Client에 보낼 연결 종료 코드.
    // 출력: 반환값 없음. PeerState에 종료 코드가 먼저 기록(처음 기록만 유지)되고 연결 종료가 요청된다.
    // Phase 10 D1: every server-side close of one peer goes through here. The code is stored before Disconnect is
    // called (see PeerState.CloseCode). Safe from any thread: NetPeer.Disconnect is thread-safe.
    public static void Close(NetPeer peer, DisconnectCode code)
    {
        if (peer.Tag is PeerState state) state.TrySetCloseCode(code);
        peer.Disconnect(DataOf(code));
    }

    // 기능: 통계 구간마다 한 번만 남기는 오류 로그 제한(수신 Handler·Socket 오류·Callback 예외)을 초기화한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 다음 오류가 다시 로그로 기록될 수 있다.
    // Called by the game loop at each stats line: the next receive-handler exception, socket error and callback exception are logged again.
    public void ResetLogLimits()
    {
        Volatile.Write(ref _handlerErrorLogged, 0);
        Volatile.Write(ref _networkErrorLogged, 0);
        Volatile.Write(ref _callbackErrorLogged, 0);
    }

    // 기능: Callback에서 잡힌 예외를 Callback 오류로 세고, 통계 구간마다 첫 예외만 로그로 남긴다.
    // 입력: ex - 잡힌 예외, callback - 예외가 난 Callback 이름(로그용).
    // 출력: 반환값 없음. Callback 오류 수가 증가하고 필요하면 오류 로그가 기록된다.
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

    // 기능: 이후의 모든 연결 요청을 거절하도록 정지 표시를 켠다. 다시 끌 수 없다.
    // 입력: 없음.
    // 출력: 반환값 없음. 정지 표시가 켜진다.
    // From now on every connection request is refused (as ServerFull: no protocol change, and the client does not
    // retry a reject). A request already accepted is closed by Stop's DisconnectAll like every other connection.
    public void BeginStopping() => _stopping = true;
    internal bool IsStopping => _stopping;

    // 기능: LiteNetLib의 연결 요청 Callback. 요청을 처리하고, 처리 중 예외가 나면 수락 전이면 거절, 수락 후면 ServerError로 끊는다. LiteNetLib 수신 Thread에서 실행된다.
    // 입력: request - 들어온 연결 요청.
    // 출력: 반환값 없음. 요청이 수락 또는 거절되고, 수락되면 Control Channel에 Connected 메시지가 들어간다.
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

    // 기능: 정원·정지 여부, IP별 연결 비율, 요청 데이터와 Protocol Version을 검사해 연결을 수락하거나 거절하고, 수락한 연결을 Game Loop에 알린다.
    // 입력: request - 들어온 연결 요청, accepted - 수락한 Peer를 돌려줄 참조(예외 처리용, 수락 전이면 null 유지).
    // 출력: 반환값 없음. 거절 Counter 또는 연결 Counter가 증가하고, 수락하면 PeerState가 붙고 Connected 메시지가 Control Channel에 들어간다. Channel이 가득 차면 ServerError로 끊는다.
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

    // 기능: 연결 요청을 사유와 함께 거절하고 사유별로 센다.
    // 입력: request - 거절할 연결 요청, reason - 거절 사유, data - Client에 보낼 거절 데이터.
    // 출력: 반환값 없음. 거절 사유별 Counter가 증가하고 요청이 거절된다.
    // D5: rejects are counted by reason and logged at Debug only, because a flood of connection requests must not
    // flood the log.
    private void Reject(ConnectionRequest request, RejectReason reason, byte[] data)
    {
        _health.AddReject(reason);
        _logger.LogDebug("Rejected connection from {EndPoint}: {Reason}", request.RemoteEndPoint, reason);
        request.Reject(data);
    }

    // 기능: LiteNetLib의 연결 완료 Callback. 수락 처리는 OnConnectionRequest에서 이미 했고 서버는 밖으로 연결하지 않으므로 아무것도 하지 않는다.
    // 입력: peer - 연결된 Peer(아직 Tag가 없을 수 있음).
    // 출력: 반환값 없음. 상태 변화 없음.
    public void OnPeerConnected(NetPeer peer)
    {
        // Runs inside request.Accept() (see OnConnectionRequest); peer.Tag is not set yet here.
        // The server never connects out, so there is nothing else to handle.
    }

    // 기능: LiteNetLib의 연결 종료 Callback. 종료를 처리하고 예외는 잡아서 센다.
    // 입력: peer - 끊긴 연결, disconnectInfo - 종료 사유 정보.
    // 출력: 반환값 없음. 종료 Counter가 증가하고 Control Channel에 Disconnected 메시지가 들어간다.
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

    // 기능: 연결 종료를 Timeout 여부로 세고 Game Loop에 Disconnected 메시지를 보낸다.
    // 입력: peer - 끊긴 연결, disconnectInfo - 종료 사유 정보.
    // 출력: 반환값 없음. 종료 Counter가 증가하고 Disconnected 메시지가 Control Channel에 들어간다. Channel이 가득 차면 경고만 남긴다.
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

    // 기능: LiteNetLib의 Packet 수신 Callback. Packet을 처리하고, Handler 예외는 해당 Peer의 잘못된 Packet으로 센다. LiteNetLib 수신 Thread에서 실행된다.
    // 입력: peer - 보낸 연결, reader - 수신 데이터, channelNumber - LiteNetLib Channel 번호(사용 안 함), deliveryMethod - 전송 방식(사용 안 함).
    // 출력: 반환값 없음. 검증된 메시지가 Inbound Channel에 들어가거나 잘못된 Packet으로 처리된다.
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

    // 기능: 수신 Packet의 ID를 읽고 종류별로 검증·비율 검사·파싱해 Join·입력·건설 메시지는 Game Loop용 Channel에, 전적 조회 요청은 StatsQueryQueue에 넘긴다.
    // 입력: peer - 보낸 연결, reader - 수신 데이터(반환 후 재사용되므로 필요한 값은 복사한다).
    // 출력: 반환값 없음. 수신 통계가 갱신되고 메시지가 Control·Input·Build Channel이나 전적 조회 Queue에 들어가거나, 잘못된 Packet·비율 초과로 처리된다.
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
                // Phase 13 D8: only from a joined connection (like input). The game loop queues it per player.
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
                if (BuildRequest.TryRead(ref packet, out var build))
                    _channels.Build.Writer.TryWrite(new BuildMessage(peer.Id, peer, build));
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

    // 기능: LiteNetLib의 Socket 오류 Callback. 오류를 세고 통계 구간마다 첫 오류만 로그로 남긴다.
    // 입력: endPoint - 오류가 난 원격 주소, socketError - Socket 오류 코드.
    // 출력: 반환값 없음. Network 오류 수가 증가하고 필요하면 경고 로그가 기록된다.
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

    // 기능: LiteNetLib의 비연결 메시지 Callback. NetManager에서 비연결 메시지를 꺼 두었으므로 아무것도 하지 않는다.
    // 입력: remoteEndPoint - 보낸 주소, reader - 메시지 내용, messageType - 비연결 메시지 종류.
    // 출력: 반환값 없음. 상태 변화 없음.
    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        // Unconnected messages are disabled on the NetManager; nothing to do.
    }

    // 기능: LiteNetLib의 Latency 갱신 Callback. 서버는 Latency 값을 쓰지 않으므로 아무것도 하지 않는다.
    // 입력: peer - 대상 연결, latency - 측정된 Latency(ms).
    // 출력: 반환값 없음. 상태 변화 없음.
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
    }

    // 기능: 잘못된 Packet을 사유별로 세고, Peer의 누적 수가 기준에 이르면 Kicked로 연결을 끊는다.
    // 입력: peer - 잘못된 Packet을 보낸 연결, reason - 잘못된 Packet 사유.
    // 출력: 반환값 없음. 잘못된 Packet Counter와 Peer의 누적 수가 증가하고, 기준에 이르면 한 번만 Kick된다.
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
