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
    [InlineData(-1, 30)]
    [InlineData(1, -1)]
    [InlineData(1, 3601)]
    public void Validate_RejectsBadLootSettings(int seed, int respawnSeconds)
    {
        Assert.NotNull(new ServerOptions { LootSeed = seed, LootRespawnSeconds = respawnSeconds }.Validate());
    }

    [Fact]
    public void LootDefaults_MatchSpec_AndZeroRespawnIsAllowed()
    {
        Assert.Equal(30, new ServerOptions().LootRespawnSeconds);   // D7
        Assert.Null(new ServerOptions { LootRespawnSeconds = 0, LootSeed = 0 }.Validate());
    }

    // Phase 5 spec §1: MinPlayers 2 (2-MaxPlayers), countdown and result 10 s, DevRespawn off, ZoneSeed.
    [Fact]
    public void MatchFlowDefaults_MatchSpec()
    {
        var options = new ServerOptions();
        Assert.Equal(2, options.MinPlayers);
        Assert.Equal(10, options.StartCountdownSeconds);
        Assert.Equal(10, options.ResultSeconds);
        Assert.False(options.DevRespawn);
        Assert.Equal(1, options.ZoneSeed);
        Assert.Equal(1, options.SpawnSeed);
        Assert.Null(new ServerOptions { MinPlayers = 2, MaxPlayers = 2 }.Validate());
        Assert.Null(new ServerOptions { MinPlayers = 16, MaxPlayers = 16, ZoneSeed = 0, SpawnSeed = 0 }.Validate());
        string? error = new ServerOptions { SpawnSeed = -1 }.Validate();
        Assert.NotNull(error);
        Assert.Contains("SpawnSeed", error);
    }

    [Theory]
    [InlineData(0, 16, 10, 10, 1)]    // nobody needed
    [InlineData(1, 16, 10, 10, 1)]    // one player finishes the match on its first tick and loops forever
    [InlineData(1, 1, 10, 10, 1)]
    [InlineData(17, 16, 10, 10, 1)]   // more than can join
    [InlineData(2, 1, 10, 10, 1)]     // MaxPlayers 1 with the default MinPlayers 2
    [InlineData(2, 16, 0, 10, 1)]
    [InlineData(2, 16, 301, 10, 1)]
    [InlineData(2, 16, 10, 0, 1)]
    [InlineData(2, 16, 10, 301, 1)]
    [InlineData(2, 16, 10, 10, -1)]
    public void Validate_RejectsBadMatchFlowSettings(int minPlayers, int maxPlayers, int countdown, int result, int zoneSeed)
    {
        Assert.NotNull(new ServerOptions
        {
            MinPlayers = minPlayers, MaxPlayers = maxPlayers, StartCountdownSeconds = countdown, ResultSeconds = result, ZoneSeed = zoneSeed,
        }.Validate());
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
