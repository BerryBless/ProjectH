using System;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Rules on one PlayerEntity with the test catalog and the combat loadout (TestGameData.CombatLoadout):
// slot 0 Test Auto (3-tick interval, 6 rounds, 30-tick reload, Medium), slot 1 Test Semi (15-tick interval,
// 2 rounds, 60-tick reload, Heavy), slot 2 empty; 60 Medium and 30 Heavy in reserve.
public class WeaponRulesTests
{
    private readonly GameData _data = TestGameData.Create();
    private readonly PlayerEntity _player = new(1, 1, "a", 8);

    public WeaponRulesTests()
    {
        TestGameData.CombatLoadout.ApplyTo(_player.Inventory, _data.Weapons);
        WeaponRules.ResetState(_player);
    }

    private ref HeldWeapon Slot(int index) => ref _player.Inventory.Slots[index];

    private int Reserve(AmmoType type) => _player.Inventory.GetAmmo(type);

    // One server tick for a living player that sent an input, in Match order: reload done -> slot -> reload/fire.
    private bool Tick(uint now, InputButtons buttons, bool aimValid = true)
    {
        WeaponRules.UpdateReload(_player, now);
        WeaponRules.SelectSlot(_player, buttons, now);
        return WeaponRules.Apply(_player, buttons, aimValid, now);
    }

    // Review fix C2 (SEC-8): each held weapon keeps its own fire interval, so without an equip wait three Kestrels fired three
    // shots in five ticks by switching. A switch makes the weapon now in hand wait its EquipTicks (shipped: 0.4 s = 12 ticks)
    // before it fires; the blocked presses spend no round and no interval.
    [Fact]
    public void SwitchingSlots_DoesNotFireBeforeEquipTicks()
    {
        var catalog = WeaponCatalog.LoadFile(System.IO.Path.Combine(AppContext.BaseDirectory, "weapons.json"), 30);
        Assert.True(catalog.TryGetById(2, out WeaponDefinition kestrel));
        Assert.Equal(12, kestrel.EquipTicks);
        var player = new PlayerEntity(1, 1, "k", 8);
        for (int i = 0; i < Inventory.SlotCount; i++)
            player.Inventory.Slots[i] = new HeldWeapon { Weapon = kestrel, MagAmmo = kestrel.MagazineSize };
        player.Inventory.SetAmmo(AmmoType.Heavy, 20);
        WeaponRules.ResetState(player);
        int shots = 0;
        bool Step(uint now, InputButtons buttons)
        {
            WeaponRules.UpdateReload(player, now);
            WeaponRules.SelectSlot(player, buttons, now);
            bool fired = WeaponRules.Apply(player, buttons, aimValid: true, now);
            if (fired) shots++;
            return fired;
        }

        const uint t = 100;
        Assert.True(Step(t, InputButtons.Fire));
        Assert.False(Step(t + 1, InputButtons.None));
        Assert.False(Step(t + 2, InputButtons.Slot2 | InputButtons.Fire));
        Assert.Equal(t + 2 + 12, player.SwitchReadyTick);
        Assert.False(Step(t + 3, InputButtons.None));
        Assert.False(Step(t + 4, InputButtons.Slot3 | InputButtons.Fire));
        Assert.Equal(t + 4 + 12, player.SwitchReadyTick);
        for (uint now = t + 5; now < t + 16; now++) Assert.False(Step(now, now % 2 == 0 ? InputButtons.Fire : InputButtons.None));
        Assert.Equal(kestrel.MagazineSize, player.Inventory.Slots[2].MagAmmo);   // nothing spent while it was coming up
        Assert.True(Step(t + 16, InputButtons.Fire));                           // 12 ticks after the last switch
        Assert.Equal(2, shots);
    }

