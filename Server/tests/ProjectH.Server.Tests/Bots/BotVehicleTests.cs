using ProjectH.Bots;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Bots;

// Phase 19 D13, D16: the bots only keep VehicleStates for QA (an older tick is dropped) and get out of a car they entered.
public class BotVehicleTests
{
    [Fact]
    public void ApplyVehicles_FindsOurSeat_AndDropsAnOlderTick()
    {
        var view = new BotView { MyId = 7 };
        var records = new[]
        {
            new VehicleRecord { Id = 3, Driver = 9, Passenger = 7 },
            new VehicleRecord { Id = 4 },
        };
        Assert.True(view.ApplyVehicles(records, 2, 100));
        Assert.Equal(3, view.MyVehicleId);
        Assert.Equal(VehicleSettings.PassengerSeat, view.MySeat);
        Assert.Equal(2, view.VehicleCount);
        Assert.False(view.ApplyVehicles(new VehicleRecord[0], 0, 99));   // Unreliable: an older packet arrived late
        Assert.Equal(3, view.MyVehicleId);
        Assert.True(view.ApplyVehicles(new VehicleRecord[0], 0, 102));
        Assert.Equal(0, view.MyVehicleId);
        Assert.Equal(-1, view.MySeat);
        Assert.Equal(2, view.VehicleStatesReceived);
    }

    [Fact]
    public void ASeatedBot_PressesE_EveryOtherInput_AndDrivesNowhere()
    {
        var brain = new BotBrain(1);
        var view = new BotView { Joined = true, HasSnapshot = true, Alive = true, MyId = 7, MySeat = 0, MyVehicleId = 3 };
        Assert.True(brain.Tick(view, 0f, out InputCommand first));
        Assert.True(brain.Tick(view, 0.05f, out InputCommand second));
        Assert.NotEqual(first.Buttons & InputButtons.Interact, second.Buttons & InputButtons.Interact);
        Assert.Equal(0f, first.MoveY);
        Assert.Equal(0f, second.MoveY);
        Assert.Equal(InputButtons.None, (first.Buttons | second.Buttons) & ~InputButtons.Interact);
    }
}
