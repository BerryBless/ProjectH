using System;
using System.Linq;
using System.Numerics;
using ProjectH.Client.Game;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Build;
using ProjectH.Server.Game.Flow;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 16 D4: the container target rule, its client copy and the door preference.
public class ContainerRulesTests
{
    private static readonly LootContainer[] One = { new(LootContainerKind.Chest, new Vector3(0f, 0f, 2f), 0f) };

    [Fact]
    public void InReach_UsesTheDoorGeometry()
    {
        Assert.True(ContainerRules.InReach(Vector3.Zero, 0f, new Vector3(0f, 0f, 2.5f), out float sq));
        Assert.Equal(6.25f, sq, 4);
        Assert.False(ContainerRules.InReach(Vector3.Zero, 0f, new Vector3(0f, 0f, 2.6f), out _));              // too far
        Assert.False(ContainerRules.InReach(Vector3.Zero, 180f, new Vector3(0f, 0f, 2f), out _));              // behind
        Assert.True(ContainerRules.InReach(Vector3.Zero, 55f, new Vector3(0f, 0f, 2f), out _));                // within 60 degrees
        Assert.False(ContainerRules.InReach(Vector3.Zero, 65f, new Vector3(0f, 0f, 2f), out _));
        Assert.False(ContainerRules.InReach(new Vector3(0f, 1.1f, 0f), 0f, new Vector3(0f, 0f, 2f), out _));   // another floor
        Assert.False(ContainerRules.InReach(new Vector3(0f, -1.1f, 0f), 0f, new Vector3(0f, 0f, 2f), out _));
        Assert.True(ContainerRules.InReach(new Vector3(0f, 0f, 2f), 180f, new Vector3(0f, 0f, 2f), out _));    // on top of it: any yaw
    }

    [Fact]
    public void FindTarget_SkipsClosedOutMasks_PrefersTheNearer_AndContainersOnTies()
    {
        var containers = new[]
        {
            new LootContainer(LootContainerKind.Chest, new Vector3(0f, 0f, 2f), 0f),
            new LootContainer(LootContainerKind.AmmoBox, new Vector3(0.5f, 0f, 1.5f), 0f),
        };
        var drops = new[] { new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, 2f) };
        Assert.Equal(1, ContainerRules.FindTarget(Vector3.Zero, 0f, containers, 0b11, drops, 0, out _));
        Assert.Equal(0, ContainerRules.FindTarget(Vector3.Zero, 0f, containers, 0b01, drops, 0, out float sq));
        Assert.Equal(4f, sq, 4);
        Assert.Equal(ContainerRules.SupplyDropTargetBase, ContainerRules.FindTarget(Vector3.Zero, 0f, containers, 0b11, drops, 0b01, out _));
        // Same distance as container 0: the container (seen first) wins.
        Assert.Equal(0, ContainerRules.FindTarget(Vector3.Zero, 0f, containers, 0b01, drops, 0b10, out _));
        Assert.Equal(-1, ContainerRules.FindTarget(Vector3.Zero, 0f, containers, 0, drops, 0, out sq));
        Assert.Equal(0f, sq);
    }

    [Fact]
    public void PreferContainer_TheNearerWins_TiesGoToTheDoor()
    {
        var doors = new[] { Box.FromCenterSize(new Vector3(0f, 1.5f, 2f), new Vector3(1.5f, 3f, 0.2f)) };
        Assert.True(ContainerRules.PreferContainer(Vector3.Zero, -1, doors, 100f));
        Assert.True(ContainerRules.PreferContainer(Vector3.Zero, 0, doors, 3.9f));
        Assert.False(ContainerRules.PreferContainer(Vector3.Zero, 0, doors, 4f));    // same distance: the door
        Assert.False(ContainerRules.PreferContainer(Vector3.Zero, 0, doors, 4.1f));
        Assert.True(ContainerRules.PreferContainer(Vector3.Zero, 5, doors, 4.1f));   // not a door
    }

    // The client's prompt copy (Client/Assets/Scripts/Game/ContainerRule.cs) picks exactly what the server picks.
    [Fact]
    public void TheClientsCopy_PicksTheSameTarget()
    {
        var rng = new Random(1601);
        ReadOnlySpan<LootContainer> all = LootContainers.All;
        var drops = new Vector3[SupplyDropsPacket.MaxSupplyDrops];
        for (int n = 0; n < 20_000; n++)
        {
            Vector3 anchor = all[rng.Next(all.Length)].Position;
            var feet = new Vector3(anchor.X + (float)(rng.NextDouble() * 6 - 3), anchor.Y + (float)(rng.NextDouble() * 2.4 - 1.2), anchor.Z + (float)(rng.NextDouble() * 6 - 3));
            float yaw = (float)(rng.NextDouble() * 360);
            for (int k = 0; k < drops.Length; k++) drops[k] = new Vector3(feet.X + (float)(rng.NextDouble() * 6 - 3), feet.Y, feet.Z + (float)(rng.NextDouble() * 6 - 3));
            ulong closed = ((ulong)(uint)rng.Next() << 32) | (uint)rng.Next();
            int dropMask = rng.Next(16);
            int server = ContainerRules.FindTarget(feet, yaw, all, closed, drops, dropMask, out float serverSq);
            int client = ContainerRule.FindTarget(feet, yaw, all, closed, drops, dropMask, out float clientSq);
            Assert.Equal(server, client);
            Assert.Equal(serverSq, clientSq);
            int door = DoorRules.FindTarget(feet, yaw, GameMap.Doors);
            Assert.Equal(ContainerRules.PreferContainer(feet, door, GameMap.Doors, serverSq), ContainerRule.PreferContainer(feet, door, GameMap.Doors, clientSq));
            Assert.Equal(ContainerRules.InReach(feet, yaw, drops[0], out float a), ContainerRule.InReach(feet, yaw, drops[0], out float b));
            Assert.Equal(a, b);
        }
        Assert.Equal(ContainerRules.SupplyDropTargetBase, ContainerRule.SupplyDropTargetBase);
    }
}

