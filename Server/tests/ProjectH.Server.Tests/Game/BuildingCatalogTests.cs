using System;
using System.IO;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Build;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 13 D4: building.json. The shipped file holds the spec's numbers and equals the built-in default; a bad file is
// refused with a message naming the field.
public class BuildingCatalogTests
{
    private static BuildingCatalog Shipped() => BuildingCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, GameData.BuildingFile), 30);

    [Fact]
    public void TheShippedFile_HoldsTheSpecsNumbers()
    {
        BuildingCatalog c = Shipped();
        int[] health = { 150, 240, 360 };
        int[] ticks = { 45, 90, 150 };   // 1.5, 3 and 5 s at 30 Hz
        for (int m = 0; m < 3; m++)
        {
            BuildMaterialConfig material = c.Material((BuildMaterialType)m);
            Assert.Equal(10, material.ResourceCost);
            Assert.Equal(health[m], material.MaxHealth);
            Assert.Equal((int)MathF.Round(health[m] * 0.3f), material.InitialHealth);
            Assert.Equal(ticks[m], material.ConstructionTicks);
            Assert.Equal(1f, material.StructureDamageMultiplier);
            Assert.Equal(1f, material.HarvestToolDamageMultiplier);
        }
        Assert.Equal(500, c.MaxResource);
        Assert.Equal(2.5f, c.HarvestRange);
        Assert.Equal(12, c.HarvestCooldownTicks);
        Assert.Equal(25, c.EnvironmentDamage);
        Assert.Equal(50, c.HarvestStructureDamage);
        Assert.Equal(0.4f, c.WeakPointRadius);
        Assert.Equal(2, c.WeakPointMultiplier);
        Assert.Equal(150, c.Harvestable(HarvestKind.Tree).Health);
        Assert.Equal(100, c.Harvestable(HarvestKind.Crate).Health);
        Assert.Equal(200, c.Harvestable(HarvestKind.Rock).Health);
        Assert.Equal(250, c.Harvestable(HarvestKind.Wreck).Health);
        Assert.Equal(6, c.Harvestable(HarvestKind.Tree).BaseResourcePerHit);
        Assert.Equal(5, c.Harvestable(HarvestKind.Rock).BaseResourcePerHit);
        Assert.Equal(4, c.Harvestable(HarvestKind.Wreck).BaseResourcePerHit);
        Assert.Equal(10, c.Harvestable(HarvestKind.Tree).DestroyBonus);
        Assert.Equal(7f, c.BuildRange);
        Assert.Equal(75f, c.ViewAngleDegrees);
        Assert.Equal(3, c.MinBuildIntervalTicks);
        Assert.Equal(20000, c.MaxPiecesPerMatch);
        Assert.Equal(500, c.MaxPiecesPerPlayer);
        Assert.Equal(20, c.MaxRequestsPerSecond);
        Assert.Equal(20f, c.InterestCellSize);
        Assert.Equal(2, c.InterestRadius);
        Assert.Equal(1, c.InterestKeepMargin);
    }

    [Fact]
    public void TheShippedFile_EqualsTheDefault()
    {
        BuildingCatalog a = Shipped();
        BuildingCatalog b = BuildingCatalog.Default(30);
        for (int m = 0; m < 3; m++)
        {
            BuildMaterialConfig x = a.Material((BuildMaterialType)m);
            BuildMaterialConfig y = b.Material((BuildMaterialType)m);
            Assert.Equal((x.ResourceCost, x.MaxHealth, x.InitialHealth, x.ConstructionTicks, x.StructureDamageMultiplier, x.HarvestToolDamageMultiplier),
                (y.ResourceCost, y.MaxHealth, y.InitialHealth, y.ConstructionTicks, y.StructureDamageMultiplier, y.HarvestToolDamageMultiplier));
        }
        for (int k = 0; k < 4; k++)
        {
            HarvestableConfig x = a.Harvestable((HarvestKind)k);
            HarvestableConfig y = b.Harvestable((HarvestKind)k);
            Assert.Equal((x.Health, x.BaseResourcePerHit, x.DestroyBonus), (y.Health, y.BaseResourcePerHit, y.DestroyBonus));
        }
        Assert.Equal((a.MaxResource, a.HarvestRange, a.HarvestCooldownTicks, a.EnvironmentDamage, a.HarvestStructureDamage, a.WeakPointRadius, a.WeakPointMultiplier),
            (b.MaxResource, b.HarvestRange, b.HarvestCooldownTicks, b.EnvironmentDamage, b.HarvestStructureDamage, b.WeakPointRadius, b.WeakPointMultiplier));
        Assert.Equal((a.BuildRange, a.ViewAngleDegrees, a.MinBuildIntervalTicks, a.MaxPiecesPerMatch, a.MaxPiecesPerPlayer, a.MaxRequestsPerSecond),
            (b.BuildRange, b.ViewAngleDegrees, b.MinBuildIntervalTicks, b.MaxPiecesPerMatch, b.MaxPiecesPerPlayer, b.MaxRequestsPerSecond));
        Assert.Equal((a.InterestCellSize, a.InterestRadius, a.InterestKeepMargin), (b.InterestCellSize, b.InterestRadius, b.InterestKeepMargin));
    }

    [Fact]
    public void GameData_LoadsTheBuildingFile_AndDefaultsWithoutOne()
    {
        GameData data = GameData.LoadDirectory(AppContext.BaseDirectory, 30);
        Assert.Equal(500, data.Building.MaxResource);
        Assert.Equal(500, TestGameData.Create().Building.MaxResource);
    }

    [Theory]
    [InlineData("\"type\": \"Wood\", \"resourceCost\": 10", "\"type\": \"Stone\", \"resourceCost\": 10", "materials[1]")]   // Stone twice
    [InlineData("\"maxHealth\": 150", "\"maxHealth\": 0", "maxHealth")]
    [InlineData("\"initialHealthRatio\": 0.3, \"constructionSeconds\": 1.5", "\"initialHealthRatio\": 1.5, \"constructionSeconds\": 1.5", "initialHealthRatio")]
    [InlineData("\"constructionSeconds\": 1.5", "\"constructionSeconds\": -1", "constructionSeconds")]
    [InlineData("\"maxResource\": 500", "\"maxResource\": 0", "maxResource")]
    [InlineData("\"range\": 2.5", "\"range\": 0", "harvestTool.range")]
    [InlineData("\"weakPointMultiplier\": 2", "\"weakPointMultiplier\": 0", "weakPointMultiplier")]
    [InlineData("{ \"kind\": \"Crate\", \"health\": 100", "{ \"kind\": \"Tree\", \"health\": 100", "harvestables[1]")]
    [InlineData("\"maxBuildPiecesPerPlayer\": 500", "\"maxBuildPiecesPerPlayer\": 30000", "maxBuildPiecesPerPlayer")]
    [InlineData("\"cellSize\": 20", "\"cellSize\": 30", "interest.cellSize")]
    [InlineData("\"cellSize\": 20", "\"cellSize\": 10", "interest.cellSize")]
    [InlineData("\"minimumBuildInterval\": 0.1", "\"minimumBuildInterval\": 0", "minimumBuildInterval")]
    public void ABadValue_IsRefused_WithItsName(string from, string to, string expected)
    {
        string json = BuildingCatalog.DefaultJson.Replace(from, to);
        Assert.NotEqual(BuildingCatalog.DefaultJson, json);
        Assert.False(BuildingCatalog.TryParse(json, 30, out _, out string? error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void BrokenJson_IsRefused()
    {
        Assert.False(BuildingCatalog.TryParse("{ not json", 30, out _, out string? error));
        Assert.Contains("invalid JSON", error);
        Assert.False(BuildingCatalog.TryParse(BuildingCatalog.DefaultJson, 0, out _, out _));
    }
}
