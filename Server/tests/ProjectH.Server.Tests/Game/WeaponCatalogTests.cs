using System;
using System.IO;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Game;

public class WeaponCatalogTests
{
    private static string One(string fields) => "{ \"weapons\": [ { " + fields + " } ] }";

    private const string ValidFields =
        "\"id\": 1, \"name\": \"Vesper AR\", \"damage\": 20, \"fireIntervalSeconds\": 0.1, \"magazineSize\": 30, " +
        "\"reloadSeconds\": 2.0, \"range\": 150, \"automatic\": true, \"ammoType\": \"Medium\"";

    private static string Parse(string json, int simHz = 30)
    {
        Assert.False(WeaponCatalog.TryParse(json, simHz, out var catalog, out string? error));
        Assert.Null(catalog);
        Assert.False(string.IsNullOrEmpty(error));
        return error!;
    }

    [Fact]
    public void Valid_ConvertsSecondsToTicks()
    {
        Assert.True(WeaponCatalog.TryParse(TestWeapons.Json(), 30, out var catalog, out string? error), error);
        Assert.Equal(3, catalog!.Count);
        Assert.Equal(30, catalog.SimHz);
        Assert.Equal("Test Auto", catalog[0].Name);
        Assert.Equal(3, catalog[0].FireIntervalTicks);
        Assert.Equal(30, catalog[0].ReloadTicks);
        Assert.Equal(15, catalog[1].FireIntervalTicks);
        Assert.Equal(60, catalog[1].ReloadTicks);
        Assert.False(catalog[1].Automatic);
        Assert.Equal(0f, catalog[1].SpreadDegrees);
        Assert.Equal(3, catalog.WireInfos.Length);
        Assert.Equal("Test Semi", catalog.WireInfos[1].Name);
        Assert.Equal(AmmoType.Heavy, catalog[1].AmmoType);
        Assert.Equal(AmmoType.Heavy, catalog.WireInfos[1].AmmoType);
    }

    [Fact]
    public void TryGetById_FindsEveryWeapon_AndNothingElse()
    {
        var catalog = TestWeapons.Create();
        Assert.True(catalog.TryGetById(TestWeapons.LightId, out var light));
        Assert.Equal("Test Light", light.Name);
        Assert.Equal(AmmoType.Light, light.AmmoType);
        Assert.False(catalog.TryGetById(0, out _));
        Assert.False(catalog.TryGetById(4, out _));
        Assert.False(catalog.TryGetById(255, out _));
    }

