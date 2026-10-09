using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using ProjectH.Bots;
using System.Collections.Concurrent;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Tests.Integration;

// Minimal client for tests: same wire protocol as the Unity client, no prediction or rendering.
// Every received combat, item and match event is kept in a list; a test client lives for one test only.
public sealed class HeadlessClient : IDisposable
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];
    private NetPeer _peer = null!;   // set by Connect(); tests always connect first
    private uint _nextSeq = 1;

    // 기능: 시험용 Client를 만들고 소켓을 연다(조각 상한 리뷰 수정 A1, 거절·끊김 기록과 쿠키 재시도 리뷰 수정 A3, 봇과 같은 인증 계층·
    //   UserMtu 리뷰 수정 B3).
    // 입력: 없음.
    // 출력: Connect 전의 HeadlessClient(Dispose가 닫는다).
    public HeadlessClient()
    {
        // Review fix A1: the same fragment limit as the server and the Unity client. Review fix B3: the bots' authentication
        // layer (the Unity client's rule) and the user MTU that leaves room for its tail.
        _net = new NetManager(_listener, _auth)
        {
            UnsyncedEvents = false, ChannelsCount = ProtocolConstants.ChannelCount, MaxFragmentsCount = ProtocolLimits.MaxFragments,
            MtuOverride = ProtocolLimits.UserMtu,
        };
        _listener.PeerConnectedEvent += _ => Connected = true;
        _listener.PeerDisconnectedEvent += (_, info) =>
        {
            int rejectBytes = info.Reason == LiteNetLib.DisconnectReason.ConnectionRejected && info.AdditionalData != null
                ? info.AdditionalData.AvailableBytes : 0;
            // Review fix A3: 16 bytes of reject data are the server's cookie. The request goes again at once with it
            // (once), before anything is marked: to the test this is still one connect.
            if (rejectBytes == ProtocolLimits.CookieBytes && _retryCookie && CookieRetries == 0)
            {
                info.AdditionalData!.GetBytes(_cookie, ProtocolLimits.CookieBytes);
                CookieRetries++;
                SendConnect(_cookie);
                return;
            }
            Disconnected = true;
            DisconnectReason = info.Reason;
            RejectDataLength = rejectBytes;
            // Phase 10 D1: a reject carries a RejectReason, a server close a DisconnectCode, both as one byte.
            if (rejectBytes == 1)
                RejectReason = (RejectReason)info.AdditionalData!.GetByte();
            if (info.Reason == LiteNetLib.DisconnectReason.RemoteConnectionClose && info.AdditionalData != null)
                DisconnectCode = DisconnectCodes.Read(info.AdditionalData.GetRemainingBytesSpan());
        };
        _listener.NetworkReceiveEvent += OnReceive;
        _net.Start();
    }

    // Review fix A3: what Connect sent, so the cookie retry sends the same request with the cookie.
    private readonly byte[] _cookie = new byte[ProtocolLimits.CookieBytes];
    private int _port;
    private string _devPlayerId = string.Empty;
    private ushort _protocolVersion;
    private bool _retryCookie = true;

    // Review fixes B2-B4: the authentication layer, this connect's session key, blob and keys (the cookie retry repeats them),
    // its resume proof, and the ticket that keeps the resume key. Like an honest client that keeps its keys, the ticket is
    // remembered per (server port, name) across HeadlessClient objects, so a test that reconnects with a new client of the
    // same name resumes as the Unity client would; Connect(..., resume: false) makes a client without it (an impostor).
    private static readonly ConcurrentDictionary<(int Port, string Name), ResumeTicket> Tickets = new();
    private readonly ProjectH.Bots.AuthPacketLayer _auth = new();
    private byte[] _keyBlob = Array.Empty<byte>();
    private string _serverPublicKeyXml = DevServerPublicKey.Xml;
    private SessionKeys? _keys;
    private ResumeTicket? _ticket;
    private readonly byte[] _resumeProof = new byte[ProtocolLimits.ResumeProofBytes];
    private bool _hasResume;
    private uint _resumeNonce;
    // Review fix B3: datagrams this client dropped for a failed tail after the first verified one (should stay 0).
    public long AuthDrops => _auth.AuthDrops;
    public bool SentResumeProof => _hasResume;
    // Review fix B3: a datagram from the server has opened with this connection's keys.
    public bool Verified => _auth.Verified;

    // Review fix A3: cookie retries made (1 for a normal connect), and the length of the last reject's data (16 = a cookie,
    // 1 = a RejectReason).
    public int CookieRetries { get; private set; }
    public int RejectDataLength { get; private set; }

    public bool Connected { get; private set; }
    public bool Disconnected { get; private set; }
    public DisconnectReason DisconnectReason { get; private set; }
    public RejectReason RejectReason { get; private set; }
    public DisconnectCode DisconnectCode { get; private set; }
    public JoinMatchResponse? JoinResponse { get; private set; }
    public ushort MyEntityId => JoinResponse?.MyEntityId ?? 0;
    public HashSet<ushort> Spawned { get; } = new();
    // Phase 11 D9: the name each PlayerSpawned carried, by entity id.
    public Dictionary<ushort, string> SpawnNames { get; } = new();
    public HashSet<ushort> Despawned { get; } = new();
    public Dictionary<ushort, SnapshotEntity> LastSnapshot { get; } = new();
    public uint LastAckInputSeq { get; private set; }
    public uint LastServerTick { get; private set; }
    // Counts snapshot packets, not ticks: above 90 players one tick is two packets.
    public int SnapshotsReceived { get; private set; }

    public WeaponInfo[]? Weapons { get; private set; }
    public ItemCatalogData? Items { get; private set; }
    public SnapshotSelf LastSelf { get; private set; }
    // One entry per snapshot packet, not per tick: above 90 players one tick is two packets.
    public List<SnapshotSelf> SelfHistory { get; } = new();
    public List<ShotFired> Shots { get; } = new();
    public List<HitConfirmed> Hits { get; } = new();
    public List<DamageTaken> DamageEvents { get; } = new();
    public List<PlayerDied> Deaths { get; } = new();
    public List<PlayerRespawned> Respawns { get; } = new();

    // Phase 4: the world item list as this client sees it (WorldItems at join, then upserts and removals),
    // plus every item event in arrival order.
    public Dictionary<ushort, WorldItemData> WorldItems { get; } = new();
    public List<WorldItemData> ItemsSpawned { get; } = new();
    public List<ushort> ItemsRemoved { get; } = new();
    public List<InventoryState> Inventories { get; } = new();
    public List<PickupResult> PickupResults { get; } = new();
    // Phase 16 D3, D7: every ContainerStates (spawned, opened) and SupplyDrops count received, in order.
    public List<(ulong Spawned, ulong Opened)> ContainerStates { get; } = new();
    public List<int> SupplyDropLists { get; } = new();
    private readonly SupplyDropInfo[] _supplyDrops = new SupplyDropInfo[SupplyDropsPacket.MaxSupplyDrops];

    // Phase 5: every match event in arrival order.
    public List<MatchState> MatchStates { get; } = new();
    public List<ZoneState> ZoneStates { get; } = new();
    public List<MatchResult> MatchResults { get; } = new();
    // Phase 11 D8: every StatsResponse in arrival order.
    public List<StatsResponse> StatsResponses { get; } = new();
    // Phase 12: every TransportRoute in arrival order.
    public List<DropRoute> TransportRoutes { get; } = new();
    // Phase 13: build results, and every building packet with the channel it came on.
    public List<BuildResult> BuildResults { get; } = new();
    public List<(PacketId Id, byte Channel)> BuildPackets { get; } = new();
    // Phase 15: every TeamMarkers received (ping and waypoint counts) and the latest lists.
    public List<(int Pings, int Waypoints)> TeamMarkers { get; } = new();
    public MarkerPing[] LastPings { get; } = new MarkerPing[MapMarkerConstants.MaxTeamPings];
    public MarkerWaypoint[] LastWaypoints { get; } = new MarkerWaypoint[MapMarkerConstants.MaxWaypoints];

    // 기능: 서버에 접속을 요청한다. 세션 키를 만들어 개발용 공개키로 암호화하고(리뷰 수정 B2), 이 서버·이름의 Resume 키가 있으면 증명을
    //   넣는다(B4). 쿠키가 돌아오면(리뷰 수정 A3) 같은 요청을 쿠키와 함께 한 번 바로 다시 보낸다.
    // 입력: port - 서버 포트, devPlayerId - 이름, protocolVersion - 보낼 버전(기본 현재 버전), resume - false면 Resume 키를 쓰지도
    //   남기지도 않는다(같은 이름을 쓰는 다른 사람), serverPublicKeyXml - 세션 키를 암호화할 공개키(null = 개발용 키). 같은 객체로 다시
    //   부르면(같은 로컬 포트) 연결 상태를 처음으로 돌린다.
    // 출력: 반환값 없음. 접속이 시작된다.
    public void Connect(int port, string devPlayerId, ushort protocolVersion = ProtocolConstants.ProtocolVersion, bool resume = true,
        string? serverPublicKeyXml = null)
    {
        Connected = false;
        Disconnected = false;
        JoinResponse = null;
        _serverPublicKeyXml = serverPublicKeyXml ?? DevServerPublicKey.Xml;
        _port = port;
        _devPlayerId = devPlayerId;
        _protocolVersion = protocolVersion;
        CookieRetries = 0;   // one cookie retry per Connect
        PrepareKeys(resume);
        SendConnect(null);
    }

    // 기능: 이번 접속의 세션 키·blob·키를 만들고, Resume 키가 있으면 증명을 만든다.
    // 입력: resume - Resume 표를 쓸지(false면 표를 찾지도 만들지도 않는다).
    // 출력: 반환값 없음. _keys·_keyBlob·_ticket이 새로 정해지고 증명을 만들었으면 _hasResume가 true가 된다.
    private void PrepareKeys(bool resume)
    {
        byte[] sessionKey;
        (sessionKey, _keyBlob) = SessionKeyExchange.Create(_serverPublicKeyXml);
        _keys = new SessionKeys(sessionKey, isServer: false);
        _ticket = resume ? Tickets.GetOrAdd((_port, _devPlayerId), _ => new ResumeTicket()) : null;
        _hasResume = _ticket != null && _ticket.TryMakeProof(sessionKey, _devPlayerId, _resumeProof, out _resumeNonce);
    }

    // 기능: 주어진 쿠키로 한 번만 접속을 요청한다(리뷰 수정 A3 시험: 틀린 쿠키). 쿠키가 돌아와도 다시 요청하지 않는다.
    // 입력: port - 서버 포트, devPlayerId - 이름, cookie - 보낼 16 B.
    // 출력: 반환값 없음. 접속이 시작된다.
    public void ConnectWithCookie(int port, string devPlayerId, byte[] cookie)
    {
        _port = port;
        _devPlayerId = devPlayerId;
        _protocolVersion = ProtocolConstants.ProtocolVersion;
        _retryCookie = false;
        PrepareKeys(resume: false);
        SendConnect(cookie);
    }

    // 기능: 저장한 포트·이름·버전으로 접속 요청 하나를 보낸다(cookie가 있으면 HasCookie와 쿠키, 리뷰 수정 B2·B4: 세션 키 blob과 Resume 증명.
    //   B3: Connect 직전에 이 키를 인증 계층에 넣는다).
    // 입력: cookie - 보낼 쿠키(null = 없음).
    // 출력: 반환값 없음. _peer가 새 연결이 된다.
    private void SendConnect(byte[]? cookie)
    {
        var writer = new PacketWriter(_buffer);
        ConnectRequestData.Write(ref writer, new ConnectRequestData
        {
            ProtocolVersion = _protocolVersion,
            Flags = (cookie != null ? ConnectFlags.HasCookie : ConnectFlags.None) | (_hasResume ? ConnectFlags.HasResume : ConnectFlags.None),
            Cookie = cookie,
            SessionKeyBlob = _keyBlob,
            DevPlayerId = _devPlayerId,
            ResumeNonce = _resumeNonce,
            ResumeProof = _hasResume ? _resumeProof : null,
        });
        var data = new NetDataWriter();
        data.Put(_buffer, 0, writer.Length);
        _auth.Use(_keys!);   // review fix B3: before Connect, so the request goes out sealed with this request's keys
        _peer = _net.Connect("127.0.0.1", _port, data);
    }

    // A connect request with arbitrary payload (Phase 10: malformed requests are rejected and counted).
    // The peer id the server gave this connection (LiteNetLib's RemoteId), for tests about reused ids.
    public int ServerPeerId => _peer.RemoteId;

    // 기능: 연결 요청 데이터를 그대로 보낸다(잘못된 요청 시험). 쿠키가 돌아와도 다시 요청하지 않는다(리뷰 수정 A3).
    // 입력: port - 서버 포트, payload - 요청 데이터.
    // 출력: 반환값 없음. 접속이 시작된다.
    public void ConnectRaw(int port, byte[] payload)
    {
        _retryCookie = false;   // review fix A3: a raw request is sent as it is; a cookie answer ends it

        var data = new NetDataWriter();
        data.Put(payload);
        _peer = _net.Connect("127.0.0.1", port, data);
    }

    // 기능: Join 요청을 보낸다.
    // 입력: 없음.
    // 출력: 반환값 없음. ReliableOrdered로 JoinMatchRequest 패킷 하나가 나간다.
    public void SendJoin()
    {
        var writer = new PacketWriter(_buffer);
        JoinMatchRequest.Write(ref writer);
        _peer.Send(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // 기능: 이동·조준 값으로 입력 하나를 만들어 보낸다.
    // 입력: moveX·moveY - 이동 축, yaw - 바라보는 각도, buttons - 누른 버튼.
    // 출력: 반환값 없음. 다음 Seq가 붙은 입력 패킷 하나가 나간다.
    public void SendMove(float moveX, float moveY, float yaw, InputButtons buttons = InputButtons.None)
    {
        SendInput(new InputCommand { MoveX = moveX, MoveY = moveY, Yaw = yaw, Buttons = buttons });
    }

    // 기능: 입력 하나에 다음 Seq를 붙여 보낸다.
    // 입력: command - 보낼 입력(Seq는 여기서 덮어쓴다).
    // 출력: 반환값 없음. Unreliable 입력 패킷 하나가 나가고 _nextSeq가 1 는다.
    // Sends one input; Seq is assigned here.
    public void SendInput(InputCommand command)
    {
        command.Seq = _nextSeq++;
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, command);
        var writer = new PacketWriter(_buffer);
        PlayerInputPacket.Write(ref writer, packet);
        _peer.Send(writer.WrittenSpan, DeliveryMethod.Unreliable);
    }

    // 기능: Seq를 바꾸지 않고 입력 하나를 보낸다(리뷰 수정 A4 시험: 창 밖 Seq). 다음 SendInput의 Seq는 그대로다.
    // 입력: command - Seq까지 정한 입력.
    // 출력: 반환값 없음. Unreliable 입력 패킷 하나가 나간다.
    public void SendInputWithSeq(InputCommand command)
    {
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, command);
        var writer = new PacketWriter(_buffer);
        PlayerInputPacket.Write(ref writer, packet);
        _peer.Send(writer.WrittenSpan, DeliveryMethod.Unreliable);
    }

    // 기능: 바이트를 그대로 채널 0에 보낸다(잘못된 패킷 시험).
    // 입력: data - 보낼 패킷 바이트.
    // 출력: 반환값 없음. ReliableOrdered 패킷 하나가 나간다.
    public void SendRaw(byte[] data) => _peer.Send(data, DeliveryMethod.ReliableOrdered);

    // 기능: 건설 요청을 건설 채널로 보낸다(Phase 13 D8).
    // 입력: request - 건설 요청.
    // 출력: 반환값 없음. 건설 채널에 ReliableOrdered 패킷 하나가 나간다.
    // Phase 13 D8: a build request on the building channel.
    public void SendBuild(in BuildRequest request)
    {
        var writer = new PacketWriter(_buffer);
        BuildRequest.Write(ref writer, request);
        _peer.Send(writer.WrittenSpan, ProtocolConstants.BuildChannel, DeliveryMethod.ReliableOrdered);
    }

    // 기능: 건설 편집 요청을 건설 채널로 보낸다(Phase 13.5 D4).
    // 입력: request - 편집 요청.
    // 출력: 반환값 없음. 건설 채널에 ReliableOrdered 패킷 하나가 나간다.
    // Phase 13.5 D4: an edit request on the building channel.
    public void SendBuildEdit(in BuildEditRequest request)
    {
        var writer = new PacketWriter(_buffer);
        BuildEditRequest.Write(ref writer, request);
        _peer.Send(writer.WrittenSpan, ProtocolConstants.BuildChannel, DeliveryMethod.ReliableOrdered);
    }

    // 기능: Phase 15: 지도 표시 요청을 채널 0으로 보낸다.
    // 입력: marker - 요청.
    // 출력: 반환값 없음. 채널 0에 ReliableOrdered 패킷 하나가 나간다.
    public void SendMapMarker(in MapMarker marker)
    {
        var writer = new PacketWriter(_buffer);
        MapMarker.Write(ref writer, marker);
        _peer.Send(writer.WrittenSpan, ProtocolConstants.ReliableChannel, DeliveryMethod.ReliableOrdered);
    }

    // 기능: 전적 조회 요청을 보낸다(Phase 11 D8).
    // 입력: 없음.
    // 출력: 반환값 없음. ReliableOrdered로 StatsRequest 패킷 하나가 나간다.
    public void SendStatsRequest()
    {
        var writer = new PacketWriter(_buffer);
        StatsRequest.Write(ref writer);
        _peer.Send(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // 기능: 쌓인 네트워크 이벤트를 이 스레드에서 처리한다(연결·끊김·수신 상태가 여기서 갱신된다).
    // 입력: 없음.
    // 출력: 반환값 없음. Connected·Disconnected·수신 목록이 갱신된다.
    public void Poll() => _net.PollEvents();

    // 기능: 서버에 끊는다고 알리고 연결을 닫는다(소켓은 열어 둔 채, 같은 로컬 포트로 다시 Connect할 수 있다).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void Leave() => _peer.Disconnect();

    // 기능: 서버에 알리지 않고 소켓을 닫아 Client 크래시를 흉내 낸다.
    // 입력: 없음.
    // 출력: 반환값 없음. 소켓이 닫히고 서버는 Timeout으로만 끊김을 안다.
    // Simulates a crash: the socket closes without telling the server.
    public void Kill() => _net.Stop(false);

    // 기능: 서버에 끊김을 알리고 소켓을 닫는다.
    // 입력: 없음.
    // 출력: 반환값 없음. 이 Client는 더 쓸 수 없다.
    public void Dispose() => _net.Stop();

    // 기능: 받은 서버 패킷을 테스트가 보는 목록에 기록한다(리뷰 수정 B4: Join 성공이면 이 서버·이름의 Resume 키를 바꾼다)(Phase 16: ContainerStates, SupplyDrops).
    // 입력: peer·reader·channel·method - LiteNetLib 수신 정보.
    // 출력: 반환값 없음. 패킷 종류에 맞는 속성·목록이 갱신되고, 패킷 id를 읽지 못하면 무시한다.
    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var r = new PacketReader(reader.GetRemainingBytesSpan());
        if (!r.TryReadPacketId(out PacketId id)) return;
        switch (id)
        {
            case PacketId.JoinMatchResponse:
                if (JoinMatchResponse.TryRead(ref r, out var response))
                {
                    JoinResponse = response;
                    // Review fix B4: this connection's resume key is the one the next connection with this name must prove.
                    if ((response.Result == JoinResult.Ok || response.Result == JoinResult.Resumed) && _keys != null) _ticket?.Joined(_keys.ResumeKey);
                }
                break;
            case PacketId.PlayerSpawned:
                if (PlayerSpawned.TryRead(ref r, out var spawned))
                {
                    Spawned.Add(spawned.EntityId);
                    SpawnNames[spawned.EntityId] = spawned.Name;
                }
                break;
            case PacketId.PlayerDespawned:
                if (PlayerDespawned.TryRead(ref r, out var despawned)) Despawned.Add(despawned.EntityId);
                break;
            case PacketId.WorldSnapshot:
                if (!WorldSnapshotHeader.TryRead(ref r, out var header)) return;
                // Phase 8: the packets of one tick add up; a new tick starts over.
                if (header.ServerTick != LastServerTick) LastSnapshot.Clear();
                for (int i = 0; i < header.Count; i++)
                {
                    if (SnapshotEntity.TryRead(ref r, out var e)) LastSnapshot[e.EntityId] = e;
                }
                LastAckInputSeq = header.AckInputSeq;
                LastServerTick = header.ServerTick;
                LastSelf = header.Self;
                SelfHistory.Add(header.Self);
                SnapshotsReceived++;
                break;
            case PacketId.WeaponCatalog:
                if (WeaponCatalogPacket.TryRead(ref r, out var weapons)) Weapons = weapons;
                break;
            case PacketId.ItemCatalog:
                if (ItemCatalogPacket.TryRead(ref r, out var items)) Items = items;
                break;
            case PacketId.ShotFired:
                if (ShotFired.TryRead(ref r, out var shot)) Shots.Add(shot);
                break;
            case PacketId.HitConfirmed:
                if (HitConfirmed.TryRead(ref r, out var hit)) Hits.Add(hit);
                break;
            case PacketId.DamageTaken:
                if (DamageTaken.TryRead(ref r, out var damage)) DamageEvents.Add(damage);
                break;
            case PacketId.PlayerDied:
                if (PlayerDied.TryRead(ref r, out var died)) Deaths.Add(died);
                break;
            case PacketId.PlayerRespawned:
                if (PlayerRespawned.TryRead(ref r, out var respawned)) Respawns.Add(respawned);
                break;
            case PacketId.WorldItems:
                if (!WorldItemsPacket.TryReadHeader(ref r, out int count)) return;
                for (int i = 0; i < count; i++)
                {
                    if (WorldItemData.TryRead(ref r, out var listed)) WorldItems[listed.ItemId] = listed;
                }
                break;
            case PacketId.ItemSpawned:
                if (ItemSpawnedPacket.TryRead(ref r, out var item))
                {
                    WorldItems[item.ItemId] = item;
                    ItemsSpawned.Add(item);
                }
                break;
            case PacketId.ItemRemoved:
                if (ItemRemoved.TryRead(ref r, out var removed))
                {
                    WorldItems.Remove(removed.ItemId);
                    ItemsRemoved.Add(removed.ItemId);
                }
                break;
            case PacketId.InventoryState:
                if (InventoryState.TryRead(ref r, out var inventory)) Inventories.Add(inventory);
                break;
            case PacketId.PickupResult:
                if (PickupResult.TryRead(ref r, out var pickup)) PickupResults.Add(pickup);
                break;
            case PacketId.MatchState:
                if (MatchState.TryRead(ref r, out var match)) MatchStates.Add(match);
                break;
            case PacketId.ZoneState:
                if (ZoneState.TryRead(ref r, out var zone)) ZoneStates.Add(zone);
                break;
            case PacketId.MatchResult:
                if (MatchResult.TryRead(ref r, out var result)) MatchResults.Add(result);
                break;
            case PacketId.StatsResponse:
                if (StatsResponse.TryRead(ref r, out var stats)) StatsResponses.Add(stats);
                break;
            case PacketId.TransportRoute:
                if (TransportRoutePacket.TryRead(ref r, out var route)) TransportRoutes.Add(route);
                break;
            case PacketId.BuildResult:
                BuildPackets.Add((id, channel));
                if (BuildResult.TryRead(ref r, out var built)) BuildResults.Add(built);
                break;
            case PacketId.TeamMarkers:
                if (TeamMarkersPacket.TryRead(ref r, LastPings, LastWaypoints, out int pings, out int waypoints)) TeamMarkers.Add((pings, waypoints));
                break;
            case PacketId.ContainerStates:
                if (ContainerStatesPacket.TryRead(ref r, out ulong spawnedMask, out ulong openedMask)) ContainerStates.Add((spawnedMask, openedMask));
                break;
            case PacketId.SupplyDrops:
                if (SupplyDropsPacket.TryRead(ref r, _supplyDrops, out int drops)) SupplyDropLists.Add(drops);
                break;
            case PacketId.BuildEvents:
            case PacketId.BuildSync:
            case PacketId.BuildInterest:
            case PacketId.BuildCatalog:
                BuildPackets.Add((id, channel));
                break;
        }
    }
}

public static class Pump
{
    // 기능: 주어진 Client들을 5ms마다 Poll하며 조건이 참이 될 때까지 기다린다(끝에 한 번 더 Poll하고 확인한다).
    // 입력: condition - 기다릴 조건, timeoutMs - 제한 시간(ms), clients - Poll할 Client들.
    // 출력: 제한 시간 안에(또는 마지막 확인에서) 조건이 참이면 true, 아니면 false.
    public static bool Until(Func<bool> condition, int timeoutMs, params HeadlessClient[] clients)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            foreach (var client in clients) client.Poll();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        foreach (var client in clients) client.Poll();
        return condition();
    }
}
