using System;
using System.IO;
using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Build;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 17 D1-D5, D11, D12: spread, falloff, pellets, structure multipliers and the old weapons' behaviour, with the shipped
// weapons.json (ids 1 AR, 2 Sniper, 3 SMG, 4 Shotgun, 5 Pistol, 6 Rocket) on the dev sandbox (damage always allowed). Shooters
// stand on the flat plaza (y 0) south of z 0.
public class WeaponsPhase17Tests
{
    public const byte Ar = 1, Sniper = 2, Smg = 3, Shotgun = 4, Pistol = 5, Rocket = 6;

    // 기능: 운영 weapons.json과 테스트 아이템·Loot·자기장 데이터로 GameData를 만든다. 리뷰 수정 C2: 교체 대기(equipSeconds)만 0으로 바꾼다
    //   (이 테스트들은 칸을 바꾸는 입력으로 바로 쏘는 무기 동작을 본다. 교체 대기는 WeaponRulesTests·PickupDropTests가 운영 값으로 본다).
    // 입력: 없음.
    // 출력: GameData.
    public static GameData Data()
    {
        ItemCatalog items = TestGameData.Items();
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "weapons.json"));
        string noWait = System.Text.RegularExpressions.Regex.Replace(json, "\"equipSeconds\":\\s*[0-9.]+", "\"equipSeconds\": 0");
        Assert.NotEqual(json, noWait);
        Assert.True(WeaponCatalog.TryParse(noWait, 30, out WeaponCatalog? weapons, out string? error), error);
        return new GameData(weapons!, items, TestGameData.Loot(items), TestGameData.Zones());
    }

    // 기능: 세 칸에 무기를 든 장비(실드 0, 탄·수류탄 넉넉히)를 만든다.
    // 입력: a·b·c - 칸 0·1·2의 무기 id, rarity - 등급.
    // 출력: StartingLoadout.
    public static StartingLoadout Loadout(byte a, byte b, byte c, byte rarity = 0) => new()
    {
        Weapons = new[] { new LoadoutWeapon(a, rarity), new LoadoutWeapon(b, rarity), new LoadoutWeapon(c, rarity) },
        LightAmmo = 180, MediumAmmo = 150, HeavyAmmo = 30, ShellsAmmo = 40, RocketsAmmo = 6, Grenades = 6,
    };

    // 기능: 운영 무기 데이터와 주어진 장비로 개발 모드 경기를 만든다.
    // 입력: loadout - 시작 장비.
    // 출력: SandboxHarness.
    private static SandboxHarness Harness(StartingLoadout loadout) => new(loadout, data: Data());

    private static readonly Vector3 Chest = new(0f, 1.2f, 0f);

    // ---- pure rules ----

    [Fact]
    public void Spread_IsDeterministic_InsideTheCone_AndZeroIsExact()
    {
        Vector3 aim = Vector3.Normalize(new Vector3(0.3f, -0.1f, 1f));
        Assert.Equal(aim, WeaponSpread.Spread(aim, 0f, 7, 100, 0, 0UL));
        Assert.Equal(WeaponSpread.Spread(aim, 6f, 7, 100, 3, 0UL), WeaponSpread.Spread(aim, 6f, 7, 100, 3, 0UL));
        Assert.NotEqual(WeaponSpread.Spread(aim, 6f, 7, 100, 3, 0UL), WeaponSpread.Spread(aim, 6f, 7, 100, 4, 0UL));
        Assert.NotEqual(WeaponSpread.Spread(aim, 6f, 7, 100, 3, 0UL), WeaponSpread.Spread(aim, 6f, 7, 101, 3, 0UL));
        Assert.NotEqual(WeaponSpread.Spread(aim, 6f, 7, 100, 3, 0UL), WeaponSpread.Spread(aim, 6f, 8, 100, 3, 0UL));
        float maxAngle = 0f;
        for (int i = 0; i < 5000; i++)
        {
            Vector3 d = WeaponSpread.Spread(aim, 6f, (ushort)(i % 50), (uint)(i / 8), i % 8, 0UL);
            Assert.Equal(1f, d.Length(), 4);
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(d, aim), -1f, 1f)) * 180f / MathF.PI;
            Assert.True(angle <= 6f + 1e-3f, $"angle {angle}");
            maxAngle = MathF.Max(maxAngle, angle);
        }
        Assert.True(maxAngle > 5f);   // the cone is used, not only its centre
    }

    // Review fix C1 (SEC-11): the match secret is part of the hash, so a client that knows the tick, its entity id and the code
    // cannot work out the server's spread; the same secret still gives the same rays.
    [Fact]
    public void DifferentSecrets_GiveDifferentDirections()
    {
        Vector3 aim = Vector3.Normalize(new Vector3(0.3f, -0.1f, 1f));
        Assert.NotEqual(WeaponSpread.Spread(aim, 6f, 7, 100, 3, 0x1234_5678_9ABC_DEF0UL), WeaponSpread.Spread(aim, 6f, 7, 100, 3, 0x0FED_CBA9_8765_4321UL));
        Assert.NotEqual(WeaponSpread.Spread(aim, 6f, 7, 100, 3, 1UL), WeaponSpread.Spread(aim, 6f, 7, 100, 3, 0UL));
    }

    [Fact]
    public void TheSameSecret_IsDeterministic()
    {
        Vector3 aim = Vector3.Normalize(new Vector3(-0.2f, 0.1f, 1f));
        const ulong secret = 0xDEAD_BEEF_0123_4567UL;
        Assert.Equal(WeaponSpread.Spread(aim, 6f, 9, 500, 2, secret), WeaponSpread.Spread(aim, 6f, 9, 500, 2, secret));
    }

    [Fact]
    public void Falloff_IsFlatThenLinearToTheMinimum()
    {
        Assert.Equal(1f, CombatRules.FalloffMultiplier(10f, 50f, 150f, 0.7f));
        Assert.Equal(1f, CombatRules.FalloffMultiplier(50f, 50f, 150f, 0.7f));
        Assert.Equal(0.85f, CombatRules.FalloffMultiplier(100f, 50f, 150f, 0.7f), 4);
        Assert.Equal(0.7f, CombatRules.FalloffMultiplier(150f, 50f, 150f, 0.7f), 4);
        Assert.Equal(0.7f, CombatRules.FalloffMultiplier(200f, 50f, 150f, 0.7f), 4);
        Assert.Equal(1f, CombatRules.FalloffMultiplier(299f, 300f, 300f, 1f));   // no falloff (the sniper)
    }

    [Fact]
    public void ScaledDamage_FromARawSum_MatchesTheIntegerRule()
    {
        for (int damage = 1; damage < 200; damage++)
        {
            foreach (float m in new[] { 1f, 1.05f, 1.1f, 1.15f, 1.2f })
                Assert.Equal(CombatRules.ScaledDamage((ushort)damage, m), CombatRules.ScaledDamage((float)damage, m));
        }
        Assert.Equal(106, CombatRules.ScaledDamage(88f, 1.2f));   // eight 11-damage pellets, Legendary: rounded once
        Assert.Equal(0, CombatRules.ScaledDamage(0f, 1.2f));
    }

    [Fact]
    public void ExplosionDamage_FallsLinearlyToTheRadius()
    {
        Assert.Equal(80, CombatRules.ExplosionDamage(80, 0f, 5f, 1f));
        Assert.Equal(40, CombatRules.ExplosionDamage(80, 2.5f, 5f, 1f));
        Assert.Equal(16, CombatRules.ExplosionDamage(80, 4f, 5f, 1f));
        Assert.Equal(0, CombatRules.ExplosionDamage(80, 5f, 5f, 1f));
        Assert.Equal(0, CombatRules.ExplosionDamage(80, 9f, 5f, 1f));
        Assert.Equal(90, CombatRules.ExplosionDamage(75, 0f, 4f, 1.2f));   // a Legendary rocket
        Assert.Equal(150f, CombatRules.ExplosionStructureDamage(300, 2f, 4f, 1f), 3);
    }

    // ---- shots in a match ----

    // 기능: 사수(peer 1)와 대상(peer 2)을 넣는다.
    // 입력: h - 경기, shooterFeet·targetFeet - 발 위치.
    // 출력: [사수, 대상].
    private static PlayerEntity[] Two(SandboxHarness h, Vector3 shooterFeet, Vector3 targetFeet)
    {
        PlayerEntity shooter = h.Join(1, shooterFeet);
        PlayerEntity target = h.Join(2, targetFeet);
        return new[] { shooter, target };
    }

    // 기능: 마지막으로 보낸 ShotFired를 읽는다.
    // 입력: h - 경기.
    // 출력: ShotFired.
    private static ShotFired LastShot(SandboxHarness h)
    {
        PacketReader r = SandboxHarness.Body(h.Packets.Last(s => s.Id == PacketId.ShotFired));
        Assert.True(ShotFired.TryRead(ref r, out ShotFired shot));
        return shot;
    }

    [Fact]
    public void Sniper_StaysExact_90Damage()
    {
        var h = Harness(Loadout(Sniper, Ar, Shotgun));
        PlayerEntity[] p = Two(h, SandboxHarness.Ground(2.5f, -40f), SandboxHarness.Ground(2.5f, -5f));
        Vector3 aimAt = p[1].State.Position + Chest;
        h.Act(p[0], InputButtons.Fire, aimAt);
        ShotFired shot = LastShot(h);
        Vector3 aim = Vector3.Normalize(aimAt - shot.Start);
        Vector3 travelled = shot.End - shot.Start;
        Assert.True(Vector3.Distance(Vector3.Normalize(travelled), aim) < 1e-4f, $"{shot.Start} {shot.End} {aim}");   // no spread
        Assert.Equal(10, p[1].Health);
        PacketReader r = SandboxHarness.Body(Assert.Single(h.To(1, PacketId.HitConfirmed)));
        Assert.True(HitConfirmed.TryRead(ref r, out HitConfirmed hit));
        Assert.Equal(90, hit.Damage);
    }

    [Fact]
    public void AssaultRifle_SpreadsInsideOneDegree_AndFallsOffWithDistance()
    {
        var h = Harness(Loadout(Ar, Sniper, Shotgun));
        PlayerEntity shooter = h.Join(1, SandboxHarness.Ground(2.5f, -60f));
        Vector3 aimAt = new(2.5f, 1.6f, 60f);
        float maxAngle = 0f;
        var ends = new System.Collections.Generic.List<Vector3>();
        for (int i = 0; i < 20; i++)
        {
            h.Ticks(3);
            h.Act(shooter, InputButtons.Fire, aimAt);
            h.Act(shooter, InputButtons.None, aimAt);
            ShotFired shot = LastShot(h);
            Vector3 aim = Vector3.Normalize(aimAt - shot.Start);
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(shot.End - shot.Start), aim), -1f, 1f)) * 180f / MathF.PI;
            Assert.True(angle <= 1.001f, $"angle {angle}");
            maxAngle = MathF.Max(maxAngle, angle);
            ends.Add(shot.End);
        }
        Assert.True(maxAngle > 0.05f);                        // the server does spread the AR
        Assert.True(ends.Distinct().Count() > 1);

        // Falloff: 20 damage up to 50 m, 0.7 x 20 = 14 at the 150 m range.
        WeaponDefinition ar = Data().Weapons[0];
        Assert.Equal(20, CombatRules.ScaledDamage(ar.Damage * CombatRules.FalloffMultiplier(30f, ar.FalloffStart, ar.Range, ar.FalloffMinRatio), 1f));
        Assert.Equal(17, CombatRules.ScaledDamage(ar.Damage * CombatRules.FalloffMultiplier(100f, ar.FalloffStart, ar.Range, ar.FalloffMinRatio), 1f));
        Assert.Equal(14, CombatRules.ScaledDamage(ar.Damage * CombatRules.FalloffMultiplier(150f, ar.FalloffStart, ar.Range, ar.FalloffMinRatio), 1f));
    }

    [Fact]
    public void TheSameShots_GiveTheSameRays()
    {
        Vector3 End()
        {
            var h = Harness(Loadout(Smg, Ar, Shotgun));
            PlayerEntity shooter = h.Join(1, SandboxHarness.Ground(2.5f, -60f));
            h.Ticks(10);
            h.Act(shooter, InputButtons.Fire, new Vector3(2.5f, 1.6f, 60f));
            return LastShot(h).End;
        }
        Assert.Equal(End(), End());
    }

    [Fact]
    public void Shotgun_PointBlank_OneHitConfirmed_WithEveryPelletSummed()
    {
        var h = Harness(Loadout(Ar, Shotgun, Rocket));
        PlayerEntity[] p = Two(h, new Vector3(2.5f, 0f, -10f), new Vector3(2.5f, 0f, -7f));
        h.Act(p[0], InputButtons.Fire | InputButtons.Slot2, p[1].State.Position + Chest);
        PacketReader r = SandboxHarness.Body(Assert.Single(h.To(1, PacketId.HitConfirmed)));
        Assert.True(HitConfirmed.TryRead(ref r, out HitConfirmed hit));
        Assert.Equal(88, hit.Damage);                         // 8 pellets x 11, rarity applied once
        Assert.Single(h.To(2, PacketId.DamageTaken));
        Assert.Single(h.Packets, s => s.Id == PacketId.ShotFired && s.PeerId == 1);   // one tracer per trigger pull
        Assert.Equal(12, p[1].Health);
        Assert.Equal(4, p[0].Inventory.Slots[1].MagAmmo);      // one shell for eight pellets
    }

    [Fact]
    public void Shotgun_DoesLessFartherAway()
    {
        var h = Harness(Loadout(Ar, Shotgun, Rocket));
        PlayerEntity[] p = Two(h, new Vector3(2.5f, 0f, -22f), new Vector3(2.5f, 0f, -7f));
        h.Act(p[0], InputButtons.Fire | InputButtons.Slot2, p[1].State.Position + Chest);
        int taken = 100 - p[1].Health;
        Assert.InRange(taken, 0, 74);   // pellets miss and the hits fall off (15 m: 0.82 x 11 each)
        Assert.True(h.To(1, PacketId.HitConfirmed).Count() <= 1);
    }

    [Fact]
    public void Shotgun_ShotFired_EndsOnTheUnspreadAim()
    {
        var h = Harness(Loadout(Shotgun, Ar, Rocket));
        PlayerEntity shooter = h.Join(1, new Vector3(2.5f, 0f, -20f));
        Vector3 aimAt = new(2.5f, 1.6f, 10f);
        h.Act(shooter, InputButtons.Fire, aimAt);
        ShotFired shot = LastShot(h);
        Vector3 aim = Vector3.Normalize(aimAt - shot.Start);
        Assert.True(Vector3.Distance(Vector3.Normalize(shot.End - shot.Start), aim) < 1e-4f);
        Assert.Equal(35f, Vector3.Distance(shot.Start, shot.End), 2);   // nothing in the way: the range
    }

    // D11: a wood wall (structure multiplier 1.0) takes damage x the weapon's structureMultiplier.
    [Theory]
    [InlineData(Ar, 20)]          // 20 x 1.0
    [InlineData(Sniper, 135)]     // 90 x 1.5
    [InlineData(Smg, 10)]         // round(12 x 0.8 = 9.6)
    [InlineData(Pistol, 19)]      // round(24 x 0.8 = 19.2)
    [InlineData(Shotgun, 53)]     // round(88 x 0.6 = 52.8): the pellets summed first
    public void StructureDamage_UsesTheWeaponMultiplier(byte weapon, int expected)
    {
        var h = Harness(Loadout(weapon, Ar, Rocket));
        h.Ticks(160);
        uint wall = h.Match.Build.Add(new BuildPieceShape(BuildPieceType.Wall, 16, 0, 16, 0), BuildMaterialType.Metal, 99, 0, grounded: true);
        PlayerEntity shooter = h.Join(1, new Vector3(2.5f, 0f, -2f));
        h.Act(shooter, InputButtons.Fire, new Vector3(2.5f, 1.5f, 0f));
        Assert.True(h.Match.Build.TryGetSlot(wall, out int slot));
        Assert.Equal(expected, h.Match.Build.At(slot).Damage);
    }

    [Fact]
    public void Rarity_ScalesThePelletSumOnce()
    {
        var h = Harness(Loadout(Shotgun, Ar, Rocket, rarity: 4));
        PlayerEntity[] p = Two(h, new Vector3(2.5f, 0f, -10f), new Vector3(2.5f, 0f, -7f));
        p[1].Shield = 100;
        h.Act(p[0], InputButtons.Fire, p[1].State.Position + Chest);
        PacketReader r = SandboxHarness.Body(Assert.Single(h.To(1, PacketId.HitConfirmed)));
        Assert.True(HitConfirmed.TryRead(ref r, out HitConfirmed hit));
        Assert.Equal(106, hit.Damage);   // round(88 x 1.2)
    }
}
