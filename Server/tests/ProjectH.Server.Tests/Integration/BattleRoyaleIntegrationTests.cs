using System;
using System.Linq;
using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Integration;

// Phase 5 spec §6: two headless clients over real UDP, with a 1 s countdown and a 1 s result screen. Both start with
// the combat loadout (TestGameData.CombatLoadout), so A can kill B without looting. The zone is the spec one: its
// first circle covers the whole arena for 20 s, so the zone cannot kill anyone while the clients play this out
// (a starved test thread must not let the zone decide the kill).
public sealed class BattleRoyaleIntegrationTests : IDisposable
{
    // Phase 6 D9: two drop spots 10 m apart in the plaza, so A sees and can shoot B right after the start.
    private static readonly Vector3[] TwoDropSpots = { new(5f, 0f, 0f), new(-5f, 0f, 0f) };

    private readonly GameLoop _server;

    public BattleRoyaleIntegrationTests()
    {
        _server = new GameLoop(new ServerOptions
        {
            Port = 0,
            MaxPlayers = 4,
            MinPlayers = 2,
            StartCountdownSeconds = 1,
            ResultSeconds = 1,
            DisconnectTimeoutMs = 1000,
            StatsIntervalSeconds = 60,
        }, TestGameData.Create(), NullLogger.Instance, TestGameData.CombatLoadout, TwoDropSpots);
        _server.Start();
    }

    public void Dispose() => _server.Dispose();

    private HeadlessClient Join(string devId)
    {
        var client = new HeadlessClient();
        client.Connect(_server.LocalPort, devId);
        Assert.True(Pump.Until(() => client.Connected, 3000, client), "connect");
        client.SendJoin();
        Assert.True(Pump.Until(() => client.JoinResponse.HasValue, 3000, client), "join response");
        Assert.Equal(JoinResult.Ok, client.JoinResponse?.Result);
        return client;
    }

    private static bool Saw(HeadlessClient c, MatchFlowState state, int round = 1) =>
        c.MatchStates.Any(s => s.State == state && s.Round == round);

    [Fact]
    public void Countdown_Match_Kill_Result_ThenTheNextRound()
    {
        using var a = Join("winner");
        using var b = Join("loser");

        // 1. Join -> countdown -> Playing, seen by both, with the zone's first phase.
        Assert.True(Pump.Until(() => Saw(a, MatchFlowState.Starting) && Saw(b, MatchFlowState.Starting), 3000, a, b), "countdown");
        Assert.True(Pump.Until(() => Saw(a, MatchFlowState.Playing) && Saw(b, MatchFlowState.Playing), 3000, a, b), "playing");
        Assert.True(Pump.Until(() => a.ZoneStates.Any(z => z.Phase == 1) && b.ZoneStates.Any(z => z.Phase == 1), 3000, a, b), "zone phase 1");
        Assert.True(a.ZoneStates.Last(z => z.Phase == 1).SameAs(b.ZoneStates.Last(z => z.Phase == 1)), "both see the same zone");
        MatchState playing = a.MatchStates.Last();
        Assert.Equal(2, playing.Participants);
        Assert.Equal(2, playing.Alive);
        // Snapshots taken after the start show both on their drop spots.
        uint startSeen = a.LastServerTick;
        Assert.True(Pump.Until(() => a.LastServerTick > startSeen + 2 && a.LastSnapshot.ContainsKey(b.MyEntityId), 3000, a, b), "snapshot");

        // 2. A kills B (the loadout's automatic weapon: five hits).
        Vector3 shooter = a.LastSnapshot[a.MyEntityId].Position;
        Vector3 target = a.LastSnapshot[b.MyEntityId].Position;
        TestAim.YawPitch(shooter, target + new Vector3(0f, 1.2f, 0f), out float yaw, out float pitch);
        for (int i = 0; i < 90 && b.Deaths.Count == 0; i++)
        {
            a.SendInput(new InputCommand { Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = a.LastServerTick });
            Pump.Until(() => false, 33, a, b);
        }
        Assert.True(Pump.Until(() => a.Deaths.Count > 0 && b.Deaths.Count > 0, 3000, a, b), "death");
        PlayerDied died = b.Deaths.Single();
        Assert.Equal(b.MyEntityId, died.VictimId);
        Assert.Equal(a.MyEntityId, died.KillerId);
        Assert.Equal(2, died.Placement);
        int bRespawns = b.Respawns.Count(r => r.EntityId == b.MyEntityId);   // the match start teleport

        // 3. Both get Finished and their own result.
        Assert.True(Pump.Until(() => Saw(a, MatchFlowState.Finished) && Saw(b, MatchFlowState.Finished) &&
                                     a.MatchResults.Count > 0 && b.MatchResults.Count > 0, 3000, a, b), "finished and results");
        MatchResult won = a.MatchResults.Single();
        Assert.Equal(a.MyEntityId, won.WinnerId);
        Assert.Equal(1, won.Placement);
        Assert.Equal(1, won.Kills);
        Assert.Equal(2, won.Participants);
        MatchResult lost = b.MatchResults.Single();
        Assert.Equal(a.MyEntityId, lost.WinnerId);
        Assert.Equal(2, lost.Placement);
        Assert.Equal(0, lost.Kills);
        Assert.Equal(bRespawns, b.Respawns.Count(r => r.EntityId == b.MyEntityId));   // D4: no respawn in the match

        // 4. After the result screen: Closing -> the next round counts down, everyone alive again, no zone.
        Assert.True(Pump.Until(() => Saw(a, MatchFlowState.Starting, round: 2) && Saw(b, MatchFlowState.Starting, round: 2), 4000, a, b),
            "next round");
        Assert.True(Pump.Until(() => b.Respawns.Count(r => r.EntityId == b.MyEntityId) == bRespawns + 1 &&
                                     b.ZoneStates.Last().Phase == 0, 3000, a, b), "B alive again, zone off");
        Assert.Single(a.MatchResults);
    }
}
