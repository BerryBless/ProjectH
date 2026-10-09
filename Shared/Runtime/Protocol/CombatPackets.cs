using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Shared.Protocol
{
    // Phase 17 D6: what a projectile is. Values are the wire format; 0 = none (a hitscan weapon).
    public enum ProjectileKind : byte
    {
        None = 0,
        Grenade = 1,
        Rocket = 2,
    }

    // One weapon of the server's catalog (D4). Tick values use the server's SimHz.
    // Phase 17 D2: plus the values the client presents with: pellets per shot, the spread cone, the camera recoil and the
    // projectile a projectile weapon fires (None = hitscan). The server decides every hit; the client only draws.
    public struct WeaponInfo
    {
        public byte WeaponId;
        public string Name;
        public ushort Damage;        // per pellet (Phase 17)
        public ushort FireIntervalTicks;
        public byte MagazineSize;
        public ushort ReloadTicks;
        public float Range;
        public bool Automatic;
        public AmmoType AmmoType;   // Phase 4 (D3): Light, Medium or Heavy; Phase 17 D13: Shells, Rockets
        public byte Pellets;                 // Phase 17 D4: 1..WeaponCatalogPacket.MaxPellets (8 for the shotgun)
        public float SpreadDegrees;          // Phase 17 D3: the cone's half angle, 0..MaxSpreadDegrees
        public float RecoilDegrees;          // Phase 17 D3: the client's camera kick per shot, 0..MaxRecoilDegrees
        public ProjectileKind Projectile;    // Phase 17 D6: None = hitscan
        // Review fix C2: ticks after the weapon comes into the hand (a slot switch, or a pickup or swap into the hand) before it
        // can fire, 0..WeaponCatalogPacket.MaxEquipTicks. The client's WeaponState waits the same before predicting a shot.
        public ushort EquipTicks;
    }

    // Phase 17 D2, D7: what the client needs of a projectile kind to extrapolate and draw it between the projectile events:
    // the launch speed (m/s), the gravity (m/s^2, pulling down; 0 = straight flight), the explosion radius (m) and the most
    // ticks it lives before it explodes (the fuse or the lifetime; a client may drop one it has not heard of by then).
    public struct ProjectileInfo
    {
        public const int Size = 15;   // kind 1 + speed 4 + gravity 4 + radius 4 + lifetime 2

        public ProjectileKind Kind;
        public float Speed;
        public float Gravity;
        public float ExplosionRadius;
        public ushort LifetimeTicks;
    }

    // S->C, ReliableOrdered, once right after a successful Join. Lists every weapon that can exist as an
    // item; which weapon sits in which inventory slot comes with InventoryState (Phase 4).
    // Phase 17 D2: after the weapons, the projectile kinds. Layout: [Count 1] Count x WeaponInfo [ProjectileCount 1]
    // ProjectileCount x ProjectileInfo. Every projectile a weapon fires is in the list.
    public static class WeaponCatalogPacket
    {
        public const int MaxWeapons = 8;
        public const int MaxNameBytes = 16;
        // Phase 17: the limits the server validates weapons.json with and the client accepts.
        public const int MaxPellets = 16;
        public const float MaxSpreadDegrees = 30f;
        public const float MaxRecoilDegrees = 30f;
        public const int MaxProjectiles = 2;   // one per ProjectileKind
        // Review fix C2: weapons.json allows equipSeconds 0-2 and SimHz is at most 128, so at most 256 ticks.
        public const int MaxEquipTicks = 256;

        // 기능: WeaponCatalog 패킷을 쓴다(Phase 17: 산탄·퍼짐·반동·투사체 종류와 투사체 목록 포함, 리뷰 수정 C2: 무기마다 끝에 EquipTicks u16).
        // 입력: writer - 대상, weapons - 1..MaxWeapons개(서버가 시작 때 검증), projectiles - 0..MaxProjectiles개(종류마다 하나, null = 없음).
        // 출력: 반환값 없음. writer에 패킷이 쓰인다.
        public static void Write(ref PacketWriter writer, WeaponInfo[] weapons, ProjectileInfo[] projectiles)
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
                writer.WriteByte(w.Pellets);
                writer.WriteSingle(w.SpreadDegrees);
                writer.WriteSingle(w.RecoilDegrees);
                writer.WriteByte((byte)w.Projectile);
                writer.WriteUInt16(w.EquipTicks);   // review fix C2
            }
            int projectileCount = projectiles == null ? 0 : projectiles.Length;
            writer.WriteByte((byte)projectileCount);
            for (int i = 0; i < projectileCount; i++)
            {
                ProjectileInfo p = projectiles[i];
                writer.WriteByte((byte)p.Kind);
                writer.WriteSingle(p.Speed);
                writer.WriteSingle(p.Gravity);
                writer.WriteSingle(p.ExplosionRadius);
                writer.WriteUInt16(p.LifetimeTicks);
            }
        }

        // 기능: WeaponCatalog 본문을 읽는다(투사체 목록은 검증만 하고 버린다).
        // 입력: reader - 본문(PacketId 뒤).
        // 출력: 성공하면 true와 무기 배열, 아니면 false. 할당한다(입장 때 한 번).
        public static bool TryRead(ref PacketReader reader, out WeaponInfo[] weapons) => TryRead(ref reader, out weapons, out _);

        // 기능: WeaponCatalog 본문을 읽는다(Phase 17: 투사체 목록 포함, 리뷰 수정 C2: EquipTicks 0..MaxEquipTicks, 리뷰 수정 D3: 투사체 속력·중력·
        //   폭발 반경은 ProtocolLimits 이하). 서버가 보내지 않는 값은 거절한다.
        // 입력: reader - 본문(PacketId 뒤).
        // 출력: 성공하면 true와 무기·투사체 배열. 범위 밖 값, 같은 투사체 종류 둘, 목록에 없는 투사체를 쏘는 무기가 있으면 false.
        //   할당한다(입장 때 한 번, Tick 경로가 아니다).
        public static bool TryRead(ref PacketReader reader, out WeaponInfo[] weapons, out ProjectileInfo[] projectiles)
        {
            weapons = null;
            projectiles = null;
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
                if (!reader.TryReadByte(out w.Pellets) || w.Pellets == 0 || w.Pellets > MaxPellets) return false;
                if (!reader.TryReadSingle(out w.SpreadDegrees) || !InRange(w.SpreadDegrees, MaxSpreadDegrees)) return false;
                if (!reader.TryReadSingle(out w.RecoilDegrees) || !InRange(w.RecoilDegrees, MaxRecoilDegrees)) return false;
                if (!reader.TryReadByte(out byte projectile) || projectile > (byte)ProjectileKind.Rocket) return false;
                w.Projectile = (ProjectileKind)projectile;
                if (!reader.TryReadUInt16(out w.EquipTicks) || w.EquipTicks > MaxEquipTicks) return false;
                result[i] = w;
            }

            if (!reader.TryReadByte(out byte projectileCount) || projectileCount > MaxProjectiles) return false;
            var kinds = new ProjectileInfo[projectileCount];
            for (int i = 0; i < projectileCount; i++)
            {
                var p = new ProjectileInfo();
                if (!reader.TryReadByte(out byte kind) || kind == 0 || kind > (byte)ProjectileKind.Rocket) return false;
                p.Kind = (ProjectileKind)kind;
                if (Find(kinds, i, p.Kind) >= 0) return false;
                // Review fix D3: the same limits the server's weapons.json check uses (ProtocolLimits).
                if (!reader.TryReadSingle(out p.Speed) || !(p.Speed > 0f && p.Speed <= ProtocolLimits.ProjectileSpeedLimit)) return false;
                if (!reader.TryReadSingle(out p.Gravity) || !(p.Gravity >= 0f && p.Gravity <= ProtocolLimits.ProjectileGravityLimit)) return false;
                if (!reader.TryReadSingle(out p.ExplosionRadius) || !(p.ExplosionRadius > 0f && p.ExplosionRadius <= ProtocolLimits.ProjectileRadiusLimit))
                    return false;
                if (!reader.TryReadUInt16(out p.LifetimeTicks) || p.LifetimeTicks == 0) return false;
                kinds[i] = p;
            }
            for (int i = 0; i < count; i++)
            {
                if (result[i].Projectile != ProjectileKind.None && Find(kinds, kinds.Length, result[i].Projectile) < 0) return false;
            }
            weapons = result;
            projectiles = kinds;
            return true;
        }

        // 기능: 투사체 목록의 앞 count개에서 종류를 찾는다(Client가 무기의 투사체 정보를 찾을 때도 쓴다).
        // 입력: projectiles - 목록(null 가능), count - 볼 개수, kind - 찾을 종류.
        // 출력: 위치, 없으면 -1.
        public static int Find(ProjectileInfo[] projectiles, int count, ProjectileKind kind)
        {
            if (projectiles == null) return -1;
            if (count > projectiles.Length) count = projectiles.Length;
            for (int i = 0; i < count; i++)
            {
                if (projectiles[i].Kind == kind) return i;
            }
            return -1;
        }

        // 기능: 각도 값이 유한하고 0..max 안인지 본다(퍼짐·반동 검사).
        // 입력: value - 검사할 값, max - 허용 상한.
        // 출력: 0 이상 max 이하면 true(NaN·Infinity는 false).
        private static bool InRange(float value, float max) => Finite.Check(value) && value >= 0f && value <= max;
    }

    // S->C, Unreliable, to everyone: one processed shot, from the shooter's eye to where it stopped (D11).
    // Phase 18 D4: plus the weapon that fired (WeaponInfo.WeaponId of WeaponCatalog), so a client can play that weapon's
    // gunshot. One per trigger pull (a shotgun's pellets share one). Layout: [PacketId 1][ShooterId 2][Start 12][End 12][WeaponId 1].
    public struct ShotFired
    {
        public const int PayloadSize = 27;

        public ushort ShooterId;
        public Vector3 Start;
        public Vector3 End;
        public byte WeaponId;

        // 기능: ShotFired 패킷을 쓴다(Phase 18: 무기 id 포함).
        // 입력: writer - 대상, s - 사격 사건.
        // 출력: 반환값 없음. writer에 28바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in ShotFired s)
        {
            writer.WriteByte((byte)PacketId.ShotFired);
            writer.WriteUInt16(s.ShooterId);
            writer.WriteVector3(s.Start);
            writer.WriteVector3(s.End);
            writer.WriteByte(s.WeaponId);
        }

        // 기능: ShotFired 본문을 읽는다. 무기 id는 범위를 알 수 없어 그대로 둔다(받는 쪽이 카탈로그에서 찾고, 없으면 기본 소리).
        // 입력: reader - 본문(PacketId 뒤).
        // 출력: 성공하면 true와 사건. 짧거나 시작·끝이 유한하지 않으면 false.
        public static bool TryRead(ref PacketReader reader, out ShotFired s)
        {
            s = default;
            if (reader.Remaining < PayloadSize) return false;
            reader.TryReadUInt16(out s.ShooterId);
            reader.TryReadVector3(out s.Start);
            reader.TryReadVector3(out s.End);
            reader.TryReadByte(out s.WeaponId);
            return Finite.Check(s.Start) && Finite.Check(s.End);
        }
    }

    // S->C, ReliableOrdered, to the shooter.
    public struct HitConfirmed
    {
        public ushort TargetId;
        public ushort Damage;
        public bool Killed;

        // 기능: HitConfirmed 패킷(id, 대상 id, 피해, 처치 여부)을 쓴다.
        // 입력: writer - 대상, h - 명중 확인.
        // 출력: 반환값 없음. writer에 6바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in HitConfirmed h)
        {
            writer.WriteByte((byte)PacketId.HitConfirmed);
            writer.WriteUInt16(h.TargetId);
            writer.WriteUInt16(h.Damage);
            writer.WriteByte(h.Killed ? (byte)1 : (byte)0);
        }

        // 기능: HitConfirmed 본문(PacketId 뒤)을 읽는다.
        // 입력: reader - 본문, h - 읽은 명중 확인을 받을 변수.
        // 출력: 본문이 5바이트 이상이면 true와 명중 확인(Killed는 0이 아닌 바이트), 짧으면 false.
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
    // Phase 18 D8: plus Flags: ShieldHitFlag = the shield was above 0 before this damage, ShieldBrokenFlag = this damage took
    // it to 0 (only with ShieldHitFlag). Neither = only health was hit (a fall never touches the shield).
    // Layout: [PacketId 1][AttackerId 2][Damage 2][FromDirection 12][Flags 1] = 18 bytes.
    public struct DamageTaken
    {
        public const int PayloadSize = 17;
        public const byte ShieldHitFlag = 1;
        public const byte ShieldBrokenFlag = 2;

        public ushort AttackerId;
        public ushort Damage;
        public Vector3 FromDirection;
        public byte Flags;

        public bool ShieldHit => (Flags & ShieldHitFlag) != 0;
        public bool ShieldBroken => (Flags & ShieldBrokenFlag) != 0;

        // 기능: 맞기 전·후 실드로 플래그를 정한다(서버의 모든 피해 경로가 같은 규칙을 쓴다).
        // 입력: shieldBefore - 피해 전 실드, shieldAfter - 피해 뒤 실드.
        // 출력: 실드가 있었고 줄었으면 ShieldHitFlag, 거기에 0이 되었으면 ShieldBrokenFlag를 더한 값. 실드가 없었거나 줄지 않았으면 0.
        public static byte FlagsFor(int shieldBefore, int shieldAfter)
        {
            if (shieldBefore <= 0 || shieldAfter >= shieldBefore) return 0;
            return shieldAfter <= 0 ? (byte)(ShieldHitFlag | ShieldBrokenFlag) : ShieldHitFlag;
        }

        // 기능: DamageTaken 패킷을 쓴다(Phase 18: 플래그 포함).
        // 입력: writer - 대상, d - 피해 사건.
        // 출력: 반환값 없음. writer에 18바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in DamageTaken d)
        {
            writer.WriteByte((byte)PacketId.DamageTaken);
            writer.WriteUInt16(d.AttackerId);
            writer.WriteUInt16(d.Damage);
            writer.WriteVector3(d.FromDirection);
            writer.WriteByte(d.Flags);
        }

        // 기능: DamageTaken 본문을 읽는다.
        // 입력: reader - 본문(PacketId 뒤).
        // 출력: 성공하면 true와 사건. 짧거나, 방향이 유한하지 않거나, 모르는 플래그 비트가 있거나, 실드 맞음 없이 실드 깨짐이면 false.
        public static bool TryRead(ref PacketReader reader, out DamageTaken d)
        {
            d = default;
            if (reader.Remaining < PayloadSize) return false;
            reader.TryReadUInt16(out d.AttackerId);
            reader.TryReadUInt16(out d.Damage);
            reader.TryReadVector3(out d.FromDirection);
            reader.TryReadByte(out d.Flags);
            if ((d.Flags & ~(ShieldHitFlag | ShieldBrokenFlag)) != 0) return false;
            if ((d.Flags & ShieldBrokenFlag) != 0 && (d.Flags & ShieldHitFlag) == 0) return false;
            return Finite.Check(d.FromDirection);
        }
    }

    // Phase 12 D10: what killed a player when no player did (PlayerDied.KillerId 0). A player's kill and the
    // spectating notice a newcomer gets also carry 0. Values are the wire format.
    public enum DeathCause : byte
    {
        Zone = 0,
        Fall = 1,
        // Phase 17 D8: a grenade or a rocket. Unlike Zone and Fall it also comes with a killer (the projectile's owner while
        // it is still in the match, else 0).
        Explosion = 2,
    }

    // S->C, ReliableOrdered, to everyone. Same channel as PlayerRespawned, so a client always sees a
    // death before the matching respawn.
    // KillerId 0 = no killer (the zone, Phase 5 D8; a fall, Phase 12 D10: Cause says which). Phase 17 D8: Cause Explosion with
    // the projectile's owner as KillerId (0 when it left the match); a shot keeps Cause Zone (0) with its KillerId. Placement (Phase 5 D11) =
    // living participants left + 1 during a match, 0 outside one (dev respawn mode, or a newcomer told it is spectating).
    public struct PlayerDied
    {
        public ushort VictimId;
        public ushort KillerId;
        public byte Placement;
        public DeathCause Cause;

        // 기능: PlayerDied 패킷(id, 희생자, 처치자, 순위, 원인)을 쓴다.
        // 입력: writer - 대상, d - 사망 사건.
        // 출력: 반환값 없음. writer에 7바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in PlayerDied d)
        {
            writer.WriteByte((byte)PacketId.PlayerDied);
            writer.WriteUInt16(d.VictimId);
            writer.WriteUInt16(d.KillerId);
            writer.WriteByte(d.Placement);
            writer.WriteByte((byte)d.Cause);
        }

        // 기능: PlayerDied 본문을 읽는다.
        // 입력: reader - 본문(PacketId 뒤).
        // 출력: 성공하면 true와 사건. 짧거나 원인이 Explosion(2)보다 크면 false.
        public static bool TryRead(ref PacketReader reader, out PlayerDied d)
        {
            d = default;
            if (reader.Remaining < 6) return false;
            reader.TryReadUInt16(out d.VictimId);
            reader.TryReadUInt16(out d.KillerId);
            reader.TryReadByte(out d.Placement);
            reader.TryReadByte(out byte cause);
            if (cause > (byte)DeathCause.Explosion) return false;   // Phase 17: the highest cause
            d.Cause = (DeathCause)cause;
            return true;
        }
    }

    // S->C, ReliableOrdered, to everyone: the player is alive again at Position with full Health,
    // Shield 0 and empty-handed (no weapon, no ammo); it re-arms from loot (D9).
    // Phase 12 D11: Mode is the movement mode it starts in: Transport at a match start (aboard the drop transport),
    // Ground otherwise. Phase 14: every mode value up to Downed reads (the server sends Ground for a reboot).
    public struct PlayerRespawned
    {
        public ushort EntityId;
        public Vector3 Position;
        public float Yaw;
        public MovementMode Mode;

        // 기능: PlayerRespawned 패킷(id, 엔티티 id, 위치, Yaw, 이동 모드)을 쓴다.
        // 입력: writer - 대상, r - 재생성 사건.
        // 출력: 반환값 없음. writer에 20바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in PlayerRespawned r)
        {
            writer.WriteByte((byte)PacketId.PlayerRespawned);
            writer.WriteUInt16(r.EntityId);
            writer.WriteVector3(r.Position);
            writer.WriteSingle(r.Yaw);
            writer.WriteByte((byte)r.Mode);
        }

        // 기능: PlayerRespawned 본문(PacketId 뒤)을 읽는다.
        // 입력: reader - 본문, r - 읽은 사건을 받을 변수.
        // 출력: 성공하면 true와 사건. 짧거나, 모드가 Downed보다 크거나, 위치·Yaw가 유한하지 않으면 false.
        public static bool TryRead(ref PacketReader reader, out PlayerRespawned r)
        {
            r = default;
            if (reader.Remaining < 19) return false;
            reader.TryReadUInt16(out r.EntityId);
            reader.TryReadVector3(out r.Position);
            reader.TryReadSingle(out r.Yaw);
            reader.TryReadByte(out byte mode);
            if (mode > (byte)MovementMode.Downed) return false;
            r.Mode = (MovementMode)mode;
            return Finite.Check(r.Position) && Finite.Check(r.Yaw);
        }
    }

    // Values received from the network may be NaN/Infinity; a packet carrying one is rejected.
    internal static class Finite
    {
        // 기능: 받은 float가 유한한 값인지 본다.
        // 입력: value - 검사할 값.
        // 출력: NaN도 Infinity도 아니면 true.
        public static bool Check(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // 기능: 받은 벡터의 세 성분이 모두 유한한지 본다.
        // 입력: value - 검사할 벡터.
        // 출력: X, Y, Z가 모두 유한하면 true.
        public static bool Check(Vector3 value) => Check(value.X) && Check(value.Y) && Check(value.Z);
    }
}
