using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using ProjectH.Server.Game;
using ProjectH.Server.Qa;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Qa;

// Phase 15 D14: what the QA observation shows of a team's pings and waypoints, and the map numbers in /qa/health.
public sealed class QaMapTests
{
    [Fact]
    public async Task ThePlayerView_ShowsItsTeamsPingsAndWaypoints_AndHealthShowsTheCounts()
    {
        using var h = new QaHarness(teamSize: 2);
        PlayerEntity a1 = h.Join(1, "qa-a1");
        h.Join(2, "qa-a2");
        PlayerEntity b1 = h.Join(3, "qa-b1");
        h.Join(4, "qa-b2");
        for (int i = 0; i < 200 && !h.Match.Flow.InMatch; i++) h.Loop.RunTick();
        Assert.True(h.Match.Flow.InMatch);
        b1.State.Position = a1.State.Position + new Vector3(0f, 0f, 6f);
        await h.Run(t =>
        {
            t.Match.HandleMarker(1, new MapMarker { Kind = MapMarkerKind.Enemy, Position = new Vector3(1f, 0f, 1f), TargetId = b1.EntityId });
            t.Match.HandleMarker(1, new MapMarker { Kind = MapMarkerKind.WaypointSet, Position = new Vector3(10f, 0f, -10f) });
            return QaResult.Data(true);
        });
        h.Loop.RunTick();

        var (_, players) = await h.Run(t => QaResult.Data(QaQueries.Players(t.Match)));
        JsonElement a2 = players.EnumerateArray().First(p => p.GetProperty("devPlayerId").GetString() == "qa-a2");
        Assert.Equal(1, a2.GetProperty("teamPingCount").GetInt32());
        JsonElement ping = a2.GetProperty("teamPings")[0];
        Assert.Equal("Enemy", ping.GetProperty("kind").GetString());
        Assert.Equal("qa-a1", ping.GetProperty("owner").GetString());
        Assert.Equal("qa-b1", ping.GetProperty("target").GetString());
        Assert.Equal(1, a2.GetProperty("teamWaypointCount").GetInt32());
        Assert.Equal("qa-a1", a2.GetProperty("teamWaypoints")[0].GetProperty("owner").GetString());
        Assert.Equal(JsonValueKind.Null, a2.GetProperty("waypoint").ValueKind);
        JsonElement me = players.EnumerateArray().First(p => p.GetProperty("devPlayerId").GetString() == "qa-a1");
        Assert.Equal(10f, me.GetProperty("waypoint").GetProperty("x").GetSingle(), 2);
        JsonElement other = players.EnumerateArray().First(p => p.GetProperty("devPlayerId").GetString() == "qa-b1");
        Assert.Equal(0, other.GetProperty("teamPingCount").GetInt32());   // never another team's
        Assert.Equal(0, other.GetProperty("teamWaypointCount").GetInt32());

        var (_, health) = await h.Run(t => QaResult.Data(QaQueries.Health(t)));
        JsonElement map = health.GetProperty("map");
        Assert.Equal(1, map.GetProperty("pings").GetInt64());
        Assert.Equal(1, map.GetProperty("enemyConfirmed").GetInt64());
        Assert.Equal(1, map.GetProperty("waypoints").GetInt64());
        Assert.Equal(0, map.GetProperty("markerDrops").GetInt64());
    }
}
