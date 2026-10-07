using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ProjectH.Server.Game;
using ProjectH.Server.Qa;
using Xunit;

namespace ProjectH.Server.Tests.Qa;

// Phase 14: the squad QA commands (downPlayer, giveRebootCard, setStationCooldown) and what the observation shows.
public sealed class QaSquadTests
{
    // 기능: TeamSize 2, 4명으로 QA 하네스 경기를 시작한다.
    // 입력: 없음.
    // 출력: 하네스와 팀 1의 두 명, 팀 2의 첫 사람.
    private static (QaHarness H, PlayerEntity A1, PlayerEntity A2, PlayerEntity B1) Duo()
    {
        var h = new QaHarness(teamSize: 2);
        PlayerEntity a1 = h.Join(1, "qa-a1");
        PlayerEntity a2 = h.Join(2, "qa-a2");
        PlayerEntity b1 = h.Join(3, "qa-b1");
        h.Join(4, "qa-b2");
        for (int i = 0; i < 200 && !h.Match.Flow.InMatch; i++) h.Loop.RunTick();
        Assert.True(h.Match.Flow.InMatch);
        return (h, a1, a2, b1);
    }

    [Fact]
    public async Task DownPlayer_KnocksDown_OnlyWithAStandingTeammate()
    {
        var (h, a1, a2, _) = Duo();
        using (h)
        {
            var (status, body) = await h.Command("downPlayer", "qa-a1");
            Assert.Equal(200, status);
            Assert.True(QaHarness.Result(body).GetProperty("downed").GetBoolean());
            Assert.True(a1.IsDowned);
            Assert.Equal(409, (await h.Command("downPlayer", "qa-a1")).Status);   // already down
            Assert.Equal(409, (await h.Command("downPlayer", "qa-a2")).Status);   // its only teammate is down
            // setPosition keeps a downed player down.
            Assert.Equal(200, (await h.Command("setPosition", "qa-a1", new { x = 3, z = 3 })).Status);
            Assert.True(a1.IsDowned);
            var (_, players) = await h.Run(t => QaResult.Data(QaQueries.Players(t.Match)));
            JsonElement me = players.EnumerateArray().First(p => p.GetProperty("devPlayerId").GetString() == "qa-a1");
            Assert.True(me.GetProperty("downed").GetBoolean());
            Assert.Equal(1, me.GetProperty("teamId").GetInt32());
            Assert.True(a2.IsUp);
        }
    }

    [Fact]
    public async Task GiveRebootCard_NeedsAnEliminatedTeammate()
    {
        var (h, a1, a2, b1) = Duo();
        using (h)
        {
            Assert.Equal(409, (await h.Command("giveRebootCard", "qa-a2", new { owner = "qa-a1" })).Status);   // a1 is alive
            Assert.Equal(200, (await h.Command("killPlayer", "qa-a1")).Status);
            Assert.Equal(409, (await h.Command("giveRebootCard", "qa-b1", new { owner = "qa-a1" })).Status);   // another team
            Assert.Equal(404, (await h.Command("giveRebootCard", "qa-a2", new { owner = "qa-nobody" })).Status);
            var (status, body) = await h.Command("giveRebootCard", "qa-a2", new { owner = "qa-a1" });
            Assert.Equal(200, status);
            Assert.Equal(1, QaHarness.Result(body).GetProperty("cards").GetInt32());
            Assert.Equal(1, a2.Inventory.CardCount);
            Assert.Equal(0, h.Match.WorldItems.CardCount);   // the dropped card went into the hand
            Assert.False(a1.Alive);
            Assert.True(b1.Alive);
        }
    }

    [Fact]
    public async Task SetStationCooldown_AndTheMatchView()
    {
        var (h, _, _, _) = Duo();
        using (h)
        {
            Assert.Equal(200, (await h.Command("setStationCooldown", args: new { station = 2, seconds = 10 })).Status);
            Assert.Equal(400, (await h.Command("setStationCooldown", args: new { station = 4, seconds = 10 })).Status);
            var (_, match) = await h.Run(t => QaResult.Data(QaQueries.Match(t.Match, 30)));
            JsonElement data = match;
            Assert.Equal(2, data.GetProperty("teamSize").GetInt32());
            Assert.Equal(2, data.GetProperty("teams").GetInt32());
            Assert.Equal(2, data.GetProperty("teamsAlive").GetInt32());
            JsonElement station = data.GetProperty("stations")[2];
            Assert.True(station.GetProperty("coolingDown").GetBoolean());
            Assert.False(data.GetProperty("stations")[0].GetProperty("coolingDown").GetBoolean());
        }
    }
}
