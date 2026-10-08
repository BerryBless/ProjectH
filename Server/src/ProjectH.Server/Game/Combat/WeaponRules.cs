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
    // 기능: 대기 중인 것이 없는 상태로 되돌린다(입장·리스폰, 인벤토리를 채운 뒤. Phase 17: 수류탄 간격도, 리뷰 수정 C2: 교체 대기도).
    // 입력: player - 플레이어.
    // 출력: 반환값 없음.
    public static void ResetState(PlayerEntity player)
    {
        player.Reloading = false;
        player.ReloadEndTick = 0;
        player.FireHeld = false;
        player.NextGrenadeTick = 0;
        player.SwitchReadyTick = 0;
    }

    // 기능: 손에 든 것이 바뀐 뒤의 교체 대기를 둔다(리뷰 수정 C2, SEC-8). 칸 교체, 손으로 줍기·교환, 버리기 뒤에 부른다.
    //   무기는 칸마다 자기 발사 간격을 갖고 있어서, 이 대기가 없으면 칸을 바꿔 가며 간격을 건너뛸 수 있다.
    // 입력: player - 플레이어(지금 손에 든 칸이 이미 바뀐 상태), now - 마지막 Tick.
    // 출력: 반환값 없음. SwitchReadyTick = now + 손에 든 무기의 EquipTicks(빈손이면 now).
    public static void Equip(PlayerEntity player, uint now)
    {
        ref HeldWeapon held = ref player.Inventory.Current;
        player.SwitchReadyTick = now + (held.IsEmpty ? 0u : held.Weapon!.EquipTicks);
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

    // 기능: Slot1/Slot2/Slot3 중 정확히 하나가 눌렸으면 그 칸을 든다(비어 있어도. 빈 칸은 무기를 넣은 상태). 여러 비트는 모순이라 무시한다.
    //   바뀌면 재장전(넣는 무기의 것)을 취소하고, 리뷰 수정 C2: 새로 든 무기의 교체 대기를 둔다(Equip).
    // 입력: player - 플레이어, buttons - 이번 입력에서 눌린 버튼, now - 마지막 Tick.
    // 출력: 칸이 바뀌었으면 true, 아니면 false(같은 칸은 대기를 새로 두지 않는다).
    public static bool SelectSlot(PlayerEntity player, InputButtons buttons, uint now)
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
        Equip(player, now);
        return true;
    }

    // 기능: Client가 보낸 입력 하나를 재장전 → 발사 순서로 처리한다. 쏘면 탄 하나(산탄총도 방아쇠 한 번에 하나)와 발사 간격을 쓴다.
    // 입력: player - 플레이어, buttons - 눌린 버튼, aimValid - 조준이 유효하고(유한) 쏠 수 있는지(Phase 17: 투사체 무기는 빈 투사체 칸이
    //   있고 ProjectilesAllowed가 true일 때(시작 카운트다운·결과 화면 아님)만 true. false면 탄도 간격도 쓰지 않는다), now - 마지막 Tick.
    // 출력: 쐈으면 true. 빈 칸은 쏘지 않는다. 리뷰 수정 C2: now < SwitchReadyTick(교체 대기 중)이면 쏘지 않는다(탄·간격을 쓰지 않고 누름은 쓴다,
    //   발사 간격과 같다). R 재장전은 대기 중에도 시작한다.
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
        if (!trigger || !aimValid || player.Reloading || now < held.NextFireTick || now < player.SwitchReadyTick) return false;

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
