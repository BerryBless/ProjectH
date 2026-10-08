using System;
using System.IO;
using ProjectH.Client.Game;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Vehicles;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 19 D12: vehicles.json loading and validation, and the client's display copy of maxHealth.
public class VehicleCatalogTests
{
    [Fact]
    public void TheShippedFile_EqualsTheDefault_AndTheSpecNumbers()
    {
        VehicleCatalog file = VehicleCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, VehicleCatalog.FileName), 30);
        VehicleCatalog d = VehicleCatalog.Default(30);
        Assert.Equal(d.MaxHealth, file.MaxHealth);
        Assert.Equal(d.ImpactMinSpeed, file.ImpactMinSpeed);
        Assert.Equal(d.ImpactDamagePerMps, file.ImpactDamagePerMps);
        Assert.Equal(d.RunOverMinSpeed, file.RunOverMinSpeed);
        Assert.Equal(d.RunOverDamagePerMps, file.RunOverDamagePerMps);
        Assert.Equal(d.RunOverCooldownTicks, file.RunOverCooldownTicks);
        Assert.Equal(d.WreckTicks, file.WreckTicks);
        Assert.Equal(d.WreckOccupantDamage, file.WreckOccupantDamage);
        Assert.Equal(d.WreckCreditTicks, file.WreckCreditTicks);
        Assert.Equal(d.InterestRange, file.InterestRange);
        Assert.Equal(400, d.MaxHealth);
        Assert.Equal(8f, d.ImpactMinSpeed);
        Assert.Equal(6f, d.ImpactDamagePerMps);
        Assert.Equal(6f, d.RunOverMinSpeed);
        Assert.Equal(2f, d.RunOverDamagePerMps);
        Assert.Equal(30u, d.RunOverCooldownTicks);
        Assert.Equal(150u, d.WreckTicks);
        Assert.Equal(25, d.WreckOccupantDamage);
        Assert.Equal(300u, d.WreckCreditTicks);
        Assert.Equal(120f, d.InterestRange);
    }

    [Fact]
    public void TheClientsDisplayCopy_EqualsTheDefault()
    {
        Assert.Equal(VehiclePrompt.MaxHealth, VehicleCatalog.Default(30).MaxHealth);
    }

    [Theory]
    [InlineData("maxHealth", "0")]
    [InlineData("maxHealth", "70000")]
    [InlineData("impactMinSpeed", "-1")]
    [InlineData("runOverCooldownSeconds", "0")]
    [InlineData("wreckSeconds", "0")]
    [InlineData("wreckOccupantDamage", "-5")]
    [InlineData("wreckCreditSeconds", "0")]
    [InlineData("interestRange", "5")]
    public void OutOfRangeValues_AreRefused(string field, string value)
    {
        string json = VehicleCatalog.DefaultJson;
        int at = json.IndexOf($"\"{field}\":", StringComparison.Ordinal);
        int end = json.IndexOfAny(new[] { ',', '\n' }, at + field.Length + 3);
        json = json[..(at + field.Length + 3)] + " " + value + json[end..];
        Assert.False(VehicleCatalog.TryParse(json, 30, out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void AMissingFile_OrBadJson_StopsTheServer()
    {
        Assert.Throws<InvalidOperationException>(() => VehicleCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, "no-such-vehicles.json"), 30));
        Assert.False(VehicleCatalog.TryParse("{", 30, out _, out _));
        Assert.False(VehicleCatalog.TryParse("{}", 30, out _, out _));
    }

    [Fact]
    public void GameData_RefusesVehicleDataOfAnotherSimHz()
    {
        Assert.Throws<ArgumentException>(() => TestGameData.Create(vehicles: VehicleCatalog.Default(60)));
    }
}
