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

    // 기능: 수동 모드 LiteNetLib NetManager를 만들고 연결·끊김·수신 이벤트를 연결한다.
    // 입력: reconnect - 자동 재접속 시도면 true(짧은 연결 시도 예산 사용).
    // 출력: 소켓이 열렸지만 아직 접속하지 않은 연결 객체(Connected·Disconnected 모두 false, 입력 Seq는 1부터).
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
        // 기능: 연결이 끊기면 서버의 끊김 코드를 읽고 재접속 가능 여부를 정한다.
        // 입력: info - LiteNetLib 끊김 정보(사유와 서버가 붙인 추가 데이터).
        // 출력: 반환값 없음. Disconnected·Code·Retryable·DisconnectReason이 설정된다.
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

    // 기능: 프로토콜 버전과 봇 이름을 담은 ConnectRequest로 서버에 접속을 시작한다.
    // 입력: host - 서버 주소, port - 서버 UDP 포트, devPlayerId - 서버에 보낼 봇 이름.
    // 출력: 반환값 없음. 접속 시도 중인 peer가 저장된다. 결과는 이후 Update에서 연결·끊김 이벤트로 온다.
    public void Connect(string host, int port, string devPlayerId)
    {
        var writer = new PacketWriter(_buffer);
        ConnectRequestData.Write(ref writer, new ConnectRequestData { ProtocolVersion = ProtocolConstants.ProtocolVersion, DevPlayerId = devPlayerId });
        var data = new NetDataWriter();
        data.Put(_buffer, 0, writer.Length);
        _peer = _net.Connect(host, port, data);
    }

    // 기능: 호출한 러너 스레드에서 수신, LiteNetLib 타이머 처리, 이벤트 발생을 한 번 돌린다.
    // 입력: elapsedMs - 이전 Update 이후 지난 시간(ms).
    // 출력: 반환값 없음. 받은 패킷이 View에 반영되고 연결 상태가 갱신될 수 있다.
    // Receive, run LiteNetLib's timers and raise this bot's events. elapsedMs since the previous Update.
    public void Update(float elapsedMs)
    {
        _net.ManualUpdate(elapsedMs);
        _net.PollEvents();
    }

    // 기능: 새 입력에 Seq를 붙이고, 최근 입력들(최대 MaxInputsPerPacket개)과 함께 Unreliable로 보낸다.
    // 입력: command - 이번 Tick 입력(Seq는 여기서 덮어씀).
    // 출력: 반환값 없음. 접속 중이면 서버에 PlayerInput 패킷이 전송되고 Seq·InputsSent가 늘어난다. 접속 전이나 끊긴 뒤면 아무것도 하지 않는다.
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

    // 기능: 건설 요청 하나를 건설 채널로 보낸다.
    // 입력: request - 보낼 건설 요청.
    // 출력: 반환값 없음. 접속 중이면 서버에 BuildRequest 패킷이 전송되고 BuildsSent가 늘어난다. 접속 전이나 끊긴 뒤면 아무것도 하지 않는다.
    // Phase 13 D8: one build request on the building channel (ReliableOrdered; the server answers there too).
    public void SendBuild(in BuildRequest request)
    {
        if (_peer == null || !Connected || Disconnected) return;
        var writer = new PacketWriter(_buffer);
        BuildRequest.Write(ref writer, request);
        _peer.Send(writer.WrittenSpan, ProtocolConstants.BuildChannel, DeliveryMethod.ReliableOrdered);
        BuildsSent++;
    }

    // 기능: NetManager를 멈춰 소켓을 닫는다.
    // 입력: 없음.
    // 출력: 반환값 없음. 연결과 소켓이 정리된다.
    public void Dispose() => _net.Stop();

    // 기능: 접속이 성립하면 바로 매치 참가를 요청한다.
    // 입력: 없음.
    // 출력: 반환값 없음. Connected가 true가 되고 서버에 JoinMatchRequest가 ReliableOrdered로 전송된다.
    private void OnConnected()
    {
        Connected = true;
        var writer = new PacketWriter(_buffer);
        JoinMatchRequest.Write(ref writer);
        _peer!.Send(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // 기능: 받은 패킷을 PacketId별로 읽어 BotView에 반영한다. 읽기에 실패한 패킷은 버린다.
    // 입력: peer - 보낸 서버 peer, reader - 패킷 데이터, channel - 수신 채널, method - 전송 방식.
    // 출력: 반환값 없음. 수신 통계와 View(참가·스냅샷·아이템·인벤토리·매치·건설 상태 등)가 갱신된다.
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
                if (BuildResult.TryRead(ref r, out var built)) view.BuildResults[(int)built.Code]++;
                break;
            case PacketId.BuildSync:
                if (!BuildSyncPacket.TryReadHeader(ref r, out _, out bool reset, out int synced)) return;
                if (reset) view.Pieces.Clear();
                for (int i = 0; i < synced && BuildPieceRecord.TryReadSync(ref r, out var piece); i++) view.AddPiece(piece.Id);
                break;
            case PacketId.BuildEvents:
                if (!BuildEventsPacket.TryReadHeader(ref r, out _, out int placed, out int health, out int destroyed)) return;
                for (int i = 0; i < placed && BuildPieceRecord.TryReadPlaced(ref r, out var piece); i++) view.AddPiece(piece.Id);
                for (int i = 0; i < health && BuildEventsPacket.TryReadHealth(ref r, out _, out _); i++) { }
                for (int i = 0; i < destroyed && BuildEventsPacket.TryReadDestroyed(ref r, out uint gone); i++) view.Pieces.Remove(gone);
                break;
            case PacketId.BuildInterest:
                // The window moved: what we keep is not worth tracking per cell for a bot; the next syncs bring it back.
                if (BuildInterestPacket.TryRead(ref r, out _)) view.Pieces.Clear();
                break;
        }
    }
}
