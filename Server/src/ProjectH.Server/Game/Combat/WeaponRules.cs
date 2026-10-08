using System;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Combat;

// Slot switch, magazine, reload and fire interval (Phase 3 D14, Phase 4 D10), enforced in server ticks.
// Pure state changes on PlayerEntity and its Inventory: Match decides when to call them and does the ray
// and the packets. The client copies these rules for presentation (Client WeaponState); keep the two in step.
public static class WeaponRules
{
    // 기능: 대기 중인 것이 없는 상태로 되돌린다(입장·리스폰, 인벤토리를 채운 뒤. Phase 17: 수류탄 간격도).
    // 입력: player - 플레이어.
    // 출력: 반환값 없음.
    public static void ResetState(PlayerEntity player)
    {
        player.Reloading = false;
        player.ReloadEndTick = 0;
        player.FireHeld = false;
        player.NextGrenadeTick = 0;
    }

    // Runs every tick for a living player, whether or not an input arrived. A finished reload moves rounds
    // from the reserve into the magazine; it never makes rounds (Review Focus).
    public static void UpdateReload(PlayerEntity player, uint now)
    {
        if (!player.Reloading || now < player.ReloadEndTick) return;
        player.Reloading = false;

        Inventory inventory = player.Inventory;
        ref HeldWeapon held = ref inventory.Current;
        if (held.IsEmpty) return;
        AmmoType type = held.Weapon!.AmmoType;
        int take = Math.Min(held.Weapon.MagazineSize - held.MagAmmo, inventory.GetAmmo(type));
        if (take <= 0) return;
        held.MagAmmo += take;
        inventory.SetAmmo(type, inventory.GetAmmo(type) - take);
        inventory.Changed = true;
    }

    // Exactly one of Slot1/Slot2/Slot3 selects that slot, empty or not (an empty slot means no weapon out);
    // several bits at once are contradictory and ignored. A switch cancels the reload, which belongs to the
    // weapon being put away. Returns true when the current slot changed.
    public static bool SelectSlot(PlayerEntity player, InputButtons buttons)
    {
        int target;
        switch (buttons & (InputButtons.Slot1 | InputButtons.Slot2 | InputButtons.Slot3))
        {
            case InputButtons.Slot1: target = 0; break;
            case InputButtons.Slot2: target = 1; break;
            case InputButtons.Slot3: target = 2; break;
            default: return false;
        }
        if (target == player.Inventory.CurrentSlot) return false;
        player.Inventory.CurrentSlot = target;
        player.Reloading = false;
        return true;
    }

    // 기능: Client가 보낸 입력 하나를 재장전 → 발사 순서로 처리한다. 쏘면 탄 하나(산탄총도 방아쇠 한 번에 하나)와 발사 간격을 쓴다.
    // 입력: player - 플레이어, buttons - 눌린 버튼, aimValid - 조준이 유효하고(유한) 쏠 수 있는지(Phase 17: 투사체 무기는 빈 투사체 칸이
    //   있고 ProjectilesAllowed가 true일 때(시작 카운트다운·결과 화면 아님)만 true. false면 탄도 간격도 쓰지 않는다), now - 마지막 Tick.
    // 출력: 쐈으면 true. 빈 칸은 쏘지 않는다.
    public static bool Apply(PlayerEntity player, InputButtons buttons, bool aimValid, uint now)
    {
        bool fireHeld = (buttons & InputButtons.Fire) != 0;
        ref HeldWeapon held = ref player.Inventory.Current;
        if (held.IsEmpty)
        {
            player.FireHeld = fireHeld;
            return false;
        }
        WeaponDefinition weapon = held.Weapon!;

        if ((buttons & InputButtons.Reload) != 0 && !player.Reloading && held.MagAmmo < weapon.MagazineSize)
            TryStartReload(player, weapon, now);

        bool trigger = fireHeld && (weapon.Automatic || !player.FireHeld);
        player.FireHeld = fireHeld;
        if (!trigger || !aimValid || player.Reloading || now < held.NextFireTick) return false;

        if (held.MagAmmo == 0)
        {
            TryStartReload(player, weapon, now);
            return false;
        }

        held.MagAmmo--;
        held.NextFireTick = now + weapon.FireIntervalTicks;
        if (held.MagAmmo == 0) TryStartReload(player, weapon, now);
        return true;
    }

    // A reload needs reserve rounds of the weapon's type. Without them it does not start at all, so the
    // snapshot never reports a reload that cannot finish. That holds only while nothing empties a reserve
    // during a reload: every path that takes reserve rounds out of the inventory must cancel the reload
    // first (today only the death drop takes reserve rounds, and Kill cancels the reload before it runs).
    // Even if one did not, UpdateReload moves at most what the reserve still holds, so it moves 0 rounds
    // and never makes any.
    private static void TryStartReload(PlayerEntity player, WeaponDefinition weapon, uint now)
    {
        if (player.Inventory.GetAmmo(weapon.AmmoType) == 0) return;
        player.Reloading = true;
        player.ReloadEndTick = now + weapon.ReloadTicks;
    }
}
