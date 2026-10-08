using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Combat;

// One validated weapon from weapons.json. Times are already in simulation ticks. Immutable, shared by
// every player: per-player state (magazine, next fire tick) lives in the player's Inventory.
// Phase 17 D2: plus pellets, the spread cone, the distance falloff, the structure multiplier, the client's recoil and the
// projectile a projectile weapon fires (null = hitscan).
public sealed class WeaponDefinition
{
    // 기능: 검증이 끝난 무기 정의를 만든다.
    // 입력: id·name·damage(산탄 하나당)·fireIntervalTicks·magazineSize·reloadTicks·range·automatic·ammoType - Phase 4 값,
    //   pellets - 한 발의 산탄 수, spreadDegrees - 퍼짐 원뿔 반각, falloffStart·falloffMinRatio - 감쇠 시작 거리와 사거리에서의 비율,
    //   structureMultiplier - 구조물 피해 배율, recoilDegrees - Client 반동, projectile - 투사체 정의(null = Hitscan),
    //   equipTicks - 손에 든 뒤 쏠 수 있을 때까지의 Tick(리뷰 수정 C2, 0 = 바로).
    // 출력: 바뀌지 않는 WeaponDefinition.
    public WeaponDefinition(byte id, string name, ushort damage, ushort fireIntervalTicks, byte magazineSize,
        ushort reloadTicks, float range, bool automatic, AmmoType ammoType, byte pellets = 1, float spreadDegrees = 0f,
        float falloffStart = float.PositiveInfinity, float falloffMinRatio = 1f, float structureMultiplier = 1f, float recoilDegrees = 0f,
        ProjectileDefinition? projectile = null, ushort equipTicks = 0)
    {
        EquipTicks = equipTicks;
        Id = id;
        Name = name;
        Damage = damage;
        FireIntervalTicks = fireIntervalTicks;
        MagazineSize = magazineSize;
        ReloadTicks = reloadTicks;
        Range = range;
        Automatic = automatic;
        AmmoType = ammoType;
        Pellets = pellets;
        SpreadDegrees = spreadDegrees;
        FalloffStart = falloffStart < range ? falloffStart : range;
        FalloffMinRatio = falloffMinRatio;
        StructureMultiplier = structureMultiplier;
        RecoilDegrees = recoilDegrees;
        Projectile = projectile;
    }

    public byte Id { get; }
    public string Name { get; }
    // Phase 17 D4: per pellet (a one-pellet weapon: per shot).
    public ushort Damage { get; }
    public ushort FireIntervalTicks { get; }
    public byte MagazineSize { get; }
    public ushort ReloadTicks { get; }
    public float Range { get; }
    public bool Automatic { get; }
    // Phase 4 (D3): the reserve a reload draws from.
    public AmmoType AmmoType { get; }

    // Phase 17 D4: rays per shot (the shotgun 8); D3: the half angle of the cone the server spreads each ray in (0 = exact).
    public byte Pellets { get; }
    public float SpreadDegrees { get; }
    // Phase 17 D2: full damage up to FalloffStart, then linear down to FalloffMinRatio x damage at Range (FalloffStart =
    // Range: no falloff).
    public float FalloffStart { get; }
    public float FalloffMinRatio { get; }
    // Phase 17 D11: a piece takes damage x StructureMultiplier x its material's multiplier.
    public float StructureMultiplier { get; }
    // Phase 17 D3: the client's camera kick; the server never reads it.
    public float RecoilDegrees { get; }
    // Phase 17 D6: what the weapon launches instead of a ray (null = hitscan).
    public ProjectileDefinition? Projectile { get; }
    public bool IsProjectile => Projectile != null;
    // Review fix C2 (SEC-8): ticks from coming into the hand (WeaponRules.Equip) to the first shot.
    public ushort EquipTicks { get; }

    // 기능: 무기를 WeaponCatalog 와이어 값으로 바꾼다(Phase 17: 산탄·퍼짐·반동·투사체 종류 포함, 리뷰 수정 C2: 교체 대기 Tick).
    // 입력: 없음.
    // 출력: WeaponInfo.
    public WeaponInfo ToWire() => new WeaponInfo
    {
        WeaponId = Id,
        Name = Name,
        Damage = Damage,
        FireIntervalTicks = FireIntervalTicks,
        MagazineSize = MagazineSize,
        ReloadTicks = ReloadTicks,
        Range = Range,
        Automatic = Automatic,
        AmmoType = AmmoType,
        Pellets = Pellets,
        SpreadDegrees = SpreadDegrees,
        RecoilDegrees = RecoilDegrees,
        Projectile = Projectile?.Kind ?? ProjectileKind.None,
        EquipTicks = EquipTicks,
    };
}
