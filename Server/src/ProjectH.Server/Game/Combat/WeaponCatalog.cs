using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Combat;

// Weapon numbers live in data, not code (D4, request §16). Loaded and validated once at startup; a
// bad file stops the server the same way a bad ServerOptions value does. Immutable afterwards, so the
// game loop reads it without locks.
// Phase 17 D2: plus the "projectiles" section (one definition per ProjectileKind, the grenade's throw included). It is
// optional here (small test catalogs leave it out: no grenade can be thrown then); GameData.LoadDirectory requires the
// grenade (RequireGrenade).
public sealed class WeaponCatalog
{
    // Phase 17: limits of the projectile numbers. The explosion radius bounds the build columns one explosion visits
    // (Match.ExplodePieces' fixed buffer). Review fix D3: the same constants the clients' parser checks (ProtocolLimits).
    public const float MaxProjectileSpeed = ProtocolLimits.ProjectileSpeedLimit;
    public const float MaxProjectileGravity = ProtocolLimits.ProjectileGravityLimit;
    public const double MaxProjectileLifetimeSeconds = 30;
    // Review fix C2: the wait after a weapon comes into the hand. 0.4 s is a third of the Kestrel's 1.25 s interval.
    public const double DefaultEquipSeconds = 0.4;
    public const double MaxEquipSeconds = 2;
    public const float MaxExplosionRadius = ProtocolLimits.ProjectileRadiusLimit;
    // Review D round 1: the highest eye a projectile can leave from: on the top of the highest building level (a roof's rise on
    // it) over the highest terrain, plus the eye. Conservative (pieces stand on the absolute grid, not on the terrain); riders,
    // freefall and the glider cannot act at all.
    private static readonly float MaxLaunchHeight =
        GameMap.Terrain.MaxHeight + BuildGrid.LevelBase(BuildGrid.Levels) + BuildGrid.RoofRise + CombatRules.EyeHeight;
    public const float MaxBounce = 0.95f;
    public const float MaxThrowUpDegrees = 45f;

    private readonly WeaponDefinition[] _weapons;
    // Weapon id -> index in _weapons, -1 when unknown. Ids are bytes, so 256 entries cover every id.
    private readonly int[] _indexById = new int[256];
    // Phase 17: index = ProjectileKind (0 unused); null = not defined.
    private readonly ProjectileDefinition?[] _projectiles;

    // 기능: 검증이 끝난 무기·투사체 정의로 카탈로그를 만들고 와이어 값을 한 번 만든다.
    // 입력: weapons - 무기들, projectiles - 종류 색인 투사체 정의(null 칸 = 없음), simHz - 변환에 쓴 Tick 속도.
    // 출력: 바뀌지 않는 WeaponCatalog.
    private WeaponCatalog(WeaponDefinition[] weapons, ProjectileDefinition?[] projectiles, int simHz)
    {
        _weapons = weapons;
        _projectiles = projectiles;
        SimHz = simHz;
        Array.Fill(_indexById, -1);
        WireInfos = new WeaponInfo[weapons.Length];
        for (int i = 0; i < weapons.Length; i++)
        {
            _indexById[weapons[i].Id] = i;
            WireInfos[i] = weapons[i].ToWire();
        }
        WireProjectiles = projectiles.Where(p => p != null).Select(p => p!.ToWire()).ToArray();
    }

    public int Count => _weapons.Length;
    // Tick values were converted with this rate; Match refuses a catalog built for another SimHz.
    public int SimHz { get; }
    // Built once for the WeaponCatalog packet sent at every join.
    public WeaponInfo[] WireInfos { get; }
    // Phase 17: built once for the WeaponCatalog packet (every defined kind, the grenade included).
    public ProjectileInfo[] WireProjectiles { get; }

    public WeaponDefinition this[int index] => _weapons[index];

    // Weapon items and inventory slots store the weapon id (Phase 4).
    public bool TryGetById(byte id, out WeaponDefinition weapon)
    {
        int index = _indexById[id];
        weapon = index >= 0 ? _weapons[index] : null!;
        return index >= 0;
    }