    // The wait belongs to the weapon now in hand: an empty slot has none, and selecting the slot already in hand starts none.
    [Fact]
    public void TheEquipWait_IsTheWeaponInHand_AndOnlyOnAChange()
    {
        var catalog = TestWeapons.Create(equipSeconds: 0.5f);   // 15 ticks
        var player = new PlayerEntity(1, 1, "e", 8);
        TestGameData.CombatLoadout.ApplyTo(player.Inventory, catalog);
        WeaponRules.ResetState(player);
        Assert.Equal(0u, player.SwitchReadyTick);
        Assert.False(WeaponRules.SelectSlot(player, InputButtons.Slot1, 50));   // already in hand
        Assert.Equal(0u, player.SwitchReadyTick);
        Assert.True(WeaponRules.SelectSlot(player, InputButtons.Slot3, 50));    // empty slot: nothing to bring up
        Assert.Equal(50u, player.SwitchReadyTick);
        Assert.True(WeaponRules.SelectSlot(player, InputButtons.Slot2, 60));
        Assert.Equal(75u, player.SwitchReadyTick);
        WeaponRules.ResetState(player);                                          // a respawn starts with the weapon up
        Assert.Equal(0u, player.SwitchReadyTick);
    }

    [Fact]
    public void Loadout_FillsSlots_AndReserves()
    {
        Assert.Equal(0, _player.Inventory.CurrentSlot);
        Assert.Equal(TestWeapons.AutoMagazine, Slot(0).MagAmmo);
        Assert.Equal(TestWeapons.SemiMagazine, Slot(1).MagAmmo);
        Assert.True(Slot(2).IsEmpty);
        Assert.Equal(TestGameData.LoadoutMediumAmmo, Reserve(AmmoType.Medium));
        Assert.False(_player.Reloading);
    }

    [Fact]
    public void Auto_FireEveryTick_IsLimitedToInterval()
    {
        int shots = 0;
        for (uint now = 0; now < 9; now++)
        {
            bool fired = Tick(now, InputButtons.Fire);
            Assert.Equal(now % TestWeapons.AutoInterval == 0, fired);
            if (fired) shots++;
        }
        Assert.Equal(3, shots);
        Assert.Equal(TestWeapons.AutoMagazine - 3, Slot(0).MagAmmo);
    }

    [Fact]
    public void Auto_LastRound_StartsReload_ThatBlocksFireUntilDone_AndTakesFromTheReserve()
    {
        uint now = 0;
        for (int i = 0; i < TestWeapons.AutoMagazine; i++, now += TestWeapons.AutoInterval) Assert.True(Tick(now, InputButtons.Fire));
        uint lastShot = now - TestWeapons.AutoInterval;   // 15
        Assert.Equal(0, Slot(0).MagAmmo);
        Assert.True(_player.Reloading);
        Assert.Equal(lastShot + TestWeapons.AutoReload, _player.ReloadEndTick);

        for (; now < lastShot + TestWeapons.AutoReload; now++) Assert.False(Tick(now, InputButtons.Fire));
        Assert.True(Tick(now, InputButtons.Fire));   // reload finished this tick, then fired
        Assert.Equal(TestWeapons.AutoMagazine - 1, Slot(0).MagAmmo);
        Assert.Equal(TestGameData.LoadoutMediumAmmo - TestWeapons.AutoMagazine, Reserve(AmmoType.Medium));
    }

    [Fact]
    public void Semi_HoldingFire_FiresOnce_PressAgainFiresAfterInterval()
    {
        Assert.True(Tick(0, InputButtons.Slot2 | InputButtons.Fire));   // switch, then fire on the press
        Assert.Equal(1, _player.Inventory.CurrentSlot);
        for (uint now = 1; now < 40; now++) Assert.False(Tick(now, InputButtons.Fire));

        Assert.False(Tick(40, InputButtons.None));
        Assert.True(Tick(41, InputButtons.Fire));
    }

    [Fact]
    public void Semi_PressDuringInterval_IsLost()
    {
        Assert.True(Tick(0, InputButtons.Slot2 | InputButtons.Fire));
        Assert.False(Tick(1, InputButtons.None));
        Assert.False(Tick(5, InputButtons.Fire));        // too early: the press is used up
        Assert.False(Tick(20, InputButtons.Fire));       // still held: no new press
        Assert.False(Tick(21, InputButtons.None));
        Assert.True(Tick(22, InputButtons.Fire));
    }

