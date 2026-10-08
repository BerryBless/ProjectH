using System;
using System.Collections.Generic;
using System.IO;
using ProjectH.Client.Game;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Review fix C2: the client's WeaponState predicts the server's WeaponRules, equip wait included. The same input sequence goes
// through both (server tick now = client step, the first at 0), and every step must agree on the shot, the slot, the magazine,
// the reload and the wait left (server SwitchReadyTick - next tick vs client SwitchRemainingSteps). Shipped weapons.json
// (equipSeconds 0.4 = 12 ticks at 30 Hz).
public class ClientWeaponStateParityTests
{
    private const byte Ar = 1, Kestrel = 2;
    private static readonly WeaponCatalog Shipped = WeaponCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, "weapons.json"), 30);

    // 기능: 같은 무기 배치로 서버 플레이어와 Client WeaponState를 만든다(Client는 서버 인벤토리를 첫 InventoryState로 받는다).
    // 입력: slots - 칸 0·1·2의 무기 id(0 = 빈 칸), mag - 칸마다의 탄창(null = 가득), heavy·medium - 예비탄.
    // 출력: (서버 플레이어, Client 상태).
    private static (PlayerEntity Server, WeaponState Client) Make(byte[] slots, int[]? mag = null, int heavy = 20, int medium = 90)
    {
        var player = new PlayerEntity(1, 1, "parity", 8);
        for (int i = 0; i < Inventory.SlotCount; i++)
        {
            if (slots[i] == 0) continue;
            Assert.True(Shipped.TryGetById(slots[i], out WeaponDefinition weapon));
            player.Inventory.Slots[i] = new HeldWeapon { Weapon = weapon, MagAmmo = mag?[i] ?? weapon.MagazineSize };
        }
        player.Inventory.SetAmmo(AmmoType.Heavy, heavy);
        player.Inventory.SetAmmo(AmmoType.Medium, medium);
        WeaponRules.ResetState(player);
        var client = new WeaponState(Shipped.WireInfos);
        client.ApplyInventory(player.Inventory.ToWire(0));
        return (player, client);
    }

    // 기능: 입력열을 서버 규칙과 Client 복사본에 같은 순서로 넣고 Step마다 결과를 비교한다.
    // 입력: server·client - 같은 상태에서 시작한 두 쪽, inputs - Step마다의 버튼.
    // 출력: 쏜 Step 목록(두 쪽이 같다).
    private static List<int> Run(PlayerEntity server, WeaponState client, InputButtons[] inputs)
    {
        var shots = new List<int>();
        for (int i = 0; i < inputs.Length; i++)
        {
            uint now = (uint)i;
            WeaponRules.UpdateReload(server, now);
            WeaponRules.SelectSlot(server, inputs[i], now);
            bool serverFired = WeaponRules.Apply(server, inputs[i], aimValid: true, now);
            bool clientFired = client.Step((uint)(i + 1), inputs[i]);
            Assert.True(serverFired == clientFired, $"step {i}: server fired {serverFired}, client {clientFired}");
            Assert.Equal(server.Inventory.CurrentSlot, client.Slot);
            Assert.Equal(server.Inventory.Current.IsEmpty ? 0 : server.Inventory.Current.MagAmmo, client.Ammo);
            Assert.Equal(server.Reloading, client.Reloading);
            Assert.Equal((int)Math.Max(0L, (long)server.SwitchReadyTick - (now + 1)), client.SwitchRemainingSteps);
            if (serverFired) shots.Add(i);
        }
        return shots;
    }

    // 기능: 누르고 떼기를 번갈아 하는 입력열 조각을 만든다(짝수 Step에 누름).
    // 입력: from·to - Step 범위 [from, to), press - 누를 버튼.
    // 출력: 버튼 배열 조각.
    private static IEnumerable<InputButtons> Taps(int from, int to, InputButtons press)
    {
        for (int i = from; i < to; i++) yield return i % 2 == 0 ? press : InputButtons.None;
    }

    [Fact]
    public void ThreeKestrels_SwitchingFire_OnTheSameSteps()
    {
        var (server, client) = Make(new[] { Kestrel, Kestrel, Kestrel });
        var inputs = new List<InputButtons>
        {
            InputButtons.Fire, InputButtons.None, InputButtons.Slot2 | InputButtons.Fire, InputButtons.None,
            InputButtons.Slot3 | InputButtons.Fire,
        };
        inputs.AddRange(Taps(5, 16, InputButtons.Fire));
        inputs.Add(InputButtons.Fire);   // step 16: 12 after the last switch
        inputs.AddRange(Taps(17, 30, InputButtons.Fire));
        Assert.Equal(new[] { 0, 16 }, Run(server, client, inputs.ToArray()));
    }

    // Selecting the slot in hand starts no wait; an empty slot has none; coming back waits the full equip time.
    [Fact]
    public void TheSameSlot_AnEmptySlot_AndBack_AgreeStepByStep()
    {
        var (server, client) = Make(new[] { Ar, Kestrel, (byte)0 });
        var inputs = new List<InputButtons>
        {
            InputButtons.Fire, InputButtons.Fire, InputButtons.Slot1 | InputButtons.Fire, InputButtons.Fire, InputButtons.Fire,
            InputButtons.Slot3 | InputButtons.Fire, InputButtons.Fire, InputButtons.None, InputButtons.Slot1 | InputButtons.Fire,
        };
        for (int i = inputs.Count; i < 30; i++) inputs.Add(InputButtons.Fire);
        List<int> shots = Run(server, client, inputs.ToArray());
        Assert.Equal(new[] { 0, 3, 20, 23, 26, 29 }, shots);   // AR every 3 ticks; back in hand at 8, first shot at 8 + 12
    }

    // R during the wait starts the reload on both sides; the wait and the reload both have to pass before the next shot.
    [Fact]
    public void AReloadStarted_DuringTheWait_AgreesStepByStep()
    {
        var (server, client) = Make(new[] { Kestrel, Ar, (byte)0 }, mag: new[] { 5, 10, 0 });
        var inputs = new List<InputButtons> { InputButtons.Slot2 | InputButtons.Reload, InputButtons.Fire, InputButtons.None };
        for (int i = inputs.Count; i < 80; i++) inputs.Add(InputButtons.Fire);
        List<int> shots = Run(server, client, inputs.ToArray());
        Assert.True(server.Inventory.Slots[1].MagAmmo > 10 - shots.Count, "the reload refilled the magazine");
        Assert.True(shots.Count > 0 && shots[0] >= 12, $"first shot at {(shots.Count > 0 ? shots[0] : -1)}");
    }
}
