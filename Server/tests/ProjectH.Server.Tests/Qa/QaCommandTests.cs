using System;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Server.Qa;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Qa;

// QA-1 §22: every command on a real match, run by the game loop's tick (D5), with its argument checks.
public sealed class QaCommandTests
{
    [Fact]
    public async Task Mark_Answers_TheTick()
    {
        using var h = new QaHarness();
        var (status, body) = await h.Command("mark", args: new { text = "step 3" });
        Assert.Equal(200, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(400, (await h.Command("mark")).Status);
        Assert.Equal(400, (await h.Command("mark", args: new { text = new string('x', 600) })).Status);
    }

    [Fact]
    public async Task UnknownCommand_MissingOrUnknownPlayer()
    {
        using var h = new QaHarness();
        h.Join(1, "qa-a");
        Assert.Equal(400, (await h.Command("teleportEverywhere", "qa-a")).Status);
        Assert.Equal(400, (await h.Command("setHealth", null, new { value = 50 })).Status);
        var (status, body) = await h.Command("setHealth", "qa-nobody", new { value = 50 });
        Assert.Equal(404, status);
        Assert.False(body.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task SetPosition_Teleports_WithoutAMovementAnomaly()
    {
        using var h = new QaHarness();
        PlayerEntity a = h.Join(1, "qa-a");
        h.Ticks(3);
        a.State.HorizontalVelocity = new Vector2(5f, 0f);

        var (status, body) = await h.Command("setPosition", "qa-a", new { x = 40, z = "-30", yaw = 90 });
        Assert.Equal(200, status);
        float ground = GameMap.Terrain.Height(40f, -30f);
        Assert.Equal(new Vector3(40f, ground, -30f), a.State.Position);
        Assert.Equal(Vector2.Zero, a.State.HorizontalVelocity);
        Assert.Equal(MovementMode.Ground, a.State.Mode);
        Assert.Equal(90f, a.State.Yaw);
        // The lag compensation history starts at the new place (a rewind never reaches the old one).
        Assert.Equal(a.State.Position, a.History.Sample(h.Match.ServerTick - 5));
        Assert.Equal(ground, HarnessResult(body).GetProperty("y").GetSingle());

        h.Ticks(10);
        Assert.Equal(0, h.Loop.Health.MovementAnomalies);
        Assert.Equal(0, h.Match.MovementAnomalies);

        Assert.Equal(400, (await h.Command("setPosition", "qa-a", new { x = 500, z = 0 })).Status);
        Assert.Equal(400, (await h.Command("setPosition", "qa-a", new { x = "NaN", z = 0 })).Status);
        Assert.Equal(400, (await h.Command("setPosition", "qa-a", new { x = 1 })).Status);
        Assert.Equal(400, (await h.Command("setPosition", "qa-a", new { x = "abc", z = 0 })).Status);
    }

    [Fact]
    public async Task SetPosition_IntoAMapBox_IsRefused()
    {
        using var h = new QaHarness();
        PlayerEntity a = h.Join(1, "qa-a");
        // The first boxes are the outer walls; take a building box inside the map that a standing body would overlap.
        Box box = default;
        bool found = false;
        foreach (Box b in GameMap.Boxes)
        {
            Vector3 c = b.Center;
            if (Math.Abs(c.X) >= GameMap.HalfSize - 1 || Math.Abs(c.Z) >= GameMap.HalfSize - 1) continue;
            Vector3 feet = new(c.X, GameMap.Terrain.Height(c.X, c.Z), c.Z);
            if (!MovementSimulation.OverlapsAny(feet, GameMap.Boxes)) continue;
            box = b;
            found = true;
            break;
        }
        Assert.True(found);
        Vector3 before = a.State.Position;
        var (status, _) = await h.Command("setPosition", "qa-a", new { x = box.Center.X, z = box.Center.Z });
        Assert.Equal(409, status);
        Assert.Equal(before, a.State.Position);
        h.Ticks(5);
        Assert.Equal(0, h.Match.MovementAnomalies);
    }

    // 기능: 명령 응답 본문에서 result 항목을 꺼낸다(QaHarness.Result와 같다).
    // 입력: body - 명령 응답 본문.
    // 출력: body의 result 요소.
    private static JsonElement HarnessResult(JsonElement body) => QaHarness.Result(body);

    [Fact]
    public async Task SetHealth_SetShield_Ranges()
    {
        using var h = new QaHarness();
        PlayerEntity a = h.Join(1, "qa-a");
        Assert.Equal(200, (await h.Command("setHealth", "qa-a", new { value = 37 })).Status);
        Assert.Equal(37, a.Health);
        Assert.Equal(200, (await h.Command("setShield", "qa-a", new { value = 80 })).Status);
        Assert.Equal(80, a.Shield);
        Assert.Equal(400, (await h.Command("setHealth", "qa-a", new { value = 0 })).Status);
        Assert.Equal(400, (await h.Command("setHealth", "qa-a", new { value = 101 })).Status);
        Assert.Equal(400, (await h.Command("setHealth", "qa-a", new { value = 50.5 })).Status);
        Assert.Equal(400, (await h.Command("setShield", "qa-a", new { value = -1 })).Status);
        Assert.Equal(37, a.Health);
    }

    [Fact]
    public async Task GiveWeapon_ByNameOrId_FullMagazine_Selected()
    {
        using var h = new QaHarness();
        PlayerEntity a = h.Join(1, "qa-a");
        h.Ticks(1);
        var (status, body) = await h.Command("giveWeapon", "qa-a", new { weapon = "test semi", rarity = "Epic", slot = 2 });
        Assert.Equal(200, status);
        HeldWeapon held = a.Inventory.Slots[2];
        Assert.Equal(TestWeapons.SemiId, held.Weapon!.Id);
        Assert.Equal(3, held.Rarity);
        Assert.Equal(TestWeapons.SemiMagazine, held.MagAmmo);
        Assert.Equal(2, a.Inventory.CurrentSlot);
        Assert.Equal(2, QaHarness.Result(body).GetProperty("slot").GetInt32());

        // By id, not selected: the first empty slot.
        Assert.Equal(200, (await h.Command("giveWeapon", "qa-a", new { weapon = 1, select = false })).Status);
        Assert.Equal(TestWeapons.AutoId, a.Inventory.Slots[0].Weapon!.Id);
        Assert.Equal(2, a.Inventory.CurrentSlot);

        Assert.Equal(400, (await h.Command("giveWeapon", "qa-a", new { weapon = "Laser" })).Status);
        Assert.Equal(400, (await h.Command("giveWeapon", "qa-a", new { weapon = 1, rarity = 9 })).Status);
        Assert.Equal(400, (await h.Command("giveWeapon", "qa-a", new { weapon = 1, slot = 3 })).Status);
        Assert.Equal(400, (await h.Command("giveWeapon", "qa-a")).Status);
    }

    [Fact]
    public async Task GiveAmmo_Item_Resource_ClampToTheirLimits()
    {
        using var h = new QaHarness();
        PlayerEntity a = h.Join(1, "qa-a");
        Assert.Equal(200, (await h.Command("giveAmmo", "qa-a", new { type = "medium", amount = 9999 })).Status);
        Assert.Equal(TestGameData.MediumMax, a.Inventory.GetAmmo(AmmoType.Medium));
        Assert.Equal(200, (await h.Command("giveItem", "qa-a", new { item = "shieldCell", count = 2 })).Status);
        Assert.Equal(2, a.Inventory.ShieldCells);
        Assert.Equal(200, (await h.Command("giveItem", "qa-a", new { item = "MEDKIT", count = 50 })).Status);
        Assert.Equal(TestGameData.MedkitMaxStack, a.Inventory.Medkits);
        Assert.Equal(200, (await h.Command("giveResource", "qa-a", new { material = "stone", amount = 120 })).Status);
        Assert.Equal(120, a.Inventory.Resource(BuildMaterialType.Stone));

        // Phase 17: rockets, shells and grenades exist now.
        Assert.Equal(200, (await h.Command("giveAmmo", "qa-a", new { type = "rockets", amount = 99 })).Status);
        Assert.Equal(6, a.Inventory.GetAmmo(AmmoType.Rockets));
        Assert.Equal(200, (await h.Command("giveAmmo", "qa-a", new { type = "shells", amount = 3 })).Status);
        Assert.Equal(3, a.Inventory.GetAmmo(AmmoType.Shells));
        Assert.Equal(200, (await h.Command("giveGrenade", "qa-a", new { count = 50 })).Status);
        Assert.Equal(6, a.Inventory.Grenades);
        Assert.Equal(200, (await h.Command("giveItem", "qa-a", new { item = "medkit", count = 1 })).Status);
        Assert.Equal(6, a.Inventory.Grenades);   // never into another stack
        Assert.Equal(400, (await h.Command("giveAmmo", "qa-a", new { type = "plasma", amount = 1 })).Status);
        Assert.Equal(400, (await h.Command("giveAmmo", "qa-a", new { type = "light", amount = 0 })).Status);
        Assert.Equal(400, (await h.Command("giveItem", "qa-a", new { item = "bandage", count = 1 })).Status);
        Assert.Equal(400, (await h.Command("giveResource", "qa-a", new { material = "gold", amount = 1 })).Status);
    }

    [Fact]
    public async Task DamageAndKill_UseTheRealDeathPath()
    {
        using var h = new QaHarness();
        var (a, b) = h.StartMatch();
        a.Shield = 20;
        var (status, body) = await h.Command("damagePlayer", "qa-a", new { amount = 50 });
        Assert.Equal(200, status);
        Assert.Equal(0, a.Shield);
        Assert.Equal(70, a.Health);

        Assert.Equal(200, (await h.Command("killPlayer", "qa-b")).Status);
        Assert.False(b.Alive);
        Assert.Equal(2, b.Placement);   // the first of two eliminated
        Assert.Equal(409, (await h.Command("killPlayer", "qa-b")).Status);
        Assert.Equal(409, (await h.Command("setHealth", "qa-b", new { value = 50 })).Status);
        h.Ticks(1);
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);   // one participant left
        Assert.Equal(a.EntityId, h.Match.WinnerId);
        Assert.Equal(0, h.Loop.Health.TickFailures);
    }

    [Fact]
    public async Task ForceMatchState_StartAndFinish()
    {
        using var h = new QaHarness();
        h.Join(1, "qa-a");
        Assert.Equal(409, (await h.Command("forceMatchState", args: new { state = "start" })).Status);   // one player, MinPlayers 2
        h.Join(2, "qa-b");
        Assert.Equal(200, (await h.Command("forceMatchState", args: new { state = "start" })).Status);
        h.Ticks(1);
        Assert.True(h.Match.Flow.InMatch);
        Assert.Equal(409, (await h.Command("forceMatchState", args: new { state = "start" })).Status);

        Assert.Equal(200, (await h.Command("forceMatchState", args: new { state = "finish" })).Status);
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        Assert.Equal(409, (await h.Command("forceMatchState", args: new { state = "finish" })).Status);
        Assert.Equal(400, (await h.Command("forceMatchState", args: new { state = "pause" })).Status);
    }

    [Fact]
    public async Task ForceStart_DuringTheCountdown_StartsNextTick()
    {
        using var h = new QaHarness();
        h.Join(1, "qa-a");
        h.Join(2, "qa-b");
        h.Ticks(2);
        Assert.Equal(MatchFlowState.Starting, h.Match.Flow.State);
        Assert.Equal(200, (await h.Command("forceMatchState", args: new { state = "start" })).Status);
        h.Ticks(1);
        Assert.Equal(MatchFlowState.Playing, h.Match.Flow.State);
    }

    [Fact]
    public async Task SetZone_AdvancesPhases_OnlyInAMatch()
    {
        using var h = new QaHarness();
        h.Join(1, "qa-a");
        h.Join(2, "qa-b");
        Assert.Equal(409, (await h.Command("setZone", args: new { advance = true })).Status);
        for (int i = 0; i < 200 && !h.Match.Flow.InMatch; i++) h.Loop.RunTick();
        Assert.Equal(1, h.Match.Zone.Phase);

        var (status, body) = await h.Command("setZone", args: new { advance = true });
        Assert.Equal(200, status);
        Assert.Equal(2, h.Match.Zone.Phase);
        Assert.Equal(2, QaHarness.Result(body).GetProperty("phase").GetInt32());

        Assert.Equal(200, (await h.Command("setZone", args: new { phase = 5 })).Status);
        Assert.Equal(5, h.Match.Zone.Phase);
        Assert.Equal(MatchFlowState.FinalPhase, h.Match.Flow.State);
        Assert.Equal(409, (await h.Command("setZone", args: new { advance = true })).Status);
        Assert.Equal(400, (await h.Command("setZone", args: new { phase = 9 })).Status);
        Assert.Equal(400, (await h.Command("setZone")).Status);
        h.Ticks(3);
        Assert.Equal(0, h.Loop.Health.TickFailures);
    }

    [Fact]
    public async Task SpawnLoot_PutsAnItemInTheWorld()
    {
        using var h = new QaHarness();
        int before = h.Match.WorldItems.Count;
        var (status, body) = await h.Command("spawnLoot", args: new { kind = "weapon", id = "Test Auto", rarity = 4, x = 3, z = 4 });
        Assert.Equal(200, status);
        int itemId = QaHarness.Result(body).GetProperty("itemId").GetInt32();
        Assert.Equal(before + 1, h.Match.WorldItems.Count);
        int index = h.Match.WorldItems.IndexOf((ushort)itemId);
        WorldItemData item = h.Match.WorldItems[index].Data;
        Assert.Equal(ItemKind.Weapon, item.Kind);
        Assert.Equal(4, item.Rarity);
        Assert.Equal(TestWeapons.AutoMagazine, item.Amount);
        Assert.Equal(GameMap.Terrain.Height(3f, 4f), item.Position.Y);

        Assert.Equal(200, (await h.Command("spawnLoot", args: new { kind = "ammo", id = "heavy", x = 0, z = 0 })).Status);
        Assert.Equal(200, (await h.Command("spawnLoot", args: new { kind = "medkit", amount = 2, x = 0, z = 0 })).Status);
        Assert.Equal(200, (await h.Command("spawnLoot", args: new { kind = "material", id = "wood", x = 0, z = 0 })).Status);
        Assert.Equal(400, (await h.Command("spawnLoot", args: new { kind = "ammo", x = 0, z = 0 })).Status);
        Assert.Equal(400, (await h.Command("spawnLoot", args: new { kind = "pizza", x = 0, z = 0 })).Status);
        Assert.Equal(400, (await h.Command("spawnLoot", args: new { kind = "medkit", x = 0 })).Status);
    }

    [Fact]
    public async Task SpawnAndDamageBuild_UseThePieceRules()
    {
        using var h = new QaHarness();
        var (status, body) = await h.Command("spawnBuildPiece", args: new { piece = "floor", material = "wood", cellX = 16, level = 0, cellZ = 16 });
        Assert.Equal(200, status);
        uint floor = QaHarness.Result(body).GetProperty("pieceId").GetUInt32();
        Assert.True(h.Match.Build.Contains(floor));
        // Same slot again: occupied. A wall standing on the floor is held by it.
        Assert.Equal(409, (await h.Command("spawnBuildPiece", args: new { piece = "floor", material = "wood", cellX = 16, level = 0, cellZ = 16 })).Status);
        var wall = await h.Command("spawnBuildPiece", args: new { piece = "wall", material = "stone", cellX = 16, level = 0, cellZ = 16, rotation = 0 });
        Assert.Equal(200, wall.Status);
        // Floating high above the map with nothing around: unsupported.
        Assert.Equal(409, (await h.Command("spawnBuildPiece", args: new { piece = "floor", material = "wood", cellX = 2, level = 12, cellZ = 2 })).Status);
        Assert.Equal(400, (await h.Command("spawnBuildPiece", args: new { piece = "floor", material = "wood", cellX = 40, level = 0, cellZ = 2 })).Status);
        Assert.Equal(400, (await h.Command("spawnBuildPiece", args: new { piece = "tower", material = "wood", cellX = 1, level = 0, cellZ = 1 })).Status);
        h.Ticks(1);
        Assert.Equal(2, h.Match.BuildPieces);

        var (damaged, damageBody) = await h.Command("damageBuild", args: new { pieceId = floor, amount = 5 });
        Assert.Equal(200, damaged);
        Assert.False(QaHarness.Result(damageBody).GetProperty("destroyed").GetBoolean());
        Assert.Equal(200, (await h.Command("damageBuild", args: new { pieceId = floor, amount = 100000 })).Status);
        Assert.False(h.Match.Build.Contains(floor));
        Assert.Equal(404, (await h.Command("damageBuild", args: new { pieceId = floor, amount = 1 })).Status);
        Assert.Equal(400, (await h.Command("damageBuild", args: new { pieceId = 0, amount = 1 })).Status);
        h.Ticks(2);
        Assert.Equal(0, h.Loop.Health.TickFailures);
    }

    [Fact]
    public async Task ManyPiecesBetweenTicks_AreAllReplicated()
    {
        // More placements in one tick than players (the replication list grows instead of dropping the event).
        using var h = new QaHarness();
        int placed = 0;
        var tasks = new System.Collections.Generic.List<Task<QaResult>>();
        for (int x = 15; x <= 17; x++)
        {
            for (int z = 15; z <= 17; z++)
            {
                JsonElement args = JsonSerializer.SerializeToElement(new { piece = "floor", material = "wood", cellX = x, level = 0, cellZ = z });
                tasks.Add(h.Qa.SubmitAsync(t => QaCommands.Execute(t, "spawnBuildPiece", null, null, args,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)));
                placed++;
            }
        }
        Assert.True(placed > h.Options.MaxPlayers);
        h.Ticks(1);
        foreach (Task<QaResult> task in tasks) Assert.Equal(200, (await task).Status);
        Assert.Equal(placed, h.Match.BuildPieces);
        Assert.Equal(placed, h.Match.Replication.PlacedCount);
        h.Ticks(1);
        Assert.Equal(0, h.Loop.Health.TickFailures);
    }
}
