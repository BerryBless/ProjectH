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

    // 기능: 진행 중인 사용을 중단 버튼(발사·칸·도구·버리기·다른 회복)이 눌렸으면 취소한다(spec §2 2단계, 실제 입력만).
    // 입력: player - 플레이어, buttons - 눌린 버튼.
    // 출력: 진행 중인 사용을 취소했으면 true.
    public static bool CancelIfInterrupted(PlayerEntity player, InputButtons buttons)
    {
        Inventory inventory = player.Inventory;
        if (inventory.Using == ConsumableType.None) return false;
        InputButtons other = inventory.Using == ConsumableType.Medkit ? InputButtons.UseShieldCell : InputButtons.UseMedkit;
        if ((buttons & (Interrupts | other)) == 0) return false;
        Cancel(inventory);
        return true;
    }

    // 기능: 회복 사용을 시작한다(spec §2 8단계, 실제 입력만): 회복 버튼이 정확히 하나, 진행 중인 사용 없음, 소지 1개 이상, 회복할
    //   것이 남아 있을 때만. 진행 중인 회복을 다시 눌러도 아무것도 바뀌지 않는다.
    // 입력: player - 플레이어, items - 아이템 데이터, buttons - 눌린 버튼, now - 마지막 Tick.
    // 출력: 시작했으면 true(Using·UseEndTick·Changed가 바뀐다).
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

    // 기능: 채널이 끝난 사용을 마무리한다(spec §2 9단계, 살아 있는 플레이어마다 매 Tick): 아이템 하나를 쓰고 체력·보호막을 최대까지 올린다.
    // 입력: player - 플레이어, items - 아이템 데이터, now - 마지막 Tick.
    // 출력: 아이템을 써서 회복했으면 true. 진행 중이 아니거나 아직 끝나지 않았으면 false.
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

    // 기능: 진행 중인 사용을 취소한다.
    // 입력: inventory - 인벤토리.
    // 출력: 반환값 없음. 진행 중이었으면 Using = None, UseEndTick = 0, Changed가 켜진다.
    public static void Cancel(Inventory inventory)
    {
        if (inventory.Using == ConsumableType.None) return;
        inventory.Using = ConsumableType.None;
        inventory.UseEndTick = 0;
        inventory.Changed = true;
    }

    // 기능: 회복 소모품의 소지 수를 돌려준다.
    // 입력: inventory - 인벤토리, type - Medkit 또는 ShieldCell.
    // 출력: Medkit이면 Medkits, 아니면 ShieldCells.
    private static int Count(Inventory inventory, ConsumableType type) =>
        type == ConsumableType.Medkit ? inventory.Medkits : inventory.ShieldCells;
}
