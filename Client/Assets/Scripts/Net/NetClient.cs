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

    public delegate void SnapshotHandler(in WorldSnapshotHeader header, SnapshotEntity[] entities, int count);
    // Phase 15 D10: the whole team marker list, in NetClient's reused arrays (valid only during the call).
    public delegate void TeamMarkersHandler(MarkerPing[] pings, int pingCount, MarkerWaypoint[] waypoints, int waypointCount);
    // Phase 16 D7: the whole supply drop list, in NetClient's reused array (valid only during the call).
    public delegate void SupplyDropsHandler(SupplyDropInfo[] drops, int count);

    // Owns the LiteNetLib client. Main thread only: UnsyncedEvents is off and Poll() is called from
    // Update, so every callback below runs on the Unity main thread. Buffers are reused: receiving
    // and sending do not allocate per packet.
    public sealed class NetClient : INetEventListener, IDisposable
    {
        private readonly NetManager _net;
        private readonly NetDataWriter _connectData = new NetDataWriter();
        private readonly byte[] _sendBuffer = new byte[ProtocolConstants.MaxPacketSize];
        private readonly SnapshotEntity[] _snapshotEntities = new SnapshotEntity[ProtocolConstants.MaxSnapshotEntities];
        // Phase 15 D10: TeamMarkers is read into these (TryRead may leave them half written on a bad packet, so the event is
        // raised only after a whole read; the receiver copies what it keeps).
        private readonly MarkerPing[] _markerPings = new MarkerPing[MapMarkerConstants.MaxTeamPings];
        private readonly MarkerWaypoint[] _markerWaypoints = new MarkerWaypoint[MapMarkerConstants.MaxWaypoints];
        // Phase 16 D7: SupplyDrops is read into this (same rule: the event only after a whole read; the receiver copies).
        private readonly SupplyDropInfo[] _supplyDrops = new SupplyDropInfo[SupplyDropsPacket.MaxSupplyDrops];
        // The current connection. Events from any other peer (an attempt CancelConnect gave up on) are ignored.
        private NetPeer _server;
        private bool _disposed;
        // LiteNetLib's own connect budget, used by a manual Connect (Phase 10 D10).
        private readonly int _defaultReconnectDelay;
        private readonly int _defaultMaxConnectAttempts;

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
        // Phase 17 D2: with the projectile kinds (their gravity, radius and lifetime; both arrays allocated once per join).
        public event Action<WeaponInfo[], ProjectileInfo[]> CatalogReceived;
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
        // Phase 13.5 D8: a piece's state after its last edit of a tick (id, BuildEdit state, version). Applied by id.
        public event Action<uint, ushort, uint> BuildEditedReceived;
        // Phase 18 D6: a Placed record of the event stream (not a Sync), raised right after its BuildPieceReceived: a piece placed
        // now (sounds), not one this client is only told about.
        public event Action<BuildPieceRecord> BuildPlacedEventReceived;
        // Phase 18 D6, D11: a destroyed record with why it went (Destroyed by damage or Collapsed).
        public event Action<uint, BuildDestroyReason, uint> BuildDestroyedReceived;
        public event Action<uint> BuildResetReceived;
        public event Action<ulong> BuildInterestReceived;
        // Phase 14 D2, D5, D8, D10: our team, a knock-down (to everyone), a revive or reboot channel of our team, and the
        // reboot stations' cooldowns. Structs, so raising them does not allocate.
        public event Action<TeamState> TeamStateReceived;
        public event Action<PlayerDowned> PlayerDownedReceived;
        public event Action<ChannelState> ChannelStateReceived;
        public event Action<RebootStationsState> RebootStationsReceived;
        // Phase 15 D10: our team's pings and waypoints (the whole list each time).
        public event TeamMarkersHandler TeamMarkersReceived;
        // Phase 16 D3, D7: which loot containers spawned and which are open (spawned, opened), and the match's supply drops.
        public event Action<ulong, ulong> ContainerStatesReceived;
        public event SupplyDropsHandler SupplyDropsReceived;
        // Phase 17 D7: a projectile launched (or resent at a join or resume), bounced or came to rest, and exploded. Structs.
        public event Action<ProjectileSpawned> ProjectileSpawnedReceived;
        public event Action<ProjectileState> ProjectileStateReceived;
        public event Action<ProjectileExploded> ProjectileExplodedReceived;
        // Phase 18 D7: a harvest hit near us by another player (Unreliable). Struct.
        public event Action<WorldSound> WorldSoundReceived;

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
        public int RoundTripMs => _server != null ? _server.RoundTripTime : 0;

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

        public void Poll()
        {
            if (_net.IsRunning) _net.PollEvents();
        }

        public void SendInput(in PlayerInputPacket packet)
        {
            if (State != ClientState.Joined) return;
            var writer = new PacketWriter(_sendBuffer);
            PlayerInputPacket.Write(ref writer, packet);
            _server.Send(writer.WrittenSpan, DeliveryMethod.Unreliable);
        }

        public void Disconnect()
        {
            if (_server != null) _net.DisconnectPeer(_server);
        }

        // Phase 13 D8: one placement on the building channel. False when not joined.
        public bool SendBuild(in BuildRequest request)
        {
            if (State != ClientState.Joined) return false;
            var writer = new PacketWriter(_sendBuffer);
            BuildRequest.Write(ref writer, request);
            _server.Send(writer.WrittenSpan, ProtocolConstants.BuildChannel, DeliveryMethod.ReliableOrdered);
            return true;
        }

        // 기능: 조각 편집 요청 하나를 건설 채널(1)로 보낸다(Phase 13.5 D4, Reset도 같은 요청).
        // 입력: request - 순번(배치와 같은 카운터), 조각 id, 상태(BuildEdit.PackState).
        // 출력: 보냈으면 true, 참가 중이 아니면 false(아무것도 보내지 않음).
        public bool SendBuildEdit(in BuildEditRequest request)
        {
            if (State != ClientState.Joined) return false;
            var writer = new PacketWriter(_sendBuffer);
            BuildEditRequest.Write(ref writer, request);
            _server.Send(writer.WrittenSpan, ProtocolConstants.BuildChannel, DeliveryMethod.ReliableOrdered);
            return true;
        }

        // 기능: Ping 하나나 내 Waypoint 설정·삭제 요청을 신뢰 채널 0으로 보낸다(Phase 15 D7).
        // 입력: marker - 종류·위치·대상(Enemy·Item만 대상 id, 그 밖은 0: 서버 Reader가 강제한다).
        // 출력: 보냈으면 true, 참가 중이 아니면 false(아무것도 보내지 않음).
        public bool SendMapMarker(in MapMarker marker)
        {
            if (State != ClientState.Joined) return false;
            var writer = new PacketWriter(_sendBuffer);
            MapMarker.Write(ref writer, marker);
            _server.Send(writer.WrittenSpan, ProtocolConstants.ReliableChannel, DeliveryMethod.ReliableOrdered);
            return true;
        }

        // Phase 11 D8: asks for this player's statistics; the answer comes as StatsReceived. False when not joined.
        public bool RequestStats()
        {
            if (State != ClientState.Joined) return false;
            var writer = new PacketWriter(_sendBuffer);
            StatsRequest.Write(ref writer);
            _server.Send(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
            return true;
        }

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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_net.IsRunning) _net.Stop(true);
            _server = null;
            State = ClientState.Disconnected;
        }

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            if (peer != _server) return;   // an abandoned attempt (CancelConnect)
            State = ClientState.Connected;
            var writer = new PacketWriter(_sendBuffer);
            JoinMatchRequest.Write(ref writer);
            peer.Send(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
            Connected?.Invoke();
        }

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

        // 기능: 받은 패킷 하나를 읽어 해당 이벤트를 올린다(Phase 13.5: BuildEvents의 Edited 기록 포함, Phase 14: 분대 패킷 4종,
        //   Phase 15: TeamMarkers, Phase 16: ContainerStates·SupplyDrops, Phase 17: 투사체 카탈로그와 투사체 패킷 3종,
        //   Phase 18: 사건 Placed 기록은 BuildPlacedEventReceived도 올리고(Sync는 아님), Destroyed 기록은 이유와 함께, WorldSound).
        //   메인 스레드에서 Poll이 부른다.
        // 입력: peer - 보낸 쪽(지금 연결이 아니면 무시), reader - 패킷, channelNumber·deliveryMethod - 쓰지 않는다.
        // 출력: 반환값 없음. 읽기에 실패한 기록이 있으면 그 패킷의 나머지는 버린다.
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
                    if (WeaponCatalogPacket.TryRead(ref packet, out var weapons, out var projectiles)) CatalogReceived?.Invoke(weapons, projectiles);
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
                    // Phase 13.5 D8: Placed -> Edited -> Health -> Destroyed. A bad record stops the packet, as before.
                    if (!BuildEventsPacket.TryReadHeader(ref packet, out uint version, out int placed, out int edited, out int health, out int gone)) return;
                    for (int i = 0; i < placed; i++)
                    {
                        if (!BuildPieceRecord.TryReadPlaced(ref packet, out var piece)) return;
                        BuildPieceReceived?.Invoke(piece, version);
                        BuildPlacedEventReceived?.Invoke(piece);
                    }
                    for (int i = 0; i < edited; i++)
                    {
                        if (!BuildEventsPacket.TryReadEdited(ref packet, out uint editedId, out ushort editedState)) return;
                        BuildEditedReceived?.Invoke(editedId, editedState, version);
                    }
                    for (int i = 0; i < health; i++)
                    {
                        if (!BuildEventsPacket.TryReadHealth(ref packet, out uint pieceId, out ushort pieceDamage)) return;
                        BuildHealthReceived?.Invoke(pieceId, pieceDamage, version);
                    }
                    for (int i = 0; i < gone; i++)
                    {
                        if (!BuildEventsPacket.TryReadDestroyed(ref packet, out uint goneId, out BuildDestroyReason goneReason)) return;
                        BuildDestroyedReceived?.Invoke(goneId, goneReason, version);
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

                case PacketId.TeamState:
                    if (TeamState.TryRead(ref packet, out var team)) TeamStateReceived?.Invoke(team);
                    break;

                case PacketId.PlayerDowned:
                    if (PlayerDowned.TryRead(ref packet, out var downed)) PlayerDownedReceived?.Invoke(downed);
                    break;

                case PacketId.ChannelState:
                    if (ChannelState.TryRead(ref packet, out var channel)) ChannelStateReceived?.Invoke(channel);
                    break;

                case PacketId.RebootStations:
                    if (RebootStationsState.TryRead(ref packet, out var stations)) RebootStationsReceived?.Invoke(stations);
                    break;

                case PacketId.TeamMarkers:
                    if (TeamMarkersPacket.TryRead(ref packet, _markerPings, _markerWaypoints, out int pingCount, out int waypointCount))
                        TeamMarkersReceived?.Invoke(_markerPings, pingCount, _markerWaypoints, waypointCount);
                    break;

                case PacketId.ContainerStates:
                    if (ContainerStatesPacket.TryRead(ref packet, out ulong spawnedMask, out ulong openedMask))
                        ContainerStatesReceived?.Invoke(spawnedMask, openedMask);
                    break;

                case PacketId.SupplyDrops:
                    if (SupplyDropsPacket.TryRead(ref packet, _supplyDrops, out int dropCount)) SupplyDropsReceived?.Invoke(_supplyDrops, dropCount);
                    break;

                case PacketId.ProjectileSpawned:
                    if (ProjectileSpawned.TryRead(ref packet, out var projectileSpawned)) ProjectileSpawnedReceived?.Invoke(projectileSpawned);
                    break;

                case PacketId.ProjectileState:
                    if (ProjectileState.TryRead(ref packet, out var projectileState)) ProjectileStateReceived?.Invoke(projectileState);
                    break;

                case PacketId.ProjectileExploded:
                    if (ProjectileExploded.TryRead(ref packet, out var projectileExploded)) ProjectileExplodedReceived?.Invoke(projectileExploded);
                    break;

                case PacketId.WorldSound:
                    if (WorldSound.TryRead(ref packet, out var worldSound)) WorldSoundReceived?.Invoke(worldSound);
                    break;
            }
        }

        void INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            // A client never accepts incoming connections.
            request.Reject();
        }

        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            LastError = "Network error: " + socketError;
        }

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
        }

        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
        }
    }
}
