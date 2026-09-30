using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class BoxTests
{
    [Fact]
    public void FromCenterSize_ComputesMinMax()
    {
        var box = Box.FromCenterSize(new Vector3(0f, 0.5f, 12f), new Vector3(2f, 1f, 2f));
        Assert.Equal(new Vector3(-1f, 0f, 11f), box.Min);
        Assert.Equal(new Vector3(1f, 1f, 13f), box.Max);
        Assert.Equal(new Vector3(0f, 0.5f, 12f), box.Center);
        Assert.Equal(new Vector3(2f, 1f, 2f), box.Size);
    }
}
