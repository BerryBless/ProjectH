using System;
using System.IO;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Map;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 15 D9: map.json loading and validation.
public class MapCatalogTests
{
    [Fact]
    public void TheShippedFile_EqualsTheDefault_AndTheSpec()
    {
        MapCatalog file = MapCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, MapCatalog.FileName), 30);
        MapCatalog d = MapCatalog.Default(30);
        Assert.Equal(d.PingTicks, file.PingTicks);
        Assert.Equal(d.EnemyPingTicks, file.EnemyPingTicks);
        Assert.Equal(d.PingsPerPlayer, file.PingsPerPlayer);
        Assert.Equal(d.PingsPerTeam, file.PingsPerTeam);
        Assert.Equal(d.EnemyPingRange, file.EnemyPingRange);
        Assert.Equal(d.ItemPingRange, file.ItemPingRange);
        Assert.Equal(d.PingsPerSecond, file.PingsPerSecond);
        Assert.Equal(d.PingBurst, file.PingBurst);
        Assert.Equal(d.MaxMarkerPacketsPerSecond, file.MaxMarkerPacketsPerSecond);
        // Spec D7-D9 at 30 Hz.
        Assert.Equal(240u, d.PingTicks);
        Assert.Equal(120u, d.EnemyPingTicks);
        Assert.Equal(3, d.PingsPerPlayer);
        Assert.Equal(8, d.PingsPerTeam);
        Assert.Equal(150f, d.EnemyPingRange);
        Assert.Equal(60f, d.ItemPingRange);
        Assert.Equal(2, d.PingsPerSecond);
        Assert.Equal(4, d.PingBurst);
        Assert.Equal(20, d.MaxMarkerPacketsPerSecond);
    }

    [Fact]
    public void GameData_LoadsMapJson()
    {
        GameData data = GameData.LoadDirectory(AppContext.BaseDirectory, 30);
        Assert.Equal(8, data.Map.PingsPerTeam);
    }

    [Theory]
    [InlineData("\"pingSeconds\": 8", "\"pingSeconds\": 0")]
    [InlineData("\"enemyPingSeconds\": 4", "\"enemyPingSeconds\": 61")]
    [InlineData("\"pingsPerPlayer\": 3", "\"pingsPerPlayer\": 9")]     // above pingsPerTeam
    [InlineData("\"pingsPerPlayer\": 3", "\"pingsPerPlayer\": 0")]
    [InlineData("\"pingsPerTeam\": 8", "\"pingsPerTeam\": 9")]         // above the TeamMarkers limit
    [InlineData("\"enemyPingRange\": 150", "\"enemyPingRange\": 0")]
    [InlineData("\"itemPingRange\": 60", "\"itemPingRange\": 1000")]
    [InlineData("\"pingsPerSecond\": 2", "\"pingsPerSecond\": 0")]
    [InlineData("\"pingBurst\": 4", "\"pingBurst\": 21")]
    [InlineData("\"maxMarkerPacketsPerSecond\": 20", "\"maxMarkerPacketsPerSecond\": 3")]   // below the burst
    public void InvalidValues_AreRefused(string from, string to)
    {
        string json = MapCatalog.DefaultJson.Replace(from, to);
        Assert.NotEqual(MapCatalog.DefaultJson, json);
        Assert.False(MapCatalog.TryParse(json, 30, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void MissingNumbers_AndBadJson_AreRefused()
    {
        Assert.False(MapCatalog.TryParse("{ \"pingSeconds\": 8 }", 30, out _, out _));
        Assert.False(MapCatalog.TryParse("{ not json", 30, out _, out _));
    }
}
