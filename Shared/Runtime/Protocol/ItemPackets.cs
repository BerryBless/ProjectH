using System.Numerics;

namespace ProjectH.Shared.Protocol
{
    // Phase 4 item enums. The values are the wire format, and 0 always means "none".
    public enum AmmoType : byte
    {
        None = 0,
        Light = 1,
        Medium = 2,
        Heavy = 3,
        Shells = 4,    // Phase 17 D13: the shotgun's
        Rockets = 5,   // Phase 17 D13: the rocket launcher's
    }

    public enum ItemKind : byte
    {
        None = 0,
        Weapon = 1,       // DefId = weapon id, Rarity 0-4, Amount = rounds in the magazine (may be 0)
        Ammo = 2,         // DefId = AmmoType, Amount = rounds
        Consumable = 3,   // DefId = ConsumableType, Amount = count
        Material = 4,     // Phase 13 D15: DefId = BuildMaterialType + 1, Amount = resources (a dead player's, picked up on touch)
        RebootCard = 5,   // Phase 14 D9: DefId 0, Rarity 0, Amount = the card owner's entity id (only its team is told of it)
    }

    public enum ConsumableType : byte
    {
        None = 0,
        Medkit = 1,
        ShieldCell = 2,
        // Phase 17 D9: thrown with 6 (InputButtons.ThrowGrenade), never "used" over a channel: its catalog entry has
        // UseTicks, Heal and Shield 0, and InventoryState.Using is never Grenade.
        Grenade = 3,
    }

    // D8: the server picks the target itself, so "too far" and "gone" are one case for the client:
    // nothing it could pick up was in range.
    public enum PickupResultCode : byte
    {
        Ok = 0,
        NothingInRange = 1,
        Full = 2,
    }

    public static class ItemConstants
    {
        public const int RarityCount = 5;          // D4: Common, Uncommon, Rare, Epic, Legendary (index 0-4)
        public const int AmmoTypeCount = 5;        // D3: Light, Medium, Heavy; Phase 17 D13: Shells, Rockets
        public const int ConsumableTypeCount = 3;  // D10: Medkit, ShieldCell; Phase 17 D9: Grenade
        public const int WeaponSlotCount = 3;      // D10
        public const int MaxNameBytes = 16;
    }

    public struct RarityInfo
    {
        public string Name;
        public float DamageMultiplier;
    }

    public struct AmmoInfo
    {
        public AmmoType Type;
        public string Name;
        public ushort Max;
    }

    public struct ConsumableInfo
    {
        public ConsumableType Type;
        public string Name;
        public ushort UseTicks;
        public ushort Heal;
        public ushort Shield;
        public byte MaxStack;
    }

    // Everything the client shows about items (D2). Arrays are indexed by value - 1 for ammo and
    // consumables, by rarity for rarities.
    public sealed class ItemCatalogData
    {
        public RarityInfo[] Rarities;
        public AmmoInfo[] Ammo;
        public ConsumableInfo[] Consumables;
    }

    // S->C, ReliableOrdered, once right after the WeaponCatalog.
    // Largest possible packet: 1 + (1 + 5 x 21) + (1 + 5 x 20) + (1 + 3 x 25) = 284 bytes (Phase 17: 5 ammo types, 3 consumables).
    public static class ItemCatalogPacket
    {
        public const int MaxSize = 284;

