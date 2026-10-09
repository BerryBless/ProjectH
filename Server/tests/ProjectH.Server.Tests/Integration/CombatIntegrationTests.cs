using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Integration;

// Phase 3 spec §5: two headless clients over real UDP. The server runs the test catalog and the combat
// loadout (TestGameData.CombatLoadout), whose automatic weapon does 30 damage every 3 ticks, so five hits
// take shield 50 + health 100 to 0.
public sealed class CombatIntegrationTests : IDisposable
{
    private readonly GameLoop _server;

    // 기능: 전투 장비(30 피해 자동 무기)를 주는 DevRespawn 샌드박스 서버를 포트 0에 띄운다.
    // 입력: 없음.
    // 출력: 시작된 GameLoop를 가진 테스트 Fixture(Dispose가 서버를 닫는다).
    public CombatIntegrationTests()
    {
        _server = new GameLoop(new ServerOptions
        {
            Port = 0,
            MaxPlayers = 4,
            DisconnectTimeoutMs = 1000,
            StatsIntervalSeconds = 60,
            DevRespawn = true,   // Phase 5 D4: the Phase 3/4 sandbox (respawn, no match flow)
        }, TestGameData.Create(), NullLogger.Instance, TestGameData.CombatLoadout);
        _server.Start();
    }

    // 기능: 테스트 서버를 멈추고 정리한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 서버 소켓과 Loop 스레드가 닫힌다.
    public void Dispose() => _server.Dispose();

    // 기능: HeadlessClient를 만들어 서버에 접속하고 Join 응답이 Ok로 올 때까지 기다린다.
    // 입력: devId - 플레이어 이름(DevPlayerId).
    // 출력: Join을 마친 HeadlessClient(호출자가 Dispose한다). 3초 안에 접속·Join이 안 되거나 Ok가 아니면 테스트가 실패한다.
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

    [Fact]
    public void Shooting_DamagesThenKills_ThenTargetRespawns()
    {
        using var a = Join("shooter");
        using var b = Join("target");
        Assert.True(Pump.Until(() => a.Weapons != null && b.Weapons != null && a.Items != null && b.Items != null &&
                                     a.LastSnapshot.ContainsKey(a.MyEntityId) && a.LastSnapshot.ContainsKey(b.MyEntityId) &&
                                     b.SnapshotsReceived > 0, 3000, a, b), "catalog and first snapshots");
        Assert.Equal("Test Auto", a.Weapons![0].Name);
        Assert.Equal(CombatRules.MaxHealth, b.LastSelf.Health);
        Assert.Equal(TestGameData.LoadoutShield, b.LastSelf.Shield);

        // Both stand on the 5 m spawn ring, which has nothing between its points (the plaza, GameMap.PlazaRadius 12 m).
        Vector3 shooterFeet = a.LastSnapshot[a.MyEntityId].Position;
        Vector3 targetFeet = a.LastSnapshot[b.MyEntityId].Position;
        TestAim.YawPitch(shooterFeet, targetFeet + new Vector3(0f, 1.2f, 0f), out float yaw, out float pitch);

        // Trigger held, one input per tick, until the target hears of its own death.
        for (int i = 0; i < 90 && b.Deaths.Count == 0; i++)
        {
            a.SendInput(new InputCommand { Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = a.LastServerTick });
            Pump.Until(() => false, 33, a, b);
        }
        Assert.True(Pump.Until(() => a.Deaths.Count > 0 && b.Deaths.Count > 0, 3000, a, b), "both clients told of the death");
        var deathSeen = Stopwatch.StartNew();

        // B: five DamageTaken from A, and its own snapshot block showed the loss before the death.
        Assert.Equal(5, b.DamageEvents.Count);
        Assert.All(b.DamageEvents, d => Assert.Equal(a.MyEntityId, d.AttackerId));
        Assert.Contains(b.SelfHistory, s => s.Shield < TestGameData.LoadoutShield);
        Assert.Contains(b.SelfHistory, s => s.Health > 0 && s.Health < CombatRules.MaxHealth);

        // A: five HitConfirmed, the last one the kill; everyone saw A's tracers.
        Assert.Equal(5, a.Hits.Count);
        Assert.All(a.Hits, h => Assert.Equal(b.MyEntityId, h.TargetId));
        Assert.True(a.Hits.Last().Killed);
        Assert.Contains(b.Shots, s => s.ShooterId == a.MyEntityId);

        foreach (var client in new[] { a, b })
        {
            var died = Assert.Single(client.Deaths);
            Assert.Equal(b.MyEntityId, died.VictimId);
            Assert.Equal(a.MyEntityId, died.KillerId);
        }

        // Respawn about 3 s later at the spawn point, with full health and shield.
        Assert.True(Pump.Until(() => a.Respawns.Count > 0 && b.Respawns.Count > 0, 5000, a, b), "respawn");
        Assert.True(deathSeen.Elapsed.TotalSeconds > 2.5, $"respawned after {deathSeen.Elapsed.TotalSeconds:F2} s");
        var respawned = Assert.Single(b.Respawns);
        Assert.Equal(b.MyEntityId, respawned.EntityId);
        Assert.Equal(Match.SpawnPosition(b.MyEntityId), respawned.Position);
        Assert.True(Pump.Until(() => b.LastSelf.Health == CombatRules.MaxHealth && b.LastSelf.Shield == TestGameData.LoadoutShield &&
                                     b.LastSnapshot.TryGetValue(b.MyEntityId, out var self) && self.IsAlive, 3000, a, b), "alive again");
    }
}
