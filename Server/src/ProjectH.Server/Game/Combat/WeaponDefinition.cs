using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Combat;

// One validated weapon from weapons.json. Times are already in simulation ticks. Immutable, shared by
// every player: per-player state (ammo, next fire tick, reload) lives in PlayerEntity.
public sealed class WeaponDefinition
{
    public WeaponDefinition(byte id, string name, ushort damage, ushort fireIntervalTicks, byte magazineSize,
        ushort reloadTicks, float range, bool automatic, float spread, float recoil)
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
    }

    public byte Id { get; }
    public string Name { get; }
    public ushort Damage { get; }
    public ushort FireIntervalTicks { get; }
    public byte MagazineSize { get; }
    public ushort ReloadTicks { get; }
    public float Range { get; }
    public bool Automatic { get; }

    // D5: data fields only, always 0 in this phase and not applied by the combat code.
    public float Spread { get; }
    public float Recoil { get; }

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
    };
}