// Phase 16 D2-D7, D10: containers and supply drops in a match.
public class LootContainerMatchTests
{
    private const int Chest = 14;    // (15, 16), open ground
    private const int AmmoBox = 19;  // (20, -16), open ground

    // 기능: 두 명이 들어와 경기가 시작된 하네스를 만든다.
    // 입력: a, b - 들어온 플레이어, lootJson - Loot 데이터.
    // 출력: 경기 중인 RoyaleHarness.
    private static RoyaleHarness Started(out PlayerEntity a, out PlayerEntity b, string lootJson = TestGameData.LootJson)
    {
        var h = new RoyaleHarness(lootJson: lootJson);
        a = h.Join(1);
        b = h.Join(2);
        h.RunToMatch();
        return h;
    }

    // 기능: 대상의 남쪽 distance m 지면 위치를 돌려준다(yaw 0이면 대상을 바라본다).
    // 입력: target - 대상 위치, distance - 거리.
    // 출력: 발 위치.
    private static Vector3 InFront(Vector3 target, float distance = 2f) =>
        new(target.X, GameMap.Terrain.Height(target.X, target.Z - distance), target.Z - distance);

    // 기능: E 한 번 누름을 입력 버퍼에 넣는다(먼저 뗀 입력, 그다음 누른 입력: E는 누른 입력에서만 동작한다). 호출자가 2 Tick을 돌린다.
    // 입력: h - 경기, p - 플레이어, yaw - 바라보는 방향.
    // 출력: 반환값 없음.
    private static void Press(RoyaleHarness h, PlayerEntity p, float yaw = 0f)
    {
        h.Send(p, new InputCommand { Yaw = yaw });
        h.Send(p, new InputCommand { Buttons = InputButtons.Interact, Yaw = yaw });
    }

    // 기능: 점 둘레(수평 radius) 안의 월드 아이템 수를 센다.
    // 입력: m - 경기, at - 점, radius - 반지름.
    // 출력: 아이템 수.
    private static int ItemsNear(Match m, Vector3 at, float radius = 2f)
    {
        int count = 0;
        for (int i = 0; i < m.WorldItems.Count; i++)
        {
            Vector3 d = m.WorldItems[i].Data.Position - at;
            if (d.X * d.X + d.Z * d.Z <= radius * radius) count++;
        }
        return count;
    }

    // 기능: Container가 열렸는지 본다.
    // 입력: m - 경기, id - Container id.
    // 출력: 열렸으면 true.
    private static bool IsOpen(Match m, int id) => (m.ContainerOpenedMask & (1UL << id)) != 0;

    [Fact]
    public void SameSeed_SameContainers_NextRound_Differs()
    {
        static (ulong, string) Of(RoyaleHarness h)
        {
            string loot = string.Join(";", Enumerable.Range(0, LootContainers.Count).Select(i =>
                string.Join(",", h.Match.ContainerLoot(i).ToArray().Select(r => $"{r.Kind}/{r.DefId}/{r.Rarity}/{r.Amount}"))));
            return (h.Match.ContainerSpawnedMask, loot);
        }
        var first = Started(out _, out _);
        var second = Started(out _, out _);
        Assert.Equal(Of(first), Of(second));
        Assert.NotEqual(0UL, first.Match.ContainerSpawnedMask);
        Assert.Equal(0UL, first.Match.ContainerOpenedMask);

        var round1 = Of(first);
        first.Match.Flow.Eliminate();
        first.Match.Flow.Finish(first.Match.ServerTick);
        first.TickUntil(() => first.Match.Flow.Round == 2, RoyaleHarness.ResultTicks + 5);
        Assert.Equal(0UL, first.Match.ContainerSpawnedMask);   // the lobby has none
        first.RunToMatch();
        Assert.NotEqual(round1, Of(first));
    }

    // D2 regression: opening containers never changes the floor loot (its items and its own Random stream).
    [Fact]
    public void OpeningAContainer_LeavesTheFloorLootAlone()
    {
        var h = Started(out var a, out _);
        string FloorItems() => string.Join(";", Enumerable.Range(0, h.Match.WorldItems.Count).Select(i => h.Match.WorldItems[i])
            .Where(w => w.SpawnPoint >= 0).Select(w => $"{w.Data.ItemId}/{w.Data.Kind}/{w.Data.DefId}/{w.Data.Rarity}/{w.Data.Amount}/{w.Data.Position}"));
        string before = FloorItems();
        Assert.True(h.Match.QaSetContainer(Chest, 1));
        h.Place(a, InFront(LootContainers.All[Chest].Position));
        Press(h, a);
        h.Ticks(2);
        Assert.True(IsOpen(h.Match, Chest));
        Assert.Equal(before, FloorItems());
    }

