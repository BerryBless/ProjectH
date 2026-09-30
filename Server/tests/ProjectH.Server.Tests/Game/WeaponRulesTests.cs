using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Rules on one PlayerEntity with the test catalog (TestWeapons): slot 0 auto, 3-tick interval,
// 6 rounds, 30-tick reload; slot 1 semi, 15-tick interval, 2 rounds, 60-tick reload.
public class WeaponRulesTests
{
    private readonly WeaponCatalog _weapons = TestWeapons.Create();
    private readonly PlayerEntity _player = new(1, 1, "a", 8);

    public WeaponRulesTests()
    {
        WeaponRules.Equip(_player, _weapons);
    }

    // One server tick for a living player that sent an input.
    private bool Tick(uint now, InputButtons buttons, bool aimValid = true)
    {
        WeaponRules.UpdateReload(_player, _weapons, now);
        return WeaponRules.Apply(_player, _weapons, buttons, aimValid, now);
    }

    [Fact]
    public void Equip_FillsLoadout()
    {
        Assert.Equal(0, _player.WeaponSlot);
        Assert.Equal(TestWeapons.AutoMagazine, _player.Ammo[0]);
        Assert.Equal(TestWeapons.SemiMagazine, _player.Ammo[1]);
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
        Assert.Equal(TestWeapons.AutoMagazine - 3, _player.Ammo[0]);
    }

    [Fact]
    public void Auto_LastRound_StartsReload_ThatBlocksFireUntilDone()
    {
        uint now = 0;
        for (int i = 0; i < TestWeapons.AutoMagazine; i++, now += TestWeapons.AutoInterval) Assert.True(Tick(now, InputButtons.Fire));
        uint lastShot = now - TestWeapons.AutoInterval;   // 15
        Assert.Equal(0, _player.Ammo[0]);
        Assert.True(_player.Reloading);
        Assert.Equal(lastShot + TestWeapons.AutoReload, _player.ReloadEndTick);

        for (; now < lastShot + TestWeapons.AutoReload; now++) Assert.False(Tick(now, InputButtons.Fire));
        Assert.True(Tick(now, InputButtons.Fire));   // reload finished this tick, then fired
        Assert.Equal(TestWeapons.AutoMagazine - 1, _player.Ammo[0]);
    }

    [Fact]
    public void Semi_HoldingFire_FiresOnce_PressAgainFiresAfterInterval()
    {
        Assert.True(Tick(0, InputButtons.Slot2 | InputButtons.Fire));   // switch, then fire on the press
        Assert.Equal(1, _player.WeaponSlot);
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
    public void ReloadButton_RefillsPartialMagazine_AfterReloadTime()
    {
        Assert.True(Tick(0, InputButtons.Fire));
        Assert.False(Tick(1, InputButtons.Reload));
        Assert.True(_player.Reloading);
        Assert.Equal(1u + TestWeapons.AutoReload, _player.ReloadEndTick);
        Assert.False(Tick(10, InputButtons.Fire));   // no fire while reloading

        Tick(1 + TestWeapons.AutoReload, InputButtons.None);
        Assert.False(_player.Reloading);
        Assert.Equal(TestWeapons.AutoMagazine, _player.Ammo[0]);
    }

    [Fact]
    public void ReloadButton_WithFullMagazine_DoesNothing()
    {
        Tick(0, InputButtons.Reload);
        Assert.False(_player.Reloading);
    }

    [Fact]
    public void Switch_CancelsReload_AndKeepsAmmoPerSlot()
    {
        Assert.True(Tick(0, InputButtons.Fire));
        Tick(1, InputButtons.Reload);
        Tick(2, InputButtons.Slot2);
        Assert.Equal(1, _player.WeaponSlot);
        Assert.False(_player.Reloading);

        Tick(3, InputButtons.Slot1);
        Assert.Equal(0, _player.WeaponSlot);
        Assert.Equal(TestWeapons.AutoMagazine - 1, _player.Ammo[0]);   // the cancelled reload did not refill
        Assert.Equal(TestWeapons.SemiMagazine, _player.Ammo[1]);
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

    [Fact]
    public void BothSlotBits_AreIgnored()
    {
        Tick(0, InputButtons.Slot1 | InputButtons.Slot2);
        Assert.Equal(0, _player.WeaponSlot);
        Tick(1, InputButtons.Slot2);
        Tick(2, InputButtons.Slot1 | InputButtons.Slot2);
        Assert.Equal(1, _player.WeaponSlot);
    }

    [Fact]
    public void SlotBeyondLoadout_IsIgnored()
    {
        string json = "{ \"weapons\": [ { \"id\": 1, \"name\": \"Only\", \"damage\": 10, \"fireIntervalSeconds\": 0.1, " +
                      "\"magazineSize\": 3, \"reloadSeconds\": 1, \"range\": 50, \"automatic\": true } ] }";
        Assert.True(WeaponCatalog.TryParse(json, 30, out var single, out _));
        var player = new PlayerEntity(2, 2, "b", 8);
        WeaponRules.Equip(player, single!);

        WeaponRules.Apply(player, single!, InputButtons.Slot2, true, 0);
        Assert.Equal(0, player.WeaponSlot);
        Assert.Equal(0, player.Ammo[1]);
    }

    [Fact]
    public void InvalidAim_DoesNotFire_NorSpendAmmoOrInterval()
    {
        Assert.False(Tick(0, InputButtons.Fire, aimValid: false));
        Assert.Equal(TestWeapons.AutoMagazine, _player.Ammo[0]);
        Assert.True(Tick(1, InputButtons.Fire));
    }

    [Fact]
    public void FireWithEmptyMagazineAndNoReload_StartsReload()
    {
        _player.Ammo[0] = 0;
        Assert.False(Tick(0, InputButtons.Fire));
        Assert.True(_player.Reloading);
    }
}
