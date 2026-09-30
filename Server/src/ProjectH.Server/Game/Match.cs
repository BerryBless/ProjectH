using System;
using System.Collections.Generic;
using System.Numerics;
using LiteNetLib;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

public delegate void SendPacket(int peerId, ReadOnlySpan<byte> data, DeliveryMethod method);

// All player state of one match. Game loop thread only: the network thread never touches it,
// so nothing here takes a lock.
public sealed class Match
{
    private const float SpawnRadius = 5f;

    private readonly Dictionary<int, PlayerEntity> _playersByPeer = new();
    // Players are removed only in Leave(); both collections are updated together.
    private readonly List<PlayerEntity> _players = new();
    private readonly byte[] _sendBuffer = new byte[ProtocolConstants.MaxPacketSize];
    private readonly SendPacket _send;
    private readonly int _maxPlayers;
    private readonly int _snapshotEveryTicks;
    private readonly int _inputCapacity;
    private readonly byte _simHz;
    private readonly byte _snapshotHz;
    private readonly float _tickSeconds;
    private ushort _nextEntityId = 1;

    public Match(ServerOptions options, SendPacket send)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _maxPlayers = options.MaxPlayers;
        _snapshotEveryTicks = options.SnapshotEveryTicks;
        _inputCapacity = options.InputBufferPerPlayer;
        _simHz = (byte)options.SimHz;
        _snapshotHz = options.SnapshotHz;
        _tickSeconds = 1f / options.SimHz;
    }

    public uint ServerTick { get; private set; }
    public int PlayerCount => _players.Count;

    public long TotalBufferDrops
    {
        get
        {
            long total = 0;
            foreach (var player in _players) total += player.Inputs.DroppedCount;
            return total;
        }
    }

    public bool TryGetPlayer(int peerId, out PlayerEntity player) => _playersByPeer.TryGetValue(peerId, out player!);

    public JoinResult TryJoin(int peerId, string devPlayerId)
    {
        if (_playersByPeer.ContainsKey(peerId)) return JoinResult.AlreadyJoined;
        if (_players.Count >= _maxPlayers)
        {
            SendJoinResponse(peerId, JoinResult.MatchFull, 0);
            return JoinResult.MatchFull;
        }

        var player = new PlayerEntity(AllocateEntityId(), peerId, devPlayerId, _inputCapacity);
        player.State.Position = SpawnPosition(player.EntityId);
        _playersByPeer.Add(peerId, player);
        _players.Add(player);

        SendJoinResponse(peerId, JoinResult.Ok, player.EntityId);
        // Everyone (including itself) to the newcomer, then the newcomer to everyone else.
        foreach (var other in _players) SendSpawned(peerId, other);
        foreach (var other in _players)
        {
            if (other != player) SendSpawned(other.PeerId, player);
        }
        return JoinResult.Ok;
    }

    public void Leave(int peerId)
    {
        if (!_playersByPeer.Remove(peerId, out var player)) return;
        _players.Remove(player);

        var writer = new PacketWriter(_sendBuffer);
        PlayerDespawned.Write(ref writer, new PlayerDespawned { EntityId = player.EntityId });
        foreach (var other in _players) _send(other.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    public void EnqueueInput(int peerId, in PlayerInputPacket packet)
    {
        if (!_playersByPeer.TryGetValue(peerId, out var player)) return;
        for (int i = 0; i < packet.Count; i++) player.Inputs.Add(packet.Get(i));
    }

    public void Tick()
    {
        foreach (var player in _players)
        {
            InputCommand input;
            if (player.Inputs.TryTake(out input))
            {
                player.LastInput = input;
                player.LastProcessedSeq = input.Seq;
                player.MissedTicks = 0;
            }
            else if (player.MissedTicks < _simHz / 2)
            {
                player.MissedTicks++;
                // No input arrived in time: keep moving the same way, but never repeat a jump.
                // The ack does not advance, so the client replays its own input over this.
                input = player.LastInput;
                input.Buttons &= ~InputButtons.Jump;
            }
            else
            {
                // Input missing for over half a second (paused or backgrounded client): stop walking
                // instead of repeating the last move until the disconnect timeout. Gravity still applies.
                input = new InputCommand { Seq = player.LastInput.Seq, Yaw = player.LastInput.Yaw };
            }
            // Same boxes as client prediction (LocalPlayerPredictor), so predictions match.
            MovementSimulation.Step(ref player.State, input, _tickSeconds, TestArena.Boxes);
        }

        ServerTick++;
        if (ServerTick % (uint)_snapshotEveryTicks == 0) SendSnapshots();
    }

    private void SendSnapshots()
    {
        if (_players.Count == 0) return;

        // One payload for everyone; only AckInputSeq differs per recipient and is patched in place.
        var writer = new PacketWriter(_sendBuffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = ServerTick, AckInputSeq = 0, Count = (ushort)_players.Count });
        foreach (var p in _players)
        {
            SnapshotEntity.Write(ref writer, new SnapshotEntity
            {
                EntityId = p.EntityId,
                Position = p.State.Position,
                VelocityY = p.State.VelocityY,
                Yaw = p.State.Yaw,
            });
        }
        // Cannot overflow: ServerOptions.Validate caps MaxPlayers at MaxSnapshotEntities.
        if (writer.Overflowed) return;

        Span<byte> packet = _sendBuffer.AsSpan(0, writer.Length);
        foreach (var p in _players)
        {
            WorldSnapshotHeader.PatchAckInputSeq(packet, p.LastProcessedSeq);
            _send(p.PeerId, packet, DeliveryMethod.Sequenced);
        }
    }

    private void SendJoinResponse(int peerId, JoinResult result, ushort entityId)
    {
        var writer = new PacketWriter(_sendBuffer);
        JoinMatchResponse.Write(ref writer, new JoinMatchResponse
        {
            Result = result,
            MyEntityId = entityId,
            ServerTick = ServerTick,
            SimHz = _simHz,
            SnapshotHz = _snapshotHz,
        });
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void SendSpawned(int recipientPeerId, PlayerEntity player)
    {
        var writer = new PacketWriter(_sendBuffer);
        PlayerSpawned.Write(ref writer, new PlayerSpawned { EntityId = player.EntityId, Position = player.State.Position, Yaw = player.State.Yaw });
        _send(recipientPeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // Entity ids are ushort and 0 means "none". With at most 50 players a free id is always found.
    private ushort AllocateEntityId()
    {
        while (_nextEntityId == 0 || IsEntityIdInUse(_nextEntityId)) _nextEntityId++;
        return _nextEntityId++;
    }

    private bool IsEntityIdInUse(ushort id)
    {
        foreach (var p in _players)
        {
            if (p.EntityId == id) return true;
        }
        return false;
    }

    // Spread players on a circle (golden angle) so they do not spawn inside each other.
    // The 5 m ring lies inside TestArena.ClearRadius (checked by TestArenaTests).
    internal static Vector3 SpawnPosition(ushort entityId)
    {
        float angle = entityId * 2.39996f;
        return new Vector3(MathF.Cos(angle) * SpawnRadius, 0f, MathF.Sin(angle) * SpawnRadius);
    }
}
