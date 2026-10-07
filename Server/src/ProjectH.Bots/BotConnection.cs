using LiteNetLib;
using LiteNetLib.Utils;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Bots;

// Phase 7 D2: one bot's connection. LiteNetLib in manual mode: no threads of its own; the runner thread calls
// Update, which receives, runs LiteNetLib's logic and raises events on that same thread. Packets go into the
// BotView; inputs go out exactly like the Unity client's (D8). Dispose closes the socket.
public sealed class BotConnection : IDisposable
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];
    private readonly InputCommand[] _sent = new InputCommand[ProtocolConstants.MaxInputsPerPacket];
    private NetPeer? _peer;
    private int _sentCount;
    private uint _nextSeq = 1;

    // reconnect: an automatic reconnect attempt (Phase 10 D11). It gets the short connect budget of DisconnectCodes, so
    // it fails within its slot instead of after LiteNetLib's default 5.5 s. Each attempt is a new BotConnection.
    public BotConnection(bool reconnect = false)
    {
        // Phase 13 D13: the same channels as the server (channel 1: building).
        _net = new NetManager(_listener) { UnsyncedEvents = false, AutoRecycle = true, ChannelsCount = ProtocolConstants.ChannelCount };
        if (reconnect)
        {
            _net.ReconnectDelay = DisconnectCodes.ReconnectRequestIntervalMs;
            _net.MaxConnectAttempts = DisconnectCodes.ReconnectRequestAttempts;
        }
        _listener.PeerConnectedEvent += _ => OnConnected();
        _listener.PeerDisconnectedEvent += (_, info) =>
        {
            Disconnected = true;
            // Phase 10 D1, D11: the server's code, and the same reconnect rule as the Unity client (DisconnectCodes).
            bool remoteClose = info.Reason == LiteNetLib.DisconnectReason.RemoteConnectionClose;
            Code = remoteClose && info.AdditionalData != null
                ? DisconnectCodes.Read(info.AdditionalData.GetRemainingBytesSpan())
                : DisconnectCode.None;
            bool networkLoss = info.Reason is LiteNetLib.DisconnectReason.Timeout or LiteNetLib.DisconnectReason.ConnectionFailed
                or LiteNetLib.DisconnectReason.HostUnreachable or LiteNetLib.DisconnectReason.NetworkUnreachable;
            Retryable = DisconnectCodes.ShouldReconnect(remoteClose, Code, networkLoss);
            DisconnectReason = Code == DisconnectCode.None ? info.Reason.ToString() : $"{info.Reason} ({Code})";
        };
        _listener.NetworkReceiveEvent += OnReceive;
        _net.StartInManualMode(0);
    }

    public BotView View { get; } = new();
    public bool Connected { get; private set; }
    public bool Disconnected { get; private set; }
    public string DisconnectReason { get; private set; } = string.Empty;
    public DisconnectCode Code { get; private set; }
    // Phase 10 D11: whether this disconnect may be retried (DisconnectCodes.ShouldReconnect).
    public bool Retryable { get; private set; }
    public long PacketsIn { get; private set; }
    public long BytesIn { get; private set; }
    public long InputsSent { get; private set; }
    // Phase 13 D17: build requests sent (on the building channel).
    public long BuildsSent { get; private set; }
    // Phase 13.5 D4: edit requests sent (QA buildEdit).
    public long BuildEditsSent { get; private set; }

    public void Connect(string host, int port, string devPlayerId)
    {
        var writer = new PacketWriter(_buffer);
        ConnectRequestData.Write(ref writer, new ConnectRequestData { ProtocolVersion = ProtocolConstants.ProtocolVersion, DevPlayerId = devPlayerId });
        var data = new NetDataWriter();
        data.Put(_buffer, 0, writer.Length);
        _peer = _net.Connect(host, port, data);
    }

    // Receive, run LiteNetLib's timers and raise this bot's events. elapsedMs since the previous Update.
    public void Update(float elapsedMs)
    {
        _net.ManualUpdate(elapsedMs);
        _net.PollEvents();
    }

    // One new input: Seq is assigned here, and the packet repeats the last ones for loss (D8, like the client).
    public void SendInput(InputCommand command)
    {
        if (_peer == null || !Connected || Disconnected) return;
        command.Seq = _nextSeq++;
        if (_sentCount < _sent.Length)
        {
            _sent[_sentCount++] = command;
        }
        else
        {
            for (int i = 1; i < _sent.Length; i++) _sent[i - 1] = _sent[i];
            _sent[_sent.Length - 1] = command;
        }
        var packet = new PlayerInputPacket { Count = (byte)_sentCount };
        for (int i = 0; i < _sentCount; i++) packet.Set(i, _sent[i]);
        var writer = new PacketWriter(_buffer);
        PlayerInputPacket.Write(ref writer, packet);
        _peer.Send(writer.WrittenSpan, DeliveryMethod.Unreliable);
        InputsSent++;
    }

    // Phase 13 D8: one build request on the building channel (ReliableOrdered; the server answers there too).
    public void SendBuild(in BuildRequest request)
    {
        if (_peer == null || !Connected || Disconnected) return;
        var writer = new PacketWriter(_buffer);
        BuildRequest.Write(ref writer, request);
        _peer.Send(writer.WrittenSpan, ProtocolConstants.BuildChannel, DeliveryMethod.ReliableOrdered);
        BuildsSent++;
    }

    // 기능: 편집 요청 하나를 건설 채널로 보낸다(Phase 13.5 D4, ReliableOrdered, 서버 답도 같은 채널).
    // 입력: request - 편집 요청(순번은 BuildRequest와 같은 카운터).
    // 출력: 반환값 없음. 연결되어 있으면 패킷이 전송되고 BuildEditsSent가 는다.
    public void SendBuildEdit(in BuildEditRequest request)
    {
        if (_peer == null || !Connected || Disconnected) return;
        var writer = new PacketWriter(_buffer);
        BuildEditRequest.Write(ref writer, request);
        _peer.Send(writer.WrittenSpan, ProtocolConstants.BuildChannel, DeliveryMethod.ReliableOrdered);
        BuildEditsSent++;
    }

    public void Dispose() => _net.Stop();

    // QA tool (D14, request §127): send bytes as they are, on the reliable channel so the server receives every one
    // (fragmented when larger than the MTU). For invalid-packet tests only; the bots never call it. Returns false when
    // not connected.
    public bool SendRaw(ReadOnlySpan<byte> data)
    {
        if (_peer == null || !Connected || Disconnected || data.Length == 0) return false;
        _peer.Send(data, DeliveryMethod.ReliableOrdered);
        return true;
    }

    // QA tool: round trip time of the connection in ms (0 before it connects). Read on the thread that calls Update.
    public int RoundTripTimeMs => _peer?.RoundTripTime ?? 0;

    // QA tool: close the socket without telling the server, like a pulled cable. The server notices only through its
    // own timeout, so the drop takes the network-loss path (grace) instead of a clean leave. Marks this connection gone
    // at once: LiteNetLib raises no disconnect event for a local stop.
    public void Abort()
    {
        _net.Stop(false);
        if (!Disconnected)
        {
            Disconnected = true;
            DisconnectReason = "Aborted (QA)";
        }
    }

    private void OnConnected()
    {
        Connected = true;
        var writer = new PacketWriter(_buffer);
        JoinMatchRequest.Write(ref writer);
        _peer!.Send(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        PacketsIn++;
        BytesIn += reader.AvailableBytes;
        var r = new PacketReader(reader.GetRemainingBytesSpan());
        if (!r.TryReadPacketId(out PacketId id)) return;
        BotView view = View;
        switch (id)
        {
            case PacketId.JoinMatchResponse:
                // Phase 10 D2: Resumed is a join too (the server's grace kept this bot's character).
                if (JoinMatchResponse.TryRead(ref r, out var response) &&
                    (response.Result == JoinResult.Ok || response.Result == JoinResult.Resumed))
                {
                    view.Joined = true;
                    view.MyId = response.MyEntityId;
                    if (response.SimHz > 0) view.SimHz = response.SimHz;
                }
                break;
            case PacketId.WorldSnapshot:
                if (!WorldSnapshotHeader.TryRead(ref r, out var header)) return;
                view.ApplySnapshot(header);
                for (int i = 0; i < header.Count; i++)
                {
                    if (SnapshotEntity.TryRead(ref r, out var entity)) view.ApplyEntity(entity);
                    else break;
                }
                break;
            case PacketId.WeaponCatalog:
                if (WeaponCatalogPacket.TryRead(ref r, out var weapons)) view.Weapons = weapons;
                break;
            case PacketId.ItemCatalog:
                if (ItemCatalogPacket.TryRead(ref r, out var catalog)) view.Catalog = catalog;
                break;
            case PacketId.HitConfirmed:
                if (HitConfirmed.TryRead(ref r, out _)) view.HitsLanded++;
                break;
            case PacketId.PlayerDied:
                if (PlayerDied.TryRead(ref r, out var died)) view.ApplyDeath(died);
                break;
            case PacketId.PlayerRespawned:
                if (PlayerRespawned.TryRead(ref r, out var respawned)) view.ApplyRespawn(respawned);
                break;
            case PacketId.WorldItems:
                if (!WorldItemsPacket.TryReadHeader(ref r, out int count)) return;
                for (int i = 0; i < count; i++)
                {
                    if (WorldItemData.TryRead(ref r, out var listed)) view.ApplyItem(listed);
                    else break;
                }
                break;
            case PacketId.ItemSpawned:
                if (ItemSpawnedPacket.TryRead(ref r, out var item)) view.ApplyItem(item);
                break;
            case PacketId.ItemRemoved:
                if (ItemRemoved.TryRead(ref r, out var removed)) view.Items.Remove(removed.ItemId);
                break;
            case PacketId.InventoryState:
                if (InventoryState.TryRead(ref r, out var inventory))
                {
                    view.Inventory = inventory;
                    view.HasInventory = true;
                }
                break;
            case PacketId.MatchState:
                if (MatchState.TryRead(ref r, out var match)) view.ApplyMatch(match);
                break;
            case PacketId.ZoneState:
                if (ZoneState.TryRead(ref r, out var zone)) view.Zone = zone;
                break;
            case PacketId.MatchResult:
                if (MatchResult.TryRead(ref r, out var result))
                {
                    view.LastResult = result;
                    view.MatchResults++;
                }
                break;
            case PacketId.TransportRoute:
                if (TransportRoutePacket.TryRead(ref r, out var route))
                {
                    view.Route = route;
                    view.HasRoute = true;
                }
                break;
            // Phase 13 D17.
            case PacketId.DamageTaken:
                if (DamageTaken.TryRead(ref r, out var damage)) view.ApplyDamage(damage);
                break;
            case PacketId.BuildCatalog:
                if (BuildCatalogPacket.TryRead(ref r, out var buildCatalog)) view.BuildCatalog = buildCatalog;
                break;
            case PacketId.ResourcesState:
                if (ResourcesState.TryRead(ref r, out var resources)) view.Resources = resources;
                break;
            case PacketId.BuildResult:
                if (BuildResult.TryRead(ref r, out var built))
                {
                    // TryRead bounds the code (at most NotFound), the array's last index.
                    view.BuildResults[(int)built.Code]++;
                    view.AddBuildResult(built);
                }
                break;
            case PacketId.BuildSync:
                if (!BuildSyncPacket.TryReadHeader(ref r, out _, out bool reset, out int synced)) return;
                if (reset) view.ClearPieces();
                for (int i = 0; i < synced && BuildPieceRecord.TryReadSync(ref r, out var piece); i++) view.AddPiece(piece);
                break;
            case PacketId.BuildEvents:
                // Phase 13.5 D8: Placed, Edited, Health, Destroyed in that order.
                if (!BuildEventsPacket.TryReadHeader(ref r, out _, out int placed, out int edited, out int health, out int destroyed)) return;
                for (int i = 0; i < placed && BuildPieceRecord.TryReadPlaced(ref r, out var piece); i++) view.AddPiece(piece);
                for (int i = 0; i < edited && BuildEventsPacket.TryReadEdited(ref r, out uint editedId, out ushort state); i++) view.ApplyEdited(editedId, state);
                for (int i = 0; i < health && BuildEventsPacket.TryReadHealth(ref r, out _, out _); i++) { }
                for (int i = 0; i < destroyed && BuildEventsPacket.TryReadDestroyed(ref r, out uint gone); i++) view.RemovePiece(gone);
                break;
            case PacketId.BuildInterest:
                // The window moved: what we keep is not worth tracking per cell for a bot; the next syncs bring it back.
                if (BuildInterestPacket.TryRead(ref r, out _)) view.ClearPieces();
                break;
        }
    }
}
