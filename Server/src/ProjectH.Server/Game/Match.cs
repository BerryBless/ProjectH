using System;
using System.Collections.Generic;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game.Combat;
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
    private readonly WeaponCatalog _weapons;
    private readonly int _maxPlayers;
    private readonly int _snapshotEveryTicks;
    private readonly int _inputCapacity;
    private readonly byte _simHz;
    private readonly byte _snapshotHz;
    private readonly float _tickSeconds;
    private readonly uint _respawnTicks;
    private readonly int _maxRewindTicks;
    private ushort _nextEntityId = 1;

    public Match(ServerOptions options, WeaponCatalog weapons, SendPacket send)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _weapons = weapons ?? throw new ArgumentNullException(nameof(weapons));
        if (weapons.SimHz != options.SimHz)
            throw new ArgumentException($"Weapon catalog was built for SimHz {weapons.SimHz}, the match runs at {options.SimHz}.", nameof(weapons));
        _maxPlayers = options.MaxPlayers;
        _snapshotEveryTicks = options.SnapshotEveryTicks;
        _inputCapacity = options.InputBufferPerPlayer;
        _simHz = (byte)options.SimHz;
        _snapshotHz = options.SnapshotHz;
        _tickSeconds = 1f / options.SimHz;
        _respawnTicks = CombatRules.TicksFromSeconds(CombatRules.RespawnSeconds, options.SimHz);
        _maxRewindTicks = CombatRules.MaxRewindTicks(options.SimHz);
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
        ResetCombat(player);
        player.History.Reset(ServerTick, player.State.Position);
        _playersByPeer.Add(peerId, player);
        _players.Add(player);

        SendJoinResponse(peerId, JoinResult.Ok, player.EntityId);
        // Before any spawn: the client needs the weapon data before it can show its own weapon (D4).
        SendCatalog(peerId);
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
        // now = the last completed tick; this call simulates tick now + 1. Weapon timers
        // (NextFireTick, ReloadEndTick, RespawnAtTick) are compared against it.
        uint now = ServerTick;

        foreach (var player in _players)
        {
            if (!player.Alive && now >= player.RespawnAtTick) Respawn(player);
        }

        foreach (var player in _players)
        {
            bool sent = TakeInput(player, out InputCommand input);
            // D9: a dead player's input is still taken and acked (LastProcessedSeq) but moves and fires nothing.
            // A player killed earlier in this loop is already dead here.
            if (!player.Alive) continue;

            // Same boxes as client prediction (LocalPlayerPredictor), so predictions match.
            MovementSimulation.Step(ref player.State, input, _tickSeconds, TestArena.Boxes);
            WeaponRules.UpdateReload(player, _weapons, now);
            // Only an input the client really sent can fire, switch or reload: the missed-input repeat
            // must never invent shots.
            if (sent) ProcessWeapons(player, input, now);
        }

        ServerTick++;
        // After every move of this tick, so all players are recorded at the same moment. A snapshot with
        // ServerTick N shows exactly the positions recorded at N, which is what ViewTick refers to.
        foreach (var player in _players) player.History.Record(ServerTick, player.State.Position);
        if (ServerTick % (uint)_snapshotEveryTicks == 0) SendSnapshots();
    }

    // One buffered input per tick (Phase 0). Returns false when the input is made up by the server.
    private bool TakeInput(PlayerEntity player, out InputCommand input)
    {
        if (player.Inputs.TryTake(out input))
        {
            player.LastInput = input;
            player.LastProcessedSeq = input.Seq;
            player.MissedTicks = 0;
            return true;
        }
        if (player.MissedTicks < _simHz / 2)
        {
            player.MissedTicks++;
            // No input arrived in time: keep moving the same way, but never repeat a jump.
            // The ack does not advance, so the client replays its own input over this.
            input = player.LastInput;
            input.Buttons &= ~InputButtons.Jump;
            return false;
        }
        // Input missing for over half a second (paused or backgrounded client): stop walking
        // instead of repeating the last move until the disconnect timeout. Gravity still applies.
        input = new InputCommand { Seq = player.LastInput.Seq, Yaw = player.LastInput.Yaw };
        return false;
    }

    private void ProcessWeapons(PlayerEntity shooter, in InputCommand input, uint now)
    {
        bool aimValid = CombatRules.TryAimDirection(input.AimYaw, input.AimPitch, out Vector3 direction);
        if (WeaponRules.Apply(shooter, _weapons, input.Buttons, aimValid, now))
            FireShot(shooter, direction, input.ViewTick);
    }

    // D7: from the eye along the aim, the nearest arena surface or living player stops the shot. The
    // client only sent a direction; which player is hit is decided here (D12, request §17).
    // D6: other players are tested where the shooter saw them, at ViewTick (clamped to the last
    // _maxRewindTicks ticks). The shooter itself and the arena are not rewound.
    private void FireShot(PlayerEntity shooter, Vector3 direction, float viewTick)
    {
        WeaponDefinition weapon = _weapons[shooter.WeaponSlot];
        Vector3 origin = shooter.State.Position + new Vector3(0f, CombatRules.EyeHeight, 0f);
        float nearest = HitScan.TraceWorld(origin, direction, weapon.Range, TestArena.Boxes);
        double rewindTick = CombatRules.ClampViewTick(viewTick, ServerTick, _maxRewindTicks);

        PlayerEntity? target = null;
        foreach (var other in _players)
        {
            if (other == shooter || !other.Alive) continue;
            Vector3 feet = other.History.Sample(rewindTick);
            if (HitScan.TracePlayer(origin, direction, nearest, feet, out float distance) &&
                (target == null || distance < nearest))
            {
                nearest = distance;
                target = other;
            }
        }

        var writer = new PacketWriter(_sendBuffer);
        ShotFired.Write(ref writer, new ShotFired { ShooterId = shooter.EntityId, Start = origin, End = origin + direction * nearest });
        Broadcast(writer.WrittenSpan, DeliveryMethod.Unreliable);

        if (target != null) ApplyHit(shooter, target, weapon.Damage);
    }

    private void ApplyHit(PlayerEntity shooter, PlayerEntity target, ushort damage)
    {
        bool killed = CombatRules.ApplyDamage(ref target.Health, ref target.Shield, damage);

        var writer = new PacketWriter(_sendBuffer);
        HitConfirmed.Write(ref writer, new HitConfirmed { TargetId = target.EntityId, Damage = damage, Killed = killed });
        _send(shooter.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        Vector3 toAttacker = shooter.State.Position - target.State.Position;
        float length = toAttacker.Length();
        writer = new PacketWriter(_sendBuffer);
        DamageTaken.Write(ref writer, new DamageTaken
        {
            AttackerId = shooter.EntityId,
            Damage = damage,
            FromDirection = length > 1e-4f ? toAttacker / length : Vector3.Zero,
        });
        _send(target.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        if (killed) Kill(target, shooter);
    }

    // D9: death is decided here. The same ReliableOrdered channel carries DamageTaken, PlayerDied and
    // PlayerRespawned, so every client sees them in that order.
    private void Kill(PlayerEntity victim, PlayerEntity killer)
    {
        victim.Alive = false;
        victim.RespawnAtTick = ServerTick + _respawnTicks;
        // The reload dies with the player; otherwise the corpse's snapshots would report it (D10).
        victim.Reloading = false;
        victim.ReloadEndTick = 0;

        var writer = new PacketWriter(_sendBuffer);
        PlayerDied.Write(ref writer, new PlayerDied { VictimId = victim.EntityId, KillerId = killer.EntityId });
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void Respawn(PlayerEntity player)
    {
        // Keep the yaw so the camera does not snap; everything else starts over at the spawn point.
        player.State = new MoveState { Position = SpawnPosition(player.EntityId), Yaw = player.State.Yaw };
        ResetCombat(player);
        player.History.Reset(ServerTick, player.State.Position);
        // The missed-input repeat starts over too: a late input right after the respawn must not replay a
        // move from the old life. Seq stays (the client's seq continues across the respawn), and so does
        // LastProcessedSeq.
        player.LastInput = new InputCommand { Seq = player.LastInput.Seq, Yaw = player.State.Yaw };
        player.MissedTicks = 0;

        var writer = new PacketWriter(_sendBuffer);
        PlayerRespawned.Write(ref writer, new PlayerRespawned { EntityId = player.EntityId, Position = player.State.Position, Yaw = player.State.Yaw });
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void ResetCombat(PlayerEntity player)
    {
        player.Alive = true;
        player.Health = CombatRules.MaxHealth;
        player.Shield = CombatRules.MaxShield;
        WeaponRules.Equip(player, _weapons);
    }

    private void Broadcast(ReadOnlySpan<byte> data, DeliveryMethod method)
    {
        foreach (var p in _players) _send(p.PeerId, data, method);
    }

    private void SendSnapshots()
    {
        if (_players.Count == 0) return;

        // One payload for everyone; AckInputSeq and the self block differ per recipient and are patched in place.
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
                Flags = p.Alive ? SnapshotEntity.AliveFlag : (byte)0,
            });
        }
        // Cannot overflow: ServerOptions.Validate caps MaxPlayers at MaxSnapshotEntities (17 + 23 * 50 = 1167 bytes).
        if (writer.Overflowed) return;

        Span<byte> packet = _sendBuffer.AsSpan(0, writer.Length);
        foreach (var p in _players)
        {
            WorldSnapshotHeader.PatchRecipient(packet, p.LastProcessedSeq, SelfBlock(p));
            _send(p.PeerId, packet, DeliveryMethod.Sequenced);
        }
    }

    private SnapshotSelf SelfBlock(PlayerEntity p)
    {
        // While a reload runs the remaining time is reported as at least 1, even on its last tick: 0 means
        // "not reloading" to the client, and reporting 0 early would make it re-sync at every reload end.
        ushort reloadRemaining = 0;
        if (p.Reloading)
            reloadRemaining = (ushort)Math.Clamp(p.ReloadEndTick > ServerTick ? p.ReloadEndTick - ServerTick : 1u, 1u, ushort.MaxValue);

        return new SnapshotSelf
        {
            Health = (byte)Math.Clamp(p.Health, 0, byte.MaxValue),
            Shield = (byte)Math.Clamp(p.Shield, 0, byte.MaxValue),
            WeaponSlot = (byte)p.WeaponSlot,
            Ammo = (byte)p.Ammo[p.WeaponSlot],
            ReloadRemainingTicks = reloadRemaining,
        };
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

    private void SendCatalog(int peerId)
    {
        var writer = new PacketWriter(_sendBuffer);
        WeaponCatalogPacket.Write(ref writer, _weapons.WireInfos);
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
