using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class CombatPacketTests
{
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];

    private PacketReader ReaderAfterId(int length, PacketId expected)
    {
        var reader = new PacketReader(_buffer.AsSpan(0, length));
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(expected, id);
        return reader;
    }

    private static readonly ProjectileInfo[] NoProjectiles = System.Array.Empty<ProjectileInfo>();

    private static WeaponInfo Weapon(byte id, string name) => new WeaponInfo
    {
        WeaponId = id,
        Name = name,
        Damage = 20,
        FireIntervalTicks = 3,
        MagazineSize = 30,
        ReloadTicks = 60,
        Range = 150f,
        Automatic = true,
        AmmoType = AmmoType.Medium,
        Pellets = 1,   // Phase 17
    };

    [Fact]
    public void WeaponCatalog_RoundTrip()
    {
        var weapons = new[] { Weapon(1, "Vesper AR"), Weapon(2, "Kestrel LR") };
        weapons[1].Automatic = false;
        weapons[1].Range = 300f;

        var writer = new PacketWriter(_buffer);
        WeaponCatalogPacket.Write(ref writer, weapons, NoProjectiles);
        Assert.False(writer.Overflowed);
        var reader = ReaderAfterId(writer.Length, PacketId.WeaponCatalog);
        Assert.True(WeaponCatalogPacket.TryRead(ref reader, out var read));

        Assert.Equal(2, read.Length);
        Assert.Equal("Vesper AR", read[0].Name);
        Assert.Equal(20, read[0].Damage);
        Assert.Equal(3, read[0].FireIntervalTicks);
        Assert.Equal(30, read[0].MagazineSize);
        Assert.Equal(60, read[0].ReloadTicks);
        Assert.True(read[0].Automatic);
        Assert.Equal(2, read[1].WeaponId);
        Assert.False(read[1].Automatic);
        Assert.Equal(300f, read[1].Range);
        Assert.Equal(AmmoType.Medium, read[1].AmmoType);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void WeaponCatalog_EightMaximumLengthNames_FitsOnePacket()
    {
        var weapons = new WeaponInfo[WeaponCatalogPacket.MaxWeapons];
        for (int i = 0; i < weapons.Length; i++) weapons[i] = Weapon((byte)(i + 1), new string('w', WeaponCatalogPacket.MaxNameBytes));
        var writer = new PacketWriter(_buffer);
        WeaponCatalogPacket.Write(ref writer, weapons, NoProjectiles);
        Assert.False(writer.Overflowed);
        Assert.Equal(2 + 8 * 41 + 1, writer.Length);   // Phase 4: ammo type; Phase 17: pellets, spread, recoil, projectile + the projectile count
        Assert.True(writer.Length <= ProtocolConstants.MaxPacketSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void WeaponCatalog_BadCount_IsRejected(byte count)
    {
        var reader = new PacketReader(new byte[] { count, 0, 0, 0 });
        Assert.False(WeaponCatalogPacket.TryRead(ref reader, out _));
    }

    [Fact]
    public void WeaponCatalog_NonFiniteRange_IsRejected()
    {
        var weapon = Weapon(1, "Bad");
        weapon.Range = float.NaN;
        var writer = new PacketWriter(_buffer);
        WeaponCatalogPacket.Write(ref writer, new[] { weapon }, NoProjectiles);
        var reader = ReaderAfterId(writer.Length, PacketId.WeaponCatalog);
        Assert.False(WeaponCatalogPacket.TryRead(ref reader, out _));
    }

    [Theory]
    [InlineData(AmmoType.None)]
    [InlineData((AmmoType)6)]   // Phase 17: Shells 4 and Rockets 5 are known
    public void WeaponCatalog_UnknownAmmoType_IsRejected(AmmoType ammoType)
    {
        var weapon = Weapon(1, "Bad");
        weapon.AmmoType = ammoType;
        var writer = new PacketWriter(_buffer);
        WeaponCatalogPacket.Write(ref writer, new[] { weapon }, NoProjectiles);
        var reader = ReaderAfterId(writer.Length, PacketId.WeaponCatalog);
        Assert.False(WeaponCatalogPacket.TryRead(ref reader, out _));
    }

    [Fact]
    public void WeaponCatalog_Truncated_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        WeaponCatalogPacket.Write(ref writer, new[] { Weapon(1, "Vesper AR") }, NoProjectiles);
        var reader = ReaderAfterId(writer.Length - 1, PacketId.WeaponCatalog);
        Assert.False(WeaponCatalogPacket.TryRead(ref reader, out _));
    }

    [Fact]
    public void ShotFired_RoundTrip_AndRejectsNaN()
    {
        var writer = new PacketWriter(_buffer);
        ShotFired.Write(ref writer, new ShotFired { ShooterId = 4, Start = new Vector3(1, 1.6f, 2), End = new Vector3(1, 1.6f, 40), WeaponId = 7 });
        Assert.Equal(1 + ShotFired.PayloadSize, writer.Length);
        Assert.Equal(28, writer.Length);   // Phase 18 D4: + the weapon id
        var reader = ReaderAfterId(writer.Length, PacketId.ShotFired);
        Assert.True(ShotFired.TryRead(ref reader, out var shot));
        Assert.Equal(4, shot.ShooterId);
        Assert.Equal(new Vector3(1, 1.6f, 2), shot.Start);
        Assert.Equal(new Vector3(1, 1.6f, 40), shot.End);
        Assert.Equal(7, shot.WeaponId);
        reader = ReaderAfterId(writer.Length - 1, PacketId.ShotFired);   // the v16 size (no weapon id) is refused
        Assert.False(ShotFired.TryRead(ref reader, out _));

        writer = new PacketWriter(_buffer);
        ShotFired.Write(ref writer, new ShotFired { ShooterId = 4, End = new Vector3(float.NaN, 0, 0) });
        reader = ReaderAfterId(writer.Length, PacketId.ShotFired);
        Assert.False(ShotFired.TryRead(ref reader, out _));
    }

    [Fact]
    public void HitAndDamage_RoundTrip()
    {
        var writer = new PacketWriter(_buffer);
        HitConfirmed.Write(ref writer, new HitConfirmed { TargetId = 9, Damage = 90, Killed = true });
        var reader = ReaderAfterId(writer.Length, PacketId.HitConfirmed);
        Assert.True(HitConfirmed.TryRead(ref reader, out var hit));
        Assert.Equal(9, hit.TargetId);
        Assert.Equal(90, hit.Damage);
        Assert.True(hit.Killed);

        writer = new PacketWriter(_buffer);
        DamageTaken.Write(ref writer, new DamageTaken { AttackerId = 3, Damage = 20, FromDirection = new Vector3(0, 0, -1) });
        reader = ReaderAfterId(writer.Length, PacketId.DamageTaken);
        Assert.True(DamageTaken.TryRead(ref reader, out var damage));
        Assert.Equal(3, damage.AttackerId);
        Assert.Equal(20, damage.Damage);
        Assert.Equal(new Vector3(0, 0, -1), damage.FromDirection);
        Assert.Equal(0, damage.Flags);
        Assert.Equal(18, writer.Length);   // Phase 18 D8: + the flags
    }

    // Phase 18 D8: the shield flags round-trip; an unknown bit or "broken" without "hit" is refused.
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(0x81, false)]
    public void DamageTaken_Flags_RoundTrip_OrAreRefused(byte flags, bool ok)
    {
        var writer = new PacketWriter(_buffer);
        DamageTaken.Write(ref writer, new DamageTaken { AttackerId = 3, Damage = 20, FromDirection = Vector3.UnitX, Flags = flags });
        var reader = ReaderAfterId(writer.Length, PacketId.DamageTaken);
        Assert.Equal(ok, DamageTaken.TryRead(ref reader, out var damage));
        if (!ok) return;
        Assert.Equal(flags, damage.Flags);
        Assert.Equal((flags & 1) != 0, damage.ShieldHit);
        Assert.Equal((flags & 2) != 0, damage.ShieldBroken);
    }

    [Theory]
    [InlineData(0, 0, 0)]      // no shield: health only
    [InlineData(50, 20, 1)]    // shield hit, still up
    [InlineData(50, 0, 3)]     // shield broken
    [InlineData(50, 50, 0)]    // shield not reduced (no damage applied)
    public void DamageTaken_FlagsFor_FollowsTheShield(int before, int after, byte expected)
    {
        Assert.Equal(expected, DamageTaken.FlagsFor(before, after));
    }

    [Fact]
    public void DiedAndRespawned_RoundTrip()
    {
        var writer = new PacketWriter(_buffer);
        PlayerDied.Write(ref writer, new PlayerDied { VictimId = 2, KillerId = 1 });
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerDied);
        Assert.True(PlayerDied.TryRead(ref reader, out var died));
        Assert.Equal(2, died.VictimId);
        Assert.Equal(1, died.KillerId);

        writer = new PacketWriter(_buffer);
        PlayerRespawned.Write(ref writer, new PlayerRespawned { EntityId = 2, Position = new Vector3(3, 0, 4), Yaw = 90f });
        reader = ReaderAfterId(writer.Length, PacketId.PlayerRespawned);
        Assert.True(PlayerRespawned.TryRead(ref reader, out var respawned));
        Assert.Equal(2, respawned.EntityId);
        Assert.Equal(new Vector3(3, 0, 4), respawned.Position);
        Assert.Equal(90f, respawned.Yaw);
    }

    [Fact]
    public void ShortCombatPackets_AreRejected()
    {
        var empty = new PacketReader(ReadOnlySpan<byte>.Empty);
        Assert.False(HitConfirmed.TryRead(ref empty, out _));
        var shortDamage = new PacketReader(new byte[16]);   // Phase 18: the v16 body size
        Assert.False(DamageTaken.TryRead(ref shortDamage, out _));
        var shortDied = new PacketReader(new byte[3]);
        Assert.False(PlayerDied.TryRead(ref shortDied, out _));
        var shortRespawn = new PacketReader(new byte[17]);
        Assert.False(PlayerRespawned.TryRead(ref shortRespawn, out _));
    }
}
