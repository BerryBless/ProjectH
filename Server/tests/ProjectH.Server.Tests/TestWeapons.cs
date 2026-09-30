using System;
using System.Globalization;
using ProjectH.Server.Game.Combat;

namespace ProjectH.Server.Tests;

// In-memory catalog for tests, so no test depends on the shipped weapons.json numbers.
// At 30 Hz: slot 0 "Test Auto" = 30 damage, 3-tick interval, 6 rounds, 30-tick reload;
//           slot 1 "Test Semi" = 90 damage, 15-tick interval, 2 rounds, 60-tick reload.
// 30 damage makes shield 50 + health 100 exactly five hits.
internal static class TestWeapons
{
    public const int AutoDamage = 30;
    public const int AutoInterval = 3;
    public const int AutoMagazine = 6;
    public const int AutoReload = 30;
    public const int SemiDamage = 90;
    public const int SemiInterval = 15;
    public const int SemiMagazine = 2;

    public static string Json(float autoRange = 100f) => $$"""
        {
          "weapons": [
            { "id": 1, "name": "Test Auto", "damage": 30, "fireIntervalSeconds": 0.1, "magazineSize": 6,
              "reloadSeconds": 1.0, "range": {{autoRange.ToString(CultureInfo.InvariantCulture)}}, "automatic": true },
            { "id": 2, "name": "Test Semi", "damage": 90, "fireIntervalSeconds": 0.5, "magazineSize": 2,
              "reloadSeconds": 2.0, "range": 300, "automatic": false }
          ]
        }
        """;

    public static WeaponCatalog Create(int simHz = 30, float autoRange = 100f)
    {
        if (!WeaponCatalog.TryParse(Json(autoRange), simHz, out var catalog, out string? error))
            throw new InvalidOperationException("Test catalog is invalid: " + error);
        return catalog!;
    }
}