    [Fact]
    public void HalfTick_RoundsAwayFromZero_AndTinyTimesBecomeOneTick()
    {
        string json = "{ \"weapons\": [ { \"id\": 1, \"name\": \"A\", \"damage\": 1, \"fireIntervalSeconds\": 1.25, " +
                      "\"magazineSize\": 1, \"reloadSeconds\": 0.001, \"range\": 1, \"ammoType\": \"Light\" } ] }";
        Assert.True(WeaponCatalog.TryParse(json, 30, out var catalog, out _));
        Assert.Equal(38, catalog![0].FireIntervalTicks);   // 37.5
        Assert.Equal(1, catalog[0].ReloadTicks);
        Assert.Equal(1, catalog.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{ \"weapons\": [] }")]
    [InlineData("{ \"weapons\": [ null ] }")]
    public void EmptyOrMalformed_IsRejected(string json)
    {
        Parse(json);
    }

    [Theory]
    [InlineData("\"damage\": -5")]
    [InlineData("\"damage\": 0")]
    [InlineData("\"damage\": 70000")]
    [InlineData("\"fireIntervalSeconds\": 0")]
    [InlineData("\"fireIntervalSeconds\": -0.1")]
    [InlineData("\"magazineSize\": 0")]
    [InlineData("\"magazineSize\": 256")]
    [InlineData("\"reloadSeconds\": -2")]
    [InlineData("\"reloadSeconds\": 100000")]
    [InlineData("\"range\": 0")]
    [InlineData("\"range\": -1")]
    [InlineData("\"range\": 1e39")]
    [InlineData("\"spreadDegrees\": -1")]
    [InlineData("\"spreadDegrees\": 31")]
    [InlineData("\"equipSeconds\": -0.1")]   // review fix C2: 0-2
    [InlineData("\"equipSeconds\": 2.1")]
    [InlineData("\"pellets\": 0")]
    [InlineData("\"pellets\": 17")]
    [InlineData("\"falloffStart\": 151")]
    [InlineData("\"falloffMinRatio\": 1.5")]
    [InlineData("\"structureMultiplier\": -1")]
    [InlineData("\"projectile\": \"Rocket\"")]   // Phase 17: not defined in this catalog
    [InlineData("\"id\": 0")]
    [InlineData("\"id\": 256")]
    [InlineData("\"ammoType\": \"None\"")]
    [InlineData("\"ammoType\": \"light\"")]
    [InlineData("\"ammoType\": \"2\"")]
    [InlineData("\"ammoType\": null")]
    public void BadNumber_IsRejected(string overrideField)
    {
        // A later duplicate key overrides the valid value (System.Text.Json keeps the last one).
        Parse(One(ValidFields + ", " + overrideField));
    }

    // Review fix C2: equipSeconds is optional (0.4 s when missing), 0-2, rounded to ticks like the other times; 0 is allowed
    // (no wait). The tick count goes to the client in the catalog.
    [Theory]
    [InlineData("", 30, 12)]
    [InlineData(", \"equipSeconds\": 0", 30, 0)]
    [InlineData(", \"equipSeconds\": 0.4", 60, 24)]
    [InlineData(", \"equipSeconds\": 2", 128, 256)]
    [InlineData(", \"equipSeconds\": 0.01", 30, 1)]
    public void EquipSeconds_IsOptional_AndBecomesTicks(string field, int simHz, int ticks)
    {
        Assert.True(WeaponCatalog.TryParse(One(ValidFields + field), simHz, out var catalog, out string? error), error);
        Assert.Equal(ticks, catalog![0].EquipTicks);
        Assert.Equal(ticks, catalog.WireInfos[0].EquipTicks);
    }

    // Review fix D3: a projectile kind's numbers stay within the limits the clients' parser accepts (ProtocolLimits), and the
    // speed it can reach before it explodes (launch speed + gravity x lifetime) stays within the speed limit too, so no
    // ProjectileState the server sends is refused.
    [Theory]
    [InlineData(40.0, 0.0, 4.0, true)]
    [InlineData(18.0, 9.81, 3.0, true)]        // the shipped grenade: 18 + 29.4
    [InlineData(150.0, 20.0, 3.0, false)]      // 150 + 60 > 200
    [InlineData(201.0, 0.0, 1.0, false)]
    [InlineData(150.0, 0.0, 4.0, false)]       // review D round 1: 600 m of flight leaves the +-512 m the clients accept
    [InlineData(100.0, 0.0, 4.0, true)]        // 80 + 400 = 480 m across, and up from the highest eye within 512 m
    [InlineData(20.0, 10.0, 8.0, false)]       // review D round 1: 160 m of launch speed, but falling adds 320 m a bounce can turn sideways: 80 + 480 > 512
    public void AProjectileKind_StaysWithinTheClientLimits(double speed, double gravity, double lifetime, bool valid)
    {
        string number(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string json = "{ \"weapons\": [ { " + ValidFields + " } ], \"projectiles\": { " +
                      "\"Grenade\": { \"speed\": 18, \"gravity\": 9.81, \"lifetimeSeconds\": 3.0, \"explosionRadius\": 5, \"explosionDamage\": 80, " +
                      "\"structureDamage\": 120, \"bounce\": 0.4, \"throwIntervalSeconds\": 1.0, \"throwUpDegrees\": 8 }, " +
                      "\"Rocket\": { \"speed\": " + number(speed) + ", \"gravity\": " + number(gravity) + ", \"lifetimeSeconds\": " + number(lifetime) +
                      ", \"explosionRadius\": 4, \"explosionDamage\": 75, \"structureDamage\": 300, \"bounce\": 0 } } }";
        bool parsed = WeaponCatalog.TryParse(json, 30, out _, out string? error);
        Assert.True(parsed == valid, error);
    }

    // Phase 17–19 review: a rocket explodes on its first hit, so a bounce above 0 is a data error (it would turn it into a grenade).
    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.3, false)]
    public void ARocketThatBounces_IsRejected(double bounce, bool valid)
    {
        string json = "{ \"weapons\": [ { " + ValidFields + " } ], \"projectiles\": { " +
                      "\"Grenade\": { \"speed\": 18, \"gravity\": 9.81, \"lifetimeSeconds\": 3.0, \"explosionRadius\": 5, \"explosionDamage\": 80, " +
                      "\"structureDamage\": 120, \"bounce\": 0.4, \"throwIntervalSeconds\": 1.0, \"throwUpDegrees\": 8 }, " +
                      "\"Rocket\": { \"speed\": 40, \"gravity\": 0, \"lifetimeSeconds\": 4.0, \"explosionRadius\": 4, \"explosionDamage\": 75, " +
                      "\"structureDamage\": 300, \"bounce\": " + bounce.ToString(System.Globalization.CultureInfo.InvariantCulture) + " } } }";
        bool parsed = WeaponCatalog.TryParse(json, 30, out _, out string? error);
        Assert.True(parsed == valid, error);
        if (!valid) Assert.Contains("rocket must not bounce", error);
    }

