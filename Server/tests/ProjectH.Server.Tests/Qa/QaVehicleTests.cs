using System.Text.Json;
using System.Threading.Tasks;
using ProjectH.Server.Game;
using ProjectH.Server.Qa;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Qa;

// Phase 19 D14: spawnVehicle, damageVehicle, GET /qa/vehicles and the player's vehicleId and seat.
public sealed class QaVehicleTests
{
    [Fact]
    public async Task SpawnVehicle_DamageVehicle_AndTheView()
    {
        using var h = new QaHarness();
        PlayerEntity a = h.Join(1, "qa-a");
        var (status, body) = await h.Command("spawnVehicle", args: new { x = 0, z = 0, heading = 90 });
        Assert.Equal(200, status);
        int id = QaHarness.Result(body).GetProperty("vehicleId").GetInt32();
        Assert.InRange(id, 1, 255);
        Assert.Equal(90f, QaHarness.Result(body).GetProperty("heading").GetSingle());
        // Over a map box: refused, nothing made.
        Assert.Equal(409, (await h.Command("spawnVehicle", args: new { x = 26, z = 0 })).Status);
        Assert.Equal(400, (await h.Command("spawnVehicle", args: new { x = 200, z = 0 })).Status);
        // Over a building piece (the south wall of cell 16, z 0): refused too.
        Assert.Equal(200, (await h.Command("spawnBuildPiece", args: new { piece = "wall", material = "wood", cellX = 16, level = 0, cellZ = 16 })).Status);
        Assert.Equal(409, (await h.Command("spawnVehicle", args: new { x = 2.5, z = 0 })).Status);

        var (_, view) = await h.Run(t => QaResult.Data(QaQueries.Vehicles(t.Match)));
        JsonElement data = view;
        Assert.Equal(1, data.GetProperty("count").GetInt32());
        JsonElement car = data.GetProperty(id.ToString());
        Assert.Equal("Active", car.GetProperty("state").GetString());
        Assert.Equal(400, car.GetProperty("health").GetInt32());

        (status, body) = await h.Command("damageVehicle", args: new { vehicleId = id, amount = 150 });
        Assert.Equal(200, status);
        Assert.Equal(250, QaHarness.Result(body).GetProperty("health").GetInt32());
        Assert.Equal(404, (await h.Command("damageVehicle", args: new { vehicleId = 200, amount = 1 })).Status);
        (status, body) = await h.Command("damageVehicle", args: new { vehicleId = id, amount = 1000 });
        Assert.Equal("Wrecked", QaHarness.Result(body).GetProperty("state").GetString());
        Assert.Equal(409, (await h.Command("damageVehicle", args: new { vehicleId = id, amount = 1 })).Status);

        var (_, player) = await h.Run(t => QaResult.Data(QaQueries.Player(t.Match, a)));
        Assert.Equal(0, player.GetProperty("vehicleId").GetInt32());
        Assert.Equal(-1, player.GetProperty("seat").GetInt32());
    }

    [Fact]
    public async Task SetPosition_TakesASeatedPlayerOutFirst()
    {
        using var h = new QaHarness();
        PlayerEntity a = h.Join(1, "qa-a");
        Assert.True(h.Match.SpawnVehicle(0f, 0f, 0f, out var v));
        h.Ticks(1);
        a.State = new MoveState { Position = new System.Numerics.Vector3(-2f, 0f, 0f) };
        var packet = new ProjectH.Shared.Protocol.PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = 1, Buttons = InputButtons.Interact });
        h.Match.EnqueueInput(1, packet);
        h.Ticks(1);
        Assert.Same(a, v!.Driver);
        var (status, _) = await h.Command("setPosition", "qa-a", new { x = 10, z = 10 });
        Assert.Equal(200, status);
        Assert.False(a.InVehicle);
        Assert.Null(v.Driver);
    }
}
