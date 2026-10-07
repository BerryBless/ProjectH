using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ProjectH.Server.Qa;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Qa;

// Phase 16 D10: the QA observation of containers, supply drops and world items, and the setContainer / spawnSupplyDrop commands.
public sealed class QaLootTests
{
    [Fact]
    public async Task SetContainer_SpawnSupplyDrop_AndTheLootView()
    {
        using var h = new QaHarness();
        h.StartMatch();

        var (status, body) = await h.Command("setContainer", args: new { container = 14, state = "closed" });
        Assert.Equal(200, status);
        Assert.Equal(3, QaHarness.Result(body).GetProperty("loot").GetInt32());
        (status, _) = await h.Command("setContainer", args: new { container = 14, state = "ajar" });
        Assert.Equal(400, status);
        (status, _) = await h.Command("setContainer", args: new { container = LootContainers.Count, state = "open" });
        Assert.Equal(400, status);

        (status, body) = await h.Command("spawnSupplyDrop", args: new { x = 6, z = 26 });
        Assert.Equal(200, status);
        Assert.Equal(0, QaHarness.Result(body).GetProperty("id").GetInt32());
        (status, _) = await h.Command("spawnSupplyDrop", args: new { x = 6 });
        Assert.Equal(400, status);
        (status, _) = await h.Command("spawnSupplyDrop");   // the server's spot rule
        Assert.Equal(200, status);

        var (_, loot) = await h.Run(t => QaResult.Data(QaQueries.Loot(t.Match, 15f, 16f, 3f, QaQueries.MaxLootItems)));
        JsonElement chest = loot.GetProperty("containers")[14];
        Assert.Equal("Chest", chest.GetProperty("kind").GetString());
        Assert.Equal("closed", chest.GetProperty("state").GetString());
        Assert.Equal("Weapon", chest.GetProperty("loot")[0].GetProperty("kind").GetString());
        Assert.Equal(2, loot.GetProperty("supplyDropCount").GetInt32());
        Assert.Equal("Falling", loot.GetProperty("supplyDrops")[0].GetProperty("state").GetString());
        Assert.Equal(4, loot.GetProperty("supplyDrops")[0].GetProperty("loot").GetArrayLength());
        Assert.Equal(0, loot.GetProperty("items").GetProperty("count").GetInt32());

        var (_, match) = await h.Run(t => QaResult.Data(QaQueries.Match(t.Match, 30)));
        Assert.Equal(2, match.GetProperty("supplyDrops").GetInt32());
        Assert.True(match.GetProperty("containersSpawned").GetInt32() >= 1);

        (status, _) = await h.Command("setContainer", args: new { container = 14, state = "open" });
        Assert.Equal(200, status);
        h.Loop.RunTick();
        var (_, health) = await h.Run(t => QaResult.Data(QaQueries.Health(t)));
        Assert.Equal(2, health.GetProperty("loot").GetProperty("dropsSpawned").GetInt64());
        Assert.True(health.GetProperty("loot").GetProperty("packets").GetInt64() > 0);
    }

    [Fact]
    public async Task Commands_AreRefused_OutsideTheMatch()
    {
        using var h = new QaHarness();
        var (status, _) = await h.Command("spawnSupplyDrop");
        Assert.Equal(409, status);
        (status, _) = await h.Command("setContainer", args: new { container = 0, state = "closed" });
        Assert.Equal(409, status);
        Assert.Contains("setContainer", QaCommands.Names);
        Assert.Contains("spawnSupplyDrop", QaCommands.Names);
    }
}