    [Fact]
    public void Opening_SpawnsTheRolledLootAround_AndTellsEveryone()
    {
        var h = Started(out var a, out var b);
        Assert.True(h.Match.QaSetContainer(Chest, 1));
        LootRoll[] loot = h.Match.ContainerLoot(Chest).ToArray();
        Assert.Equal(3, loot.Length);
        Vector3 at = LootContainers.All[Chest].Position;
        int itemsBefore = h.Match.WorldItems.Count;
        h.Ticks(1);   // the forced state goes out first
        h.Packets.Clear();
        h.Place(a, InFront(at));
        Press(h, a);
        h.Ticks(2);

        Assert.True(IsOpen(h.Match, Chest));
        Assert.Equal(itemsBefore + 3, h.Match.WorldItems.Count);
        Assert.Equal(3, ItemsNear(h.Match, at, 1.3f));
        Assert.Equal(1, h.Match.ContainersOpened);
        foreach (int peer in new[] { 1, 2 })
        {
            var states = h.SentTo(peer, PacketId.ContainerStates);
            Assert.Single(states);
            var r = RoyaleHarness.Reader(states[0]);
            Assert.True(ContainerStatesPacket.TryRead(ref r, out ulong spawned, out ulong opened));
            Assert.True((opened & (1UL << Chest)) != 0);
            Assert.Equal(h.Match.ContainerSpawnedMask, spawned);
            Assert.Equal(3, h.SentTo(peer, PacketId.ItemSpawned).Count);
        }
        // Dropped-item rules: not a loot point's item.
        for (int i = itemsBefore; i < h.Match.WorldItems.Count; i++) Assert.True(h.Match.WorldItems[i].IsDropped);
    }

    // D4 (request §60): every server check.
    [Fact]
    public void Open_IsRefused_OutOfReach_FacingAway_NotSpawned_AlreadyOpen_Dead()
    {
        var h = Started(out var a, out var b);
        Vector3 at = LootContainers.All[Chest].Position;
        Assert.True(h.Match.QaSetContainer(Chest, 1));

        h.Place(a, InFront(at, 2.8f));            // too far
        Press(h, a);
        h.Ticks(2);
        Assert.False(IsOpen(h.Match, Chest));

        h.Place(a, InFront(at));                  // facing away
        Press(h, a, 180f);
        h.Ticks(2);
        Assert.False(IsOpen(h.Match, Chest));

        Assert.True(h.Match.QaSetContainer(Chest, 0));   // not spawned
        h.Ticks(1);
        Press(h, a);
        h.Ticks(2);
        Assert.False(IsOpen(h.Match, Chest));
        Assert.Equal(0, h.Match.ContainersOpened);

        Assert.True(h.Match.QaSetContainer(Chest, 2));   // already open: E picks up instead, no loot
        int items = h.Match.WorldItems.Count;
        h.Ticks(1);
        Press(h, a);
        h.Ticks(2);
        Assert.Equal(items, h.Match.WorldItems.Count);

        Assert.True(h.Match.QaSetContainer(Chest, 1));   // dead
        Assert.True(h.Match.KillPlayer(b) || !b.Alive);
        h.Place(b, InFront(at));
        Press(h, b);
        h.Ticks(2);
        Assert.False(IsOpen(h.Match, Chest));
    }

    [Fact]
    public void Open_IsRefused_BehindAPiece_AndCountedAsBlocked()
    {
        var h = Started(out var a, out _);
        Vector3 at = LootContainers.All[Chest].Position;
        Assert.True(h.Match.QaSetContainer(Chest, 1));
        // A wall on the grid line between the player (z 14) and the chest (z 16): z = 15 is not a grid line, so use the cell
        // edge the chest's cell starts at.
        int cellX = (int)MathF.Floor((at.X + GameMap.HalfSize) / BuildGrid.CellSize);
        int cellZ = (int)MathF.Floor((at.Z + GameMap.HalfSize) / BuildGrid.CellSize);
        Assert.True(BuildGrid.TryNormalize(BuildPieceType.Wall, cellX, 0, cellZ, 0, out BuildPieceShape wall));
        Assert.Equal(BuildResultCode.Ok, h.Match.PlacePiece(wall, BuildMaterialType.Wood, out _));
        Box bounds = BuildGrid.BoundsOf(wall);
        // Stand south of the wall, the chest north of it.
        Assert.True(bounds.Max.Z <= at.Z + 0.3f && bounds.Min.Z > at.Z - 2.5f, $"wall {bounds.Min.Z}..{bounds.Max.Z}, chest z {at.Z}");
        h.Place(a, new Vector3(at.X, at.Y, bounds.Min.Z - 0.5f));
        // Phase 16 review: an item lies at the player's feet. A blocked container press must not fall through to a pickup
        // (the client shows "[E] 열기" there and no pickup prompt).
        Vector3 feet = a.State.Position;
        Assert.NotEqual((ushort)0, h.Match.SpawnItem(new LootRoll(ItemKind.Consumable, (byte)ConsumableType.Medkit, 0, 1), feet + new Vector3(0.5f, 0f, -0.5f), -1));
        int items = h.Match.WorldItems.Count;
        h.Packets.Clear();
        Press(h, a);
        h.Ticks(2);
        Assert.False(IsOpen(h.Match, Chest));
        Assert.Equal(1, h.Match.ContainerOpensBlocked);
        Assert.Equal(items, h.Match.WorldItems.Count);
        Assert.Equal(0, a.Inventory.Medkits);
        Assert.Empty(h.SentTo(1, PacketId.PickupResult));
    }