    // 기능: 종류의 투사체 정의를 돌려준다.
    // 입력: kind - 종류.
    // 출력: 정의, 없으면 null.
    public ProjectileDefinition? Projectile(ProjectileKind kind) => (int)kind < _projectiles.Length ? _projectiles[(int)kind] : null;

    // 기능: 운영 시작 검증(Phase 17 D9): 수류탄 정의(던지기 간격 포함)가 있는지 본다.
    // 입력: 없음.
    // 출력: 있으면 null, 없으면 이유.
    public string? RequireGrenade() =>
        Projectile(ProjectileKind.Grenade) == null ? "weapons.json needs a \"Grenade\" entry in \"projectiles\" (Phase 17)." : null;

    public static WeaponCatalog LoadFile(string path, int simHz)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"Weapon data not found: {path}");
        string json = File.ReadAllText(path);
        if (!TryParse(json, simHz, out var catalog, out string? error))
            throw new InvalidOperationException($"Invalid weapon data {path}: {error}");
        return catalog!;
    }

    // 기능: weapons.json을 읽고 검증한다(Phase 17: 새 무기 필드와 projectiles 절).
    // 입력: json - 파일 내용, simHz - 서버 Tick 속도.
    // 출력: 성공하면 true와 카탈로그, 실패하면 false와 이유.
    public static bool TryParse(string json, int simHz, out WeaponCatalog? catalog, out string? error)
    {
        catalog = null;
        if (simHz < 1)
        {
            error = "SimHz must be positive.";
            return false;
        }

        if (!DataJson.TryDeserialize(json, out CatalogJson? root, out error)) return false;

        var projectiles = new ProjectileDefinition?[(int)ProjectileKind.Rocket + 1];
        if (root!.Projectiles != null)
        {
            foreach (var pair in root.Projectiles)
            {
                if (!TryParseProjectileKind(pair.Key, out ProjectileKind kind))
                {
                    error = $"projectiles: unknown kind \"{pair.Key}\" (Grenade or Rocket).";
                    return false;
                }
                string? problem = ValidateProjectile(kind, pair.Value, simHz, out projectiles[(int)kind]);
                if (problem != null)
                {
                    error = $"projectiles.{pair.Key}: {problem}";
                    return false;
                }
            }
        }

        List<WeaponJson?>? list = root.Weapons;
        if (list == null || list.Count < 1 || list.Count > WeaponCatalogPacket.MaxWeapons)
        {
            error = $"\"weapons\" must hold 1-{WeaponCatalogPacket.MaxWeapons} entries.";
            return false;
        }

        var weapons = new WeaponDefinition[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            string? problem = Validate(list[i], simHz, projectiles, out WeaponDefinition? weapon);
            if (problem != null)
            {
                error = $"weapons[{i}]: {problem}";
                return false;
            }
            weapons[i] = weapon!;
            for (int j = 0; j < i; j++)
            {
                if (weapons[j].Id == weapons[i].Id)
                {
                    error = $"weapons[{i}]: duplicate id {weapons[i].Id}.";
                    return false;
                }
                if (weapons[j].Name == weapons[i].Name)
                {
                    error = $"weapons[{i}]: duplicate name \"{weapons[i].Name}\".";
                    return false;
                }
            }
        }

        catalog = new WeaponCatalog(weapons, projectiles, simHz);
        error = null;
        return true;
    }

    // 기능: 투사체 종류 이름을 ProjectileKind로 바꾼다(enum 이름만).
    // 입력: text - JSON의 이름.
    // 출력: Grenade·Rocket이면 true와 종류.
    private static bool TryParseProjectileKind(string? text, out ProjectileKind kind)
    {
        switch (text)
        {
            case "Grenade": kind = ProjectileKind.Grenade; return true;
            case "Rocket": kind = ProjectileKind.Rocket; return true;
            default: kind = ProjectileKind.None; return false;
        }
    }

    // 기능: 무기 항목 하나를 검증한다(Phase 17: 산탄·퍼짐·감쇠·구조물 배율·반동·투사체, 리뷰 수정 C2: 교체 대기 equipSeconds 0-2 → Tick. 빠지면 기본값).
    // 입력: w - JSON 항목, simHz - Tick 속도, projectiles - 이미 읽은 투사체 정의, weapon - 결과.
    // 출력: 맞으면 null과 무기, 틀리면 이유.
    private static string? Validate(WeaponJson? w, int simHz, ProjectileDefinition?[] projectiles, out WeaponDefinition? weapon)
    {
        weapon = null;
        if (w == null) return "entry is null.";
        if (w.Id < 1 || w.Id > byte.MaxValue) return "id must be 1-255.";
        if (string.IsNullOrWhiteSpace(w.Name)) return "name is required.";
        if (Encoding.UTF8.GetByteCount(w.Name) > WeaponCatalogPacket.MaxNameBytes)
            return $"name must be at most {WeaponCatalogPacket.MaxNameBytes} UTF-8 bytes.";
        if (w.Damage < 1 || w.Damage > ushort.MaxValue) return "damage must be 1-65535.";
        if (w.MagazineSize < 1 || w.MagazineSize > byte.MaxValue) return "magazineSize must be 1-255.";
        if (!DataJson.TryTicks(w.FireIntervalSeconds, simHz, out ushort fireTicks)) return "fireIntervalSeconds must be positive and finite (at most 65535 ticks).";
        if (!DataJson.TryTicks(w.ReloadSeconds, simHz, out ushort reloadTicks)) return "reloadSeconds must be positive and finite (at most 65535 ticks).";
        float range = (float)w.Range;
        if (!float.IsFinite(range) || range <= 0f) return "range must be positive and finite.";
        if (!ItemCatalog.TryParseAmmoType(w.AmmoType, out AmmoType ammoType)) return "ammoType must be Light, Medium, Heavy, Shells or Rockets.";

        // Phase 17 D2: optional, with the old behaviour as the default (one exact ray, no falloff, structure x 1).
        int pellets = w.Pellets ?? 1;
        if (pellets < 1 || pellets > WeaponCatalogPacket.MaxPellets) return $"pellets must be 1-{WeaponCatalogPacket.MaxPellets}.";
        float spread = (float)(w.SpreadDegrees ?? 0);
        if (!float.IsFinite(spread) || spread < 0f || spread > WeaponCatalogPacket.MaxSpreadDegrees)
            return $"spreadDegrees must be 0-{WeaponCatalogPacket.MaxSpreadDegrees}.";
        float recoil = (float)(w.RecoilDegrees ?? 0);
        if (!float.IsFinite(recoil) || recoil < 0f || recoil > WeaponCatalogPacket.MaxRecoilDegrees)
            return $"recoilDegrees must be 0-{WeaponCatalogPacket.MaxRecoilDegrees}.";
        float falloffStart = (float)(w.FalloffStart ?? range);
        if (!float.IsFinite(falloffStart) || falloffStart < 0f || falloffStart > range) return "falloffStart must be 0 to range.";
        float falloffMin = (float)(w.FalloffMinRatio ?? 1);
        if (!float.IsFinite(falloffMin) || falloffMin < 0f || falloffMin > 1f) return "falloffMinRatio must be 0-1.";
        float structure = (float)(w.StructureMultiplier ?? 1);
        if (!float.IsFinite(structure) || structure < 0f || structure > 10f) return "structureMultiplier must be 0-10.";
        // Review fix C2: optional, 0.4 s when missing, 0-2 s (0 = no wait).
        double equipSeconds = w.EquipSeconds ?? DefaultEquipSeconds;
        if (!double.IsFinite(equipSeconds) || equipSeconds < 0 || equipSeconds > MaxEquipSeconds) return $"equipSeconds must be 0-{MaxEquipSeconds}.";
        ushort equipTicks = equipSeconds == 0 ? (ushort)0 : (ushort)Math.Max(1, Math.Round(equipSeconds * simHz, MidpointRounding.AwayFromZero));
        if (equipTicks > WeaponCatalogPacket.MaxEquipTicks) return $"equipSeconds must be at most {WeaponCatalogPacket.MaxEquipTicks} ticks.";
        ProjectileDefinition? projectile = null;
        if (w.Projectile != null)
        {
            if (TryParseProjectileKind(w.Projectile, out ProjectileKind kind)) projectile = projectiles[(int)kind];
            if (projectile == null) return $"projectile \"{w.Projectile}\" is not defined in \"projectiles\".";
            if (pellets != 1) return "a projectile weapon fires one projectile (pellets 1).";
        }

        weapon = new WeaponDefinition((byte)w.Id, w.Name, (ushort)w.Damage, fireTicks, (byte)w.MagazineSize,
            reloadTicks, range, w.Automatic, ammoType, (byte)pellets, spread, falloffStart, falloffMin, structure, recoil, projectile, equipTicks);
        return null;
    }

    // 기능: 투사체 정의 하나를 검증한다(Phase 17 D2, D6, D9). 수류탄은 bounce > 0, 로켓은 bounce = 0이어야 한다. 리뷰 수정 D3: 한계는 Client 파서와
    //   같은 ProtocolLimits이고, speed + gravity × lifetime도 속력 한계 이하여야 한다. 리뷰 D 1차: 비행 거리 상한(speed × lifetime +
    //   ½ × gravity × lifetime²)을 맵 반폭·최고 발사 높이에 더해도 위치 한계(±ProjectilePositionLimit) 안이어야 한다.
    // 입력: kind - 종류, p - JSON 항목, simHz - Tick 속도, definition - 결과.
    // 출력: 맞으면 null과 정의, 틀리면 이유.
    private static string? ValidateProjectile(ProjectileKind kind, ProjectileJson? p, int simHz, out ProjectileDefinition? definition)
    {
        definition = null;
        if (p == null) return "entry is null.";
        float speed = (float)p.Speed;
        if (!float.IsFinite(speed) || speed <= 0f || speed > MaxProjectileSpeed) return $"speed must be above 0 and at most {MaxProjectileSpeed}.";
        float gravity = (float)p.Gravity;
        if (!float.IsFinite(gravity) || gravity < 0f || gravity > MaxProjectileGravity) return $"gravity must be 0-{MaxProjectileGravity}.";
        if (!(p.LifetimeSeconds <= MaxProjectileLifetimeSeconds) || !DataJson.TryTicks(p.LifetimeSeconds, simHz, out ushort lifetime))
            return $"lifetimeSeconds must be above 0 and at most {MaxProjectileLifetimeSeconds}.";
        // Review fix D3: the fastest it can fly before it explodes (falling the whole lifetime) is what a ProjectileState can carry.
        if (speed + gravity * (float)p.LifetimeSeconds > MaxProjectileSpeed)
            return $"speed + gravity x lifetimeSeconds must be at most {MaxProjectileSpeed} (the fastest a projectile may fly).";
        // Review D round 1: nor may it fly out of the +-ProjectilePositionLimit the clients accept (a ProjectileSpawned resent to
        // a late joiner, or a ProjectileState of a bounce, would be refused and the projectile not drawn). Gravity adds at most
        // gravity x t to its speed, and a bounce (bounce <= MaxBounce < 1) never adds speed but can turn the fallen speed
        // sideways, so its farthest reach from the launch is speed x lifetime + 1/2 x gravity x lifetime^2, and it is launched
        // inside the walls at most MaxLaunchHeight up.
        float seconds = (float)p.LifetimeSeconds;
        float flight = speed * seconds + 0.5f * gravity * seconds * seconds;
        if (GameMap.HalfSize + flight > ProtocolLimits.ProjectilePositionLimit || MaxLaunchHeight + flight > ProtocolLimits.ProjectilePositionLimit)
            return $"speed x lifetimeSeconds + gravity x lifetimeSeconds^2 / 2 ({flight} m) must keep the projectile within {ProtocolLimits.ProjectilePositionLimit} m of the map's centre.";
        float radius = (float)p.ExplosionRadius;
        if (!float.IsFinite(radius) || radius <= 0f || radius > MaxExplosionRadius) return $"explosionRadius must be above 0 and at most {MaxExplosionRadius}.";
        if (p.ExplosionDamage < 0 || p.ExplosionDamage > ushort.MaxValue) return "explosionDamage must be 0-65535.";
        if (p.StructureDamage < 0 || p.StructureDamage > ushort.MaxValue) return "structureDamage must be 0-65535.";
        if (p.ExplosionDamage == 0 && p.StructureDamage == 0) return "explosionDamage or structureDamage must be above 0.";
        float bounce = (float)p.Bounce;
        if (!float.IsFinite(bounce) || bounce < 0f || bounce > MaxBounce) return $"bounce must be 0-{MaxBounce}.";
        ushort throwTicks = 0;
        float throwUp = 0f;
        if (kind == ProjectileKind.Grenade)
        {
            // D9: thrown by hand: an interval between throws and the slight upward angle.
            if (!DataJson.TryTicks(p.ThrowIntervalSeconds, simHz, out throwTicks)) return "throwIntervalSeconds must be positive (the grenade's throw interval).";
            throwUp = (float)p.ThrowUpDegrees;
            if (!float.IsFinite(throwUp) || throwUp < -MaxThrowUpDegrees || throwUp > MaxThrowUpDegrees)
                return $"throwUpDegrees must be {-MaxThrowUpDegrees}-{MaxThrowUpDegrees}.";
            if (bounce <= 0f) return "a grenade must bounce (bounce above 0): it explodes by its fuse.";
        }
        // Phase 17 review: a rocket explodes on its first hit (ExplodesOnImpact = bounce 0); any bounce would make it a grenade.
        else if (kind == ProjectileKind.Rocket && bounce != 0f) return "a rocket must not bounce (bounce 0): it explodes on impact.";
        definition = new ProjectileDefinition(kind, speed, gravity, lifetime, radius, (ushort)p.ExplosionDamage, (ushort)p.StructureDamage,
            bounce, throwTicks, throwUp);
        return null;
    }

    private sealed class CatalogJson
    {
        public List<WeaponJson?>? Weapons { get; set; }
        public Dictionary<string, ProjectileJson?>? Projectiles { get; set; }
    }

    // Missing numbers stay 0 and fail validation, so every required field must be written.
    private sealed class WeaponJson
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public int Damage { get; set; }
        public double FireIntervalSeconds { get; set; }
        public int MagazineSize { get; set; }
        public double ReloadSeconds { get; set; }
        public double Range { get; set; }
        public bool Automatic { get; set; }
        public string? AmmoType { get; set; }
        // Phase 17 D2: optional (null = the default in Validate).
        public int? Pellets { get; set; }
        public double? SpreadDegrees { get; set; }
        public double? RecoilDegrees { get; set; }
        public double? FalloffStart { get; set; }
        public double? FalloffMinRatio { get; set; }
        public double? StructureMultiplier { get; set; }
        public string? Projectile { get; set; }
        // Review fix C2: optional (null = DefaultEquipSeconds).
        public double? EquipSeconds { get; set; }
    }

    // Phase 17: every number is required (a missing one stays 0 and fails), except the grenade-only throw fields on a rocket.
    private sealed class ProjectileJson
    {
        public double Speed { get; set; }
        public double Gravity { get; set; }
        public double LifetimeSeconds { get; set; }
        public double ExplosionRadius { get; set; }
        public int ExplosionDamage { get; set; }
        public int StructureDamage { get; set; }
        public double Bounce { get; set; }
        public double ThrowIntervalSeconds { get; set; }
        public double ThrowUpDegrees { get; set; }
    }
}