        // 기능: 아이템 카탈로그를 ItemCatalog 패킷(등급 목록, 탄 목록, 소모품 목록, 각각 개수 1바이트 뒤 항목들)으로 쓴다.
        // 입력: writer - 쓸 곳, data - 등급·탄·소모품 정보(배열은 null이 아니어야 한다).
        // 출력: 반환값 없음. writer에 패킷이 쓰인다(최대 MaxSize = 284바이트).
        public static void Write(ref PacketWriter writer, ItemCatalogData data)
        {
            writer.WriteByte((byte)PacketId.ItemCatalog);
            writer.WriteByte((byte)data.Rarities.Length);
            for (int i = 0; i < data.Rarities.Length; i++)
            {
                writer.WriteString(data.Rarities[i].Name, ItemConstants.MaxNameBytes);
                writer.WriteSingle(data.Rarities[i].DamageMultiplier);
            }
            writer.WriteByte((byte)data.Ammo.Length);
            for (int i = 0; i < data.Ammo.Length; i++)
            {
                writer.WriteByte((byte)data.Ammo[i].Type);
                writer.WriteString(data.Ammo[i].Name, ItemConstants.MaxNameBytes);
                writer.WriteUInt16(data.Ammo[i].Max);
            }
            writer.WriteByte((byte)data.Consumables.Length);
            for (int i = 0; i < data.Consumables.Length; i++)
            {
                ConsumableInfo c = data.Consumables[i];
                writer.WriteByte((byte)c.Type);
                writer.WriteString(c.Name, ItemConstants.MaxNameBytes);
                writer.WriteUInt16(c.UseTicks);
                writer.WriteUInt16(c.Heal);
                writer.WriteUInt16(c.Shield);
                writer.WriteByte(c.MaxStack);
            }
        }

        // 기능: ItemCatalog 본문을 읽는다(Phase 17: 탄 5종, 소모품 3종. Grenade는 UseTicks·Heal·Shield 0, 회복은 둘 다 필요).
        // 입력: reader - 본문(PacketId 뒤).
        // 출력: 성공하면 true와 카탈로그, 서버가 보내지 않는 값이면 false.
        // Allocates the arrays and names: read once per join, never on the per-tick path.
        public static bool TryRead(ref PacketReader reader, out ItemCatalogData data)
        {
            data = null;
            if (!reader.TryReadByte(out byte rarityCount) || rarityCount != ItemConstants.RarityCount) return false;
            var rarities = new RarityInfo[rarityCount];
            for (int i = 0; i < rarityCount; i++)
            {
                if (!reader.TryReadString(ItemConstants.MaxNameBytes, out rarities[i].Name) || rarities[i].Name.Length == 0) return false;
                if (!reader.TryReadSingle(out rarities[i].DamageMultiplier) || !Finite.Check(rarities[i].DamageMultiplier) ||
                    rarities[i].DamageMultiplier <= 0f) return false;
            }

            if (!reader.TryReadByte(out byte ammoCount) || ammoCount != ItemConstants.AmmoTypeCount) return false;
            var ammo = new AmmoInfo[ammoCount];
            for (int i = 0; i < ammoCount; i++)
            {
                if (!reader.TryReadByte(out byte type) || type != i + 1) return false;
                ammo[i].Type = (AmmoType)type;
                if (!reader.TryReadString(ItemConstants.MaxNameBytes, out ammo[i].Name) || ammo[i].Name.Length == 0) return false;
                if (!reader.TryReadUInt16(out ammo[i].Max) || ammo[i].Max == 0) return false;
            }

            if (!reader.TryReadByte(out byte consumableCount) || consumableCount != ItemConstants.ConsumableTypeCount) return false;
            var consumables = new ConsumableInfo[consumableCount];
            for (int i = 0; i < consumableCount; i++)
            {
                if (!reader.TryReadByte(out byte type) || type != i + 1) return false;
                consumables[i].Type = (ConsumableType)type;
                if (!reader.TryReadString(ItemConstants.MaxNameBytes, out consumables[i].Name) || consumables[i].Name.Length == 0) return false;
                if (!reader.TryReadUInt16(out consumables[i].UseTicks)) return false;
                if (!reader.TryReadUInt16(out consumables[i].Heal)) return false;
                if (!reader.TryReadUInt16(out consumables[i].Shield)) return false;
                // Phase 17 D9: a grenade is thrown, not used: no channel and nothing healed. A heal needs both.
                if (consumables[i].Type == ConsumableType.Grenade)
                {
                    if (consumables[i].UseTicks != 0 || consumables[i].Heal != 0 || consumables[i].Shield != 0) return false;
                }
                else if (consumables[i].UseTicks == 0 || (consumables[i].Heal == 0 && consumables[i].Shield == 0))
                {
                    return false;
                }
                if (!reader.TryReadByte(out consumables[i].MaxStack) || consumables[i].MaxStack == 0) return false;
            }

            data = new ItemCatalogData { Rarities = rarities, Ammo = ammo, Consumables = consumables };
            return true;
        }
    }

