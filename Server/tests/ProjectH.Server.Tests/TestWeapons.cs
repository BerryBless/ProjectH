using System;
using System.Globalization;
using ProjectH.Server.Game.Combat;

namespace ProjectH.Server.Tests;

// In-memory catalog for tests, so no test depends on the shipped weapons.json numbers.
// At 30 Hz: id 1 "Test Auto"  = 30 damage, 3-tick interval, 6 rounds, 30-tick reload, Medium ammo;
//           id 2 "Test Semi"  = 90 damage, 15-tick interval, 2 rounds, 60-tick reload, Heavy ammo;
//           id 3 "Test Light" = 10 damage, 3-tick interval, 10 rounds, 15-tick reload, Light ammo.
// 30 damage makes shield 50 + health 100 exactly five hits.
// Phase 17: plus the shipped grenade (no weapon fires a projectile here; Phase 17 tests use the shipped weapons.json).
// Review fix C2: equipSeconds 0 unless a test asks for it, so the tests written before the equip wait keep their timing; the
// equip tests pass a wait or use the shipped weapons.json (0.4 s).
internal static class TestWeapons
{
    public const byte AutoId = 1;
    public const int AutoDamage = 30;
    public const int AutoInterval = 3;
    public const int AutoMagazine = 6;
    public const int AutoReload = 30;
    public const byte SemiId = 2;
    public const int SemiDamage = 90;
    public const int SemiInterval = 15;
    public const int SemiMagazine = 2;
    public const byte LightId = 3;
    public const int LightMagazine = 10;

    // 기능: 테스트 무기 카탈로그 JSON을 만든다.
    // 입력: autoRange - Test Auto 사거리, equipSeconds - 세 무기의 교체 대기(리뷰 수정 C2, 기본 0).
    // 출력: weapons.json 형식 문자열.
    public static string Json(float autoRange = 100f, float equipSeconds = 0f) => $$"""
        {
          "weapons": [
            { "id": 1, "name": "Test Auto", "damage": 30, "fireIntervalSeconds": 0.1, "magazineSize": 6,
              "reloadSeconds": 1.0, "range": {{autoRange.ToString(CultureInfo.InvariantCulture)}}, "automatic": true, "ammoType": "Medium",
              "equipSeconds": {{equipSeconds.ToString(CultureInfo.InvariantCulture)}} },
            { "id": 2, "name": "Test Semi", "damage": 90, "fireIntervalSeconds": 0.5, "magazineSize": 2,
              "reloadSeconds": 2.0, "range": 300, "automatic": false, "ammoType": "Heavy",
              "equipSeconds": {{equipSeconds.ToString(CultureInfo.InvariantCulture)}} },
            { "id": 3, "name": "Test Light", "damage": 10, "fireIntervalSeconds": 0.1, "magazineSize": 10,
              "reloadSeconds": 0.5, "range": 50, "automatic": true, "ammoType": "Light",
              "equipSeconds": {{equipSeconds.ToString(CultureInfo.InvariantCulture)}} }
          ],
          "projectiles": {
            "Grenade": { "speed": 18, "gravity": 9.81, "lifetimeSeconds": 3.0, "explosionRadius": 5, "explosionDamage": 80,
                         "structureDamage": 120, "bounce": 0.4, "throwIntervalSeconds": 1.0, "throwUpDegrees": 8 }
          }
        }
        """;

    // 기능: 테스트 무기 카탈로그를 만든다.
    // 입력: simHz - Tick 속도, autoRange - Test Auto 사거리, equipSeconds - 교체 대기(리뷰 수정 C2, 기본 0).
    // 출력: WeaponCatalog. 잘못되면 예외.
    public static WeaponCatalog Create(int simHz = 30, float autoRange = 100f, float equipSeconds = 0f)
    {
        if (!WeaponCatalog.TryParse(Json(autoRange, equipSeconds), simHz, out var catalog, out string? error))
            throw new InvalidOperationException("Test catalog is invalid: " + error);
        return catalog!;
    }
}
