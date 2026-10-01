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

// D11 through Match.Tick: Medkit 90 ticks +50 health, Shield Cell 60 ticks +25 shield, both capped at 100.
public class ConsumableTests
{
    private sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method)
    {
        public PacketId Id => (PacketId)Data[0];
    }

    private readonly List<Sent> _sent = new();
    private readonly Match _match;
    private readonly PlayerEntity _a;
    private uint _seq;

    public ConsumableTests()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 2, DevRespawn = true }, TestGameData.Create(),
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)),
            new StartingLoadout
            {
                Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 0) },
                MediumAmmo = 30,
                Medkits = 2,
                ShieldCells = 3,
            },
            Array.Empty<LootPoint>());
        _match.TryJoin(1, "a");
        _match.TryGetPlayer(1, out _a);
        _sent.Clear();
    }

    private void Press(InputButtons buttons, float moveY = 0f)
    {
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = ++_seq, Buttons = buttons, MoveY = moveY, AimPitch = 10f });
        _match.EnqueueInput(1, packet);
        _match.Tick();
    }

    private void Idle(int ticks)
    {
        for (int i = 0; i < ticks; i++) Press(InputButtons.None);
    }

    private List<InventoryState> Inventories() => _sent.Where(s => s.PeerId == 1 && s.Id == PacketId.InventoryState)
        .Select(s =>
        {
            var r = new PacketReader(s.Data);
            r.TryReadPacketId(out _);
            Assert.True(InventoryState.TryRead(ref r, out var state));
            return state;
        }).ToList();

    // Spec §6: 3 s later +50, never above 100.
    [Theory]
    [InlineData(30, 80)]
    [InlineData(70, 100)]
    public void Medkit_After90Ticks_Heals50_CappedAt100(int health, int expected)
    {
        _a.Health = health;
        Press(InputButtons.UseMedkit);
        Assert.Equal(ConsumableType.Medkit, _a.Inventory.Using);

        Idle(TestGameData.MedkitUseTicks - 1);
        Assert.Equal(health, _a.Health);            // nothing until the channel ends
        Assert.Equal(2, _a.Inventory.Medkits);

        Idle(1);
        Assert.Equal(expected, _a.Health);
        Assert.Equal(1, _a.Inventory.Medkits);
        Assert.Equal(ConsumableType.None, _a.Inventory.Using);

        var states = Inventories();
        Assert.Equal(2, states.Count);              // one at the start, one at the end
        Assert.Equal(ConsumableType.Medkit, states[0].Using);
        Assert.Equal(TestGameData.MedkitUseTicks - 1, states[0].UseRemainingTicks);
        Assert.Equal(ConsumableType.None, states[1].Using);
        Assert.Equal(0, states[1].UseRemainingTicks);
        Assert.Equal(1, states[1].Medkits);
    }

    [Theory]
    [InlineData(0, 25)]
    [InlineData(90, 100)]
    public void ShieldCell_After60Ticks_Adds25_CappedAt100(int shield, int expected)
    {
        _a.Shield = shield;
        Press(InputButtons.UseShieldCell);
        Idle(TestGameData.ShieldCellUseTicks - 1);
        Assert.Equal(shield, _a.Shield);
        Idle(1);
        Assert.Equal(expected, _a.Shield);
        Assert.Equal(2, _a.Inventory.ShieldCells);
    }

    // Review Focus / spec §6: at the maximum a heal does not start and nothing is used up.
    [Fact]
    public void AtMax_NothingStarts()
    {
        _a.Health = CombatRules.MaxHealth;
        _a.Shield = CombatRules.MaxShield;
        Press(InputButtons.UseMedkit);
        Press(InputButtons.UseShieldCell);
        Idle(TestGameData.MedkitUseTicks + 5);

        Assert.Equal(ConsumableType.None, _a.Inventory.Using);
        Assert.Equal(2, _a.Inventory.Medkits);
        Assert.Equal(3, _a.Inventory.ShieldCells);
        Assert.Empty(Inventories());
    }

    [Fact]
    public void WithoutOne_NothingStarts()
    {
        _a.Health = 10;
        _a.Inventory.Medkits = 0;
        Press(InputButtons.UseMedkit);
        Assert.Equal(ConsumableType.None, _a.Inventory.Using);
    }

    [Fact]
    public void BothHealButtons_AtOnce_AreIgnored()
    {
        _a.Health = 10;
        _a.Shield = 0;
        Press(InputButtons.UseMedkit | InputButtons.UseShieldCell);
        Assert.Equal(ConsumableType.None, _a.Inventory.Using);
    }

    // Spec §6: fire, switch and drop cancel the use; nothing is used up and nothing is healed.
    [Theory]
    [InlineData(InputButtons.Fire)]
    [InlineData(InputButtons.Slot2)]
    [InlineData(InputButtons.Slot1)]   // even the slot already in hand
    [InlineData(InputButtons.Drop)]
    public void Interrupt_CancelsTheUse(InputButtons interrupt)
    {
        _a.Health = 10;
        Press(InputButtons.UseMedkit);
        Idle(30);
        Press(interrupt);
        Assert.Equal(ConsumableType.None, _a.Inventory.Using);

        Idle(TestGameData.MedkitUseTicks);
        Assert.Equal(10, _a.Health);
        Assert.Equal(2, _a.Inventory.Medkits);
        Assert.Equal(ConsumableType.None, Inventories().Last().Using);
    }

    // D11: pressing the other heal cancels the running one and starts the other in the same tick.
    [Fact]
    public void OtherHeal_CancelsAndStartsItself()
    {
        _a.Health = 10;
        _a.Shield = 0;
        Press(InputButtons.UseMedkit);
        Idle(30);
        Press(InputButtons.UseShieldCell);
        Assert.Equal(ConsumableType.ShieldCell, _a.Inventory.Using);

        Idle(TestGameData.ShieldCellUseTicks);
        Assert.Equal(10, _a.Health);
        Assert.Equal(TestGameData.ShieldCellShield, _a.Shield);
        Assert.Equal(2, _a.Inventory.Medkits);
        Assert.Equal(2, _a.Inventory.ShieldCells);
    }

    // D11: moving does not interrupt (so movement prediction is untouched), and pressing the same heal
    // again does not restart the channel.
    [Fact]
    public void Moving_AndPressingTheSameHealAgain_DoNotInterrupt()
    {
        _a.Health = 10;
        Press(InputButtons.UseMedkit);
        for (int i = 1; i < TestGameData.MedkitUseTicks; i++)
            Press(i % 10 == 0 ? InputButtons.UseMedkit | InputButtons.Sprint : InputButtons.Sprint, moveY: 1f);
        Assert.Equal(10, _a.Health);
        Press(InputButtons.Sprint, moveY: 1f);
        Assert.Equal(10 + TestGameData.MedkitHeal, _a.Health);
    }

    // The missed-input repeat carries the last input's buttons; it must neither start nor cancel a use, but
    // the channel still ends on time.
    [Fact]
    public void MissedInputTicks_NeitherStartNorCancel_ButTheChannelEnds()
    {
        _a.Health = 10;
        Press(InputButtons.UseMedkit | InputButtons.Fire);   // cancel (nothing running) -> fire -> start
        Assert.Equal(ConsumableType.Medkit, _a.Inventory.Using);
        for (int i = 1; i < TestGameData.MedkitUseTicks; i++) _match.Tick();   // no inputs arrive: the repeat holds Fire
        Assert.Equal(ConsumableType.Medkit, _a.Inventory.Using);
        _match.Tick();
        Assert.Equal(10 + TestGameData.MedkitHeal, _a.Health);
        Assert.Equal(1, _a.Inventory.Medkits);
    }

    // Review Focus / spec §6: death cancels the use; the respawned player is not healed by it later.
    [Fact]
    public void Death_CancelsTheUse()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 2, DevRespawn = true }, TestGameData.Create(), static (_, _, _) => { },
            new StartingLoadout { Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 0) }, MediumAmmo = 30, Medkits = 2 },
            Array.Empty<LootPoint>());
        match.TryJoin(1, "shooter");
        match.TryJoin(2, "healer");
        match.TryGetPlayer(1, out var shooter);
        match.TryGetPlayer(2, out var healer);
        shooter.State.Position = new Vector3(0f, 0f, -3f);
        healer.State.Position = new Vector3(0f, 0f, 3f);
        healer.History.Reset(match.ServerTick, healer.State.Position);
        healer.Health = 20;

        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = 1, Buttons = InputButtons.UseMedkit });
        match.EnqueueInput(2, packet);
        match.Tick();
        Assert.Equal(ConsumableType.Medkit, healer.Inventory.Using);

        TestAim.YawPitch(shooter.State.Position, healer.State.Position + new Vector3(0f, 1.2f, 0f), out float yaw, out float pitch);
        packet.Set(0, new InputCommand { Seq = 1, Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = match.ServerTick });
        match.EnqueueInput(1, packet);
        match.Tick();
        Assert.False(healer.Alive);
        Assert.Equal(ConsumableType.None, healer.Inventory.Using);

        for (int i = 0; i < 300 && !healer.Alive; i++) match.Tick();
        Assert.True(healer.Alive);
        for (int i = 0; i < TestGameData.MedkitUseTicks; i++) match.Tick();
        Assert.Equal(CombatRules.MaxHealth, healer.Health);   // the respawn's health, not a late medkit
        Assert.Equal(ConsumableType.None, healer.Inventory.Using);
        Assert.Equal(2, healer.Inventory.Medkits);            // the loadout again
    }

    // Server hot path: starting, running and finishing a use allocates nothing.
    [Fact]
    public void UseTicks_AllocateNothing()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 1, DevRespawn = true }, TestGameData.Create(), static (_, _, _) => { },
            new StartingLoadout { Medkits = 3 }, Array.Empty<LootPoint>());
        match.TryJoin(1, "a");
        match.TryGetPlayer(1, out var a);
        var packet = new PlayerInputPacket { Count = 1 };
        long allocated = 0;
        for (uint use = 1; use <= 2; use++)   // the first use warms up the path; the second is measured
        {
            a.Health = 1;
            long start = GC.GetAllocatedBytesForCurrentThread();
            packet.Set(0, new InputCommand { Seq = use, Buttons = InputButtons.UseMedkit });
            match.EnqueueInput(1, packet);
            for (int i = 0; i <= TestGameData.MedkitUseTicks; i++) match.Tick();
            allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.Equal(1 + TestGameData.MedkitHeal, a.Health);
        }
        Assert.Equal(0, allocated);
    }
}