    // One item lying in the world (D14): 19 bytes, no PacketId. Used by WorldItems and ItemSpawned.
    public struct WorldItemData
    {
        public const int Size = 19;   // id 2 + kind 1 + defId 1 + rarity 1 + amount 2 + position 12

        public ushort ItemId;
        public ItemKind Kind;
        public byte DefId;
        public byte Rarity;
        public ushort Amount;
        public Vector3 Position;

        // 기능: 월드 아이템 하나(id, 종류, 정의 id, 등급, 수량, 위치)를 PacketId 없이 쓴다.
        // 입력: writer - 쓸 곳, item - 아이템.
        // 출력: 반환값 없음. writer에 Size(19)바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in WorldItemData item)
        {
            writer.WriteUInt16(item.ItemId);
            writer.WriteByte((byte)item.Kind);
            writer.WriteByte(item.DefId);
            writer.WriteByte(item.Rarity);
            writer.WriteUInt16(item.Amount);
            writer.WriteVector3(item.Position);
        }

        // 기능: 월드 아이템 하나(19바이트)를 읽고 종류별 값 규칙을 검사한다.
        // 입력: reader - 아이템 기록이 시작되는 곳, item - 결과.
        // 출력: 성공하면 true와 아이템. 짧거나, id 0, 모르는 종류·정의 id, 무기가 아닌데 등급이 있거나, 탄·소모품·재료·카드 수량 0, 위치가 유한하지 않으면 false.
        // Rejects values the server never sends: id 0, an unknown kind or type, a rarity on a
        // non-weapon, an empty ammo or consumable stack, a non-finite position.
        public static bool TryRead(ref PacketReader reader, out WorldItemData item)
        {
            item = default;
            if (reader.Remaining < Size) return false;
            reader.TryReadUInt16(out item.ItemId);
            reader.TryReadByte(out byte kind);
            item.Kind = (ItemKind)kind;
            reader.TryReadByte(out item.DefId);
            reader.TryReadByte(out item.Rarity);
            reader.TryReadUInt16(out item.Amount);
            reader.TryReadVector3(out item.Position);

            if (item.ItemId == 0 || !Finite.Check(item.Position)) return false;
            switch (item.Kind)
            {
                case ItemKind.Weapon:
                    return item.DefId != 0 && item.Rarity < ItemConstants.RarityCount;
                case ItemKind.Ammo:
                    return item.DefId >= 1 && item.DefId <= ItemConstants.AmmoTypeCount && item.Rarity == 0 && item.Amount > 0;
                case ItemKind.Consumable:
                    return item.DefId >= 1 && item.DefId <= ItemConstants.ConsumableTypeCount && item.Rarity == 0 && item.Amount > 0;
                case ItemKind.Material:
                    return item.DefId >= 1 && item.DefId <= 3 && item.Rarity == 0 && item.Amount > 0;
                case ItemKind.RebootCard:
                    return item.DefId == 0 && item.Rarity == 0 && item.Amount > 0;
                default:
                    return false;
            }
        }
    }

    // S->C, ReliableOrdered, at join: the whole world item list, MaxItems per packet (D13, D14).
    // Layout: [PacketId 1][Count 1] then Count x WorldItemData.
    public static class WorldItemsPacket
    {
        public const int MaxItems = 50;
        public const int MaxSize = 2 + MaxItems * WorldItemData.Size;   // 952 bytes

