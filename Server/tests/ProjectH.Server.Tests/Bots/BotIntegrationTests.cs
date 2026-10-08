using System.Diagnostics;
using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Bots;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Server.Tests.Game;

namespace ProjectH.Server.Tests.Bots;

// Phase 7 spec §3: bots over real UDP against an in-process server. The test thread drives the bots
// (BotRunner.Step) while the server runs its own game loop thread, so the bots' views are only read on the thread
// that writes them.
public sealed class BotIntegrationTests
{
    private static GameLoop StartServer(ServerOptions options, GameData data, StartingLoadout? loadout = null, Vector3[]? drops = null)
    {
        options.Port = 0;
        options.DisconnectTimeoutMs = 3000;
        options.StatsIntervalSeconds = 60;
        var server = new GameLoop(options, data, NullLogger.Instance, loadout, drops);
        server.Start();
        return server;
    }

    private static BotRunner Bots(GameLoop server, int count, bool reconnect = false) =>
        new(new BotOptions { Port = server.LocalPort, Count = count, ConnectIntervalMs = 0, Reconnect = reconnect }, _ => { });

    // Steps the bots at about 30 Hz until the condition holds or the time runs out.
    private static bool RunUntil(BotRunner bots, Func<bool> condition, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            bots.Step();
            if (condition()) return true;
            Thread.Sleep(33);
        }
        return condition();
    }

    // Phase 15 D10, D13: the bots' connection reads TeamMarkers (the QA tool's actors are built on it). A dev-mode team of
    // one: the bot's own ping comes back in its team's list.
    [Fact]
    public void ABotConnection_ReadsItsTeamsMarkers()
    {
        using GameLoop server = StartServer(new ServerOptions { MaxPlayers = 4, DevRespawn = true }, TestGameData.Create());
        using BotRunner bots = Bots(server, 1);
        BotConnection connection = bots.Connection(0);
        Assert.True(RunUntil(bots, () => connection.View.Joined && connection.View.HasSnapshot, 10000), "joined");
        connection.SendMapMarker(new MapMarker { Kind = MapMarkerKind.Danger, Position = new Vector3(4f, 0f, 4f) });
        connection.SendMapMarker(new MapMarker { Kind = MapMarkerKind.WaypointSet, Position = new Vector3(-4f, 0f, 4f) });
        Assert.True(RunUntil(bots, () => connection.View.PingCount == 1 && connection.View.WaypointCount == 1, 5000), "markers back");
        Assert.Equal(MapMarkerKind.Danger, connection.View.Pings[0].Kind);
        Assert.Equal(connection.View.MyId, connection.View.Waypoints[0].OwnerId);
        Assert.Equal(2, connection.MapMarkersSent);
    }

    // Review fix A3 (D0-5): a bot connects through the same cookie step as the Unity client: one cookie answer, one retry.
    [Fact]
    public void ABotConnection_RetriesWithTheServersCookie()
    {
        using GameLoop server = StartServer(new ServerOptions { MaxPlayers = 4, DevRespawn = true }, TestGameData.Create());
        using BotRunner bots = Bots(server, 1);
        BotConnection connection = bots.Connection(0);
        Assert.True(RunUntil(bots, () => connection.View.Joined, 10000), "joined");
        Assert.Equal(1, connection.CookieRetries);
        Assert.False(connection.Disconnected);
        Assert.Equal(1, server.Health.CookieChallenges);
        Assert.Equal(0, server.Health.CookieRejects);
    }

    // Phase 16 D3, D7: the bots' connection (and so the QA actors) reads ContainerStates and SupplyDrops; a dev-mode server
    // rolls its containers at startup, and the join brings both packets.
    [Fact]
    public void ABotConnection_ReadsContainerStatesAndSupplyDrops()
    {
        using GameLoop server = StartServer(new ServerOptions { MaxPlayers = 4, DevRespawn = true }, TestGameData.Create());
        using BotRunner bots = Bots(server, 1);
        BotConnection connection = bots.Connection(0);
        Assert.True(RunUntil(bots, () => connection.View.ContainerStatesReceived > 0 && connection.View.SupplyDropsReceived > 0, 10000), "loot packets");
        Assert.NotEqual(0UL, connection.View.ContainersSpawned);
        Assert.Equal(0UL, connection.View.ContainersOpened);
        Assert.Equal(0, connection.View.SupplyDropCount);
    }

    [Fact]
    public void ABot_FindsAndPicksUpAWeapon()
    {
        using GameLoop server = StartServer(new ServerOptions { MaxPlayers = 4, DevRespawn = true },
            TestGameData.Create(lootJson: TestGameData.WeaponsOnlyLootJson));
        using BotRunner bots = Bots(server, 1);
        BotView view = bots.Connection(0).View;
        Assert.True(RunUntil(bots, () => view.HasInventory &&
                                         !(view.Inventory.Slot0.IsEmpty && view.Inventory.Slot1.IsEmpty && view.Inventory.Slot2.IsEmpty), 20000),
            $"no weapon picked up (goal {bots.Brain(0).Goal}, at {view.MyPosition})");
    }

    [Fact]
    public void TwoArmedBots_FightUntilOneDies()
    {
        using GameLoop server = StartServer(new ServerOptions { MaxPlayers = 4, DevRespawn = true }, TestGameData.Create(),
            TestGameData.CombatLoadout);
        using BotRunner bots = Bots(server, 2);
        Assert.True(RunUntil(bots, () => bots.Connection(0).View.DeathsSeen > 0 || bots.Connection(1).View.DeathsSeen > 0, 30000),
            $"nobody died (goals {bots.Brain(0).Goal}/{bots.Brain(1).Goal}, hits {bots.Connection(0).View.HitsLanded}/{bots.Connection(1).View.HitsLanded})");
    }

    // Phase 10 D11: a match reset closes the bots with ServerError, which the shared table retries; they come back about
    // a second later and join the new match.
    [Fact]
    public void BotsWithReconnect_ComeBackAfterAMatchReset()
    {
        using GameLoop server = StartServer(new ServerOptions { MaxPlayers = 4 }, TestGameData.Create());
        using BotRunner bots = Bots(server, 2, reconnect: true);
        Assert.True(RunUntil(bots, () => bots.Connection(0).View.Joined && bots.Connection(1).View.Joined, 10000), "joined");

        server.TickFaultHook = () => throw new InvalidOperationException("test fault");
        Assert.True(RunUntil(bots, () => server.Health.MatchResets == 1, 10000), "reset");
        server.TickFaultHook = null;

        Assert.True(RunUntil(bots, () => bots.Reconnects == 2 && bots.Connection(0).View.Joined && bots.Connection(1).View.Joined, 10000),
            $"rejoined (reconnects {bots.Reconnects})");
        Assert.Equal(4, server.Health.Joins);   // two joins before the reset, two after
    }

    // D11: a kick is not retried, even with --reconnect true.
    [Fact]
    public void ABotClosedWithANonRetryableCode_StaysOut()
    {
        // 5 s: the smallest input timeout that is valid with the 3 s disconnect timeout (DisconnectTimeoutMs + 2 s).
        using GameLoop server = StartServer(new ServerOptions { MaxPlayers = 4, InputTimeoutSeconds = 5 }, TestGameData.Create());
        using BotRunner bots = Bots(server, 1, reconnect: true);
        Assert.True(RunUntil(bots, () => bots.Connection(0).View.Joined, 10000), "joined");
        // Keep the connection alive (pings answered) but stop the brain: no input, so the server closes it with
        // InputTimeout and not with LiteNetLib's network timeout.
        var clock = Stopwatch.StartNew();
        // Pumped by hand until the close arrives: a Step after the pause would pass the whole pause as one elapsed time,
        // and the bot's own LiteNetLib timeout would fire before it reads the server's close.
        while (!bots.Connection(0).Disconnected && clock.ElapsedMilliseconds < 10000)
        {
            bots.Connection(0).Update(50f);
            Thread.Sleep(50);
        }
        Assert.True(bots.Connection(0).Disconnected, "closed");
        Assert.Equal(1, server.Health.Kicks(DisconnectCode.InputTimeout));
        Assert.True(bots.Connection(0).Code == DisconnectCode.InputTimeout, bots.Connection(0).DisconnectReason);
        Assert.False(bots.Connection(0).Retryable);
        RunUntil(bots, () => false, 1500);
        Assert.Equal(0, bots.Reconnects);
    }

    [Fact]
    public void FourBots_PlayAWholeMatch_AndTheNextRoundStarts()
    {
        using GameLoop server = StartServer(new ServerOptions { MaxPlayers = 4, MinPlayers = 2, StartCountdownSeconds = 1, ResultSeconds = 1 },
            TestGameData.Create(zonesJson: TestGameData.ShortZonesJson), drops: RoyaleHarness.LobbyRingDrops);
        using BotRunner bots = Bots(server, 4);
        bool AllGotResults()
        {
            for (int i = 0; i < bots.Count; i++)
            {
                if (bots.Connection(i).View.MatchResults == 0) return false;
            }
            return true;
        }
        Assert.True(RunUntil(bots, AllGotResults, 40000), "not every bot got a MatchResult");
        BotView first = bots.Connection(0).View;
        Assert.True(RunUntil(bots, () => first.Match.Round >= 2 &&
                                         (first.Match.State == MatchFlowState.Starting || first.Match.State == MatchFlowState.Playing), 15000),
            $"the next round did not start ({first.Match.State} #{first.Match.Round})");
        int winners = 0;
        for (int i = 0; i < bots.Count; i++)
        {
            if (bots.Connection(i).View.LastResult.Placement == 1) winners++;
        }
        Assert.Equal(1, winners);
    }
}