    [Fact]
    public void ReloadButton_RefillsPartialMagazine_FromTheReserve_AfterReloadTime()
    {
        Assert.True(Tick(0, InputButtons.Fire));
        Assert.False(Tick(1, InputButtons.Reload));
        Assert.True(_player.Reloading);
        Assert.Equal(1u + TestWeapons.AutoReload, _player.ReloadEndTick);
        Assert.False(Tick(10, InputButtons.Fire));   // no fire while reloading
        Assert.Equal(TestGameData.LoadoutMediumAmmo, Reserve(AmmoType.Medium));   // nothing moves before the end

        Tick(1 + TestWeapons.AutoReload, InputButtons.None);
        Assert.False(_player.Reloading);
        Assert.Equal(TestWeapons.AutoMagazine, Slot(0).MagAmmo);
        Assert.Equal(TestGameData.LoadoutMediumAmmo - 1, Reserve(AmmoType.Medium));
        Assert.True(_player.Inventory.Changed);   // the owner must hear about the new reserve
    }

    [Fact]
    public void ReloadButton_WithFullMagazine_DoesNothing()
    {
        Tick(0, InputButtons.Reload);
        Assert.False(_player.Reloading);
    }

    // Spec §6: with fewer reserve rounds than the magazine needs, the reload fills what it can.
    [Fact]
    public void Reload_WithAShortReserve_FillsOnlyWhatTheReserveHas()
    {
        Slot(0).MagAmmo = 1;
        _player.Inventory.SetAmmo(AmmoType.Medium, 2);
        Tick(0, InputButtons.Reload);
        Tick(TestWeapons.AutoReload, InputButtons.None);

        Assert.Equal(3, Slot(0).MagAmmo);
        Assert.Equal(0, Reserve(AmmoType.Medium));
    }

    // Spec §6: with an empty reserve there is no reload, neither by the button nor after the last round,
    // so the snapshot never reports a reload that could not finish.
    [Fact]
    public void EmptyReserve_NoReload_EvenAfterTheLastRound()
    {
        _player.Inventory.SetAmmo(AmmoType.Medium, 0);
        Slot(0).MagAmmo = 1;
        Tick(0, InputButtons.Reload);
        Assert.False(_player.Reloading);

        Assert.True(Tick(1, InputButtons.Fire));
        Assert.Equal(0, Slot(0).MagAmmo);
        Assert.False(_player.Reloading);
        for (uint now = 2; now < 60; now++) Assert.False(Tick(now, InputButtons.Fire));
        Assert.False(_player.Reloading);
        Assert.Equal(0, Slot(0).MagAmmo);
    }

    // A reload whose reserve is emptied before it ends (the death drop takes the whole reserve) moves
    // nothing when it completes: it only ever moves what the reserve still holds.
    [Fact]
    public void Reload_WhoseReserveEmptiesBeforeItEnds_MovesNothing()
    {
        Slot(0).MagAmmo = 1;
        Tick(0, InputButtons.Reload);
        Assert.True(_player.Reloading);
        _player.Inventory.SetAmmo(AmmoType.Medium, 0);

        Tick(TestWeapons.AutoReload, InputButtons.None);

        Assert.False(_player.Reloading);
        Assert.Equal(1, Slot(0).MagAmmo);
        Assert.Equal(0, Reserve(AmmoType.Medium));
    }

    // Review Focus: across any mix of fire, reload and slot switches, magazine + reserve of an ammo type
    // only ever goes down, by exactly one per shot of a weapon using it. A reload never makes rounds.
    [Fact]
    public void RoundsAreConserved_AcrossFireReloadAndSwitches()
    {
        var rng = new Random(77);
        InputButtons[] choices =
        {
            InputButtons.Fire, InputButtons.Fire, InputButtons.None, InputButtons.Reload,
            InputButtons.Slot1, InputButtons.Slot2, InputButtons.Slot3, InputButtons.Fire | InputButtons.Reload,
        };
        int Total(AmmoType type)
        {
            int total = _player.Inventory.GetAmmo(type);
            foreach (HeldWeapon held in _player.Inventory.Slots)
                if (!held.IsEmpty && held.Weapon!.AmmoType == type) total += held.MagAmmo;
            return total;
        }

        int medium = Total(AmmoType.Medium);
        int heavy = Total(AmmoType.Heavy);
        for (uint now = 0; now < 3000; now++)
        {
            bool fired = Tick(now, choices[rng.Next(choices.Length)]);
            // A shot comes from the slot that is current after the tick's switch.
            AmmoType shotType = fired ? _player.Inventory.Current.Weapon!.AmmoType : AmmoType.None;
            int expectedMedium = medium - (shotType == AmmoType.Medium ? 1 : 0);
            int expectedHeavy = heavy - (shotType == AmmoType.Heavy ? 1 : 0);
            medium = Total(AmmoType.Medium);
            heavy = Total(AmmoType.Heavy);
            Assert.Equal(expectedMedium, medium);
            Assert.Equal(expectedHeavy, heavy);
        }
        Assert.True(medium < TestGameData.LoadoutMediumAmmo + TestWeapons.AutoMagazine, "the test must actually shoot");
    }