    // Phase 16 review: a zone whose first circle is its last (radius 0), so every supply drop candidate is the circle's centre;
    // no damage for 100 s.
    private const string PointZoneJson = """
        { "initialCenter": [0, 0], "initialRadius": 30, "arenaHalfSize": 19.5,
          "phases": [ { "waitSeconds": 100, "shrinkSeconds": 10, "targetRadius": 0, "damagePerSecond": 1 } ] }
        """;

    // 기능: 반지름 0 원의 경기를 만들고(supplyDrops 일정 = times, 기본 1초) 두 플레이어를 원 중심에서 offset만큼 옮긴다.
    // 입력: offset - 중심에서의 수평 거리, center - 결과 중심, a·b - 플레이어, times - loot.json supplyDrops.times 배열 내용.
    // 출력: 경기 중인 하네스.
    private static RoyaleHarness PointZoneMatch(float offset, out Vector2 center, out PlayerEntity a, out PlayerEntity b, string times = "1")
    {
        string loot = TestGameData.LootJson.Replace("\"times\": [ 60, 150 ]", "\"times\": [ " + times + " ]");
        Assert.NotEqual(TestGameData.LootJson, loot);
        var h = new RoyaleHarness(zonesJson: PointZoneJson, lootJson: loot);
        a = h.Join(1);
        b = h.Join(2);
        h.RunToMatch();
        int circle = h.Match.Zone.Phase == 0 ? 1 : h.Match.Zone.Phase;
        center = new Vector2(h.Match.Zone.CenterX(circle), h.Match.Zone.CenterZ(circle));
        Assert.Equal(0f, h.Match.Zone.Radius(circle));
        Assert.True(h.Match.SpotObstaclesClear(center.X, center.Y), $"test zone centre {center} is not obstacle-clear; pick another seed");
        h.Place(a, new Vector3(center.X + offset, GameMap.Terrain.Height(center.X + offset, center.Y), center.Y));
        h.Place(b, new Vector3(center.X - offset, GameMap.Terrain.Height(center.X - offset, center.Y), center.Y));
        return h;
    }

    // Phase 16 review: no candidate is 15 m from the players, but the centre is clear of the map and 3 m+ from them: the
    // second pass takes it (never the old unchecked fallback).
    [Fact]
    public void SpotRule_SecondPass_DropsThe15mRule_ButKeeps3m()
    {
        var h = PointZoneMatch(5f, out Vector2 center, out _, out _);
        h.TickUntil(() => h.Match.SupplyDropCount == 1, 60);
        SupplyDropInfo d = h.Match.SupplyDropAt(0);
        Assert.Equal(center.X, d.X);
        Assert.Equal(center.Y, d.Z);
    }

    // Phase 16 review: with a player within 3 m of the only spot nothing spawns (the QA command answers -1, the schedule waits
    // and tries again every tick); once the player moves away it spawns on the next tick.
    [Fact]
    public void SpotRule_NoClearSpot_WaitsAndRetries()
    {
        var h = PointZoneMatch(1f, out Vector2 center, out var a, out var b);
        Assert.Equal(-1, h.Match.QaSpawnSupplyDrop(null));
        h.Ticks(60);   // the 1 s schedule is long due
        Assert.Equal(0, h.Match.SupplyDropCount);
        h.Place(a, new Vector3(center.X + 6f, GameMap.Terrain.Height(center.X + 6f, center.Y), center.Y));
        h.Place(b, new Vector3(center.X - 6f, GameMap.Terrain.Height(center.X - 6f, center.Y), center.Y));
        uint tick = h.Match.ServerTick;
        h.Ticks(1);
        Assert.Equal(1, h.Match.SupplyDropCount);
        Assert.Equal(tick + 1, h.Match.SupplyDropAt(0).StartTick);
        Assert.Equal(center.X, h.Match.SupplyDropAt(0).X);
        // The schedule held only one drop: no second one follows.
        h.Ticks(30);
        Assert.Equal(1, h.Match.SupplyDropCount);
    }

    // Review fix: a drop that finds no clear spot for SupplyDropRetrySeconds (10 s) is skipped, so the next one on the
    // schedule still comes once a spot is clear.
    [Fact]
    public void SpotRule_NoClearSpotFor10s_SkipsThatDrop_TheNextOneStillComes()
    {
        var h = PointZoneMatch(1f, out Vector2 center, out var a, out var b, times: "1, 14");
        h.Ticks(30 * 12);   // past 1 s + 10 s, before 14 s
        Assert.Equal(0, h.Match.SupplyDropCount);
        Assert.Equal(1, h.Match.SupplyDropsSkipped);
        h.Place(a, new Vector3(center.X + 6f, GameMap.Terrain.Height(center.X + 6f, center.Y), center.Y));
        h.Place(b, new Vector3(center.X - 6f, GameMap.Terrain.Height(center.X - 6f, center.Y), center.Y));
        h.TickUntil(() => h.Match.SupplyDropCount == 1, 30 * 4);
        Assert.Equal(1, h.Match.SupplyDropCount);
        Assert.Equal(1, h.Match.SupplyDropsSkipped);
    }

