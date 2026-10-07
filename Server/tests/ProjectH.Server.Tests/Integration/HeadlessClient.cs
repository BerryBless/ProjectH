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

    public HeadlessClient()
    {
        _net = new NetManager(_listener, null) { UnsyncedEvents = false, ChannelsCount = ProtocolConstants.ChannelCount };
        _listener.PeerConnectedEvent += _ => Connected = true;
        _listener.PeerDisconnectedEvent += (_, info) =>
        {
            Disconnected = true;
            DisconnectReason = info.Reason;
            // Phase 10 D1: a reject carries a RejectReason, a server close a DisconnectCode, both as one byte.
            if (info.Reason == LiteNetLib.DisconnectReason.ConnectionRejected && info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0)
                RejectReason = (RejectReason)info.AdditionalData.GetByte();
            if (info.Reason == LiteNetLib.DisconnectReason.RemoteConnectionClose && info.AdditionalData != null)
                DisconnectCode = DisconnectCodes.Read(info.AdditionalData.GetRemainingBytesSpan());
        };
        _listener.NetworkReceiveEvent += OnReceive;
        _net.Start();
    }

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

    public void Connect(int port, string devPlayerId, ushort protocolVersion = ProtocolConstants.ProtocolVersion)
    {
        var writer = new PacketWriter(_buffer);
        ConnectRequestData.Write(ref writer, new ConnectRequestData { ProtocolVersion = protocolVersion, DevPlayerId = devPlayerId });
        var data = new NetDataWriter();
        data.Put(_buffer, 0, writer.Length);
        _peer = _net.Connect("127.0.0.1", port, data);
    }

    // A connect request with arbitrary payload (Phase 10: malformed requests are rejected and counted).
    // The peer id the server gave this connection (LiteNetLib's RemoteId), for tests about reused ids.
    public int ServerPeerId => _peer.RemoteId;

    public void ConnectRaw(int port, byte[] payload)
    {
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
