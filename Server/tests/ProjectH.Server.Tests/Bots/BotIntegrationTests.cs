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

    private static BotRunner Bots(GameLoop server, int count) =>
        new(new BotOptions { Port = server.LocalPort, Count = count, ConnectIntervalMs = 0 }, _ => { });

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
