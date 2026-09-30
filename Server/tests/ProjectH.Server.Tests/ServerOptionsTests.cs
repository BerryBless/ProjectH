using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests;

public class ServerOptionsTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        Assert.Null(new ServerOptions().Validate());
        Assert.Equal(15, new ServerOptions().SnapshotHz);
    }

    [Fact]
    public void Validate_RejectsMaxPlayersAboveSnapshotLimit()
    {
        var options = new ServerOptions { MaxPlayers = ProtocolConstants.MaxSnapshotEntities + 1 };
        Assert.NotNull(options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    public void Validate_RejectsOutOfRangeSimHz(int simHz)
    {
        Assert.NotNull(new ServerOptions { SimHz = simHz }.Validate());
    }

    [Theory]
    [InlineData(30, 4)]
    [InlineData(30, 7)]
    public void Validate_RejectsSnapshotEveryTicksThatDoesNotDivideSimHz(int simHz, int snapshotEveryTicks)
    {
        Assert.NotNull(new ServerOptions { SimHz = simHz, SnapshotEveryTicks = snapshotEveryTicks }.Validate());
    }

    [Theory]
    [InlineData(30, 1)]
    [InlineData(30, 3)]
    [InlineData(60, 4)]
    public void Validate_AcceptsSnapshotEveryTicksThatDividesSimHz(int simHz, int snapshotEveryTicks)
    {
        Assert.Null(new ServerOptions { SimHz = simHz, SnapshotEveryTicks = snapshotEveryTicks }.Validate());
    }
}
