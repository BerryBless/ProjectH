using System;
using System.IO;
using ProjectH.Server.Game.Combat;
using Xunit;

namespace ProjectH.Server.Tests.Game;

public class WeaponCatalogTests
{
    private static string One(string fields) => "{ \"weapons\": [ { " + fields + " } ] }";

    private const string ValidFields =
        "\"id\": 1, \"name\": \"Vesper AR\", \"damage\": 20, \"fireIntervalSeconds\": 0.1, \"magazineSize\": 30, " +
        "\"reloadSeconds\": 2.0, \"range\": 150, \"automatic\": true";

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
        Assert.Equal(2, catalog!.Count);
        Assert.Equal(2, catalog.LoadoutCount);
        Assert.Equal(30, catalog.SimHz);
        Assert.Equal("Test Auto", catalog[0].Name);
        Assert.Equal(3, catalog[0].FireIntervalTicks);
        Assert.Equal(30, catalog[0].ReloadTicks);
        Assert.Equal(15, catalog[1].FireIntervalTicks);
        Assert.Equal(60, catalog[1].ReloadTicks);
        Assert.False(catalog[1].Automatic);
        Assert.Equal(0f, catalog[1].Spread);
        Assert.Equal(2, catalog.WireInfos.Length);
        Assert.Equal("Test Semi", catalog.WireInfos[1].Name);
    }

    [Fact]
    public void HalfTick_RoundsAwayFromZero_AndTinyTimesBecomeOneTick()
    {
        string json = "{ \"weapons\": [ { \"id\": 1, \"name\": \"A\", \"damage\": 1, \"fireIntervalSeconds\": 1.25, " +
                      "\"magazineSize\": 1, \"reloadSeconds\": 0.001, \"range\": 1 } ] }";
        Assert.True(WeaponCatalog.TryParse(json, 30, out var catalog, out _));
        Assert.Equal(38, catalog![0].FireIntervalTicks);   // 37.5
        Assert.Equal(1, catalog[0].ReloadTicks);
        Assert.Equal(1, catalog.LoadoutCount);
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
    [InlineData("\"spread\": -1")]
    [InlineData("\"id\": 0")]
    [InlineData("\"id\": 256")]
    public void BadNumber_IsRejected(string overrideField)
    {
        // A later duplicate key overrides the valid value (System.Text.Json keeps the last one).
        Parse(One(ValidFields + ", " + overrideField));
    }

    [Fact]
    public void MissingField_IsRejected()
    {
        Parse(One("\"id\": 1, \"name\": \"A\", \"damage\": 20, \"magazineSize\": 30, \"reloadSeconds\": 2, \"range\": 150"));
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
    // must load and match spec D5.
    [Fact]
    public void ShippedWeaponsJson_MatchesSpec()
    {
        var catalog = WeaponCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, "weapons.json"), 30);

        Assert.Equal(2, catalog.Count);
        Assert.Equal("Vesper AR", catalog[0].Name);
        Assert.Equal(20, catalog[0].Damage);
        Assert.Equal(3, catalog[0].FireIntervalTicks);    // 10 rounds/s
        Assert.Equal(30, catalog[0].MagazineSize);
        Assert.Equal(60, catalog[0].ReloadTicks);         // 2.0 s
        Assert.Equal(150f, catalog[0].Range);
        Assert.True(catalog[0].Automatic);

        Assert.Equal("Kestrel LR", catalog[1].Name);
        Assert.Equal(90, catalog[1].Damage);
        Assert.Equal(38, catalog[1].FireIntervalTicks);   // 1.25 s
        Assert.Equal(5, catalog[1].MagazineSize);
        Assert.Equal(75, catalog[1].ReloadTicks);         // 2.5 s
        Assert.Equal(300f, catalog[1].Range);
        Assert.False(catalog[1].Automatic);
    }
}
