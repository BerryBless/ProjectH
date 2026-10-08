using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
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

    // 기능: 시험용 Client를 만들고 소켓을 연다(조각 상한 리뷰 수정 A1, 거절·끊김 기록과 쿠키 재시도 리뷰 수정 A3).
    // 입력: 없음.
    // 출력: Connect 전의 HeadlessClient(Dispose가 닫는다).
    public HeadlessClient()
    {
        // Review fix A1: the same fragment limit as the server and the Unity client.
        _net = new NetManager(_listener, null)
        {
            UnsyncedEvents = false, ChannelsCount = ProtocolConstants.ChannelCount, MaxFragmentsCount = ProtocolLimits.MaxFragments,
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

    // 기능: 서버에 접속을 요청한다. 쿠키가 돌아오면(리뷰 수정 A3) 한 번 바로 쿠키를 넣어 다시 요청한다.
    // 입력: port - 서버 포트, devPlayerId - 이름, protocolVersion - 보낼 버전(기본 현재 버전).
    // 출력: 반환값 없음. 접속이 시작된다.
    public void Connect(int port, string devPlayerId, ushort protocolVersion = ProtocolConstants.ProtocolVersion)
    {
        _port = port;
        _devPlayerId = devPlayerId;
        _protocolVersion = protocolVersion;
        CookieRetries = 0;   // one cookie retry per Connect
        SendConnect(null);
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
        SendConnect(cookie);
    }

    // 기능: 저장한 포트·이름·버전으로 접속 요청 하나를 보낸다(cookie가 있으면 HasCookie와 쿠키).
    // 입력: cookie - 보낼 쿠키(null = 없음).
    // 출력: 반환값 없음. _peer가 새 연결이 된다.
    private void SendConnect(byte[]? cookie)
    {
        var writer = new PacketWriter(_buffer);
        ConnectRequestData.Write(ref writer, new ConnectRequestData
        {
            ProtocolVersion = _protocolVersion,
            Flags = cookie != null ? ConnectFlags.HasCookie : ConnectFlags.None,
            Cookie = cookie,
            DevPlayerId = _devPlayerId,
        });
        var data = new NetDataWriter();
        data.Put(_buffer, 0, writer.Length);
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

    public void SendJoin()
    {
        var writer = new PacketWriter(_buffer);
        JoinMatchRequest.Write(ref writer);
        _peer.Send(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    public void SendMove(float moveX, float moveY, float yaw, InputButtons buttons = InputButtons.None)
    {
        SendInput(new InputCommand { MoveX = moveX, MoveY = moveY, Yaw = yaw, Buttons = buttons });
    }

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

    public void SendRaw(byte[] data) => _peer.Send(data, DeliveryMethod.ReliableOrdered);

    // Phase 13 D8: a build request on the building channel.
    public void SendBuild(in BuildRequest request)
    {
        var writer = new PacketWriter(_buffer);
        BuildRequest.Write(ref writer, request);
        _peer.Send(writer.WrittenSpan, ProtocolConstants.BuildChannel, DeliveryMethod.ReliableOrdered);
    }

    // Phase 13.5 D4: an edit request on the building channel.
    public void SendBuildEdit(in BuildEditRequest request)
    {
        var writer = new PacketWriter(_buffer);
        BuildEditRequest.Write(ref writer, request);
        _peer.Send(writer.WrittenSpan, ProtocolConstants.BuildChannel, DeliveryMethod.ReliableOrdered);
    }

    // 기능: Phase 15: 지도 표시 요청을 채널 0으로 보낸다.
    // 입력: marker - 요청.
    // 출력: 반환값 없음.
    public void SendMapMarker(in MapMarker marker)
    {
        var writer = new PacketWriter(_buffer);
        MapMarker.Write(ref writer, marker);
        _peer.Send(writer.WrittenSpan, ProtocolConstants.ReliableChannel, DeliveryMethod.ReliableOrdered);
    }

    public void SendStatsRequest()
    {
        var writer = new PacketWriter(_buffer);
        StatsRequest.Write(ref writer);
        _peer.Send(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    public void Poll() => _net.PollEvents();

    // Simulates a crash: the socket closes without telling the server.
    public void Kill() => _net.Stop(false);

    public void Dispose() => _net.Stop();

    // 기능: 받은 서버 패킷을 테스트가 보는 목록에 기록한다(Phase 16: ContainerStates, SupplyDrops).
    // 입력: peer·reader·channel·method - LiteNetLib 수신 정보.
    // 출력: 반환값 없음.
    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var r = new PacketReader(reader.GetRemainingBytesSpan());
        if (!r.TryReadPacketId(out PacketId id)) return;
        switch (id)
        {
            case PacketId.JoinMatchResponse:
                if (JoinMatchResponse.TryRead(ref r, out var response)) JoinResponse = response;
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
