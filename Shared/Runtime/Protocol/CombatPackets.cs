using System.Numerics;

namespace ProjectH.Shared.Protocol
{
    // One weapon of the server's catalog (D4). Tick values use the server's SimHz.
    public struct WeaponInfo
    {
        public byte WeaponId;
        public string Name;
        public ushort Damage;
        public ushort FireIntervalTicks;
        public byte MagazineSize;
        public ushort ReloadTicks;
        public float Range;
        public bool Automatic;
        public AmmoType AmmoType;   // Phase 4 (D3): Light, Medium or Heavy
    }

    // S->C, ReliableOrdered, once right after a successful Join. Lists every weapon that can exist as an
    // item; which weapon sits in which inventory slot comes with InventoryState (Phase 4).
    public static class WeaponCatalogPacket
    {
        public const int MaxWeapons = 8;
        public const int MaxNameBytes = 16;

        // weapons: 1-MaxWeapons entries (the server validates its catalog at startup).
        public static void Write(ref PacketWriter writer, WeaponInfo[] weapons)
        {
            writer.WriteByte((byte)PacketId.WeaponCatalog);
            writer.WriteByte((byte)weapons.Length);
            for (int i = 0; i < weapons.Length; i++)
            {
                WeaponInfo w = weapons[i];
                writer.WriteByte(w.WeaponId);
                writer.WriteString(w.Name, MaxNameBytes);
                writer.WriteUInt16(w.Damage);
                writer.WriteUInt16(w.FireIntervalTicks);
                writer.WriteByte(w.MagazineSize);
                writer.WriteUInt16(w.ReloadTicks);
                writer.WriteSingle(w.Range);
                writer.WriteByte(w.Automatic ? (byte)1 : (byte)0);
                writer.WriteByte((byte)w.AmmoType);
            }
        }

        // Allocates the array and the names: read once per join, never on the per-tick path.
        public static bool TryRead(ref PacketReader reader, out WeaponInfo[] weapons)
        {
            weapons = null;
            if (!reader.TryReadByte(out byte count) || count == 0 || count > MaxWeapons) return false;

            var result = new WeaponInfo[count];
            for (int i = 0; i < count; i++)
            {
                var w = new WeaponInfo();
                if (!reader.TryReadByte(out w.WeaponId)) return false;
                if (!reader.TryReadString(MaxNameBytes, out w.Name) || w.Name.Length == 0) return false;
                if (!reader.TryReadUInt16(out w.Damage) || w.Damage == 0) return false;
                if (!reader.TryReadUInt16(out w.FireIntervalTicks) || w.FireIntervalTicks == 0) return false;
                if (!reader.TryReadByte(out w.MagazineSize) || w.MagazineSize == 0) return false;
                if (!reader.TryReadUInt16(out w.ReloadTicks) || w.ReloadTicks == 0) return false;
                if (!reader.TryReadSingle(out w.Range) || !Finite.Check(w.Range) || w.Range <= 0f) return false;
                if (!reader.TryReadByte(out byte automatic) || automatic > 1) return false;
                w.Automatic = automatic == 1;
                if (!reader.TryReadByte(out byte ammoType) || ammoType == 0 || ammoType > ItemConstants.AmmoTypeCount) return false;
                w.AmmoType = (AmmoType)ammoType;
                result[i] = w;
            }
            weapons = result;
            return true;
        }
    }

    // S->C, Unreliable, to everyone: one processed shot, from the shooter's eye to where it stopped (D11).
    public struct ShotFired
    {
        public const int PayloadSize = 26;

        public ushort ShooterId;
        public Vector3 Start;
        public Vector3 End;

        public static void Write(ref PacketWriter writer, in ShotFired s)
        {
            writer.WriteByte((byte)PacketId.ShotFired);
            writer.WriteUInt16(s.ShooterId);
            writer.WriteVector3(s.Start);
            writer.WriteVector3(s.End);
        }

        public static bool TryRead(ref PacketReader reader, out ShotFired s)
        {
            s = default;
            if (reader.Remaining < PayloadSize) return false;
            reader.TryReadUInt16(out s.ShooterId);
            reader.TryReadVector3(out s.Start);
            reader.TryReadVector3(out s.End);
            return Finite.Check(s.Start) && Finite.Check(s.End);
        }
    }

