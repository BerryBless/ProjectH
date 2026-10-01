using System;
using System.IO;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Zone;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Spec §1: zones.json loading and validation. A bad file stops the server at startup.
public class ZoneDataTests
{
    private static string Json(string phases, string initialRadius = "30", string center = "[0, 0]", string half = "19.5") => $$"""
        { "initialCenter": {{center}}, "initialRadius": {{initialRadius}}, "arenaHalfSize": {{half}}, "phases": [ {{phases}} ] }
        """;

    private static string Phase(int radius) => $$"""{"waitSeconds":1,"shrinkSeconds":1,"targetRadius":{{radius}},"damagePerSecond":1}, """;

    private const string Last = """{"waitSeconds":8,"shrinkSeconds":8,"targetRadius":0,"damagePerSecond":20}""";

    [Fact]
    public void ShippedFile_MatchesTheSpec()
    {
        // The server copies zones.json next to the executable; the test output gets it through the project reference.
        ZoneData zones = ZoneData.LoadFile(Path.Combine(AppContext.BaseDirectory, GameData.ZonesFile), 30);

        Assert.Equal(0f, zones.InitialCenter.X);
        Assert.Equal(0f, zones.InitialCenter.Y);
        Assert.Equal(30f, zones.InitialRadius);
        Assert.Equal(19.5f, zones.ArenaHalfSize);
        Assert.Equal(5, zones.PhaseCount);
        // D7 at 30 Hz: (wait, shrink) in ticks, target radius, damage per second.
        (uint Wait, uint Shrink, float Radius, int Damage)[] expected =
        {
            (600, 450, 20f, 1), (450, 360, 12f, 2), (360, 300, 6f, 5), (300, 240, 2f, 10), (240, 240, 0f, 20),
        };
        for (int i = 0; i < expected.Length; i++)
        {
            ZonePhase p = zones.Phase(i);
            Assert.Equal(expected[i].Wait, p.WaitTicks);
            Assert.Equal(expected[i].Shrink, p.ShrinkTicks);
            Assert.Equal(expected[i].Radius, p.TargetRadius);
            Assert.Equal(expected[i].Damage, p.DamagePerSecond);
        }
    }

    [Fact]
    public void TestCopy_IsTheShippedFile()
    {
        // TestGameData.ZonesJson stands in for the shipped file in match tests; both are spec §1.
        ZoneData shipped = ZoneData.LoadFile(Path.Combine(AppContext.BaseDirectory, GameData.ZonesFile), 30);
        ZoneData test = TestGameData.Zones();
        Assert.Equal(shipped.PhaseCount, test.PhaseCount);
        for (int i = 0; i < shipped.PhaseCount; i++) Assert.Equal(shipped.Phase(i), test.Phase(i));
    }

    [Fact]
    public void ShortTestZones_AreValid()
    {
        ZoneData zones = TestGameData.Zones(json: TestGameData.ShortZonesJson);
        Assert.Equal(2, zones.PhaseCount);
        Assert.Equal(30u, zones.Phase(0).WaitTicks);
    }

    [Fact]
    public void FirstTarget_MayEqualTheFirstCircle()
    {
        string json = Json("""{"waitSeconds":1,"shrinkSeconds":1,"targetRadius":30,"damagePerSecond":0}, """ + Last);
        Assert.True(ZoneData.TryParse(json, 30, out var zones, out string? error), error);
        Assert.Equal(30f, zones!.Phase(0).TargetRadius);
    }

    public static TheoryData<string, string> Invalid => new()
    {
        { Json(""), "phases" },                                                                               // empty list
        { Json(Phase(10) + Phase(12) + Last), "phases[1]" },                                                   // radius grows
        { Json(Phase(10) + Phase(10) + Last), "phases[1]" },                                                   // radius stays
        { Json("""{"waitSeconds":-1,"shrinkSeconds":1,"targetRadius":10,"damagePerSecond":1}, """ + Last), "waitSeconds" },     // negative time
        { Json("""{"waitSeconds":1,"shrinkSeconds":0,"targetRadius":10,"damagePerSecond":1}, """ + Last), "shrinkSeconds" },    // zero time
        { Json("""{"waitSeconds":1,"shrinkSeconds":1,"targetRadius":-1,"damagePerSecond":1}, """ + Last), "targetRadius" },     // negative radius
        { Json("""{"waitSeconds":1,"shrinkSeconds":1,"targetRadius":10,"damagePerSecond":-1}, """ + Last), "damagePerSecond" }, // negative damage
        { Json(Last, initialRadius: "0"), "initialRadius" },
        { Json("""{"waitSeconds":1,"shrinkSeconds":1,"targetRadius":31,"damagePerSecond":1}, """ + Last), "initialRadius" },  // first target above the first circle
        { Json("""{"waitSeconds":8,"shrinkSeconds":8,"targetRadius":1,"damagePerSecond":20}"""), "last phase" },           // never closes
        { Json("""{"waitSeconds":8,"shrinkSeconds":8,"targetRadius":0,"damagePerSecond":0}"""), "last phase" },            // never hurts
        { Json(Last, center: "[20, 0]"), "initialCenter" },                                                     // outside the arena bound
        { Json(Last, center: "[0]"), "initialCenter" },
        { Json(Last, half: "0"), "arenaHalfSize" },
        { "{ not json", "invalid JSON" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void InvalidData_IsRejected_WithAReason(string json, string reason)
    {
        Assert.False(ZoneData.TryParse(json, 30, out var zones, out string? error));
        Assert.Null(zones);
        Assert.Contains(reason, error);
    }

    [Fact]
    public void MoreThanSixteenPhases_IsRejected()
    {
        var phases = new System.Text.StringBuilder();
        for (int i = 0; i < ZoneData.MaxPhases; i++)
            phases.Append($$"""{"waitSeconds":1,"shrinkSeconds":1,"targetRadius":{{29 - i}},"damagePerSecond":1}, """);
        string json = Json(phases + Last);
        Assert.False(ZoneData.TryParse(json, 30, out _, out string? error));
        Assert.Contains("1-16", error);
    }

    [Fact]
    public void MissingFile_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => ZoneData.LoadFile(Path.Combine(AppContext.BaseDirectory, "no-zones.json"), 30));
    }

    [Fact]
    public void GameData_RejectsZonesBuiltForDifferentSimHz()
    {
        var items = TestGameData.Items();
        Assert.Throws<ArgumentException>(() =>
            new GameData(TestWeapons.Create(simHz: 30), items, TestGameData.Loot(items), TestGameData.Zones(simHz: 60)));
    }
}
