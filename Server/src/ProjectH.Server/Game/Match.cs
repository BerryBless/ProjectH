using System;
using System.Collections.Generic;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
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
    private readonly ItemCatalog _items;
    private readonly WorldItems _worldItems = new();
    private readonly LootSpawner _loot;
    private readonly StartingLoadout _loadout;
    private readonly int _maxPlayers;
    private readonly int _snapshotEveryTicks;
    private readonly int _inputCapacity;
    private readonly byte _simHz;
    private readonly byte _snapshotHz;
    private readonly float _tickSeconds;
    private readonly uint _respawnTicks;
    private readonly int _maxRewindTicks;
    private ushort _nextEntityId = 1;

    // Test seams: loadout null = StartingLoadout.Empty (the production start, D1); lootPoints null = the
    // map's LootPoints.All.
    public Match(ServerOptions options, GameData data, SendPacket send, StartingLoadout? loadout = null, LootPoint[]? lootPoints = null)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        ArgumentNullException.ThrowIfNull(data);
        if (data.SimHz != options.SimHz)
            throw new ArgumentException($"Game data was built for SimHz {data.SimHz}, the match runs at {options.SimHz}.", nameof(data));
        _weapons = data.Weapons;
        _items = data.Items;
        _loadout = loadout ?? StartingLoadout.Empty;
        string? loadoutError = _loadout.Validate(data);
        if (loadoutError != null) throw new ArgumentException("Invalid starting loadout: " + loadoutError, nameof(loadout));
        _maxPlayers = options.MaxPlayers;
        _snapshotEveryTicks = options.SnapshotEveryTicks;
        _inputCapacity = options.InputBufferPerPlayer;
        _simHz = (byte)options.SimHz;
        _snapshotHz = options.SnapshotHz;
        _tickSeconds = 1f / options.SimHz;
        _respawnTicks = CombatRules.TicksFromSeconds(CombatRules.RespawnSeconds, options.SimHz);
        _maxRewindTicks = CombatRules.MaxRewindTicks(options.SimHz);

        // D6: the server fills every spawn point when the match starts; clients get the list at join.
        _loot = new LootSpawner(lootPoints is null ? LootPoints.All : new ReadOnlySpan<LootPoint>(lootPoints), data, options.LootSeed,
            (uint)options.LootRespawnSeconds * (uint)options.SimHz);
        for (int point = 0; point < _loot.Count; point++) SpawnItem(_loot.Roll(point), _loot.Position(point), point);
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

    // Test seam (InternalsVisibleTo): the store itself. Match is the only writer.
    internal WorldItems WorldItems => _worldItems;

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
        // Before any spawn: the client needs the weapon and item data before it can show them (D4, D2).
        SendCatalogs(peerId);
        SendWorldItems(peerId);
        SendInventory(player);
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
        RefillLootPoints(now);

        foreach (var player in _players)
        {
            bool sent = TakeInput(player, out InputCommand input);
            // D9: a dead player's input is still taken and acked (LastProcessedSeq) but moves and fires nothing.
            // A player killed earlier in this loop is already dead here.
            if (!player.Alive) continue;

            // Same boxes as client prediction (LocalPlayerPredictor), so predictions match.
            MovementSimulation.Step(ref player.State, input, _tickSeconds, TestArena.Boxes);
            WeaponRules.UpdateReload(player, now);
            // Only an input the client really sent can act: the missed-input repeat copies the last input's
            // buttons and must never invent a switch, reload or shot.
            if (sent) ProcessActions(player, input, now);
            ConsumableRules.Complete(player, _items, now);   // step 9: every tick, input or not
        }

        ServerTick++;
        SendInventoryChanges();
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

    // One real input of a living player, in the spec §2 order: cancel use -> slot -> drop -> pickup ->
    // reload -> fire -> start use. (Movement came first; finishing a use comes after, every tick.)
    private void ProcessActions(PlayerEntity player, in InputCommand input, uint now)
    {
        ConsumableRules.CancelIfInterrupted(player, input.Buttons);
        WeaponRules.SelectSlot(player, input.Buttons);
        if ((input.Buttons & InputButtons.Drop) != 0) DropCurrentWeapon(player);
        if ((input.Buttons & InputButtons.Interact) != 0) Pickup(player);

        bool aimValid = CombatRules.TryAimDirection(input.AimYaw, input.AimPitch, out Vector3 direction);
        if (WeaponRules.Apply(player, input.Buttons, aimValid, now))
            FireShot(player, direction, input.ViewTick);

        ConsumableRules.TryStart(player, _items, input.Buttons, now);
    }

    // D7: from the eye along the aim, the nearest arena surface or living player stops the shot. The
    // client only sent a direction; which player is hit is decided here (D12, request §17).
    // D6: other players are tested where the shooter saw them, at ViewTick (clamped to the last
    // _maxRewindTicks ticks). The shooter itself and the arena are not rewound.
    private void FireShot(PlayerEntity shooter, Vector3 direction, float viewTick)
    {
        ref HeldWeapon held = ref shooter.Inventory.Current;
        WeaponDefinition weapon = held.Weapon!;   // Apply only fires a filled slot
        ushort damage = CombatRules.ScaledDamage(weapon.Damage, _items.DamageMultiplier(held.Rarity));
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

        if (target != null) ApplyHit(shooter, target, damage);
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

    // D8, D9: the server picks the nearest item in range itself; the client never names one, so it cannot
    // reach for a far item. Items are processed in player order within a tick, so when two players reach
    // for the same item the first one takes it and the second finds it gone.
    private void Pickup(PlayerEntity player)
    {
        int index = _worldItems.FindNearest(player.State.Position, ItemRules.PickupRange, ItemRules.PickupHeight);
        if (index < 0)
        {
            SendPickupResult(player, PickupResultCode.NothingInRange, 0);
            return;
        }

        WorldItemData item = _worldItems[index].Data;
        if (item.Kind == ItemKind.Weapon)
        {
            bool taken = PickupWeapon(player, index, item);
            SendPickupResult(player, taken ? PickupResultCode.Ok : PickupResultCode.Full, item.ItemId);
            return;
        }

        int take = Math.Min(item.Amount, ItemRules.Room(player.Inventory, _items, item.Kind, item.DefId));
        if (take == 0)
        {
            SendPickupResult(player, PickupResultCode.Full, item.ItemId);
            return;
        }
        ItemRules.AddStack(player.Inventory, item.Kind, item.DefId, take);
        player.Inventory.Changed = true;
        // D9: what does not fit stays on the ground, with the smaller amount.
        if (take == item.Amount) RemoveItemAt(index);
        else SetItemAmount(index, (ushort)(item.Amount - take));
        SendPickupResult(player, PickupResultCode.Ok, item.ItemId);
    }

    // D9: the first empty slot, or, with all three full, the current slot; the weapon it held goes on the
    // ground in front of the player (like a G-drop). Empty-handed (current slot empty), the player also takes it in hand.
    // Returns false when nothing changed hands (see the swap below).
    private bool PickupWeapon(PlayerEntity player, int index, in WorldItemData item)
    {
        Inventory inventory = player.Inventory;
        int spawnPoint = _worldItems[index].SpawnPoint;
        _weapons.TryGetById(item.DefId, out WeaponDefinition weapon);   // items are made from this catalog
        var picked = new HeldWeapon
        {
            Weapon = weapon,
            Rarity = item.Rarity,
            MagAmmo = Math.Min(item.Amount, (int)weapon.MagazineSize),
            NextFireTick = inventory.DroppedFireLockTick,
        };
        RemoveItemAt(index);

        int slot = -1;
        for (int i = 0; i < Inventory.SlotCount && slot < 0; i++)
        {
            if (inventory.Slots[i].IsEmpty) slot = i;
        }

        if (slot >= 0)
        {
            inventory.Slots[slot] = picked;
            if (inventory.Current.IsEmpty)
            {
                inventory.CurrentSlot = slot;
                player.Reloading = false;
            }
        }
        else
        {
            // The ground weapon was removed first, so its record is free for the old one: spawning first
            // could evict the very item being picked up (D13). The old weapon leaves the hand only once it
            // lies in the world, so no item is lost (conservation).
            HeldWeapon old = inventory.Current;
            // It is placed like a G-drop (in front of the player), not on the loot point: the point rolls a new
            // item there after its respawn delay and the two would overlap.
            Vector3 dropOffset = ItemRules.Offset(player.State.Yaw, ItemRules.DropDistance);
            Vector3 dropAt = ItemRules.DropPosition(player.State.Position, dropOffset, TestArena.Boxes);
            if (SpawnItem(new LootRoll(ItemKind.Weapon, old.Weapon!.Id, old.Rarity, (ushort)old.MagAmmo), dropAt, -1) == 0)
            {
                // Impossible: RemoveItemAt above just freed a record, so the store is below Capacity and
                // TryAdd cannot fail. Kept so a future change to the store cannot lose an item silently:
                // put the ground weapon back as it was and keep the old one in hand.
                SpawnItem(new LootRoll(ItemKind.Weapon, item.DefId, item.Rarity, item.Amount), item.Position, spawnPoint);
                return false;
            }
            inventory.DroppedFireLockTick = Math.Max(inventory.DroppedFireLockTick, old.NextFireTick);
            picked.NextFireTick = inventory.DroppedFireLockTick;
            inventory.Current = picked;
            player.Reloading = false;   // the reload belonged to the weapon that left the hand
        }
        inventory.Changed = true;
        return true;
    }

    // D12: G drops the current weapon 1 m in front of the feet, magazine included.
    private void DropCurrentWeapon(PlayerEntity player)
    {
        Inventory inventory = player.Inventory;
        ref HeldWeapon held = ref inventory.Current;
        if (held.IsEmpty) return;

        var roll = new LootRoll(ItemKind.Weapon, held.Weapon!.Id, held.Rarity, (ushort)held.MagAmmo);
        Vector3 offset = ItemRules.Offset(player.State.Yaw, ItemRules.DropDistance);
        // Conservation: the weapon leaves the hand only once it lies in the world. SpawnItem touches the
        // world list only, so the ref into the inventory stays valid.
        if (SpawnItem(roll, ItemRules.DropPosition(player.State.Position, offset, TestArena.Boxes), -1) == 0) return;

        inventory.DroppedFireLockTick = Math.Max(inventory.DroppedFireLockTick, held.NextFireTick);
        held = default;
        player.Reloading = false;
        inventory.Changed = true;
    }

    // D12 (request §21): everything the player carried goes on a circle around the body, one item per
    // weapon, per ammo type and per consumable type, so they do not lie on one point. Then the inventory
    // is empty. Dropped items are not respawn points and can be evicted when the world is full (D13).
    // Conservation: each piece leaves the inventory only once it lies in the world. A piece the world
    // refused (unreachable: at most Capacity / 2 records are spawn-point items, so a full store always has
    // a drop to evict) stays with the body until the respawn resets the inventory.
    // Kill has already cancelled the reload, so taking the reserve here cannot feed a reload (WeaponRules).
    private void DropEverything(PlayerEntity player)
    {
        Inventory inventory = player.Inventory;
        int count = 0;
        for (int i = 0; i < Inventory.SlotCount; i++) if (!inventory.Slots[i].IsEmpty) count++;
        for (int t = 1; t <= ItemConstants.AmmoTypeCount; t++) if (inventory.GetAmmo((AmmoType)t) > 0) count++;
        if (inventory.Medkits > 0) count++;
        if (inventory.ShieldCells > 0) count++;

        int n = 0;
        for (int i = 0; i < Inventory.SlotCount; i++)
        {
            ref HeldWeapon held = ref inventory.Slots[i];
            if (held.IsEmpty) continue;
            if (DropAround(player, n++, count, new LootRoll(ItemKind.Weapon, held.Weapon!.Id, held.Rarity, (ushort)held.MagAmmo)))
                held = default;
        }
        for (int t = 1; t <= ItemConstants.AmmoTypeCount; t++)
        {
            int rounds = inventory.GetAmmo((AmmoType)t);
            if (rounds > 0 && DropAround(player, n++, count, new LootRoll(ItemKind.Ammo, (byte)t, 0, (ushort)rounds)))
                inventory.SetAmmo((AmmoType)t, 0);
        }
        if (inventory.Medkits > 0 &&
            DropAround(player, n++, count, new LootRoll(ItemKind.Consumable, (byte)ConsumableType.Medkit, 0, (ushort)inventory.Medkits)))
            inventory.Medkits = 0;
        if (inventory.ShieldCells > 0 &&
            DropAround(player, n++, count, new LootRoll(ItemKind.Consumable, (byte)ConsumableType.ShieldCell, 0, (ushort)inventory.ShieldCells)))
            inventory.ShieldCells = 0;

        // The rest of Inventory.Clear: with every piece placed, the inventory is exactly a cleared one.
        inventory.CurrentSlot = 0;
        inventory.DroppedFireLockTick = 0;
        inventory.Changed = true;
    }

    // Returns false when the world could not take the item.
    private bool DropAround(PlayerEntity player, int n, int count, in LootRoll roll)
    {
        Vector3 offset = ItemRules.Offset(player.State.Yaw + 360f * n / count, ItemRules.DeathDropRadius);
        return SpawnItem(roll, ItemRules.DropPosition(player.State.Position, offset, TestArena.Boxes), -1) != 0;
    }

    private void SendPickupResult(PlayerEntity player, PickupResultCode result, ushort itemId)
    {
        var writer = new PacketWriter(_sendBuffer);
        PickupResult.Write(ref writer, new PickupResult { Result = result, ItemId = itemId });
        _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
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
        // Likewise the heal channel: DropEverything does not call Inventory.Clear, and a late Complete
        // must not heal the corpse or the respawned player.
        ConsumableRules.Cancel(victim.Inventory);

        var writer = new PacketWriter(_sendBuffer);
        PlayerDied.Write(ref writer, new PlayerDied { VictimId = victim.EntityId, KillerId = killer.EntityId });
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        // After PlayerDied, so every client hears of the death before the items appear.
        DropEverything(victim);
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
        player.Shield = _loadout.Shield;
        _loadout.ApplyTo(player.Inventory, _weapons);
        WeaponRules.ResetState(player);
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
            WeaponSlot = (byte)p.Inventory.CurrentSlot,
            Ammo = (byte)p.Inventory.Current.MagAmmo,   // 0 for an empty slot
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

    private void SendCatalogs(int peerId)
    {
        var writer = new PacketWriter(_sendBuffer);
        WeaponCatalogPacket.Write(ref writer, _weapons.WireInfos);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        writer = new PacketWriter(_sendBuffer);
        ItemCatalogPacket.Write(ref writer, _items.Wire);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // D14: the owner's inventory, only at the end of a tick in which it changed (shots do not count).
    private void SendInventoryChanges()
    {
        foreach (var p in _players)
        {
            if (p.Inventory.Changed) SendInventory(p);
        }
    }

    private void SendInventory(PlayerEntity player)
    {
        player.Inventory.Changed = false;
        var writer = new PacketWriter(_sendBuffer);
        InventoryState.Write(ref writer, player.Inventory.ToWire(ServerTick));
        _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // D13, D14: the whole list in chunks of WorldItemsPacket.MaxItems (at most 256 items = 6 packets).
    private void SendWorldItems(int peerId)
    {
        for (int start = 0; start < _worldItems.Count; start += WorldItemsPacket.MaxItems)
        {
            int count = Math.Min(WorldItemsPacket.MaxItems, _worldItems.Count - start);
            var writer = new PacketWriter(_sendBuffer);
            WorldItemsPacket.WriteHeader(ref writer, count);
            for (int i = 0; i < count; i++) WorldItemData.Write(ref writer, _worldItems[start + i].Data);
            _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        }
    }

    // Every change to the world item list goes through these three, so each one reaches every client.
    // Each finishes its send before returning: none of them keeps a PacketWriter on _sendBuffer open
    // across another send. Returns the new id, or 0 when the store could not take the item.
    internal ushort SpawnItem(in LootRoll roll, Vector3 position, int spawnPoint)
    {
        if (!_worldItems.TryAdd(roll.Kind, roll.DefId, roll.Rarity, roll.Amount, position, spawnPoint, out ushort itemId, out ushort evictedId))
            return 0;
        if (evictedId != 0) BroadcastItemRemoved(evictedId);

        var writer = new PacketWriter(_sendBuffer);
        ItemSpawnedPacket.Write(ref writer, _worldItems[_worldItems.Count - 1].Data);
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        return itemId;
    }

    internal void RemoveItemAt(int index)
    {
        ushort itemId = _worldItems[index].Data.ItemId;
        int spawnPoint = _worldItems[index].SpawnPoint;
        _worldItems.RemoveAt(index);
        BroadcastItemRemoved(itemId);
        if (spawnPoint >= 0) _loot.OnTaken(spawnPoint, ServerTick);
    }

    // D7: a looted spawn point gets a new roll from its table once its timer is up. A point never holds two
    // items (the death-drop guarantee relies on at most one live item per point): if one is in the world
    // (a partial-pickup remainder, or the swap rollback that puts the item back), the timer is dropped.
    private void RefillLootPoints(uint now)
    {
        for (int point = 0; point < _loot.Count; point++)
        {
            if (!_loot.IsDue(point, now)) continue;
            if (PointHasItem(point) || SpawnItem(_loot.Roll(point), _loot.Position(point), point) != 0) _loot.OnRefilled(point);
        }
    }

    private bool PointHasItem(int point)
    {
        for (int i = 0; i < _worldItems.Count; i++)
            if (_worldItems[i].SpawnPoint == point) return true;
        return false;
    }

    // Partial pickup (D9): the rest stays where it was. Sent as ItemSpawned, which clients treat as an upsert.
    internal void SetItemAmount(int index, ushort amount)
    {
        _worldItems.SetAmount(index, amount);
        var writer = new PacketWriter(_sendBuffer);
        ItemSpawnedPacket.Write(ref writer, _worldItems[index].Data);
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void BroadcastItemRemoved(ushort itemId)
    {
        var writer = new PacketWriter(_sendBuffer);
        ItemRemoved.Write(ref writer, new ItemRemoved { ItemId = itemId });
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
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
