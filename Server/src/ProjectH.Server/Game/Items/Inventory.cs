using System;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Items;

// One weapon slot. Weapon null = empty slot.
public struct HeldWeapon
{
    public WeaponDefinition? Weapon;
    public byte Rarity;
    public int MagAmmo;
    // Per slot (Phase 3 D14): switching slots never skips a weapon's fire interval.
    public uint NextFireTick;

    public bool IsEmpty => Weapon == null;
}

// A player's inventory (D10), owned by the server: 3 weapon slots, the current slot, ammo reserves per
// type, Medkits and Shield Cells. Utility has no slot content yet (Projectile phase). Game loop thread only;
// fixed-size arrays, so nothing here allocates after construction.
public sealed class Inventory
{
    public const int SlotCount = ItemConstants.WeaponSlotCount;

    public readonly HeldWeapon[] Slots = new HeldWeapon[SlotCount];
    private readonly int[] _ammo = new int[ItemConstants.AmmoTypeCount];   // index = AmmoType - 1

    public int CurrentSlot;
    public int Medkits;
    public int ShieldCells;
    // The latest NextFireTick of any weapon this player dropped. A weapon picked up cannot fire before it,
    // so dropping a weapon and picking it up again (maybe into another slot) never skips its fire interval.
    public uint DroppedFireLockTick;
    // The heal being used (D11) and the tick its channel ends; None when nothing is being used.
    public ConsumableType Using;
    public uint UseEndTick;
    // Set by every change the owner must hear about (not by shots: the snapshot carries the magazine).
    // Match sends one InventoryState at the end of a tick in which it was set, then clears it (D14).
    public bool Changed;

    // Phase 13 D5: the tool in hand (the snapshot carries it), and the one before build mode (Q goes back to it).
    public ToolKind Tool;
    public ToolKind PreviousTool;
    // Phase 13 D15: wood, stone and metal (index = BuildMaterialType), 0..BuildingCatalog.MaxResource. Match sends one
    // ResourcesState at the end of a tick in which ResourcesChanged was set, then clears it.
    private readonly int[] _resources = new int[3];
    public bool ResourcesChanged;

    // Phase 14 D9: the reboot cards carried (the card slot): each one is its owner's JoinOrder. 0..CardCount-1 are in use;
    // squad.json maxCardsHeld (at most SquadConstants.MaxCardsHeld) limits the count. Emptied by Clear, a reboot (used up),
    // the holder's elimination (dropped) and the owner leaving (gone).
    public readonly uint[] CardOwners = new uint[SquadConstants.MaxCardsHeld];
    public int CardCount;

    // 기능: 카드 하나를 카드 칸에 넣는다.
    // 입력: ownerJoinOrder - 카드 주인의 JoinOrder.
    // 출력: 칸이 남아 있었으면 true(Changed가 켜진다), 가득 찼으면 false.
    public bool AddCard(uint ownerJoinOrder)
    {
        if (CardCount >= CardOwners.Length) return false;
        CardOwners[CardCount++] = ownerJoinOrder;
        Changed = true;
        return true;
    }

    // 기능: index번째 카드를 칸에서 뺀다(뒤의 카드가 앞으로 온다, 순서 유지).
    // 입력: index - 0..CardCount-1.
    // 출력: 반환값 없음. Changed가 켜진다.
    public void RemoveCardAt(int index)
    {
        for (int i = index; i < CardCount - 1; i++) CardOwners[i] = CardOwners[i + 1];
        CardCount--;
        CardOwners[CardCount] = 0;
        Changed = true;
    }

    // 기능: 이 주인의 카드를 들고 있는지 찾는다.
    // 입력: ownerJoinOrder - 카드 주인의 JoinOrder.
    // 출력: 칸 번호, 없으면 -1.
    public int IndexOfCard(uint ownerJoinOrder)
    {
        for (int i = 0; i < CardCount; i++)
        {
            if (CardOwners[i] == ownerJoinOrder) return i;
        }
        return -1;
    }

    public int Resource(BuildMaterialType material) => _resources[(int)material];

    public void SetResource(BuildMaterialType material, int amount)
    {
        if (_resources[(int)material] == amount) return;
        _resources[(int)material] = amount;
        ResourcesChanged = true;
    }

    public ResourcesState ResourcesToWire() => new()
    {
        Wood = (ushort)Math.Clamp(_resources[0], 0, ushort.MaxValue),
        Stone = (ushort)Math.Clamp(_resources[1], 0, ushort.MaxValue),
        Metal = (ushort)Math.Clamp(_resources[2], 0, ushort.MaxValue),
    };

    public ref HeldWeapon Current => ref Slots[CurrentSlot];

    public int GetAmmo(AmmoType type) => _ammo[(int)type - 1];

    public void SetAmmo(AmmoType type, int value) => _ammo[(int)type - 1] = value;

    // 기능: 인벤토리를 빈 새 생명 상태로 되돌린다(Phase 14: 카드 칸도 비운다).
    // 입력: 없음.
    // 출력: 반환값 없음. Changed·ResourcesChanged가 켜진다.
    public void Clear()
    {
        Array.Clear(Slots);
        Array.Clear(_ammo);
        CurrentSlot = 0;
        Medkits = 0;
        ShieldCells = 0;
        DroppedFireLockTick = 0;
        Using = ConsumableType.None;
        UseEndTick = 0;
        Changed = true;
        // Phase 13: a new life starts with the weapons out and no resources.
        Tool = ToolKind.Weapon;
        PreviousTool = ToolKind.Weapon;
        Array.Clear(_resources);
        ResourcesChanged = true;
        Array.Clear(CardOwners);   // Phase 14: a new life carries no card
        CardCount = 0;
    }

    // 기능: 주인에게 보낼 InventoryState를 만든다(Phase 14: 소지 카드 수 포함).
    // 입력: now - 지금 서버 Tick(Client는 진행 중인 사용의 남은 시간을 여기서부터 센다).
    // 출력: InventoryState 값.
    public InventoryState ToWire(uint now)
    {
        var state = new InventoryState
        {
            Using = Using,
            UseRemainingTicks = Using == ConsumableType.None ? (ushort)0
                : (ushort)Math.Clamp(UseEndTick > now ? UseEndTick - now : 1u, 1u, ushort.MaxValue),
            CurrentSlot = (byte)CurrentSlot,
            LightAmmo = (ushort)GetAmmo(AmmoType.Light),
            MediumAmmo = (ushort)GetAmmo(AmmoType.Medium),
            HeavyAmmo = (ushort)GetAmmo(AmmoType.Heavy),
            Medkits = (byte)Medkits,
            ShieldCells = (byte)ShieldCells,
            RebootCards = (byte)CardCount,   // Phase 14 D9
        };
        for (int i = 0; i < SlotCount; i++)
        {
            ref HeldWeapon held = ref Slots[i];
            if (held.IsEmpty) continue;
            state.SetSlot(i, new InventorySlotState { WeaponId = held.Weapon!.Id, Rarity = held.Rarity, MagAmmo = (byte)held.MagAmmo });
        }
        return state;
    }
}