    [Fact]
    public void MissingField_IsRejected()
    {
        Parse(One("\"id\": 1, \"name\": \"A\", \"damage\": 20, \"magazineSize\": 30, \"reloadSeconds\": 2, \"range\": 150, \"ammoType\": \"Light\""));
        Parse(One(ValidFields.Replace(", \"ammoType\": \"Medium\"", "")));
    }

    [Theory]
    [InlineData("12345678901234567")]   // 17 ASCII bytes
    [InlineData("가나다라마바")]           // 6 x 3 = 18 UTF-8 bytes
    [InlineData("")]
    [InlineData("   ")]
    public void BadName_IsRejected(string name)
    {
        Parse(One(ValidFields.Replace("Vesper AR", name)));
    }

    [Fact]
    public void SixteenByteName_IsAccepted()
    {
        Assert.True(WeaponCatalog.TryParse(One(ValidFields.Replace("Vesper AR", "1234567890123456")), 30, out _, out _));
    }

    [Fact]
    public void DuplicateName_IsRejected()
    {
        string json = "{ \"weapons\": [ { " + ValidFields + " }, { " + ValidFields.Replace("\"id\": 1", "\"id\": 2") + " } ] }";
        Assert.Contains("duplicate name", Parse(json));
    }

    [Fact]
    public void DuplicateId_IsRejected()
    {
        string json = "{ \"weapons\": [ { " + ValidFields + " }, { " + ValidFields.Replace("Vesper AR", "Other") + " } ] }";
        Assert.Contains("duplicate", Parse(json));
    }

    [Fact]
    public void MoreThanEightWeapons_IsRejected()
    {
        var entries = new string[9];
        for (int i = 0; i < 9; i++) entries[i] = "{ " + ValidFields.Replace("\"id\": 1", "\"id\": " + (i + 1)) + " }";
        Parse("{ \"weapons\": [ " + string.Join(", ", entries) + " ] }");
    }

    [Fact]
    public void LoadFile_MissingFile_Throws()
    {
        string path = Path.Combine(Path.GetTempPath(), "projecth-no-such-weapons-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.Throws<InvalidOperationException>(() => WeaponCatalog.LoadFile(path, 30));
    }

    // The file shipped next to the server (copied to this test's output through the project reference)
    // must load and match Phase 3 spec D5 and Phase 4 spec D3.
    [Fact]
    public void ShippedWeaponsJson_MatchesSpec()
    {
        var catalog = WeaponCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, "weapons.json"), 30);

        Assert.Equal(6, catalog.Count);   // Phase 17 D1: the three below keep their numbers; + Brute SG, Sparrow P, Thunder RL
        for (int i = 0; i < catalog.Count; i++) Assert.Equal(12, catalog[i].EquipTicks);   // review fix C2: equipSeconds 0.4
        Assert.Equal("Vesper AR", catalog[0].Name);
        Assert.Equal(20, catalog[0].Damage);
        Assert.Equal(3, catalog[0].FireIntervalTicks);    // 10 rounds/s
        Assert.Equal(30, catalog[0].MagazineSize);
        Assert.Equal(60, catalog[0].ReloadTicks);         // 2.0 s
        Assert.Equal(150f, catalog[0].Range);
        Assert.True(catalog[0].Automatic);
        Assert.Equal(AmmoType.Medium, catalog[0].AmmoType);

        Assert.Equal("Kestrel LR", catalog[1].Name);
        Assert.Equal(90, catalog[1].Damage);
        Assert.Equal(38, catalog[1].FireIntervalTicks);   // 1.25 s
        Assert.Equal(5, catalog[1].MagazineSize);
        Assert.Equal(75, catalog[1].ReloadTicks);         // 2.5 s
        Assert.Equal(300f, catalog[1].Range);
        Assert.False(catalog[1].Automatic);
        Assert.Equal(AmmoType.Heavy, catalog[1].AmmoType);

