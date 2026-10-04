using System;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Items;

// D11: Medkit (4) and Shield Cell (5) are used over a channel time, decided in server ticks. Moving does
// not interrupt; firing, switching slots or tools (Phase 13 final review B9), dropping or pressing the other heal does,
// and so does an accepted placement (Match.TryBuild). Pure state changes on
// PlayerEntity; Match calls them in the spec §2 order (cancel = step 2, start = step 8, finish = step 9).
public static class ConsumableRules
{
    private const InputButtons Interrupts = InputButtons.Fire | InputButtons.Slot1 | InputButtons.Slot2 |
                                            InputButtons.Slot3 | InputButtons.Drop | InputButtons.ToolHarvest | InputButtons.ToolBuild;

    // 기능: 진행 중인 회복 아이템 사용을 방해 입력(사격, 슬롯·도구 전환, 버리기, 다른 회복 버튼)이 있으면 취소한다.
    // 입력: player - 입력을 보낸 플레이어, buttons - 이번 Tick의 실제 입력 버튼.
    // 출력: 진행 중이던 사용을 취소했으면 true, 사용 중이 아니거나 방해 입력이 없으면 false.
    // Step 2, real inputs only. Returns true when a running use was cancelled.
    public static bool CancelIfInterrupted(PlayerEntity player, InputButtons buttons)
    {
        Inventory inventory = player.Inventory;
        if (inventory.Using == ConsumableType.None) return false;
        InputButtons other = inventory.Using == ConsumableType.Medkit ? InputButtons.UseShieldCell : InputButtons.UseMedkit;
        if ((buttons & (Interrupts | other)) == 0) return false;
        Cancel(inventory);
        return true;
    }

    // 기능: 회복 버튼 입력으로 Medkit 또는 Shield Cell 사용(채널링)을 시작한다.
    // 입력: player - 사용할 플레이어, items - 회복 아이템 정의 목록, buttons - 이번 Tick의 실제 입력 버튼, now - 현재 서버 Tick.
    // 출력: 사용을 시작했으면 true(인벤토리에 사용 종류·종료 Tick 기록, Changed 설정), 조건이 맞지 않으면 false.
    // Step 8, real inputs only: exactly one heal button, none running, one in the inventory, and not
    // already at the maximum it restores. Pressing the running heal again changes nothing.
    public static bool TryStart(PlayerEntity player, ItemCatalog items, InputButtons buttons, uint now)
    {
        ConsumableType type;
        switch (buttons & (InputButtons.UseMedkit | InputButtons.UseShieldCell))
        {
            case InputButtons.UseMedkit: type = ConsumableType.Medkit; break;
            case InputButtons.UseShieldCell: type = ConsumableType.ShieldCell; break;
            default: return false;
        }

        Inventory inventory = player.Inventory;
        if (inventory.Using != ConsumableType.None) return false;
        if (Count(inventory, type) == 0) return false;
        ConsumableDefinition definition = items.Consumable(type);
        bool healsSomething = (definition.Heal > 0 && player.Health < CombatRules.MaxHealth) ||
                              (definition.Shield > 0 && player.Shield < CombatRules.MaxShield);
        if (!healsSomething) return false;

        inventory.Using = type;
        inventory.UseEndTick = now + definition.UseTicks;
        inventory.Changed = true;
        return true;
    }

    // 기능: 채널링 종료 Tick에 도달한 회복 사용을 끝내고 아이템 1개를 소모해 체력·보호막을 올린다.
    // 입력: player - 살아 있는 플레이어, items - 회복 아이템 정의 목록, now - 현재 서버 Tick.
    // 출력: 사용이 완료되어 아이템이 소모되고 값이 올랐으면 true, 사용 중이 아니거나 아직 종료 전이면 false.
    // Step 9, every tick of a living player: the channel ends, one item is used up, the values rise to
    // at most the maximum.
    public static bool Complete(PlayerEntity player, ItemCatalog items, uint now)
    {
        Inventory inventory = player.Inventory;
        if (inventory.Using == ConsumableType.None || now < inventory.UseEndTick) return false;

        ConsumableType type = inventory.Using;
        Cancel(inventory);
        if (Count(inventory, type) == 0) return false;   // not reached: nothing removes a stack mid-use but death

        if (type == ConsumableType.Medkit) inventory.Medkits--;
        else inventory.ShieldCells--;
        ConsumableDefinition definition = items.Consumable(type);
        player.Health = Math.Min(CombatRules.MaxHealth, player.Health + definition.Heal);
        player.Shield = Math.Min(CombatRules.MaxShield, player.Shield + definition.Shield);
        return true;
    }

    // 기능: 진행 중인 회복 사용을 아이템 소모 없이 중단한다.
    // 입력: inventory - 사용을 중단할 인벤토리.
    // 출력: 반환값 없음. 사용 중이었으면 Using이 None으로, UseEndTick이 0으로 바뀌고 Changed가 설정된다.
    public static void Cancel(Inventory inventory)
    {
        if (inventory.Using == ConsumableType.None) return;
        inventory.Using = ConsumableType.None;
        inventory.UseEndTick = 0;
        inventory.Changed = true;
    }

    // 기능: 인벤토리에 있는 회복 아이템 개수를 종류별로 센다.
    // 입력: inventory - 확인할 인벤토리, type - 회복 아이템 종류.
    // 출력: Medkit이면 Medkits, 그 밖에는 ShieldCells 개수.
    private static int Count(Inventory inventory, ConsumableType type) =>
        type == ConsumableType.Medkit ? inventory.Medkits : inventory.ShieldCells;
}