    // Review fix: a supply drop lands through building pieces (D6), so a piece between the player and a landed drop does
    // not stop it opening (a container behind a piece stays blocked).
    [Fact]
    public void SupplyDrop_OpensThroughABuildingPiece()
    {
        var h = Started(out var a, out _);
        // A wall on the grid line z = 10 (cells are 5 m from -80), the drop 0.6 m north of it, the player south of it.
        var at = new Vector2(12.5f, 10.6f);
        Assert.Equal(0, h.Match.QaSpawnSupplyDrop(at));
        h.TickUntil(() => h.Match.SupplyDropAt(0).State == SupplyDropState.Landed, 460);
        int cellX = (int)MathF.Floor((at.X + GameMap.HalfSize) / BuildGrid.CellSize);
        int cellZ = (int)MathF.Floor((at.Y + GameMap.HalfSize) / BuildGrid.CellSize);
        Assert.True(BuildGrid.TryNormalize(BuildPieceType.Wall, cellX, 0, cellZ, 0, out BuildPieceShape wall));
        Assert.Equal(BuildResultCode.Ok, h.Match.PlacePiece(wall, BuildMaterialType.Wood, out _));
        Box bounds = BuildGrid.BoundsOf(wall);
        Assert.True(bounds.Min.Z < at.Y && bounds.Max.Z <= at.Y, $"wall {bounds.Min.Z}..{bounds.Max.Z}, drop z {at.Y}");
        float z = bounds.Min.Z - 0.5f;
        h.Place(a, new Vector3(at.X, GameMap.Terrain.Height(at.X, z), z));
        Press(h, a);
        h.Ticks(2);
        Assert.Equal(SupplyDropState.Opened, h.Match.SupplyDropAt(0).State);
        Assert.Equal(0, h.Match.ContainerOpensBlocked);
    }

    // D4: only during the match (or in the dev sandbox): not on the result screen.
    [Fact]
    public void Open_IsRefused_OnTheResultScreen()
    {
        var h = Started(out var a, out _);
        Assert.True(h.Match.QaSetContainer(Chest, 1));
        Assert.True(h.Match.ForceFinish());
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        h.Place(a, InFront(LootContainers.All[Chest].Position));
        Press(h, a);
        h.Ticks(2);
        Assert.False(IsOpen(h.Match, Chest));
        Assert.False(h.Match.QaSetContainer(Chest, 1));
    }

    // D4: two players pressing in one tick: the first in player order opens it, the second gets nothing more; a repeated
    // press does nothing either. The loot spawns once.
    [Fact]
    public void TwoPlayersInOneTick_AndRepeatedPresses_OpenItOnce()
    {
        var h = Started(out var a, out var b);
        Assert.True(h.Match.QaSetContainer(Chest, 1));
        Vector3 at = LootContainers.All[Chest].Position;
        int items = h.Match.WorldItems.Count;
        h.Place(a, InFront(at));
        h.Place(b, new Vector3(at.X + 0.6f, InFront(at).Y, at.Z - 2f));
        Press(h, a);
        Press(h, b);
        h.Ticks(2);
        Assert.True(IsOpen(h.Match, Chest));
        Assert.Equal(1, h.Match.ContainersOpened);
        // b's E went on to a pickup (an item just spawned may be taken), but no second set of loot appeared.
        Assert.InRange(h.Match.WorldItems.Count, items + 2, items + 3);
        for (int i = 0; i < 5; i++)
        {
            h.Send(a, new InputCommand { Yaw = 0f });
            h.Match.Tick();
            Press(h, a);
            h.Ticks(2);
        }
        Assert.Equal(1, h.Match.ContainersOpened);
        Assert.True(h.Match.ContainerLootItems == 3);
    }

    // D4, Phase 14 D7: a revive target in reach takes E before the container.
    [Fact]
    public void AReviveTargetInReach_TakesEBeforeTheContainer()
    {
        var h = new RoyaleHarness(maxPlayers: 4, minPlayers: 4, teamSize: 2);
        PlayerEntity a = h.Join(1), b = h.Join(2);
        h.Join(3);
        h.Join(4);
        h.RunToMatch();
        Assert.Equal(a.TeamId, b.TeamId);
        Assert.True(h.Match.QaSetContainer(Chest, 1));
        Vector3 at = LootContainers.All[Chest].Position;
        h.Place(a, InFront(at));
        h.Place(b, new Vector3(at.X + 1f, InFront(at).Y, at.Z - 2f));
        Assert.True(h.Match.DownPlayer(b));
        Press(h, a);
        h.Ticks(2);
        Assert.False(IsOpen(h.Match, Chest));
        // A knocked-down player cannot open it either.
        h.Place(b, InFront(at));
        Press(h, b);
        h.Ticks(2);
        Assert.False(IsOpen(h.Match, Chest));
        // Positive control: with the downed teammate out of reach, the same press of A opens it.
        h.Place(b, new Vector3(at.X + 10f, GameMap.Terrain.Height(at.X + 10f, at.Z - 2f), at.Z - 2f));
        Press(h, a);
        h.Ticks(2);
        Assert.True(IsOpen(h.Match, Chest));
    }