        // 기능: WorldItems 헤더(id + 아이템 수)를 쓴다. 뒤에 정확히 count번의 WorldItemData.Write가 따라야 한다.
        // 입력: writer - 쓸 곳, count - 이 패킷의 아이템 수(MaxItems 이하).
        // 출력: 반환값 없음. writer에 2바이트가 쓰인다.
        // Follow with exactly count WorldItemData.Write calls.
        public static void WriteHeader(ref PacketWriter writer, int count)
        {
            writer.WriteByte((byte)PacketId.WorldItems);
            writer.WriteByte((byte)count);
        }

        // 기능: WorldItems 헤더(PacketId 뒤)의 아이템 수를 읽는다.
        // 입력: reader - 본문, count - 결과.
        // 출력: 수가 1..MaxItems이고 남은 바이트가 count x 19 이상이면 true와 count, 아니면 false와 0.
        public static bool TryReadHeader(ref PacketReader reader, out int count)
        {
            count = 0;
            if (!reader.TryReadByte(out byte raw) || raw == 0 || raw > MaxItems) return false;
            if (reader.Remaining < raw * WorldItemData.Size) return false;
            count = raw;
            return true;
        }
    }

    // S->C, ReliableOrdered, to everyone: an item appeared, or its amount changed (upsert by ItemId).
    public static class ItemSpawnedPacket
    {
        public const int Size = 1 + WorldItemData.Size;   // 20 bytes

        // 기능: ItemSpawned 패킷(id + 월드 아이템 하나)을 쓴다.
        // 입력: writer - 쓸 곳, item - 나타났거나 수량이 바뀐 아이템.
        // 출력: 반환값 없음. writer에 Size(20)바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in WorldItemData item)
        {
            writer.WriteByte((byte)PacketId.ItemSpawned);
            WorldItemData.Write(ref writer, item);
        }

        // 기능: ItemSpawned 본문(PacketId 뒤)의 월드 아이템을 읽는다.
        // 입력: reader - 본문, item - 결과.
        // 출력: WorldItemData.TryRead와 같다(검증 통과면 true와 아이템).
        public static bool TryRead(ref PacketReader reader, out WorldItemData item) => WorldItemData.TryRead(ref reader, out item);
    }

    // S->C, ReliableOrdered, to everyone.
    public struct ItemRemoved
    {
        public ushort ItemId;

        // 기능: ItemRemoved 패킷(id + 아이템 id)을 쓴다.
        // 입력: writer - 쓸 곳, r - 사라진 아이템.
        // 출력: 반환값 없음. writer에 3바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in ItemRemoved r)
        {
            writer.WriteByte((byte)PacketId.ItemRemoved);
            writer.WriteUInt16(r.ItemId);
        }

        // 기능: ItemRemoved 본문(PacketId 뒤)의 아이템 id를 읽는다.
        // 입력: reader - 본문, r - 결과.
        // 출력: 2바이트가 있고 id가 0이 아니면 true, 아니면 false.
        public static bool TryRead(ref PacketReader reader, out ItemRemoved r)
        {
            r = default;
            return reader.TryReadUInt16(out r.ItemId) && r.ItemId != 0;
        }
    }

    public struct InventorySlotState
    {
        public byte WeaponId;   // 0 = empty slot
        public byte Rarity;
        public byte MagAmmo;

        public bool IsEmpty => WeaponId == 0;
    }

    // S->C, ReliableOrdered, to its owner only, at the end of a tick in which the inventory changed
    // (D14). Shots do not count as a change: the snapshot self block carries the current magazine.
    // Phase 14 D9: plus the reboot cards held (0..SquadConstants.MaxCardsHeld).
    // Phase 17 D13: plus the Shells and Rockets reserves and the grenades held, after the cards (27 bytes).
    public struct InventoryState
    {
        public const int PayloadSize = 27;   // 3 x 3 + 1 + 3 x 2 + 1 + 1 + 1 + 2 + 1 + 2 x 2 + 1

