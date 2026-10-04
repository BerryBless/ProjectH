using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Combat;

// One validated weapon from weapons.json. Times are already in simulation ticks. Immutable, shared by
// every player: per-player state (magazine, next fire tick) lives in the player's Inventory.
public sealed class WeaponDefinition
{
    // 기능: 검증된 무기 수치로 무기 정의를 만든다.
    // 입력: id - 무기 id, name - 이름, damage - 기본 피해, fireIntervalTicks - 발사 간격 Tick, magazineSize - 탄창 크기, reloadTicks - 재장전 Tick, range - 사거리, automatic - 연사 여부, spread - 탄퍼짐(미적용), recoil - 반동(미적용), ammoType - 사용하는 탄약 종류.
    // 출력: 주어진 값을 담은 불변 무기 정의.
    public WeaponDefinition(byte id, string name, ushort damage, ushort fireIntervalTicks, byte magazineSize,
        ushort reloadTicks, float range, bool automatic, float spread, float recoil, AmmoType ammoType)
    {
        Id = id;
        Name = name;
        Damage = damage;
        FireIntervalTicks = fireIntervalTicks;
        MagazineSize = magazineSize;
        ReloadTicks = reloadTicks;
        Range = range;
        Automatic = automatic;
        Spread = spread;
        Recoil = recoil;
        AmmoType = ammoType;
    }

    public byte Id { get; }
    public string Name { get; }
    public ushort Damage { get; }
    public ushort FireIntervalTicks { get; }
    public byte MagazineSize { get; }
    public ushort ReloadTicks { get; }
    public float Range { get; }
    public bool Automatic { get; }
    // Phase 4 (D3): the reserve a reload draws from.
    public AmmoType AmmoType { get; }

    // D5: data fields only, always 0 in this phase and not applied by the combat code.
    public float Spread { get; }
    public float Recoil { get; }

    // 기능: 무기 정의를 Client에 보낼 WeaponInfo로 옮긴다.
    // 입력: 없음.
    // 출력: id·이름·피해·발사 간격·탄창·재장전·사거리·연사·탄약 종류를 담은 WeaponInfo(탄퍼짐·반동은 보내지 않음).
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
    };
}
