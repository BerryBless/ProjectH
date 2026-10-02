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
    }

    public enum ItemKind : byte
    {
        None = 0,
        Weapon = 1,       // DefId = weapon id, Rarity 0-4, Amount = rounds in the magazine (may be 0)
        Ammo = 2,         // DefId = AmmoType, Amount = rounds
        Consumable = 3,   // DefId = ConsumableType, Amount = count
        Material = 4,     // Phase 13 D15: DefId = BuildMaterialType + 1, Amount = resources (a dead player's, picked up on touch)
    }

    public enum ConsumableType : byte
    {
        None = 0,
        Medkit = 1,
        ShieldCell = 2,
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
        public const int AmmoTypeCount = 3;        // D3: Light, Medium, Heavy
        public const int ConsumableTypeCount = 2;  // D10: Medkit, ShieldCell
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
    // Largest possible packet: 1 + (1 + 5 x 21) + (1 + 3 x 20) + (1 + 2 x 25) = 219 bytes.
    public static class ItemCatalogPacket
    {
        public const int MaxSize = 219;

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
                if (!reader.TryReadUInt16(out consumables[i].UseTicks) || consumables[i].UseTicks == 0) return false;
                if (!reader.TryReadUInt16(out consumables[i].Heal)) return false;
                if (!reader.TryReadUInt16(out consumables[i].Shield)) return false;
                if (consumables[i].Heal == 0 && consumables[i].Shield == 0) return false;
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

        public static void Write(ref PacketWriter writer, in WorldItemData item)
        {
            writer.WriteUInt16(item.ItemId);
            writer.WriteByte((byte)item.Kind);
            writer.WriteByte(item.DefId);
            writer.WriteByte(item.Rarity);
            writer.WriteUInt16(item.Amount);
            writer.WriteVector3(item.Position);
        }

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

        // Follow with exactly count WorldItemData.Write calls.
        public static void WriteHeader(ref PacketWriter writer, int count)
        {
            writer.WriteByte((byte)PacketId.WorldItems);
            writer.WriteByte((byte)count);
        }

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

        public static void Write(ref PacketWriter writer, in WorldItemData item)
        {
            writer.WriteByte((byte)PacketId.ItemSpawned);
            WorldItemData.Write(ref writer, item);
        }

        public static bool TryRead(ref PacketReader reader, out WorldItemData item) => WorldItemData.TryRead(ref reader, out item);
    }

    // S->C, ReliableOrdered, to everyone.
    public struct ItemRemoved
    {
        public ushort ItemId;

        public static void Write(ref PacketWriter writer, in ItemRemoved r)
        {
            writer.WriteByte((byte)PacketId.ItemRemoved);
            writer.WriteUInt16(r.ItemId);
        }

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
    public struct InventoryState
    {
        public const int PayloadSize = 21;   // 3 x 3 + 1 + 3 x 2 + 1 + 1 + 1 + 2

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

        public InventorySlotState GetSlot(int slot)
        {
            switch (slot)
            {
                case 0: return Slot0;
                case 1: return Slot1;
                default: return Slot2;
            }
        }

        public void SetSlot(int slot, in InventorySlotState value)
        {
            switch (slot)
            {
                case 0: Slot0 = value; break;
                case 1: Slot1 = value; break;
                default: Slot2 = value; break;
            }
        }

        public ushort GetAmmo(AmmoType type)
        {
            switch (type)
            {
                case AmmoType.Light: return LightAmmo;
                case AmmoType.Medium: return MediumAmmo;
                case AmmoType.Heavy: return HeavyAmmo;
                default: return 0;
            }
        }

        public void SetAmmo(AmmoType type, ushort value)
        {
            switch (type)
            {
                case AmmoType.Light: LightAmmo = value; break;
                case AmmoType.Medium: MediumAmmo = value; break;
                case AmmoType.Heavy: HeavyAmmo = value; break;
            }
        }

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
        }

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
            return s.CurrentSlot < ItemConstants.WeaponSlotCount && usingKind <= ItemConstants.ConsumableTypeCount;
        }
    }

    // S->C, ReliableOrdered, to the player who pressed Interact. Only for feedback: the inventory
    // itself changes through InventoryState.
    public struct PickupResult
    {
        public PickupResultCode Result;
        public ushort ItemId;   // 0 with NothingInRange

        public static void Write(ref PacketWriter writer, in PickupResult r)
        {
            writer.WriteByte((byte)PacketId.PickupResult);
            writer.WriteByte((byte)r.Result);
            writer.WriteUInt16(r.ItemId);
        }

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
