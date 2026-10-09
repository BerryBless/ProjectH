using System;
using System.Linq;
using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Integration;

// Spec §6: two headless clients over real UDP. Every spawn point rolls a weapon (WeaponsOnlyLootJson), and
// everyone starts with one Medkit and nothing else, so the loser has something to drop. Refills are off,
// so every ItemSpawned the test sees is a drop.
public sealed class ItemIntegrationTests : IDisposable
{
    private readonly GameLoop _server;

    // 기능: 무기만 나오는 Loot, 재보급 없음, Medkit 1개 시작 장비의 DevRespawn 샌드박스 서버를 포트 0에 띄운다.
    // 입력: 없음.
    // 출력: 서버가 시작된 테스트 Fixture(Dispose가 서버를 닫는다).
    public ItemIntegrationTests()
    {
        _server = new GameLoop(new ServerOptions
        {
            Port = 0,
            MaxPlayers = 4,
            DisconnectTimeoutMs = 1000,
            StatsIntervalSeconds = 60,
            DevRespawn = true,   // Phase 5 D4: the Phase 3/4 sandbox (respawn, no match flow)
            LootRespawnSeconds = 0,
        }, TestGameData.Create(lootJson: TestGameData.WeaponsOnlyLootJson), NullLogger.Instance,
            new StartingLoadout { Medkits = 1 });
        _server.Start();
    }

    // 기능: 테스트 서버를 멈춘다.
    // 입력: 없음.
    // 출력: 반환값 없음. 서버 스레드와 소켓이 닫힌다.
    public void Dispose() => _server.Dispose();

    // 기능: 새 HeadlessClient로 서버에 접속해 Join을 보내고 Ok 응답을 확인한다.
    // 입력: devId - 플레이어 이름.
    // 출력: Join에 성공한 HeadlessClient(호출자가 Dispose한다). 3초 안에 접속·응답이 없으면 테스트가 실패한다.
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

    // 기능: viewer의 마지막 Snapshot에서 엔티티의 발 위치를 읽는다.
    // 입력: viewer - Snapshot을 받은 Client, entityId - 찾을 엔티티.
    // 출력: 그 엔티티의 위치. Snapshot에 없으면 KeyNotFoundException.
    private static Vector3 Feet(HeadlessClient viewer, ushort entityId) => viewer.LastSnapshot[entityId].Position;

    // 기능: 두 점의 수평(XZ) 거리를 구한다.
    // 입력: a·b - 두 점.
    // 출력: Y를 뺀 거리.
    private static float Horizontal(Vector3 a, Vector3 b)
    {
        Vector3 d = a - b;
        return MathF.Sqrt(d.X * d.X + d.Z * d.Z);
    }

    [Fact]
    public void PickUp_Shoot_Kill_DeathDrop_SeenByBothClients()
    {
        using var a = Join("looter");
        using var b = Join("target");
        Assert.True(Pump.Until(() => a.WorldItems.Count == LootPoints.All.Length && b.WorldItems.Count == LootPoints.All.Length &&
                                     a.Inventories.Count > 0 && a.LastSnapshot.ContainsKey(a.MyEntityId) &&
                                     a.LastSnapshot.ContainsKey(b.MyEntityId), 3000, a, b), "world list and first snapshots");
        // Both clients see the same items at the same places (success criterion).
        Assert.Equal(a.WorldItems.OrderBy(p => p.Key), b.WorldItems.OrderBy(p => p.Key));
        Assert.True(a.Inventories.Last().Slot0.IsEmpty);   // D1: empty-handed
        Assert.Equal(1, a.Inventories.Last().Medkits);

        // 1. A walks to the nearest inner-ring weapon (LootPointsTests: the straight walk is clear) and presses E.
        Vector3 start = Feet(a, a.MyEntityId);
        WorldItemData target = a.WorldItems.Values
            .Where(i => i.Position.Y == 0f && i.Position.Length() < 8.01f)
            .OrderBy(i => Horizontal(i.Position, start)).First();
        Assert.Equal(ItemKind.Weapon, target.Kind);
        for (int i = 0; i < 150 && Horizontal(Feet(a, a.MyEntityId), target.Position) > 1f; i++)
        {
            Vector3 to = target.Position - Feet(a, a.MyEntityId);
            a.SendMove(0f, 1f, MathF.Atan2(to.X, to.Z) * 180f / MathF.PI);
            Pump.Until(() => false, 33, a, b);
        }
        for (int i = 0; i < 6; i++)   // stop: the server's missed-input repeat would keep walking otherwise
        {
            a.SendMove(0f, 0f, 0f);
            Pump.Until(() => false, 33, a, b);
        }
        Assert.True(Horizontal(Feet(a, a.MyEntityId), target.Position) <= 2f, "A stands within pickup range");

        a.SendInput(new InputCommand { Buttons = InputButtons.Interact });
        Assert.True(Pump.Until(() => a.PickupResults.Count > 0, 3000, a, b), "pickup result");
        Assert.Equal(PickupResultCode.Ok, a.PickupResults[0].Result);
        Assert.Equal(target.ItemId, a.PickupResults[0].ItemId);
        Assert.True(Pump.Until(() => a.ItemsRemoved.Contains(target.ItemId) && b.ItemsRemoved.Contains(target.ItemId), 3000, a, b),
            "both clients told the item is gone");
        Assert.True(Pump.Until(() => a.Inventories.Last().Slot0.WeaponId == target.DefId, 3000, a, b), "A holds the weapon");

        // 2. A shoots B until B dies. Fire / no fire alternates, so the semi-automatic test weapon fires too;
        //    every test weapon kills 100 health (shield 0) within one magazine.
        Vector3 shooter = Feet(a, a.MyEntityId);
        Vector3 body = Feet(a, b.MyEntityId);
        TestAim.YawPitch(shooter, body + new Vector3(0f, 1.2f, 0f), out float yaw, out float pitch);
        for (int i = 0; i < 150 && b.Deaths.Count == 0; i++)
        {
            a.SendInput(new InputCommand { Buttons = i % 2 == 0 ? InputButtons.Fire : InputButtons.None, AimYaw = yaw, AimPitch = pitch, ViewTick = a.LastServerTick });
            Pump.Until(() => false, 33, a, b);
        }
        Assert.True(Pump.Until(() => a.Deaths.Count > 0 && b.Deaths.Count > 0, 3000, a, b), "B died");
        Assert.Equal(b.MyEntityId, a.Deaths[0].VictimId);

        // 3. B's Medkit drops next to the body, and both clients get it as ItemSpawned.
        bool IsTheDrop(WorldItemData item) => item.Kind == ItemKind.Consumable && item.DefId == (byte)ConsumableType.Medkit &&
                                               item.Amount == 1 && Horizontal(item.Position, body) < 1.1f;
        Assert.True(Pump.Until(() => a.ItemsSpawned.Any(IsTheDrop) && b.ItemsSpawned.Any(IsTheDrop), 3000, a, b), "death drop seen by both");
        Assert.Equal(a.ItemsSpawned.Single(IsTheDrop), b.ItemsSpawned.Single(IsTheDrop));
        Assert.True(Pump.Until(() => b.Inventories.Last().Medkits == 0, 3000, a, b), "B's inventory emptied");
    }
}
