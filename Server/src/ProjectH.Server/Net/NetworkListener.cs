using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using LiteNetLib;
using Microsoft.Extensions.Logging;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Game.Build;
using ProjectH.Server.Game.Map;
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

    // Server review M2, review fixes A2 and A6: connection requests, connections held and penalties per remote IP (fixed
    // table). The request bucket is used by OnConnectionRequest only (LiteNetLib's receive thread). The connection count is
    // raised at Accept by the receive thread and lowered by whatever thread runs OnPeerDisconnected (the game loop's Close,
    // a LiteNetLib thread), with Interlocked (review A round 1). The game loop also calls the Interlocked Penalize.
    private readonly ConnectRateLimiter _connectRate;
    // Review fix A2: accepts per second over all addresses (receive thread only).
    private readonly AcceptRateLimiter _accept;
    // Review fix A3: the stateless connect cookie, and two 16-byte buffers made once and used on the receive thread only
    // (the cookie a request carried, the cookie a RejectForce sends back; LiteNetLib copies reject data into its packet).
    private readonly ConnectCookie _cookie;
    private readonly byte[] _cookieIn = new byte[ProtocolLimits.CookieBytes];
    private readonly byte[] _cookieReply = new byte[ProtocolLimits.CookieBytes];

    // Phase 13 D8: build requests a peer may send per second (building.json); more are invalid packets.
    private readonly int _maxBuildRequestsPerSecond;
    // Phase 15 D7 (map.json): MapMarker packets a peer gets through per second and at once, and the per-second count above
    // which they are invalid packets.
    private readonly int _pingsPerSecond;
    private readonly int _pingBurst;
    private readonly int _maxMarkerPacketsPerSecond;

    // 기능: 수신 처리기를 만든다(Phase 15: MapMarker 속도 제한 수치를 map.json에서 받는다. 리뷰 수정 A2·A3: IP별 표의 salt,
    //   전역 수락 Bucket, 쿠키 비밀을 시작 때 난수로 만든다).
    // 입력: options - 서버 설정, channels - Game Loop로 넘길 채널, stats·health - 수치, statsQueries - 통계 요청 큐, logger - 로그,
    //   maxBuildRequestsPerSecond - 건설 요청 초당 상한, map - Ping 수치(null = 배포 기본값).
    // 출력: 연결을 받을 준비가 된 NetworkListener(Manager는 GameLoop가 정한다).
    public NetworkListener(ServerOptions options, InboundChannels channels, ServerStats stats, HealthCounters health,
        StatsQueryQueue statsQueries, ILogger logger, int maxBuildRequestsPerSecond = 20, MapCatalog? map = null)
    {
        _maxBuildRequestsPerSecond = maxBuildRequestsPerSecond;
        map ??= MapCatalog.Default(options.SimHz);
        _pingsPerSecond = map.PingsPerSecond;
        _pingBurst = map.PingBurst;
        _maxMarkerPacketsPerSecond = map.MaxMarkerPacketsPerSecond;
        _options = options;
        _channels = channels;
        _stats = stats;
        _health = health;
        _statsQueries = statsQueries;
        _logger = logger;
        _connectRate = new ConnectRateLimiter(options.ConnectBurstPerIp, options.ConnectsPerIpPerSecond, options.MaxConnectionsPerIp,
            BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4)));
        _accept = new AcceptRateLimiter(options.AcceptBurst, options.AcceptsPerSecond);
        _cookie = new ConnectCookie(RandomNumberGenerator.GetBytes(ConnectCookie.SecretBytes));
    }

    // Review fix A6: the game loop penalizes a failing source through it (ConnectRateLimiter.Penalize, Interlocked).
    internal ConnectRateLimiter ConnectRate => _connectRate;

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

    // 기능: LiteNetLib 연결 요청 콜백. HandleConnectionRequest를 부르고 예외를 밖으로 내보내지 않는다.
    // 입력: request - 연결 요청.
    // 출력: 반환값 없음. 받거나 거절된다(예외면 Accept 전은 RejectForce(ServerFull), Accept 뒤는 ServerError로 닫는다).
    // Server review L9: nothing may escape into LiteNetLib's thread, which serves every connection. A request that throws
    // before Accept is refused (as ServerFull; not counted as a reject, callbackErrors counts it). One that throws after
    // Accept is closed with ServerError: the game loop may never have heard of it, and nothing else would free its slot.
    public void OnConnectionRequest(ConnectionRequest request)
    {
        NetPeer? accepted = null;
        int countedSlot = -1;
        try
        {
            HandleConnectionRequest(request, ref accepted, ref countedSlot);
        }
        catch (Exception ex)
        {
            OnCallbackError(ex, "connection request");
            try
            {
                // Review A round 1: counted but not yet handed to a PeerState (the disconnect below cannot find the slot
                // through peer.Tag), so it is given back here, once.
                if (countedSlot >= 0) _connectRate.Release(countedSlot);
                if (accepted != null)
                {
                    _health.AddKick(DisconnectCode.ServerError);
                    Close(accepted, DisconnectCode.ServerError);
                }
                else
                {
                    request.RejectForce(RejectServerFull);   // review fix A3: no peer for a request that failed before Accept
                }
            }
            catch
            {
                // The request or the peer is beyond help; LiteNetLib's own timeout ends it.
            }
        }
    }

    // 기능: 연결 요청 하나를 받거나 거절한다. 순서(리뷰 수정 A2·A3): 정지·꽉 참 → 버전(첫 필드) → 형식·이름 → 쿠키(없으면 쿠키를
    //   돌려주고, 틀리면 거절) → IP별 빈도·벌점·동시 연결 → 전역 수락 → Accept. 토큰·연결 수는 쿠키를 통과한 요청만 쓴다.
    //   모든 거절은 RejectForce(16 B 쿠키 또는 1 B RejectReason)라 서버에 임시 peer가 없다.
    // 입력: request - LiteNetLib 연결 요청, accepted - Accept한 peer를 받을 곳(예외 경로가 닫는다), countedSlot - 세었지만 아직
    //   PeerState에 넘기지 않은 IP 칸(예외 경로가 반환한다. PeerState에 넘긴 뒤에는 -1, 끊길 때 HandleDisconnect가 반환한다).
    // 출력: 반환값 없음. 받으면 PeerState가 붙고 그 IP 칸에 연결이 세어지며 Control 채널에 Connected가 들어간다.
    private void HandleConnectionRequest(ConnectionRequest request, ref NetPeer? accepted, ref int countedSlot)
    {
        CallbackFaultHook?.Invoke("request");
        if (_stopping || Manager.ConnectedPeersCount >= _options.MaxPlayers)
        {
            Reject(request, RejectReason.ServerFull, RejectServerFull);
            return;
        }

        var data = request.Data;
        if (data == null || data.AvailableBytes == 0)
        {
            Reject(request, RejectReason.BadRequest, RejectBadRequest);
            return;
        }

        var reader = new PacketReader(new ReadOnlySpan<byte>(data.RawData, data.Position, data.AvailableBytes));
        // The version is the first field of every layout, so an older client (v18 has no flags) is told VersionMismatch
        // instead of BadRequest. One byte back, no peer: as cheap as the cookie answer.
        var versionReader = reader;
        if (!versionReader.TryReadUInt16(out ushort version))
        {
            Reject(request, RejectReason.BadRequest, RejectBadRequest);
            return;
        }
        if (version != ProtocolConstants.ProtocolVersion)
        {
            Reject(request, RejectReason.VersionMismatch, RejectVersionMismatch);
            return;
        }
        if (!ConnectRequestData.TryRead(ref reader, out var connect, _cookieIn))
        {
            Reject(request, RejectReason.BadRequest, RejectBadRequest);
            return;
        }

        IPEndPoint remote = request.RemoteEndPoint;
        long now = Environment.TickCount64;
        // Review fix A3 (SEC-4): the first request of every connect has no cookie and gets one back (the normal path,
        // counted as cookieChallenges). A wrong or expired one is refused (counted as cookie) and also gets a fresh cookie,
        // so a client whose cookie went stale can retry. Neither takes a token or a slot.
        bool hasCookie = (connect.Flags & ConnectFlags.HasCookie) != 0;
        if (!hasCookie || !_cookie.Verify(remote, now, connect.Cookie))
        {
            if (hasCookie)
            {
                _health.AddCookieReject();
                _logger.LogDebug("Rejected connection from {EndPoint}: wrong connect cookie", remote);
            }
            else
            {
                _health.AddCookieChallenge();
            }
            _cookie.Make(remote, now, _cookieReply);
            request.RejectForce(_cookieReply, 0, ProtocolLimits.CookieBytes);
            return;
        }

        // Server review M2, review fixes A2 and A6: after the cookie, so a forged source spends nothing. The client is told
        // ServerFull (it does not retry a reject); the Health line counts each reason apart.
        if (!_connectRate.TryAcquire(remote.Address, now, out ConnectRefusal refusal))
        {
            switch (refusal)
            {
                case ConnectRefusal.Rate: _health.AddConnectRateReject(); break;
                case ConnectRefusal.PerIp: _health.AddPerIpReject(); break;
                default: _health.AddPenaltyReject(); break;
            }
            _logger.LogDebug("Rejected connection from {EndPoint}: {Refusal}", remote, refusal);
            request.RejectForce(RejectServerFull);
            return;
        }
        // Review fix A2: accepts over all addresses, which ServerOptions.ControlChannelCapacity is sized from.
        if (!_accept.TryAcquire(now))
        {
            _health.AddAcceptRateReject();
            _logger.LogDebug("Rejected connection from {EndPoint}: over the global accept rate", remote);
            request.RejectForce(RejectServerFull);
            return;
        }

        int slot = _connectRate.SlotOf(remote.Address);
        NetPeer peer = request.Accept();
        accepted = peer;
        // Review fix A2, review A round 1: counted right after Accept, before the PeerState exists. Until the Tag is set the
        // count belongs to this request (countedSlot: the exception path gives it back); from then on to the PeerState, whose
        // disconnect gives it back (HandleDisconnect). Nothing can disconnect the peer in between: its packets arrive on
        // this thread, and the game loop does not know it before the Connected message below.
        _connectRate.Acquired(slot);
        countedSlot = slot;
        peer.Tag = new PeerState(connect.DevPlayerId, slot);
        countedSlot = -1;
        CallbackFaultHook?.Invoke("accepted");
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

    // 기능: 연결 요청을 RejectReason 1 B로 거절하고 이유별로 센다(D5: Debug 로그만, 요청 홍수가 로그를 채우지 않게).
    //   리뷰 수정 A3: RejectForce라 서버에 임시 peer와 재전송이 없다(Client는 답을 못 받으면 요청을 다시 보내고 다시 답을 받는다).
    // 입력: request - 연결 요청, reason - 셀 이유, data - 보낼 1 B.
    // 출력: 반환값 없음. 거절 패킷 하나가 나간다.
    private void Reject(ConnectionRequest request, RejectReason reason, byte[] data)
    {
        _health.AddReject(reason);
        _logger.LogDebug("Rejected connection from {EndPoint}: {Reason}", request.RemoteEndPoint, reason);
        request.RejectForce(data);
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

    // 기능: 끊긴 연결을 처리한다: 그 IP 칸의 연결 수를 내리고(리뷰 수정 A2), 세고, Game Loop에 Disconnected를 알린다.
    // 입력: peer - 끊긴 연결, disconnectInfo - LiteNetLib 끊김 정보.
    // 출력: 반환값 없음. Control 채널에 Disconnected가 들어간다(가득 차면 stale-peer 정리가 대신한다).
    private void HandleDisconnect(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        // Review fix A2: the connection counted at accept leaves its address's count (first, so nothing below can keep it
        // counted; once: every accepted peer gets one disconnect).
        if (peer.Tag is PeerState counted && counted.ConnectSlot >= 0) _connectRate.Release(counted.ConnectSlot);
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

    // 기능: Client 패킷 하나를 검증·파싱해 해당 채널에 넣는다(Join, 입력, 건설, Phase 15 지도 표시, 통계). 잘못된 패킷은 센다.
    //   리뷰 수정 A1: ProtocolLimits.MaxClientPacketBytes보다 큰 패킷과 본문이 있는 Join은 파싱 전에 Malformed다.
    // 입력: peer - 보낸 연결, reader - 받은 바이트.
    // 출력: 반환값 없음. 채널에 메시지가 들어가거나 잘못된 패킷·속도 초과로 세어진다.
    private void Receive(NetPeer peer, NetPacketReader reader)
    {
        ReceiveFaultHook?.Invoke();
        // AutoRecycle is on: the reader's buffer is reused after this returns, so everything the
        // game loop needs is copied into value-type messages here.
        ReadOnlySpan<byte> data = reader.GetRemainingBytesSpan();
        _stats.AddIn(data.Length);
        // Review fix A1 (SEC-1): no client packet is larger (PlayerInput, 92 B, is the largest), so a bigger one is never
        // parsed. LiteNetLib already refuses a message of more than ProtocolLimits.MaxFragments fragments.
        if (data.Length > ProtocolLimits.MaxClientPacketBytes)
        {
            OnBadPacket(peer, BadPacketReason.Malformed);
            return;
        }

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
                // Review fix A1: the request has no body. Checked before JoinRequested is set, so a correct Join may follow.
                if (packet.Remaining != 0)
                {
                    OnBadPacket(peer, BadPacketReason.Malformed);
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

            case PacketId.MapMarker:
                // Phase 15 D7: only from a joined connection (like input and building; without this case the id would be a
                // WrongDirection invalid packet). Every packet counts in the 1-second window first, so a flood is invalid
                // (the kick threshold) even while the bucket drops it; then the bucket lets pingsPerSecond through and drops
                // the rest without a kick (Health markerDrops), like the input rate.
                if (peer.Tag is not PeerState markerState || !markerState.JoinRequested)
                {
                    OnBadPacket(peer, BadPacketReason.InputBeforeJoin);
                    break;
                }
                long markerNow = Environment.TickCount64;
                if (!markerState.TryCountMarkerPacket(markerNow, _maxMarkerPacketsPerSecond))
                {
                    OnBadPacket(peer, BadPacketReason.MarkerRate);
                    break;
                }
                if (!markerState.TryTakeMarkerToken(markerNow, _pingsPerSecond, _pingBurst))
                {
                    _health.AddMarkerDrop();
                    break;
                }
                if (MapMarker.TryRead(ref packet, out var marker))
                    _channels.Marker.Writer.TryWrite(new MarkerMessage(peer.Id, peer, marker));
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
