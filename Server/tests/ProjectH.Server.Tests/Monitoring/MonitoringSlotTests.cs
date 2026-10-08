using ProjectH.Monitoring.Contracts;
using ProjectH.Server.Monitoring;

namespace ProjectH.Server.Tests.Monitoring;

// Monitoring D4: capacity 1, latest only.
public class MonitoringSlotTests
{
    // 기능: Round만 다른 최소 Snapshot을 만든다.
    // 입력: round - 구분용 값.
    // 출력: ServerId "s"인 Snapshot.
    private static ServerMonitoringSnapshot Snap(int round) => new() { ServerId = "s", Round = round };

    [Fact]
    public void Take_ReturnsTheLatest_AndEmptiesTheSlot()
    {
        var slot = new MonitoringSlot();
        Assert.Null(slot.Take());
        slot.Publish(Snap(1));
        Assert.Equal(1, slot.Take()!.Round);
        Assert.Null(slot.Take());
    }

    [Fact]
    public void Publish_ReplacesAnUnsentSnapshot_AndCountsIt()
    {
        var slot = new MonitoringSlot();
        slot.Publish(Snap(1));
        slot.Publish(Snap(2));
        slot.Publish(Snap(3));
        Assert.Equal(3, slot.Take()!.Round);
        Assert.Equal(2, slot.Overwritten);
        Assert.Null(slot.Take());
    }

    [Fact]
    public void ManyPublishes_NeverGrowBeyondOne()
    {
        var slot = new MonitoringSlot();
        for (int i = 0; i < 10_000; i++) slot.Publish(Snap(i));
        Assert.Equal(9_999, slot.Take()!.Round);
        Assert.Equal(9_999, slot.Overwritten);
    }
}
