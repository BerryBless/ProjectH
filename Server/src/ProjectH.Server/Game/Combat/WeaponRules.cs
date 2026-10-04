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
    // 기능: 플레이어의 재장전·발사 버튼 상태를 초기화한다.
    // 입력: player - 대상 플레이어.
    // 출력: 반환값 없음. 진행 중 재장전과 발사 버튼 눌림 상태가 해제된다.
    // Nothing pending. Used at join and at respawn, after the inventory was filled.
    public static void ResetState(PlayerEntity player)
    {
        player.Reloading = false;
        player.ReloadEndTick = 0;
        player.FireHeld = false;
    }

    // 기능: 재장전 완료 Tick이 지났으면 재장전을 끝내고 예비 탄약을 탄창으로 옮긴다.
    // 입력: player - 살아 있는 대상 플레이어, now - 현재 서버 Tick.
    // 출력: 반환값 없음. 완료 Tick이 지났으면 재장전 상태가 풀리고, 옮길 탄약이 있으면 탄창·예비 탄약이 갱신되며 인벤토리 변경 표시가 켜진다.
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

    // 기능: 입력 버튼의 슬롯 선택으로 현재 무기 슬롯을 바꾼다.
    // 입력: player - 대상 플레이어, buttons - Client가 보낸 입력 버튼.
    // 출력: 현재 슬롯이 바뀌었으면 true(재장전 취소됨), 아니면 false.
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

    // 기능: 입력 하나의 재장전·발사 버튼을 서버 Tick 규칙으로 처리한다.
    // 입력: player - 대상 플레이어, buttons - Client가 보낸 입력 버튼, aimValid - 조준 각도가 유효한지, now - 현재 서버 Tick.
    // 출력: 이번 입력으로 발사하면 true(탄약과 발사 간격이 이미 소비됨), 아니면 false.
    // One input the client sent, in the order reload -> fire. Returns true when it fires a shot; the round
    // and the fire interval are already spent then. aimValid false (non-finite aim) is no shot, and an empty
    // slot never fires.
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

    // 기능: 예비 탄약이 있으면 재장전을 시작한다.
    // 입력: player - 대상 플레이어, weapon - 현재 무기, now - 현재 서버 Tick.
    // 출력: 반환값 없음. 예비 탄약이 있으면 재장전 상태와 완료 Tick이 설정된다.
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
