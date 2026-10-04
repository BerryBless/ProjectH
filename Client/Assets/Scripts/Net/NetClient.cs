using System;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Net
{
    public enum ClientState
    {
        Disconnected,
        Connecting,
        Connected,
        Joined,
    }

    // 기능: WorldSnapshot 수신 Handler 형식이다.
    // 입력: header - Snapshot Header, entities - 재사용되는 Entity Buffer, count - 이번 Snapshot에서 유효한 Entity 수.
    // 출력: 반환값 없음. entities는 다음 Snapshot에 덮어써지므로 Handler 안에서만 읽는다.
    public delegate void SnapshotHandler(in WorldSnapshotHeader header, SnapshotEntity[] entities, int count);

    // Owns the LiteNetLib client. Main thread only: UnsyncedEvents is off and Poll() is called from
    // Update, so every callback below runs on the Unity main thread. Buffers are reused: receiving
    // and sending do not allocate per packet.
    public sealed class NetClient : INetEventListener, IDisposable
    {
        private readonly NetManager _net;
        private readonly NetDataWriter _connectData = new NetDataWriter();
        private readonly byte[] _sendBuffer = new byte[ProtocolConstants.MaxPacketSize];
        private readonly SnapshotEntity[] _snapshotEntities = new SnapshotEntity[ProtocolConstants.MaxSnapshotEntities];
        // The current connection. Events from any other peer (an attempt CancelConnect gave up on) are ignored.
        private NetPeer _server;
        private bool _disposed;
        // LiteNetLib's own connect budget, used by a manual Connect (Phase 10 D10).
        private readonly int _defaultReconnectDelay;
        private readonly int _defaultMaxConnectAttempts;

        // 기능: LiteNetLib NetManager를 Main Thread Poll 방식으로 만든다(Socket은 Connect에서 연다).
        // 입력: 없음.
        // 출력: Disconnected 상태이고 LiteNetLib 기본 재접속 간격·시도 횟수를 기억한 NetClient.
        public NetClient()
        {
            _net = new NetManager(this, null)
            {
                UnsyncedEvents = false,
                AutoRecycle = true,
                DisconnectTimeout = 5000,   // default PingInterval (1000 ms) stays below a quarter of this
                IPv6Enabled = false,
                MtuOverride = ProtocolConstants.Mtu,   // same value as the server so both sides agree on datagram size
                ChannelsCount = ProtocolConstants.ChannelCount,   // Phase 13 D13: channel 1 is the building stream
            };
            _defaultReconnectDelay = _net.ReconnectDelay;
            _defaultMaxConnectAttempts = _net.MaxConnectAttempts;
        }

        public event Action Connected;
        public event Action<JoinMatchResponse> Joined;
        public event Action<PlayerSpawned> SpawnReceived;
        public event Action<ushort> DespawnReceived;
        public event SnapshotHandler SnapshotReceived;
        public event Action<string> Disconnected;
        // Phase 3 combat (D4, D9, D11). Payloads are structs, so raising them does not allocate
        // (the catalog array is allocated once per join by its reader).
        public event Action<WeaponInfo[]> CatalogReceived;
        public event Action<ShotFired> ShotReceived;
        public event Action<HitConfirmed> HitConfirmedReceived;
        public event Action<DamageTaken> DamageTakenReceived;
        public event Action<PlayerDied> PlayerDiedReceived;
        public event Action<PlayerRespawned> PlayerRespawnedReceived;
        // Phase 4 items (D14). WorldItems chunks and ItemSpawned both arrive as ItemReceived (an upsert).
        // The item catalog is allocated once per join by its reader; the rest are structs.
        public event Action<ItemCatalogData> ItemCatalogReceived;
        public event Action<WorldItemData> ItemReceived;
        public event Action<ushort> ItemRemovedReceived;
        public event Action<InventoryState> InventoryReceived;
        public event Action<PickupResult> PickupResultReceived;
        // Phase 5 match flow (D11): structs, so raising them does not allocate. A dev-respawn server sends none.
        public event Action<MatchState> MatchStateReceived;
        public event Action<ZoneState> ZoneStateReceived;
        public event Action<MatchResult> MatchResultReceived;
        // Phase 11 D8: the answer to RequestStats (a class allocated by its reader, once per answer).
        public event Action<StatsResponse> StatsReceived;
        // Phase 12 D5, D9: the drop transport route, and which doors are open.
        public event Action<DropRoute> TransportRouteReceived;
        public event Action<byte> DoorStatesReceived;
        // Phase 13 D6: which harvestables are destroyed.
        public event Action<ulong> HarvestStatesReceived;
        // Phase 13 D4, D7, D13-D15: the building numbers, our resources, our harvest hits, and the building stream (channel
        // 1): a request's result, pieces placed or synced (with the stream's version), health, destroyed, a reset sync, the
        // interest window. Structs, so raising them does not allocate (the catalog is allocated once per join).
        public event Action<BuildCatalogData> BuildCatalogReceived;
        public event Action<ResourcesState> ResourcesReceived;
        public event Action<HarvestHit> HarvestHitReceived;
        public event Action<BuildResult> BuildResultReceived;
        public event Action<BuildPieceRecord, uint> BuildPieceReceived;
        public event Action<uint, ushort, uint> BuildHealthReceived;
        public event Action<uint, uint> BuildDestroyedReceived;
        public event Action<uint> BuildResetReceived;
        public event Action<ulong> BuildInterestReceived;

        public ClientState State { get; private set; } = ClientState.Disconnected;
        public string LastError { get; private set; }
        // Phase 10 D1, D10: why the last connection ended (None unless the server closed it with a code), and whether
        // that kind of end may be retried (DisconnectCodes.ShouldReconnect, the same table the bots use).
        public DisconnectCode LastDisconnectCode { get; private set; }
        public bool LastDisconnectRetryable { get; private set; }
        // Phase 11 D6: the rest of what the disconnected screen explains (UiText.Disconnect). Connect resets the reject,
        // the join answer and the start failure; a disconnect sets the reason (and the reject, when refused).
        public DisconnectReason LastDisconnectReason { get; private set; }
        public RejectReason LastRejectReason { get; private set; }
        public JoinResult LastJoinResult { get; private set; }
        public bool LastConnectStartFailed { get; private set; }
        // 기능: 현재 연결의 왕복 지연을 읽는다.
        // 입력: 없음.
        // 출력: 연결 중이면 서버 Peer의 RTT(ms), 아니면 0.
        public int RoundTripMs => _server != null ? _server.RoundTripTime : 0;

        // 기능: 서버에 접속 요청(ProtocolVersion, DevPlayerId를 담은 Connect Data)을 보낸다. Disconnected 상태에서만 동작한다.
        // 입력: host - 서버 주소, port - 서버 Port, devPlayerId - 개발용 플레이어 ID(32바이트 이하), reconnectAttempt - 자동 재접속이면 짧은 접속 예산을 쓴다.
        // 출력: 반환값 없음. 시작되면 Connecting 상태가 된다. Socket 열기·ID 길이·주소 해석에 실패하면 Disconnected로 남고 LastError가 설정된다.
        // reconnectAttempt (Phase 10 D10): an automatic attempt gets the short connect budget of DisconnectCodes, so it
        // gives up within its slot (about 1.5 s instead of LiteNetLib's 5.5 s). A manual connect gets the defaults
        // back. LiteNetLib reads both fields on every update of a connecting peer (they are plain public fields,
        // written only by its constructor), so setting them before Connect applies to this connect.
        public void Connect(string host, int port, string devPlayerId, bool reconnectAttempt = false)
        {
            if (_disposed || State != ClientState.Disconnected) return;
            LastRejectReason = RejectReason.None;
            LastJoinResult = JoinResult.Ok;
            LastConnectStartFailed = true;   // until the connect below is under way
            _net.ReconnectDelay = reconnectAttempt ? DisconnectCodes.ReconnectRequestIntervalMs : _defaultReconnectDelay;
            _net.MaxConnectAttempts = reconnectAttempt ? DisconnectCodes.ReconnectRequestAttempts : _defaultMaxConnectAttempts;
            if (!_net.IsRunning && !_net.Start())
            {
                LastError = "Failed to open a local UDP socket.";
                return;
            }

            var writer = new PacketWriter(_sendBuffer);
            ConnectRequestData.Write(ref writer, new ConnectRequestData { ProtocolVersion = ProtocolConstants.ProtocolVersion, DevPlayerId = devPlayerId });
            if (writer.Overflowed)
            {
                LastError = "DevPlayerId is longer than 32 bytes.";
                return;
            }
            _connectData.Reset();
            _connectData.Put(_sendBuffer, 0, writer.Length);

            try
            {
                // Host comes from user input or command line: an unresolvable name is an expected failure.
                _server = _net.Connect(host, port, _connectData);
            }
            catch (Exception ex) when (ex is SocketException || ex is ArgumentException)
            {
                _server = null;
                LastError = "Connect failed: " + ex.Message;
                return;
            }
            LastError = null;
            LastConnectStartFailed = _server == null;
            State = _server != null ? ClientState.Connecting : ClientState.Disconnected;
        }

        // 기능: 받은 LiteNetLib Event를 처리한다. GameClient.Update에서 매 Frame 호출한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 쌓인 Event가 Main Thread에서 아래 Listener Callback으로 전달된다.
        public void Poll()
        {
            if (_net.IsRunning) _net.PollEvents();
        }

        // 기능: 플레이어 입력을 서버로 보낸다. Joined 상태가 아니면 무시한다.
        // 입력: packet - 보낼 입력 Packet.
        // 출력: 반환값 없음. PlayerInputPacket이 기본 Channel에 Unreliable로 전송된다.
        public void SendInput(in PlayerInputPacket packet)
        {
            if (State != ClientState.Joined) return;
            var writer = new PacketWriter(_sendBuffer);
            PlayerInputPacket.Write(ref writer, packet);
            _server.Send(writer.WrittenSpan, DeliveryMethod.Unreliable);
        }

        // 기능: 현재 서버 연결을 끊는다.
        // 입력: 없음.
        // 출력: 반환값 없음. 연결이 있으면 끊기고, 이후 OnPeerDisconnected에서 Disconnected 상태로 바뀌며 Disconnected Event가 발생한다.
        public void Disconnect()
        {
            if (_server != null) _net.DisconnectPeer(_server);
        }

        // 기능: 건설 배치 요청을 서버로 보낸다.
        // 입력: request - 보낼 건설 요청.
        // 출력: Joined면 true(BuildRequest가 건설 Channel에 ReliableOrdered로 전송됨), 아니면 false.
        // Phase 13 D8: one placement on the building channel. False when not joined.
        public bool SendBuild(in BuildRequest request)
        {
            if (State != ClientState.Joined) return false;
            var writer = new PacketWriter(_sendBuffer);
            BuildRequest.Write(ref writer, request);
            _server.Send(writer.WrittenSpan, ProtocolConstants.BuildChannel, DeliveryMethod.ReliableOrdered);
            return true;
        }

        // 기능: 이 플레이어의 누적 통계를 서버에 요청한다.
        // 입력: 없음.
        // 출력: Joined면 true(StatsRequest가 기본 Channel에 ReliableOrdered로 전송됨, 응답은 StatsReceived), 아니면 false.
        // Phase 11 D8: asks for this player's statistics; the answer comes as StatsReceived. False when not joined.
        public bool RequestStats()
        {
            if (State != ClientState.Joined) return false;
            var writer = new PacketWriter(_sendBuffer);
            StatsRequest.Write(ref writer);
            _server.Send(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
            return true;
        }

        // 기능: 아직 Join하지 않은 접속 시도·연결을 Disconnected Event 없이 포기한다.
        // 입력: 없음.
        // 출력: 반환값 없음. Connecting·Connected 상태였으면 Peer를 잊고 Disconnected 상태가 되며, Last* 값은 유지된다.
        // Phase 10 D10: gives up a connect that is still in progress, without a Disconnected event: the caller starts
        // the next attempt at once. Phase 11: also a connection that is connected but not joined yet (the disconnected
        // screen's Cancel), so the Last* fields keep the end that started the reconnect cycle. The peer is forgotten
        // before DisconnectPeer, because LiteNetLib may raise that peer's disconnect inside the call; its events are
        // then ignored (not _server). A joined connection is never given up this way (Disconnect raises the event).
        public void CancelConnect()
        {
            if ((State != ClientState.Connecting && State != ClientState.Connected) || _server == null) return;
            NetPeer abandoned = _server;
            _server = null;
            State = ClientState.Disconnected;
            _net.DisconnectPeer(abandoned);
        }

        // 기능: NetManager를 멈추고 연결을 정리한다. 두 번 호출해도 안전하다.
        // 입력: 없음.
        // 출력: 반환값 없음. Socket이 닫히고 Disconnected 상태가 되며 이후 Connect는 무시된다.
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_net.IsRunning) _net.Stop(true);
            _server = null;
            State = ClientState.Disconnected;
        }

        // 기능: 서버 연결 성공을 처리하고 Join 요청을 보낸다. Poll 안에서 Main Thread로 호출된다.
        // 입력: peer - 연결된 Peer.
        // 출력: 반환값 없음. 현재 연결이면 Connected 상태가 되고 JoinMatchRequest가 기본 Channel에 ReliableOrdered로 전송된 뒤 Connected Event가 발생한다. 포기한 시도의 Peer는 무시한다.
        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            if (peer != _server) return;   // an abandoned attempt (CancelConnect)
            State = ClientState.Connected;
            var writer = new PacketWriter(_sendBuffer);
            JoinMatchRequest.Write(ref writer);
            peer.Send(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
            Connected?.Invoke();
        }

        // 기능: 서버 연결 종료를 처리하고 종료 이유를 기록한다. Poll 안에서 Main Thread로 호출된다.
        // 입력: peer - 끊긴 Peer, disconnectInfo - LiteNetLib 종료 이유와 서버가 보낸 추가 데이터.
        // 출력: 반환값 없음. 현재 연결이면 Disconnected 상태가 되고 LastDisconnectCode·Retryable·Reason·RejectReason·LastError가 설정된 뒤 Disconnected Event가 발생한다.
        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            if (peer != _server) return;   // an abandoned attempt (CancelConnect): not this connection's end
            _server = null;
            State = ClientState.Disconnected;
            DisconnectReason why = disconnectInfo.Reason;
            bool remoteClose = why == DisconnectReason.RemoteConnectionClose;
            LastDisconnectCode = remoteClose && disconnectInfo.AdditionalData != null
                ? DisconnectCodes.Read(disconnectInfo.AdditionalData.GetRemainingBytesSpan())
                : DisconnectCode.None;
            bool networkLoss = why == DisconnectReason.Timeout || why == DisconnectReason.ConnectionFailed ||
                               why == DisconnectReason.HostUnreachable || why == DisconnectReason.NetworkUnreachable;
            LastDisconnectRetryable = DisconnectCodes.ShouldReconnect(remoteClose, LastDisconnectCode, networkLoss);
            LastDisconnectReason = why;
            LastRejectReason = RejectReason.None;

            string reason = why.ToString();
            if (why == DisconnectReason.ConnectionRejected &&
                disconnectInfo.AdditionalData != null && disconnectInfo.AdditionalData.AvailableBytes > 0)
            {
                LastRejectReason = (RejectReason)disconnectInfo.AdditionalData.GetByte();
                reason = "Rejected: " + LastRejectReason;
            }
            else if (LastDisconnectCode != DisconnectCode.None)
            {
                reason = Describe(LastDisconnectCode);
            }
            LastError = reason;
            Disconnected?.Invoke(reason);
        }

        // 기능: 서버 종료 코드를 화면·로그용 문장으로 바꾼다.
        // 입력: code - 서버가 보낸 종료 코드.
        // 출력: 코드에 맞는 고정 문자열. 모르는 코드는 "Disconnected".
        // Constant strings: no allocation.
        private static string Describe(DisconnectCode code)
        {
            switch (code)
            {
                case DisconnectCode.ServerShutdown: return "Server shut down";
                case DisconnectCode.Kicked: return "Kicked: too many invalid packets";
                case DisconnectCode.JoinTimeout: return "Join timed out";
                case DisconnectCode.InputTimeout: return "Disconnected: no input for too long";
                case DisconnectCode.ServerError: return "Server error: the match was reset";
                case DisconnectCode.Congested: return "Disconnected: the connection was too slow";
                default: return "Disconnected";
            }
        }

        // 기능: 현재 연결에서 받은 Packet을 PacketId별로 읽어 해당 Event를 발생시킨다. Poll 안에서 Main Thread로 호출된다.
        // 입력: peer - 보낸 Peer, reader - 받은 데이터, channelNumber - 수신 Channel, deliveryMethod - 전송 방식.
        // 출력: 반환값 없음. 읽기에 성공한 Packet마다 Event가 발생하고, JoinMatchResponse가 Ok·Resumed면 Joined 상태가 된다. 다른 Peer나 잘못된 Packet은 무시하며, 목록 Packet은 읽기 실패 지점에서 멈춘다.
        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            if (peer != _server) return;
            var packet = new PacketReader(reader.GetRemainingBytesSpan());
            if (!packet.TryReadPacketId(out PacketId id)) return;

            switch (id)
            {
                case PacketId.JoinMatchResponse:
                    if (JoinMatchResponse.TryRead(ref packet, out var response))
                    {
                        LastJoinResult = response.Result;
                        // Phase 10 D2: Resumed is a join into our own character; the server resends the full state.
                        if (response.Result == JoinResult.Ok || response.Result == JoinResult.Resumed) State = ClientState.Joined;
                        Joined?.Invoke(response);
                    }
                    break;

                case PacketId.PlayerSpawned:
                    if (PlayerSpawned.TryRead(ref packet, out var spawned)) SpawnReceived?.Invoke(spawned);
                    break;

                case PacketId.PlayerDespawned:
                    if (PlayerDespawned.TryRead(ref packet, out var despawned)) DespawnReceived?.Invoke(despawned.EntityId);
                    break;

                case PacketId.WorldSnapshot:
                    if (!WorldSnapshotHeader.TryRead(ref packet, out var header)) return;
                    int count = 0;
                    for (int i = 0; i < header.Count && i < _snapshotEntities.Length; i++)
                    {
                        if (!SnapshotEntity.TryRead(ref packet, out _snapshotEntities[count])) break;
                        count++;
                    }
                    SnapshotReceived?.Invoke(header, _snapshotEntities, count);
                    break;

                case PacketId.WeaponCatalog:
                    if (WeaponCatalogPacket.TryRead(ref packet, out var weapons)) CatalogReceived?.Invoke(weapons);
                    break;

                case PacketId.ShotFired:
                    if (ShotFired.TryRead(ref packet, out var shot)) ShotReceived?.Invoke(shot);
                    break;

                case PacketId.HitConfirmed:
                    if (HitConfirmed.TryRead(ref packet, out var hit)) HitConfirmedReceived?.Invoke(hit);
                    break;

                case PacketId.DamageTaken:
                    if (DamageTaken.TryRead(ref packet, out var damage)) DamageTakenReceived?.Invoke(damage);
                    break;

                case PacketId.PlayerDied:
                    if (PlayerDied.TryRead(ref packet, out var died)) PlayerDiedReceived?.Invoke(died);
                    break;

                case PacketId.PlayerRespawned:
                    if (PlayerRespawned.TryRead(ref packet, out var respawned)) PlayerRespawnedReceived?.Invoke(respawned);
                    break;

                case PacketId.ItemCatalog:
                    if (ItemCatalogPacket.TryRead(ref packet, out var items)) ItemCatalogReceived?.Invoke(items);
                    break;

                case PacketId.WorldItems:
                    if (!WorldItemsPacket.TryReadHeader(ref packet, out int itemCount)) return;
                    for (int i = 0; i < itemCount; i++)
                    {
                        if (!WorldItemData.TryRead(ref packet, out var listed)) break;
                        ItemReceived?.Invoke(listed);
                    }
                    break;

                case PacketId.ItemSpawned:
                    if (ItemSpawnedPacket.TryRead(ref packet, out var item)) ItemReceived?.Invoke(item);
                    break;

                case PacketId.ItemRemoved:
                    if (ItemRemoved.TryRead(ref packet, out var removed)) ItemRemovedReceived?.Invoke(removed.ItemId);
                    break;

                case PacketId.InventoryState:
                    if (InventoryState.TryRead(ref packet, out var inventory)) InventoryReceived?.Invoke(inventory);
                    break;

                case PacketId.PickupResult:
                    if (PickupResult.TryRead(ref packet, out var pickup)) PickupResultReceived?.Invoke(pickup);
                    break;

                case PacketId.MatchState:
                    if (MatchState.TryRead(ref packet, out var match)) MatchStateReceived?.Invoke(match);
                    break;

                case PacketId.ZoneState:
                    if (ZoneState.TryRead(ref packet, out var zone)) ZoneStateReceived?.Invoke(zone);
                    break;

                case PacketId.MatchResult:
                    if (MatchResult.TryRead(ref packet, out var result)) MatchResultReceived?.Invoke(result);
                    break;

                case PacketId.StatsResponse:
                    if (StatsResponse.TryRead(ref packet, out var stats)) StatsReceived?.Invoke(stats);
                    break;

                case PacketId.TransportRoute:
                    if (TransportRoutePacket.TryRead(ref packet, out var route)) TransportRouteReceived?.Invoke(route);
                    break;

                case PacketId.DoorStates:
                    if (DoorStatesPacket.TryRead(ref packet, out byte doors)) DoorStatesReceived?.Invoke(doors);
                    break;

                case PacketId.HarvestStates:
                    if (HarvestStatesPacket.TryRead(ref packet, out ulong destroyed)) HarvestStatesReceived?.Invoke(destroyed);
                    break;

                case PacketId.BuildCatalog:
                    if (BuildCatalogPacket.TryRead(ref packet, out var buildCatalog)) BuildCatalogReceived?.Invoke(buildCatalog);
                    break;

                case PacketId.ResourcesState:
                    if (ResourcesState.TryRead(ref packet, out var resources)) ResourcesReceived?.Invoke(resources);
                    break;

                case PacketId.HarvestHit:
                    if (HarvestHit.TryRead(ref packet, out var harvestHit)) HarvestHitReceived?.Invoke(harvestHit);
                    break;

                case PacketId.BuildResult:
                    if (BuildResult.TryRead(ref packet, out var buildResult)) BuildResultReceived?.Invoke(buildResult);
                    break;

                case PacketId.BuildEvents:
                    if (!BuildEventsPacket.TryReadHeader(ref packet, out uint version, out int placed, out int health, out int gone)) return;
                    for (int i = 0; i < placed; i++)
                    {
                        if (!BuildPieceRecord.TryReadPlaced(ref packet, out var piece)) return;
                        BuildPieceReceived?.Invoke(piece, version);
                    }
                    for (int i = 0; i < health; i++)
                    {
                        if (!BuildEventsPacket.TryReadHealth(ref packet, out uint pieceId, out ushort pieceDamage)) return;
                        BuildHealthReceived?.Invoke(pieceId, pieceDamage, version);
                    }
                    for (int i = 0; i < gone; i++)
                    {
                        if (!BuildEventsPacket.TryReadDestroyed(ref packet, out uint goneId)) return;
                        BuildDestroyedReceived?.Invoke(goneId, version);
                    }
                    break;

                case PacketId.BuildSync:
                    if (!BuildSyncPacket.TryReadHeader(ref packet, out uint syncVersion, out bool reset, out int synced)) return;
                    if (reset) BuildResetReceived?.Invoke(syncVersion);
                    for (int i = 0; i < synced; i++)
                    {
                        if (!BuildPieceRecord.TryReadSync(ref packet, out var piece)) return;
                        BuildPieceReceived?.Invoke(piece, syncVersion);
                    }
                    break;

                case PacketId.BuildInterest:
                    if (BuildInterestPacket.TryRead(ref packet, out ulong cells)) BuildInterestReceived?.Invoke(cells);
                    break;
            }
        }

        // 기능: 들어오는 연결 요청을 거절한다. Client는 연결을 받지 않는다.
        // 입력: request - 들어온 연결 요청.
        // 출력: 반환값 없음. 요청이 거절된다.
        void INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            // A client never accepts incoming connections.
            request.Reject();
        }

        // 기능: Socket 오류를 기록한다. Poll 안에서 Main Thread로 호출된다.
        // 입력: endPoint - 오류가 난 주소, socketError - Socket 오류 코드.
        // 출력: 반환값 없음. LastError가 설정된다.
        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            LastError = "Network error: " + socketError;
        }

        // 기능: 연결 없는 Message를 받는 Callback이다. 사용하지 않아 무시한다.
        // 입력: remoteEndPoint - 보낸 주소, reader - 받은 데이터, messageType - Message 종류.
        // 출력: 반환값 없음. 아무것도 바뀌지 않는다.
        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
        }

        // 기능: 지연 갱신 Callback이다. RTT는 RoundTripMs가 Peer 값을 직접 읽으므로 무시한다.
        // 입력: peer - 대상 Peer, latency - 갱신된 지연(ms).
        // 출력: 반환값 없음. 아무것도 바뀌지 않는다.
        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
        }
    }
}