        public InventorySlotState Slot0;
        public InventorySlotState Slot1;
        public InventorySlotState Slot2;
        public byte CurrentSlot;
        public ushort LightAmmo;
        public ushort MediumAmmo;
        public ushort HeavyAmmo;
        public byte Medkits;
        public byte ShieldCells;
        public ConsumableType Using;        // None when no heal is being used
        public ushort UseRemainingTicks;    // 0 when Using is None
        public byte RebootCards;            // Phase 14 D9
        public ushort ShellsAmmo;           // Phase 17 D13
        public ushort RocketsAmmo;          // Phase 17 D13
        public byte Grenades;               // Phase 17 D9

        // 기능: 무기 칸 하나의 상태를 돌려준다.
        // 입력: slot - 0, 1, 2(그 밖의 값은 Slot2로 본다).
        // 출력: 그 칸의 InventorySlotState 복사본.
        public InventorySlotState GetSlot(int slot)
        {
            switch (slot)
            {
                case 0: return Slot0;
                case 1: return Slot1;
                default: return Slot2;
            }
        }

        // 기능: 무기 칸 하나의 상태를 바꾼다.
        // 입력: slot - 0, 1, 2(그 밖의 값은 Slot2로 본다), value - 새 칸 상태.
        // 출력: 반환값 없음. 그 칸의 상태가 바뀐다.
        public void SetSlot(int slot, in InventorySlotState value)
        {
            switch (slot)
            {
                case 0: Slot0 = value; break;
                case 1: Slot1 = value; break;
                default: Slot2 = value; break;
            }
        }

        // 기능: 탄 종류의 예비탄 수를 돌려준다(Phase 17: Shells·Rockets 포함).
        // 입력: type - 탄 종류.
        // 출력: 예비탄 수(모르는 종류는 0).
        public ushort GetAmmo(AmmoType type)
        {
            switch (type)
            {
                case AmmoType.Light: return LightAmmo;
                case AmmoType.Medium: return MediumAmmo;
                case AmmoType.Heavy: return HeavyAmmo;
                case AmmoType.Shells: return ShellsAmmo;
                case AmmoType.Rockets: return RocketsAmmo;
                default: return 0;
            }
        }

        // 기능: 탄 종류의 예비탄 수를 정한다(Phase 17: Shells·Rockets 포함, 모르는 종류는 무시).
        // 입력: type - 탄 종류, value - 수.
        // 출력: 반환값 없음.
        public void SetAmmo(AmmoType type, ushort value)
        {
            switch (type)
            {
                case AmmoType.Light: LightAmmo = value; break;
                case AmmoType.Medium: MediumAmmo = value; break;
                case AmmoType.Heavy: HeavyAmmo = value; break;
                case AmmoType.Shells: ShellsAmmo = value; break;
                case AmmoType.Rockets: RocketsAmmo = value; break;
            }
        }

        // 기능: InventoryState 패킷을 쓴다(Phase 17: 카드 수 뒤에 Shells·Rockets·수류탄, 28B).
        // 입력: writer - 대상, s - 인벤토리.
        // 출력: 반환값 없음.
        public static void Write(ref PacketWriter writer, in InventoryState s)
        {
            writer.WriteByte((byte)PacketId.InventoryState);
            for (int i = 0; i < ItemConstants.WeaponSlotCount; i++)
            {
                InventorySlotState slot = s.GetSlot(i);
                writer.WriteByte(slot.WeaponId);
                writer.WriteByte(slot.Rarity);
                writer.WriteByte(slot.MagAmmo);
            }
            writer.WriteByte(s.CurrentSlot);
            writer.WriteUInt16(s.LightAmmo);
            writer.WriteUInt16(s.MediumAmmo);
            writer.WriteUInt16(s.HeavyAmmo);
            writer.WriteByte(s.Medkits);
            writer.WriteByte(s.ShieldCells);
            writer.WriteByte((byte)s.Using);
            writer.WriteUInt16(s.UseRemainingTicks);
            writer.WriteByte(s.RebootCards);
            writer.WriteUInt16(s.ShellsAmmo);
            writer.WriteUInt16(s.RocketsAmmo);
            writer.WriteByte(s.Grenades);
        }

