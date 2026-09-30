using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// The inventory through Match: the D1 start, InventoryState, rarity damage, respawn.
public class InventoryMatchTests
{
    private sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method)
    {
        public PacketId Id => (PacketId)Data[0];
    }

    private static readonly Vector3 Chest = new(0f, 1.2f, 0f);

    private readonly List<Sent> _sent = new();
    private readonly Dictionary<int, uint> _seq = new();
    private Match _match;

    public InventoryMatchTests()
    {
        _match = NewMatch(TestGameData.CombatLoadout);
    }

    private Match NewMatch(StartingLoadout? loadout) =>
        new(new ServerOptions { MaxPlayers = 3 }, TestGameData.Create(),
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)), loadout);

    private PlayerEntity Join(int peer, Vector3 feet)
    {
        Assert.Equal(JoinResult.Ok, _match.TryJoin(peer, "p" + peer));
        _match.TryGetPlayer(peer, out var player);
        player.State.Position = feet;
        player.History.Reset(_match.ServerTick, feet);
        return player;
    }

    private void Send(PlayerEntity player, InputCommand command)
    {
        _seq.TryGetValue(player.PeerId, out uint seq);
        command.Seq = ++seq;
        _seq[player.PeerId] = seq;
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, command);
        _match.EnqueueInput(player.PeerId, packet);
    }

    private void FireAt(PlayerEntity shooter, Vector3 point)
    {
        TestAim.YawPitch(shooter.State.Position, point, out float yaw, out float pitch);
        Send(shooter, new InputCommand { Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
    }

    private List<InventoryState> InventoriesSentTo(int peer)
    {
        var list = new List<InventoryState>();
        foreach (Sent s in _sent.Where(s => s.PeerId == peer && s.Id == PacketId.InventoryState))
        {
            Assert.Equal(DeliveryMethod.ReliableOrdered, s.Method);
            var reader = new PacketReader(s.Data);
            reader.TryReadPacketId(out _);
            Assert.True(InventoryState.TryRead(ref reader, out var state));
            list.Add(state);
        }
        return list;
    }

    // D1: production starts empty-handed: no weapon, no shield, full health; the trigger does nothing.
    [Fact]
    public void DefaultStart_IsEmpty_AndFiresNothing()
    {
        _match = NewMatch(loadout: null);
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));

        Assert.Equal(CombatRules.MaxHealth, a.Health);
        Assert.Equal(0, a.Shield);
        Assert.All(a.Inventory.Slots, held => Assert.True(held.IsEmpty));
        var joined = Assert.Single(InventoriesSentTo(1));
        Assert.True(joined.Slot0.IsEmpty && joined.Slot1.IsEmpty && joined.Slot2.IsEmpty);
        Assert.Equal(0, joined.MediumAmmo);

        FireAt(a, b.State.Position + Chest);
        _match.Tick();
        Assert.DoesNotContain(_sent, s => s.Id == PacketId.ShotFired);
        Assert.Equal(CombatRules.MaxHealth, b.Health);
    }

    [Fact]
    public void Join_SendsTheLoadout_AfterTheWorldList_BeforeSpawns()
    {
        Join(1, Vector3.Zero);

        var order = _sent.Where(s => s.PeerId == 1).Select(s => s.Id).ToList();
        Assert.True(order.IndexOf(PacketId.WorldItems) < order.IndexOf(PacketId.InventoryState));
        Assert.True(order.IndexOf(PacketId.InventoryState) < order.IndexOf(PacketId.PlayerSpawned));

        var state = Assert.Single(InventoriesSentTo(1));
        Assert.Equal(TestWeapons.AutoId, state.Slot0.WeaponId);
        Assert.Equal(TestWeapons.AutoMagazine, state.Slot0.MagAmmo);
        Assert.Equal(TestWeapons.SemiId, state.Slot1.WeaponId);
        Assert.True(state.Slot2.IsEmpty);
        Assert.Equal(0, state.CurrentSlot);
        Assert.Equal(TestGameData.LoadoutMediumAmmo, state.MediumAmmo);
        Assert.Equal(TestGameData.LoadoutHeavyAmmo, state.HeavyAmmo);
        Assert.Equal(ConsumableType.None, state.Using);
    }

    // D4: damage = weapon damage x rarity multiplier, rounded. Rare (1.10) Test Auto: 30 -> 33.
    [Fact]
    public void Rarity_ScalesTheDamage()
    {
        _match = NewMatch(new StartingLoadout
        {
            Shield = TestGameData.LoadoutShield,
            Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 2) },
            MediumAmmo = 10,
        });
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        _sent.Clear();

        FireAt(a, b.State.Position + Chest);
        _match.Tick();

        Assert.Equal(TestGameData.LoadoutShield - 33, b.Shield);
        var reader = new PacketReader(_sent.Single(s => s.PeerId == 1 && s.Id == PacketId.HitConfirmed).Data);
        reader.TryReadPacketId(out _);
        Assert.True(HitConfirmed.TryRead(ref reader, out var hit));
        Assert.Equal(33, hit.Damage);
    }

    [Theory]
    [InlineData(20, 1.05f, 21)]
    [InlineData(12, 1.2f, 14)]    // 14.4
    [InlineData(90, 1.15f, 104)]  // 103.5 rounds away from zero
    [InlineData(1, 0.1f, 1)]      // never below 1
    public void ScaledDamage_RoundsHalfAwayFromZero(int damage, float multiplier, int expected)
    {
        Assert.Equal(expected, CombatRules.ScaledDamage((ushort)damage, multiplier));
    }

    // D14: shots alone send no InventoryState (the snapshot carries the magazine); a finished reload does,
    // once, with the new reserve.
    [Fact]
    public void Shots_SendNoInventoryState_AFinishedReloadSendsOne()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        _sent.Clear();

        Send(a, new InputCommand { Buttons = InputButtons.Fire, AimPitch = 10f, ViewTick = _match.ServerTick });
        _match.Tick();
        Assert.Empty(InventoriesSentTo(1));

        Send(a, new InputCommand { Buttons = InputButtons.Reload });
        _match.Tick();
        Assert.True(a.Reloading);
        Assert.Empty(InventoriesSentTo(1));   // starting a reload changes nothing the owner cannot see
        while (a.Reloading) _match.Tick();

        var state = Assert.Single(InventoriesSentTo(1));
        Assert.Equal(TestWeapons.AutoMagazine, state.Slot0.MagAmmo);
        Assert.Equal(TestGameData.LoadoutMediumAmmo - 1, state.MediumAmmo);
    }

    [Fact]
    public void Snapshot_WithAnEmptyCurrentSlot_ReportsThatSlotAndNoAmmo()
    {
        var a = Join(1, Vector3.Zero);
        Send(a, new InputCommand { Buttons = InputButtons.Slot3 });
        _match.Tick();
        _match.Tick();

        var reader = new PacketReader(_sent.Last(s => s.PeerId == 1 && s.Id == PacketId.WorldSnapshot).Data);
        reader.TryReadPacketId(out _);
        Assert.True(WorldSnapshotHeader.TryRead(ref reader, out var header));
        Assert.Equal(2, header.Self.WeaponSlot);
        Assert.Equal(0, header.Self.Ammo);
        Assert.Equal(0, header.Self.ReloadRemainingTicks);
    }

    [Fact]
    public void Respawn_RestoresTheLoadout_AndTellsTheOwner()
    {
        var a = Join(1, Vector3.Zero);
        a.Inventory.Slots[0].MagAmmo = 1;
        a.Inventory.SetAmmo(AmmoType.Medium, 3);
        a.Alive = false;
        a.RespawnAtTick = _match.ServerTick;
        _sent.Clear();

        _match.Tick();

        Assert.True(a.Alive);
        Assert.Equal(TestGameData.LoadoutShield, a.Shield);
        var state = Assert.Single(InventoriesSentTo(1));
        Assert.Equal(TestWeapons.AutoMagazine, state.Slot0.MagAmmo);
        Assert.Equal(TestGameData.LoadoutMediumAmmo, state.MediumAmmo);
        var reliable = _sent.Where(s => s.PeerId == 1 && s.Method == DeliveryMethod.ReliableOrdered).Select(s => s.Id).ToList();
        Assert.True(reliable.IndexOf(PacketId.PlayerRespawned) < reliable.IndexOf(PacketId.InventoryState));
    }

    public static IEnumerable<object[]> BadLoadouts()
    {
        yield return new object[] { new StartingLoadout { Shield = CombatRules.MaxShield + 1 } };
        yield return new object[] { new StartingLoadout { Weapons = new[] { new LoadoutWeapon(99, 0) } } };
        yield return new object[] { new StartingLoadout { Weapons = new[] { new LoadoutWeapon(1, 5) } } };
        yield return new object[] { new StartingLoadout { Weapons = new[] { new LoadoutWeapon(1, 0), new LoadoutWeapon(2, 0), new LoadoutWeapon(3, 0), new LoadoutWeapon(1, 0) } } };
        yield return new object[] { new StartingLoadout { HeavyAmmo = TestGameData.HeavyMax + 1 } };
        yield return new object[] { new StartingLoadout { LightAmmo = -1 } };
        yield return new object[] { new StartingLoadout { Medkits = TestGameData.MedkitMaxStack + 1 } };
    }

    [Theory]
    [MemberData(nameof(BadLoadouts))]
    public void Constructor_RejectsAnInvalidLoadout(StartingLoadout loadout)
    {
        Assert.Throws<ArgumentException>(() => NewMatch(loadout));
    }
}
