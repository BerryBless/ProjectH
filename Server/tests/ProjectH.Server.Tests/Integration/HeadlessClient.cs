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
// Every received combat event is kept in a list; a test client lives for one test only.
public sealed class HeadlessClient : IDisposable
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];
    private NetPeer _peer = null!;   // set by Connect(); tests always connect first
    private uint _nextSeq = 1;

    public HeadlessClient()
    {
        _net = new NetManager(_listener, null) { UnsyncedEvents = false };
        _listener.PeerConnectedEvent += _ => Connected = true;
        _listener.PeerDisconnectedEvent += (_, info) =>
        {
            Disconnected = true;
            DisconnectReason = info.Reason;
            if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0)
                RejectReason = (RejectReason)info.AdditionalData.GetByte();
        };
        _listener.NetworkReceiveEvent += OnReceive;
        _net.Start();
    }

    public bool Connected { get; private set; }
    public bool Disconnected { get; private set; }
    public DisconnectReason DisconnectReason { get; private set; }
    public RejectReason RejectReason { get; private set; }
    public JoinMatchResponse? JoinResponse { get; private set; }
    public ushort MyEntityId => JoinResponse?.MyEntityId ?? 0;
    public HashSet<ushort> Spawned { get; } = new();
    public HashSet<ushort> Despawned { get; } = new();
    public Dictionary<ushort, SnapshotEntity> LastSnapshot { get; } = new();
    public uint LastAckInputSeq { get; private set; }
    public uint LastServerTick { get; private set; }
    public int SnapshotsReceived { get; private set; }

    public WeaponInfo[]? Weapons { get; private set; }
    public SnapshotSelf LastSelf { get; private set; }
    public List<SnapshotSelf> SelfHistory { get; } = new();
    public List<ShotFired> Shots { get; } = new();
    public List<HitConfirmed> Hits { get; } = new();
    public List<DamageTaken> DamageEvents { get; } = new();
    public List<PlayerDied> Deaths { get; } = new();
    public List<PlayerRespawned> Respawns { get; } = new();

    public void Connect(int port, string devPlayerId, ushort protocolVersion = ProtocolConstants.ProtocolVersion)
    {
        var writer = new PacketWriter(_buffer);
        ConnectRequestData.Write(ref writer, new ConnectRequestData { ProtocolVersion = protocolVersion, DevPlayerId = devPlayerId });
        var data = new NetDataWriter();
        data.Put(_buffer, 0, writer.Length);
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
                if (PlayerSpawned.TryRead(ref r, out var spawned)) Spawned.Add(spawned.EntityId);
                break;
            case PacketId.PlayerDespawned:
                if (PlayerDespawned.TryRead(ref r, out var despawned)) Despawned.Add(despawned.EntityId);
                break;
            case PacketId.WorldSnapshot:
                if (!WorldSnapshotHeader.TryRead(ref r, out var header)) return;
                LastSnapshot.Clear();
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
