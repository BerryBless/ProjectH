using System;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using ProjectH.Shared.Protocol;

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

        public NetClient()
        {
            _net = new NetManager(this, null)
            {
                UnsyncedEvents = false,
                AutoRecycle = true,
                DisconnectTimeout = 5000,   // default PingInterval (1000 ms) stays below a quarter of this
                IPv6Enabled = false,
                MtuOverride = ProtocolConstants.Mtu,   // same value as the server so both sides agree on datagram size
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
                default: return "Disconnected";
            }
        }

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
