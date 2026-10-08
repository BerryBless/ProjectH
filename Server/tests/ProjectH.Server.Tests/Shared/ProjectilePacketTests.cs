using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 17 D2, D7, D16: the projectile packets, the WeaponCatalog's new fields and projectile list, and the grenade button.
public class ProjectilePacketTests
{
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];

    private PacketReader ReaderAfterId(int length, PacketId expected)
    {
        var reader = new PacketReader(_buffer.AsSpan(0, length));
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(expected, id);
        return reader;
    }

    [Fact]
    public void Ids_AreTheWireFormat()
    {
        Assert.Equal(44, (byte)PacketId.ProjectileSpawned);
        Assert.Equal(45, (byte)PacketId.ProjectileState);
        Assert.Equal(46, (byte)PacketId.ProjectileExploded);
        Assert.Equal(32768, (int)InputButtons.ThrowGrenade);
        Assert.Equal(2, (byte)DeathCause.Explosion);
        Assert.Equal(4, (byte)AmmoType.Shells);
        Assert.Equal(5, (byte)AmmoType.Rockets);
        Assert.Equal(3, (byte)ConsumableType.Grenade);
    }

    [Fact]
    public void ProjectileSpawned_RoundTrip_AndRejects()
    {
        var p = new ProjectileSpawned
        {
            Id = 7, Kind = ProjectileKind.Grenade, OwnerId = 3, Position = new Vector3(1f, 2f, 3f), Velocity = new Vector3(0f, 2.5f, 17.8f), StartTick = 900,
        };
        var writer = new PacketWriter(_buffer);
        ProjectileSpawned.Write(ref writer, p);
        Assert.Equal(ProjectileSpawned.Size, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.ProjectileSpawned);
        Assert.True(ProjectileSpawned.TryRead(ref reader, out ProjectileSpawned read));
        Assert.Equal(p, read);

        foreach (ProjectileSpawned bad in new[] { p with { Id = 0 }, p with { Kind = ProjectileKind.None }, p with { Kind = (ProjectileKind)3 },
                     p with { Position = new Vector3(float.NaN, 0f, 0f) }, p with { Velocity = new Vector3(0f, float.PositiveInfinity, 0f) } })
        {
            writer = new PacketWriter(_buffer);
            ProjectileSpawned.Write(ref writer, bad);
            reader = ReaderAfterId(writer.Length, PacketId.ProjectileSpawned);
            Assert.False(ProjectileSpawned.TryRead(ref reader, out _));
        }
        for (int cut = 1; cut < ProjectileSpawned.Size; cut++)
        {
            writer = new PacketWriter(_buffer);
            ProjectileSpawned.Write(ref writer, p);
            reader = ReaderAfterId(cut, PacketId.ProjectileSpawned);
            Assert.False(ProjectileSpawned.TryRead(ref reader, out _));
        }
    }

    [Fact]
    public void ProjectileState_AndExploded_RoundTrip_AndReject()
    {
        var s = new ProjectileState { Id = 9, Position = new Vector3(4f, 0.05f, -2f), Velocity = Vector3.Zero, Tick = 1234 };
        var writer = new PacketWriter(_buffer);
        ProjectileState.Write(ref writer, s);
        Assert.Equal(ProjectileState.Size, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.ProjectileState);
        Assert.True(ProjectileState.TryRead(ref reader, out ProjectileState readState));
        Assert.Equal(s, readState);
        writer = new PacketWriter(_buffer);
        ProjectileState.Write(ref writer, s with { Id = 0 });
        reader = ReaderAfterId(writer.Length, PacketId.ProjectileState);
        Assert.False(ProjectileState.TryRead(ref reader, out _));

        var e = new ProjectileExploded { Id = 9, Position = new Vector3(4f, 0.05f, -2f), Kind = ProjectileKind.Rocket };
        writer = new PacketWriter(_buffer);
        ProjectileExploded.Write(ref writer, e);
        Assert.Equal(ProjectileExploded.Size, writer.Length);
        reader = ReaderAfterId(writer.Length, PacketId.ProjectileExploded);
        Assert.True(ProjectileExploded.TryRead(ref reader, out ProjectileExploded readExploded));
        Assert.Equal(e, readExploded);
        foreach (ProjectileExploded bad in new[] { e with { Id = 0 }, e with { Kind = ProjectileKind.None }, e with { Position = new Vector3(0f, float.NaN, 0f) } })
        {
            writer = new PacketWriter(_buffer);
            ProjectileExploded.Write(ref writer, bad);
            reader = ReaderAfterId(writer.Length, PacketId.ProjectileExploded);
            Assert.False(ProjectileExploded.TryRead(ref reader, out _));
        }
    }

    private static WeaponInfo Weapon(byte id, ProjectileKind projectile = ProjectileKind.None) => new()
    {
        WeaponId = id, Name = "W" + id, Damage = 11, FireIntervalTicks = 27, MagazineSize = 5, ReloadTicks = 72, Range = 35f, AmmoType = AmmoType.Shells,
        Pellets = 8, SpreadDegrees = 6f, RecoilDegrees = 4f, Projectile = projectile,
    };

    private static readonly ProjectileInfo GrenadeInfo = new()
    {
        Kind = ProjectileKind.Grenade, Speed = 18f, Gravity = 9.81f, ExplosionRadius = 5f, LifetimeTicks = 90,
    };

    private static readonly ProjectileInfo RocketInfo = new()
    {
        Kind = ProjectileKind.Rocket, Speed = 40f, Gravity = 0f, ExplosionRadius = 4f, LifetimeTicks = 120,
    };

    [Fact]
    public void WeaponCatalog_CarriesThePhase17Fields_AndTheProjectiles()
    {
        var writer = new PacketWriter(_buffer);
        WeaponCatalogPacket.Write(ref writer, new[] { Weapon(4), Weapon(6, ProjectileKind.Rocket) with { Pellets = 1 } }, new[] { GrenadeInfo, RocketInfo });
        var reader = ReaderAfterId(writer.Length, PacketId.WeaponCatalog);
        Assert.True(WeaponCatalogPacket.TryRead(ref reader, out WeaponInfo[] weapons, out ProjectileInfo[] projectiles));
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(8, weapons[0].Pellets);
        Assert.Equal(6f, weapons[0].SpreadDegrees);
        Assert.Equal(4f, weapons[0].RecoilDegrees);
        Assert.Equal(AmmoType.Shells, weapons[0].AmmoType);
        Assert.Equal(ProjectileKind.Rocket, weapons[1].Projectile);
        Assert.Equal(2, projectiles.Length);
        Assert.Equal(GrenadeInfo, projectiles[WeaponCatalogPacket.Find(projectiles, projectiles.Length, ProjectileKind.Grenade)]);
        Assert.Equal(RocketInfo, projectiles[WeaponCatalogPacket.Find(projectiles, projectiles.Length, ProjectileKind.Rocket)]);
        // The old two-argument reader still reads it.
        reader = ReaderAfterId(writer.Length, PacketId.WeaponCatalog);
        Assert.True(WeaponCatalogPacket.TryRead(ref reader, out weapons));
        Assert.Equal(2, weapons.Length);
    }

    [Fact]
    public void WeaponCatalog_BadPhase17Values_AreRejected()
    {
        bool Reads(WeaponInfo[] w, ProjectileInfo[] p)
        {
            var writer = new PacketWriter(_buffer);
            WeaponCatalogPacket.Write(ref writer, w, p);
            var reader = ReaderAfterId(writer.Length, PacketId.WeaponCatalog);
            return WeaponCatalogPacket.TryRead(ref reader, out _, out _);
        }
        ProjectileInfo[] both = { GrenadeInfo, RocketInfo };
        Assert.True(Reads(new[] { Weapon(1) }, both));
        Assert.False(Reads(new[] { Weapon(1) with { Pellets = 0 } }, both));
        Assert.False(Reads(new[] { Weapon(1) with { Pellets = 17 } }, both));
        Assert.False(Reads(new[] { Weapon(1) with { SpreadDegrees = -1f } }, both));
        Assert.False(Reads(new[] { Weapon(1) with { SpreadDegrees = float.NaN } }, both));
        Assert.False(Reads(new[] { Weapon(1) with { RecoilDegrees = 31f } }, both));
        Assert.False(Reads(new[] { Weapon(1) with { Projectile = (ProjectileKind)3 } }, both));
        Assert.False(Reads(new[] { Weapon(6, ProjectileKind.Rocket) }, new[] { GrenadeInfo }));     // fires a kind not in the list
        Assert.False(Reads(new[] { Weapon(1) }, new[] { GrenadeInfo, GrenadeInfo }));                // one entry per kind
        Assert.False(Reads(new[] { Weapon(1) }, new[] { GrenadeInfo with { Speed = 0f } }));
        Assert.False(Reads(new[] { Weapon(1) }, new[] { GrenadeInfo with { Gravity = -1f } }));
        Assert.False(Reads(new[] { Weapon(1) }, new[] { GrenadeInfo with { LifetimeTicks = 0 } }));
        Assert.False(Reads(new[] { Weapon(1) }, new[] { GrenadeInfo with { ExplosionRadius = float.PositiveInfinity } }));
        Assert.False(Reads(new[] { Weapon(1) }, new[] { GrenadeInfo, RocketInfo, GrenadeInfo }));   // more than MaxProjectiles
    }

    [Fact]
    public void PlayerDowned_ReadsTheExplosionCause()
    {
        var writer = new PacketWriter(_buffer);
        PlayerDowned.Write(ref writer, new PlayerDowned { VictimId = 2, AttackerId = 5, Cause = DeathCause.Explosion });
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerDowned);
        Assert.True(PlayerDowned.TryRead(ref reader, out PlayerDowned d));
        Assert.Equal(DeathCause.Explosion, d.Cause);
        Assert.Equal(5, d.AttackerId);
        _buffer[writer.Length - 1] = 3;
        reader = ReaderAfterId(writer.Length, PacketId.PlayerDowned);
        Assert.False(PlayerDowned.TryRead(ref reader, out _));
    }

    [Fact]
    public void InventoryState_GrenadeUse_IsRefused()
    {
        var writer = new PacketWriter(_buffer);
        InventoryState.Write(ref writer, new InventoryState { Using = ConsumableType.Grenade, UseRemainingTicks = 3 });
        var reader = ReaderAfterId(writer.Length, PacketId.InventoryState);
        Assert.False(InventoryState.TryRead(ref reader, out _));
    }

    [Fact]
    public void ItemCatalog_AGrenadeWithAChannel_OrAHealWithout_IsRefused()
    {
        ItemCatalogData Catalog(ConsumableInfo grenade, ConsumableInfo medkit) => new()
        {
            Rarities = new[] { R(), R(), R(), R(), R() },
            Ammo = new[] { A(AmmoType.Light), A(AmmoType.Medium), A(AmmoType.Heavy), A(AmmoType.Shells), A(AmmoType.Rockets) },
            Consumables = new[] { medkit, new ConsumableInfo { Type = ConsumableType.ShieldCell, Name = "C", UseTicks = 60, Shield = 25, MaxStack = 6 }, grenade },
        };
        static RarityInfo R() => new() { Name = "R", DamageMultiplier = 1f };
        static AmmoInfo A(AmmoType t) => new() { Type = t, Name = "A", Max = 10 };
        var goodGrenade = new ConsumableInfo { Type = ConsumableType.Grenade, Name = "G", MaxStack = 6 };
        var goodMedkit = new ConsumableInfo { Type = ConsumableType.Medkit, Name = "M", UseTicks = 90, Heal = 50, MaxStack = 3 };
        bool Reads(ItemCatalogData data)
        {
            var writer = new PacketWriter(_buffer);
            ItemCatalogPacket.Write(ref writer, data);
            var reader = ReaderAfterId(writer.Length, PacketId.ItemCatalog);
            return ItemCatalogPacket.TryRead(ref reader, out _);
        }
        Assert.True(Reads(Catalog(goodGrenade, goodMedkit)));
        Assert.False(Reads(Catalog(goodGrenade with { UseTicks = 30 }, goodMedkit)));
        Assert.False(Reads(Catalog(goodGrenade with { Heal = 1 }, goodMedkit)));
        Assert.False(Reads(Catalog(goodGrenade, goodMedkit with { UseTicks = 0 })));
    }
}