    [Fact]
    public void Switch_CancelsReload_AndKeepsAmmoPerSlot()
    {
        Assert.True(Tick(0, InputButtons.Fire));
        Tick(1, InputButtons.Reload);
        Tick(2, InputButtons.Slot2);
        Assert.Equal(1, _player.Inventory.CurrentSlot);
        Assert.False(_player.Reloading);

        Tick(3, InputButtons.Slot1);
        Assert.Equal(0, _player.Inventory.CurrentSlot);
        Assert.Equal(TestWeapons.AutoMagazine - 1, Slot(0).MagAmmo);   // the cancelled reload did not refill
        Assert.Equal(TestWeapons.SemiMagazine, Slot(1).MagAmmo);
        Assert.Equal(TestGameData.LoadoutMediumAmmo, Reserve(AmmoType.Medium));
    }

    [Fact]
    public void Switch_DoesNotResetTheOtherWeaponsInterval()
    {
        Assert.True(Tick(0, InputButtons.Slot2 | InputButtons.Fire));   // semi: next shot at 15
        Tick(1, InputButtons.Slot1);
        Tick(2, InputButtons.Slot2);
        Assert.False(Tick(3, InputButtons.Fire));
        Assert.False(Tick(4, InputButtons.None));
        Assert.True(Tick(15, InputButtons.Fire));
    }

    [Theory]
    [InlineData(InputButtons.Slot1 | InputButtons.Slot2)]
    [InlineData(InputButtons.Slot2 | InputButtons.Slot3)]
    [InlineData(InputButtons.Slot1 | InputButtons.Slot2 | InputButtons.Slot3)]
    public void SeveralSlotBits_AreIgnored(InputButtons bits)
    {
        Tick(0, bits);
        Assert.Equal(0, _player.Inventory.CurrentSlot);
        Tick(1, InputButtons.Slot2);
        Tick(2, bits);
        Assert.Equal(1, _player.Inventory.CurrentSlot);
    }

    // D10 decision (replaces Phase 3's "slot beyond the loadout is ignored"): an empty slot can be selected
    // (no weapon out), and then nothing fires, reloads or spends anything.
    [Fact]
    public void EmptySlot_CanBeSelected_ButNeverFiresOrReloads()
    {
        Assert.True(WeaponRules.SelectSlot(_player, InputButtons.Slot3, 0));
        Assert.Equal(2, _player.Inventory.CurrentSlot);

        for (uint now = 0; now < 10; now++) Assert.False(Tick(now, InputButtons.Fire | InputButtons.Reload));
        Assert.False(_player.Reloading);
        Assert.Equal(TestWeapons.AutoMagazine, Slot(0).MagAmmo);
        Assert.Equal(TestGameData.LoadoutMediumAmmo, Reserve(AmmoType.Medium));
    }

    [Fact]
    public void EmptyInventory_NeverFires()
    {
        var bare = new PlayerEntity(2, 2, "b", 8);
        StartingLoadout.Empty.ApplyTo(bare.Inventory, _data.Weapons);
        for (uint now = 0; now < 10; now++) Assert.False(WeaponRules.Apply(bare, InputButtons.Fire, true, now));
        Assert.False(bare.Reloading);
    }

    [Fact]
    public void InvalidAim_DoesNotFire_NorSpendAmmoOrInterval()
    {
        Assert.False(Tick(0, InputButtons.Fire, aimValid: false));
        Assert.Equal(TestWeapons.AutoMagazine, Slot(0).MagAmmo);
        Assert.True(Tick(1, InputButtons.Fire));
    }

    [Fact]
    public void FireWithEmptyMagazineAndNoReload_StartsReload()
    {
        Slot(0).MagAmmo = 0;
        Assert.False(Tick(0, InputButtons.Fire));
        Assert.True(_player.Reloading);
    }
}