    // D4 in a match: a door and a landed supply drop both in reach (Rustvale's first door, (-54, 50.25), and a drop 3.1 m
    // east of it outside the house). The nearer one takes E; the other is left as it was.
    [Fact]
    public void DoorAndContainerInReach_TheNearerOneTakesE()
    {
        var h = Started(out var a, out _);
        Assert.Equal(0, h.Match.QaSpawnSupplyDrop(new Vector2(-51f, 49.5f)));
        h.TickUntil(() => h.Match.SupplyDropAt(0).State == SupplyDropState.Landed, 460);

        // Door 1.92 m, drop 2.42 m away, both within 45 degrees of yaw 20: the door opens, the drop stays closed.
        h.Place(a, new Vector3(-53.2f, GameMap.Terrain.Height(-53.2f, 48.5f), 48.5f));
        Assert.False(h.Match.Doors.IsOpen(0));
        Press(h, a, 20f);
        h.Ticks(2);
        Assert.True(h.Match.Doors.IsOpen(0));
        Assert.Equal(SupplyDropState.Landed, h.Match.SupplyDropAt(0).State);

        // Door 2.40 m, drop 2.00 m away, both within 45 degrees of yaw 9: the drop opens, the door is left open.
        h.Place(a, new Vector3(-52.6f, GameMap.Terrain.Height(-52.6f, 48.3f), 48.3f));
        Press(h, a, 9f);
        h.Ticks(2);
        Assert.Equal(SupplyDropState.Opened, h.Match.SupplyDropAt(0).State);
        Assert.True(h.Match.Doors.IsOpen(0));
    }

    // D5: the container tables' rules hold for many seeds.
    [Fact]
    public void LootTables_FollowTheirRules()
    {
        GameData data = TestGameData.Create();
        LootTable t = data.Loot;
        var output = new LootRoll[LootTable.MaxRolls];
        var rng = new Random(16);
        for (int n = 0; n < 2000; n++)
        {
            int count = t.RollAll(t.TableIndex("Chest"), rng, data.Weapons, data.Items, output);
            Assert.Equal(3, count);
            Assert.Equal(ItemKind.Weapon, output[0].Kind);
            for (int i = 1; i < count; i++)
            {
                Assert.NotEqual(ItemKind.Weapon, output[i].Kind);
                if (output[i].Kind == ItemKind.Material)
                {
                    Assert.Equal(30, output[i].Amount);
                    Assert.InRange(output[i].DefId, 1, 3);
                }
            }

            count = t.RollAll(t.TableIndex("AmmoBox"), rng, data.Weapons, data.Items, output);
            Assert.Equal(2, count);
            Assert.Equal(ItemKind.Ammo, output[0].Kind);
            Assert.Equal(ItemKind.Material, output[1].Kind);
            Assert.Equal(10, output[1].Amount);

            count = t.RollAll(t.TableIndex("SupplyDrop"), rng, data.Weapons, data.Items, output);
            Assert.Equal(4, count);
            Assert.Equal(ItemKind.Weapon, output[0].Kind);
            Assert.InRange(output[0].Rarity, 3, 4);   // Epic or Legendary only
            for (int i = 1; i < count; i++) Assert.True(output[i].Kind is ItemKind.Ammo or ItemKind.Consumable);
        }
    }

    [Fact]
    public void SpawnChance_RoughlyFollowsLootJson()
    {
        int chests = 0, chestSpawned = 0, boxes = 0, boxSpawned = 0;
        for (int seed = 0; seed < 40; seed++)
        {
            var h = new SandboxHarness(options: new ServerOptions { MaxPlayers = 4, DevRespawn = true, LootSeed = seed });
            ulong mask = h.Match.ContainerSpawnedMask;
            for (int i = 0; i < LootContainers.Count; i++)
            {
                bool spawned = (mask & (1UL << i)) != 0;
                if (LootContainers.All[i].Kind == LootContainerKind.Chest) { chests++; if (spawned) chestSpawned++; }
                else { boxes++; if (spawned) boxSpawned++; }
            }
        }
        Assert.InRange(chestSpawned / (double)chests, 0.6, 0.8);
        Assert.InRange(boxSpawned / (double)boxes, 0.7, 0.9);
    }

    // D2: the dev sandbox rolls its containers once at start; they open there; it has no supply drops.
    [Fact]
    public void DevSandbox_HasContainers_ThatOpen_AndNoSupplyDrops()
    {
        var h = new SandboxHarness();
        Assert.NotEqual(0UL, h.Match.ContainerSpawnedMask);
        Assert.True(h.Match.QaSetContainer(AmmoBox, 1));
        Vector3 at = LootContainers.All[AmmoBox].Position;
        PlayerEntity p = h.Join(1, InFront(at));
        h.Press(p, InputButtons.Interact);
        Assert.True(IsOpen(h.Match, AmmoBox));
        Assert.Equal(-1, h.Match.QaSpawnSupplyDrop(null));
        h.Ticks(5);
        Assert.Equal(0, h.Match.SupplyDropCount);
    }