        // Wisp SMG: 12 damage, 15 rounds/s (2 ticks at 30 Hz), 25 rounds, 1.6 s reload, 80 m, automatic, Light.
        Assert.Equal(3, catalog[2].Id);
        Assert.Equal("Wisp SMG", catalog[2].Name);
        Assert.Equal(12, catalog[2].Damage);
        Assert.Equal(2, catalog[2].FireIntervalTicks);
        Assert.Equal(25, catalog[2].MagazineSize);
        Assert.Equal(48, catalog[2].ReloadTicks);
        Assert.Equal(80f, catalog[2].Range);
        Assert.True(catalog[2].Automatic);
        Assert.Equal(AmmoType.Light, catalog[2].AmmoType);

        // Phase 17 D1-D3, D11: spread, pellets and structure multipliers. The sniper stays exact (spread 0, no falloff).
        Assert.Equal(1f, catalog[0].SpreadDegrees);
        Assert.Equal(0f, catalog[1].SpreadDegrees);
        Assert.Equal(300f, catalog[1].FalloffStart);
        Assert.Equal(1.5f, catalog[1].StructureMultiplier);
        Assert.Equal(2.5f, catalog[2].SpreadDegrees);
        Assert.Equal(0.8f, catalog[2].StructureMultiplier);

        Assert.Equal("Brute SG", catalog[3].Name);
        Assert.Equal(4, catalog[3].Id);
        Assert.Equal(8, catalog[3].Pellets);
        Assert.Equal(11, catalog[3].Damage);
        Assert.Equal(27, catalog[3].FireIntervalTicks);   // 0.9 s
        Assert.Equal(5, catalog[3].MagazineSize);
        Assert.Equal(AmmoType.Shells, catalog[3].AmmoType);
        Assert.Equal(6f, catalog[3].SpreadDegrees);
        Assert.Equal(35f, catalog[3].Range);
        Assert.Equal(0.6f, catalog[3].StructureMultiplier);
        Assert.False(catalog[3].Automatic);

        Assert.Equal("Sparrow P", catalog[4].Name);
        Assert.Equal(24, catalog[4].Damage);
        Assert.Equal(6, catalog[4].FireIntervalTicks);    // 0.2 s
        Assert.Equal(12, catalog[4].MagazineSize);
        Assert.Equal(AmmoType.Light, catalog[4].AmmoType);
        Assert.Equal(1.5f, catalog[4].SpreadDegrees);
        Assert.False(catalog[4].Automatic);

        Assert.Equal("Thunder RL", catalog[5].Name);
        Assert.Equal(1, catalog[5].MagazineSize);
        Assert.Equal(90, catalog[5].ReloadTicks);         // 3 s
        Assert.Equal(AmmoType.Rockets, catalog[5].AmmoType);
        Assert.NotNull(catalog[5].Projectile);
        Assert.Equal(ProjectileKind.Rocket, catalog[5].Projectile!.Kind);

        // D9, D10: the grenade and the rocket.
        var grenade = catalog.Projectile(ProjectileKind.Grenade)!;
        Assert.Equal(18f, grenade.Speed);
        Assert.Equal(90, grenade.LifetimeTicks);          // 3 s fuse
        Assert.Equal(5f, grenade.ExplosionRadius);
        Assert.Equal(80, grenade.ExplosionDamage);
        Assert.Equal(120, grenade.StructureDamage);
        Assert.Equal(0.4f, grenade.Bounce);
        Assert.Equal(30, grenade.ThrowIntervalTicks);     // 1 s
        var rocket = catalog.Projectile(ProjectileKind.Rocket)!;
        Assert.Equal(40f, rocket.Speed);
        Assert.Equal(0f, rocket.Gravity);
        Assert.Equal(120, rocket.LifetimeTicks);          // 4 s
        Assert.Equal(4f, rocket.ExplosionRadius);
        Assert.Equal(75, rocket.ExplosionDamage);
        Assert.Equal(300, rocket.StructureDamage);
        Assert.True(rocket.ExplodesOnImpact);
        Assert.Equal(2, catalog.WireProjectiles.Length);
        Assert.Null(catalog.RequireGrenade());
    }
}
