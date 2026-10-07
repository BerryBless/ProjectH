using System;
using System.IO;
using ProjectH.Client.Game;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Flow;
using ProjectH.Server.Game.Squad;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 14 D11: squad.json loading and validation, the client's display copies of its defaults, and the team counts of
// MatchFlow (D6).
public class SquadCatalogTests
{
    [Fact]
    public void TheShippedFile_EqualsTheDefault()
    {
        SquadCatalog file = SquadCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, SquadCatalog.FileName), 30);
        SquadCatalog d = SquadCatalog.Default(30);
        Assert.Equal(d.DownedHealth, file.DownedHealth);
        Assert.Equal(d.BleedOutTicks, file.BleedOutTicks);
        Assert.Equal(d.ReviveTicks, file.ReviveTicks);
        Assert.Equal(d.ReviveRange, file.ReviveRange);
        Assert.Equal(d.ReviveHealth, file.ReviveHealth);
        Assert.Equal(d.ReviveCancelOnDamage, file.ReviveCancelOnDamage);
        Assert.Equal(d.RebootTicks, file.RebootTicks);
        Assert.Equal(d.RebootRange, file.RebootRange);
        Assert.Equal(d.CardLifetimeTicks, file.CardLifetimeTicks);
        Assert.Equal(d.MaxCardsHeld, file.MaxCardsHeld);
        Assert.Equal(d.StationCooldownTicks, file.StationCooldownTicks);
        Assert.Equal(d.RebootLoadout.Weapons, file.RebootLoadout.Weapons);
        Assert.Equal(d.RebootLoadout.LightAmmo, file.RebootLoadout.LightAmmo);
        // The spec's numbers (D5, D8-D10) at 30 Hz.
        Assert.Equal(100, d.DownedHealth);
        Assert.Equal(900u, d.BleedOutTicks);
        Assert.Equal(150u, d.ReviveTicks);
        Assert.Equal(2f, d.ReviveRange);
        Assert.Equal(30, d.ReviveHealth);
        Assert.Equal(150u, d.RebootTicks);
        Assert.Equal(3f, d.RebootRange);
        Assert.Equal(2700u, d.CardLifetimeTicks);
        Assert.Equal(3, d.MaxCardsHeld);
        Assert.Equal(900u, d.StationCooldownTicks);
        Assert.False(d.FriendlyFire);
    }

    [Fact]
    public void TheClientsDisplayCopies_EqualTheDefaults()
    {
        SquadCatalog d = SquadCatalog.Default(30);
        Assert.Equal(SquadPrompt.ReviveRange, d.ReviveRange);
        Assert.Equal(SquadPrompt.RebootRange, d.RebootRange);
        Assert.Equal(SquadCatalog.StationHeightRange, SquadPrompt.RebootHeight);
        Assert.Equal((int)SquadPrompt.DownedHealth, d.DownedHealth);
        Assert.Equal((uint)(SquadPrompt.BleedOutSeconds * 30), d.BleedOutTicks);
    }

    [Fact]
    public void GameData_LoadsSquadJson_AndChecksTheRebootLoadout()
    {
        GameData data = GameData.LoadDirectory(AppContext.BaseDirectory, 30);
        Assert.Equal(3, data.Squad.RebootLoadout.Weapons[0].WeaponId);   // weapons.json: Wisp SMG
        // A loadout naming a weapon the catalog does not have is refused at startup.
        string bad = SquadCatalog.DefaultJson.Replace("\"id\": 3", "\"id\": 99");
        Assert.True(SquadCatalog.TryParse(bad, 30, out SquadCatalog? squad, out _));
        var items = TestGameData.Items();
        Assert.Throws<ArgumentException>(() => new GameData(TestWeapons.Create(), items, TestGameData.Loot(items), TestGameData.Zones(), squad: squad));
    }

    [Theory]
    [InlineData("\"friendlyFire\": false", "\"friendlyFire\": true")]
    [InlineData("\"downedHealth\": 100", "\"downedHealth\": 0")]
    [InlineData("\"bleedOutSeconds\": 30", "\"bleedOutSeconds\": 0")]
    [InlineData("\"reviveSeconds\": 5", "\"reviveSeconds\": 0.1")]
    [InlineData("\"reviveRange\": 2.0", "\"reviveRange\": 50")]
    [InlineData("\"reviveHealth\": 30", "\"reviveHealth\": 101")]
    [InlineData("\"rebootRange\": 3.0", "\"rebootRange\": 0")]
    [InlineData("\"cardLifetimeSeconds\": 90", "\"cardLifetimeSeconds\": 0")]
    [InlineData("\"maxCardsHeld\": 3", "\"maxCardsHeld\": 4")]
    [InlineData("\"stationCooldownSeconds\": 30", "\"stationCooldownSeconds\": -1")]
    [InlineData("\"rebootLoadout\": { \"shield\": 0,", "\"rebootLoadout\": { \"shield\": 0, \"weapons\": [ { \"id\": 1, \"rarity\": 9 } ], \"x\": [")]
    public void InvalidValues_AreRefused(string from, string to)
    {
        string json = SquadCatalog.DefaultJson.Replace(from, to);
        Assert.NotEqual(SquadCatalog.DefaultJson, json);
        Assert.False(SquadCatalog.TryParse(json, 30, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void MissingRebootLoadout_AndBadJson_AreRefused()
    {
        Assert.False(SquadCatalog.TryParse("{ \"downedHealth\": 100 }", 30, out _, out _));
        Assert.False(SquadCatalog.TryParse("{ not json", 30, out _, out _));
    }

    // ---- MatchFlow team counts (D6) ----

    // 기능: players명으로 경기를 시작한 MatchFlow를 만든다.
    // 입력: players - 참가자 수.
    // 출력: InMatch인 MatchFlow.
    private static MatchFlow Started(int players)
    {
        var flow = new MatchFlow(2, 1, 1, devRespawn: false);
        flow.Update(0, players);
        flow.Update(1, players);
        Assert.True(flow.InMatch);
        return flow;
    }

    [Fact]
    public void Flow_PlacesTeams_ByTheTeamsLeft_AndFinishesAtOneTeam()
    {
        MatchFlow flow = Started(4);
        flow.SetTeams(2);
        Assert.Equal(2, flow.Teams);
        flow.EliminatePlayer();
        Assert.Equal(3, flow.Alive);
        Assert.False(flow.ShouldFinish);   // a player out, both teams in
        flow.EliminatePlayer();
        Assert.Equal(2, flow.EliminateTeam());
        Assert.True(flow.ShouldFinish);
        Assert.Equal(4, flow.Participants);
    }

    [Fact]
    public void Flow_ARebootedPlayer_CountsAsAliveAgain_NeverAboveTheParticipants()
    {
        MatchFlow flow = Started(3);
        flow.SetTeams(2);
        flow.EliminatePlayer();
        flow.RestorePlayer();
        Assert.Equal(3, flow.Alive);
        flow.RestorePlayer();
        Assert.Equal(3, flow.Alive);
    }

    [Fact]
    public void Flow_Solo_StaysAsBefore()
    {
        MatchFlow flow = Started(3);   // no SetTeams: one team per participant
        Assert.Equal(3, flow.Teams);
        Assert.Equal(3, flow.Eliminate());
        Assert.Equal(2, flow.Eliminate());
        Assert.True(flow.ShouldFinish);
    }
}