    [Fact]
    public void JoinAndResume_GetContainerStatesAndSupplyDrops()
    {
        var h = new RoyaleHarness(reconnectGraceSeconds: 10);
        PlayerEntity a = h.Join(1);
        h.Join(2);
        h.RunToMatch();
        Assert.True(h.Match.QaSpawnSupplyDrop(new Vector2(10f, 10f)) == 0);
        h.Ticks(1);
        PlayerEntity late = h.Join(3);
        var states = h.SentTo(3, PacketId.ContainerStates);
        var drops = h.SentTo(3, PacketId.SupplyDrops);
        Assert.Single(states);
        Assert.Single(drops);
        var r = RoyaleHarness.Reader(drops[0]);
        var list = new SupplyDropInfo[4];
        Assert.True(SupplyDropsPacket.TryRead(ref r, list, out int count));
        Assert.Equal(1, count);
        // The order of the join bundle: harvest states, then containers, then supply drops.
        int harvest = h.Packets.FindIndex(s => s.PeerId == 3 && s.Id == PacketId.HarvestStates);
        int containers = h.Packets.FindIndex(s => s.PeerId == 3 && s.Id == PacketId.ContainerStates);
        int dropsAt = h.Packets.FindIndex(s => s.PeerId == 3 && s.Id == PacketId.SupplyDrops);
        Assert.True(harvest < containers && containers < dropsAt);

        Assert.True(h.Match.Disconnect(1, allowGrace: true));
        h.Packets.Clear();
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(11, a.DevPlayerId));
        Assert.Single(h.SentTo(11, PacketId.ContainerStates));
        Assert.Single(h.SentTo(11, PacketId.SupplyDrops));
        Assert.NotNull(late);
    }

    // D6: the schedule (zone clock + 60 s), inside 0.6 x the next circle, 15 m from living players, the fall, landing.
    [Fact]
    public void SupplyDrop_FollowsTheSchedule_LandsAfterTheFall()
    {
        var h = Started(out var a, out var b);
        uint start = h.Match.ZoneClockStart;
        h.TickUntil(() => h.Match.SupplyDropCount == 1, 60 * 30 + 5);
        SupplyDropInfo d = h.Match.SupplyDropAt(0);
        Assert.Equal(start + 60u * 30u, d.StartTick);
        Assert.Equal(d.StartTick + 450u, d.LandTick);
        Assert.Equal(SupplyDropState.Falling, d.State);
        Assert.Equal(GameMap.Terrain.Height(d.X, d.Z), d.LandY);
        Assert.InRange(d.X, -60f, 60f);
        Assert.InRange(d.Z, -60f, 60f);
        int circle = h.Match.Zone.Phase == 0 ? 1 : h.Match.Zone.Phase;
        float cx = h.Match.Zone.CenterX(circle), cz = h.Match.Zone.CenterZ(circle), radius = h.Match.Zone.Radius(circle) * 0.6f;
        Assert.True(new Vector2(d.X - cx, d.Z - cz).Length() <= radius + 1e-3f);
        // Clear of the map things always; 15 m from the players in the first pass, at least 3 m in the second (the players
        // stand on the lobby ring near the centre of the small test zone, so the second pass may be the one that found it).
        Assert.True(MapThingsClear(d.X, d.Z), $"drop at ({d.X}, {d.Z}) is within 2 m of a map thing");
        foreach (var p in new[] { a, b }) Assert.True(new Vector2(d.X - p.State.Position.X, d.Z - p.State.Position.Z).Length() >= 3f);
        Assert.True(h.SentTo(1, PacketId.SupplyDrops).Count >= 2);

        h.TickUntil(() => h.Match.SupplyDropAt(0).State == SupplyDropState.Landed, 460);
        Assert.Equal(d.LandTick, h.Match.ServerTick);
        Assert.Equal(1, h.Match.SupplyDropsLanded);
    }

    // 기능: 점이 맵 상자 발자국·문·채집 대상·Container·스테이션·Loot 지점과 XZ 2 m 떨어졌는지 테스트 쪽에서 따로 계산한다
    //   (서버의 SpotObstaclesClear는 이미 있는 Supply Drop도 보므로, 놓인 Drop 자신을 검사할 수 없다).
    // 입력: x, z - 점.
    // 출력: 떨어졌으면 true.
    private static bool MapThingsClear(float x, float z)
    {
        static float Gap(float x, float z, in Box b)
        {
            float dx = MathF.Max(0f, MathF.Max(b.Min.X - x, x - b.Max.X));
            float dz = MathF.Max(0f, MathF.Max(b.Min.Z - z, z - b.Max.Z));
            return MathF.Sqrt(dx * dx + dz * dz);
        }
        foreach (Box box in GameMap.Boxes) if (Gap(x, z, box) < 2f) return false;
        foreach (Box door in GameMap.Doors) if (Gap(x, z, door) < 2f) return false;
        foreach (Harvestable hv in GameMap.Harvestables) if (Gap(x, z, hv.Bounds) < 2f) return false;
        foreach (LootContainer c in LootContainers.All) if (Gap(x, z, c.Bounds) < 2f) return false;
        foreach (Vector3 st in RebootStations.All) if (new Vector2(st.X - x, st.Z - z).Length() < 2f) return false;
        foreach (LootPoint l in LootPoints.All) if (new Vector2(l.Position.X - x, l.Position.Z - z).Length() < 2f) return false;
        return true;
    }

    // D6: a candidate spot is refused near the map's things and near living players.
    [Fact]
    public void SpotRule_KeepsClearOfMapThingsAndPlayers()
    {
        var h = Started(out var a, out _);
        Assert.False(h.Match.SupplyDropSpotClear(LootContainers.All[Chest].Position.X + 1f, LootContainers.All[Chest].Position.Z));
        Assert.False(h.Match.SupplyDropSpotClear(RebootStations.All[0].X, RebootStations.All[0].Z + 1.5f));
        Assert.False(h.Match.SupplyDropSpotClear(GameMap.Doors[0].Center.X, GameMap.Doors[0].Center.Z));
        Assert.False(h.Match.SupplyDropSpotClear(a.State.Position.X + 10f, a.State.Position.Z));   // 10 m from a player
    }

    [Fact]
    public void SupplyDrop_CannotOpenBeforeLanding_OpensAfter_WithAnEpicOrBetterWeapon()
    {
        var h = Started(out var a, out _);
        var at2 = new Vector2(6f, 26f);
        Assert.Equal(0, h.Match.QaSpawnSupplyDrop(at2));
        SupplyDropInfo d = h.Match.SupplyDropAt(0);
        var land = new Vector3(d.X, d.LandY, d.Z);
        h.Place(a, InFront(land));
        Press(h, a);
        h.Ticks(2);
        Assert.Equal(SupplyDropState.Falling, h.Match.SupplyDropAt(0).State);
        Assert.Equal(0, h.Match.SupplyDropsOpened);

        h.TickUntil(() => h.Match.SupplyDropAt(0).State == SupplyDropState.Landed, 460);
        int items = h.Match.WorldItems.Count;
        h.Place(a, InFront(land));
        Press(h, a);
        h.Ticks(2);
        Assert.Equal(SupplyDropState.Opened, h.Match.SupplyDropAt(0).State);
        Assert.Equal(1, h.Match.SupplyDropsOpened);
        Assert.Equal(items + 4, h.Match.WorldItems.Count);
        bool epicWeapon = false;
        for (int i = items; i < h.Match.WorldItems.Count; i++)
        {
            var item = h.Match.WorldItems[i].Data;
            if (item.Kind == ItemKind.Weapon && item.Rarity >= 3) epicWeapon = true;
        }
        Assert.True(epicWeapon);
    }

    // D6: nothing spawns, lands or opens on the result screen; the round reset and the next match start clear the list.
    [Fact]
    public void SupplyDrops_StopOnTheResultScreen_AndAreClearedAtTheReset()
    {
        var h = Started(out var a, out _);
        Assert.Equal(0, h.Match.QaSpawnSupplyDrop(new Vector2(6f, 26f)));
        Assert.True(h.Match.ForceFinish());
        h.Ticks(5);
        Assert.Equal(-1, h.Match.QaSpawnSupplyDrop(null));
        uint landTick = h.Match.SupplyDropAt(0).LandTick;
        Assert.Equal(SupplyDropState.Falling, h.Match.SupplyDropAt(0).State);
        h.TickUntil(() => h.Match.Flow.Round == 2, RoyaleHarness.ResultTicks + 5);
        Assert.Equal(0, h.Match.SupplyDropCount);
        Assert.Equal(0UL, h.Match.ContainerSpawnedMask);
        Assert.True(h.Match.ServerTick < landTick);
        h.RunToMatch();
        Assert.Equal(0, h.Match.SupplyDropCount);
    }

    [Fact]
    public void AtMostFourSupplyDrops()
    {
        var h = Started(out _, out _);
        for (int i = 0; i < 4; i++) Assert.Equal(i, h.Match.QaSpawnSupplyDrop(new Vector2(-40f + 20f * i, -30f)));
        Assert.Equal(-1, h.Match.QaSpawnSupplyDrop(null));
    }

    // Without container tables in the loot data (old test data), nothing spawns and nothing breaks.
    [Fact]
    public void WithoutContainerTables_NoContainersSpawn()
    {
        var h = Started(out _, out _, TestGameData.WeaponsOnlyLootJson);
        Assert.Equal(0UL, h.Match.ContainerSpawnedMask);
        Assert.False(h.Match.QaSetContainer(Chest, 1));
        h.Ticks(5);
        Assert.Equal(0, h.Match.SupplyDropCount);   // no supplyDrops schedule in that data
    }

    // server-hotpath: falling and landed supply drops, the schedule check and E presses that find nothing allocate nothing.
    [Fact]
    public void LootTicks_AllocateNothing()
    {
        var h = new RoyaleHarness(record: false);
        PlayerEntity a = h.Join(1);
        h.Join(2);
        h.RunToMatch();
        h.Match.QaSpawnSupplyDrop(new Vector2(6f, 26f));
        h.Match.QaSpawnSupplyDrop(new Vector2(-30f, 30f));
        h.Place(a, InFront(LootContainers.All[Chest].Position, 2.8f));   // out of reach: E goes to a pickup
        void Round()
        {
            Press(h, a);
            h.Ticks(2);
            h.Send(a, new InputCommand { Yaw = 0f });
            h.Ticks(9);
        }
        for (int i = 0; i < 60; i++) Round();   // the drops land during the warm-up
        Assert.Equal(SupplyDropState.Landed, h.Match.SupplyDropAt(0).State);
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 30; i++) Round();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }
}