        // 기능: InventoryState 본문(27B)을 읽는다.
        // 입력: reader - 본문(PacketId 뒤).
        // 출력: 성공하면 true와 인벤토리. 짧거나, 빈 칸에 등급·탄창이 있거나, 등급·현재 칸이 범위 밖이거나, 사용 중인 것이 회복이 아니거나,
        //   카드가 너무 많으면 false.
        public static bool TryRead(ref PacketReader reader, out InventoryState s)
        {
            s = default;
            if (reader.Remaining < PayloadSize) return false;
            for (int i = 0; i < ItemConstants.WeaponSlotCount; i++)
            {
                var slot = new InventorySlotState();
                reader.TryReadByte(out slot.WeaponId);
                reader.TryReadByte(out slot.Rarity);
                reader.TryReadByte(out slot.MagAmmo);
                if (slot.IsEmpty ? slot.Rarity != 0 || slot.MagAmmo != 0 : slot.Rarity >= ItemConstants.RarityCount) return false;
                s.SetSlot(i, slot);
            }
            reader.TryReadByte(out s.CurrentSlot);
            reader.TryReadUInt16(out s.LightAmmo);
            reader.TryReadUInt16(out s.MediumAmmo);
            reader.TryReadUInt16(out s.HeavyAmmo);
            reader.TryReadByte(out s.Medkits);
            reader.TryReadByte(out s.ShieldCells);
            reader.TryReadByte(out byte usingKind);
            s.Using = (ConsumableType)usingKind;
            reader.TryReadUInt16(out s.UseRemainingTicks);
            reader.TryReadByte(out s.RebootCards);
            reader.TryReadUInt16(out s.ShellsAmmo);
            reader.TryReadUInt16(out s.RocketsAmmo);
            reader.TryReadByte(out s.Grenades);
            // Phase 17 D9: Using is a heal or None (a grenade is never used over a channel).
            return s.CurrentSlot < ItemConstants.WeaponSlotCount && usingKind <= (byte)ConsumableType.ShieldCell &&
                   s.RebootCards <= SquadConstants.MaxCardsHeld;
        }
    }

    // S->C, ReliableOrdered, to the player who pressed Interact. Only for feedback: the inventory
    // itself changes through InventoryState.
    public struct PickupResult
    {
        public PickupResultCode Result;
        public ushort ItemId;   // 0 with NothingInRange

        // 기능: PickupResult 패킷(id, 결과 코드, 아이템 id)을 쓴다.
        // 입력: writer - 쓸 곳, r - 줍기 결과(NothingInRange면 ItemId 0).
        // 출력: 반환값 없음. writer에 4바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in PickupResult r)
        {
            writer.WriteByte((byte)PacketId.PickupResult);
            writer.WriteByte((byte)r.Result);
            writer.WriteUInt16(r.ItemId);
        }

        // 기능: PickupResult 본문(PacketId 뒤)을 읽는다.
        // 입력: reader - 본문, r - 결과.
        // 출력: 3바이트가 있고 결과 코드가 Full 이하면 true와 결과, 아니면 false.
        public static bool TryRead(ref PacketReader reader, out PickupResult r)
        {
            r = default;
            if (reader.Remaining < 3) return false;
            reader.TryReadByte(out byte result);
            r.Result = (PickupResultCode)result;
            reader.TryReadUInt16(out r.ItemId);
            return result <= (byte)PickupResultCode.Full;
        }
    }
}