    // S->C, ReliableOrdered, to the shooter.
    public struct HitConfirmed
    {
        public ushort TargetId;
        public ushort Damage;
        public bool Killed;

        public static void Write(ref PacketWriter writer, in HitConfirmed h)
        {
            writer.WriteByte((byte)PacketId.HitConfirmed);
            writer.WriteUInt16(h.TargetId);
            writer.WriteUInt16(h.Damage);
            writer.WriteByte(h.Killed ? (byte)1 : (byte)0);
        }

        public static bool TryRead(ref PacketReader reader, out HitConfirmed h)
        {
            h = default;
            if (reader.Remaining < 5) return false;
            reader.TryReadUInt16(out h.TargetId);
            reader.TryReadUInt16(out h.Damage);
            reader.TryReadByte(out byte killed);
            h.Killed = killed != 0;
            return true;
        }
    }

    // S->C, ReliableOrdered, to the player who was hit. FromDirection points from the victim towards
    // the attacker (unit length, or zero when they overlap).
    public struct DamageTaken
    {
        public ushort AttackerId;
        public ushort Damage;
        public Vector3 FromDirection;

        public static void Write(ref PacketWriter writer, in DamageTaken d)
        {
            writer.WriteByte((byte)PacketId.DamageTaken);
            writer.WriteUInt16(d.AttackerId);
            writer.WriteUInt16(d.Damage);
            writer.WriteVector3(d.FromDirection);
        }

        public static bool TryRead(ref PacketReader reader, out DamageTaken d)
        {
            d = default;
            if (reader.Remaining < 16) return false;
            reader.TryReadUInt16(out d.AttackerId);
            reader.TryReadUInt16(out d.Damage);
            reader.TryReadVector3(out d.FromDirection);
            return Finite.Check(d.FromDirection);
        }
    }

    // S->C, ReliableOrdered, to everyone. Same channel as PlayerRespawned, so a client always sees a
    // death before the matching respawn.
    // KillerId 0 = no killer (the zone, Phase 5 D8). Placement (Phase 5 D11) = living participants left + 1
    // during a match, 0 outside one (dev respawn mode, or a newcomer told it is spectating).
    public struct PlayerDied
    {
        public ushort VictimId;
        public ushort KillerId;
        public byte Placement;

        public static void Write(ref PacketWriter writer, in PlayerDied d)
        {
            writer.WriteByte((byte)PacketId.PlayerDied);
            writer.WriteUInt16(d.VictimId);
            writer.WriteUInt16(d.KillerId);
            writer.WriteByte(d.Placement);
        }

        public static bool TryRead(ref PacketReader reader, out PlayerDied d)
        {
            d = default;
            if (reader.Remaining < 5) return false;
            reader.TryReadUInt16(out d.VictimId);
            reader.TryReadUInt16(out d.KillerId);
            reader.TryReadByte(out d.Placement);
            return true;
        }
    }

    // S->C, ReliableOrdered, to everyone: the player is alive again at Position with full Health,
    // Shield 0 and empty-handed (no weapon, no ammo); it re-arms from loot (D9).
    public struct PlayerRespawned
    {
        public ushort EntityId;
        public Vector3 Position;
        public float Yaw;

        public static void Write(ref PacketWriter writer, in PlayerRespawned r)
        {
            writer.WriteByte((byte)PacketId.PlayerRespawned);
            writer.WriteUInt16(r.EntityId);
            writer.WriteVector3(r.Position);
            writer.WriteSingle(r.Yaw);
        }

        public static bool TryRead(ref PacketReader reader, out PlayerRespawned r)
        {
            r = default;
            if (reader.Remaining < 18) return false;
            reader.TryReadUInt16(out r.EntityId);
            reader.TryReadVector3(out r.Position);
            reader.TryReadSingle(out r.Yaw);
            return Finite.Check(r.Position) && Finite.Check(r.Yaw);
        }
    }

    // Values received from the network may be NaN/Infinity; a packet carrying one is rejected.
    internal static class Finite
    {
        public static bool Check(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static bool Check(Vector3 value) => Check(value.X) && Check(value.Y) && Check(value.Z);
    }
}
