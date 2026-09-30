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
        private NetPeer _server;
        private bool _disposed;

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

        public ClientState State { get; private set; } = ClientState.Disconnected;
        public string LastError { get; private set; }
        public int RoundTripMs => _server != null ? _server.RoundTripTime : 0;

        public void Connect(string host, int port, string devPlayerId)
        {
            if (_disposed || State != ClientState.Disconnected) return;
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
            State = ClientState.Connected;
            var writer = new PacketWriter(_sendBuffer);
            JoinMatchRequest.Write(ref writer);
            peer.Send(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
            Connected?.Invoke();
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            _server = null;
            State = ClientState.Disconnected;
            string reason = disconnectInfo.Reason.ToString();
            if (disconnectInfo.Reason == DisconnectReason.ConnectionRejected &&
                disconnectInfo.AdditionalData != null && disconnectInfo.AdditionalData.AvailableBytes > 0)
            {
                reason = "Rejected: " + (RejectReason)disconnectInfo.AdditionalData.GetByte();
            }
            LastError = reason;
            Disconnected?.Invoke(reason);
        }

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            var packet = new PacketReader(reader.GetRemainingBytesSpan());
            if (!packet.TryReadPacketId(out PacketId id)) return;

            switch (id)
            {
                case PacketId.JoinMatchResponse:
                    if (JoinMatchResponse.TryRead(ref packet, out var response))
                    {
                        if (response.Result == JoinResult.Ok) State = ClientState.Joined;
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
