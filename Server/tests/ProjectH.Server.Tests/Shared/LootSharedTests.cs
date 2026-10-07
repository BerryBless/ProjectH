using System;
using System.Collections.Generic;
using System.Numerics;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 16 D1: the loot containers are map data next to GameMap and must stay valid whenever either changes.
public class LootContainersTests
{
    private const float BoxMargin = 0.3f;           // a container never touches a map box
    private const float HarvestableClear = 1f;      // footprint to footprint
    private const float LootPointClear = 2f;        // the pickup range: E must not be ambiguous between an item and a container
    private const float StationClear = RebootStations.ClearRadius + 1f;
    private const float ContainerSpacing = 3f;      // two containers never share one E reach
    private const float DoorClear = MovementTuning.DoorInteractRange + 1f;   // D4: centre to door centre

    [Fact]
    public void Count_Is20ChestsAnd14AmmoBoxes_AtMost64()
    {
        int chests = 0, ammo = 0;
        foreach (LootContainer c in LootContainers.All)
        {
            if (c.Kind == LootContainerKind.Chest) chests++;
            else if (c.Kind == LootContainerKind.AmmoBox) ammo++;
            else Assert.Fail("unknown kind");
        }
        Assert.Equal(20, chests);
        Assert.Equal(14, ammo);
        Assert.Equal(LootContainers.All.Length, LootContainers.Count);
        Assert.True(LootContainers.Count <= LootContainers.MaxCount);
    }

    [Fact]
    public void EveryContainer_FacesAWholeQuarterTurn_InsideTheWalls_OutsideThePlaza()
    {
        foreach (LootContainer c in LootContainers.All)
        {
            Assert.Contains(c.Yaw, new[] { 0f, 90f, 180f, 270f });
            Assert.True(MathF.Abs(c.Position.X) < GameMap.HalfSize - 2f && MathF.Abs(c.Position.Z) < GameMap.HalfSize - 2f, $"{c.Position}");
            Assert.True(new Vector2(c.Position.X, c.Position.Z).Length() > GameMap.PlazaRadius + 2f, $"{c.Position} in the plaza");
        }
    }

    // On flat terrain: every corner of the footprint at the container's height (a box top would be checked here too).
    [Fact]
    public void EveryContainer_StandsOnFlatTerrain()
    {
        foreach (LootContainer c in LootContainers.All)
        {
            Assert.Equal(GameMap.Terrain.Height(c.Position.X, c.Position.Z), c.Position.Y, 3);
            Box b = c.Bounds;
            foreach (var (x, z) in new[] { (b.Min.X, b.Min.Z), (b.Min.X, b.Max.Z), (b.Max.X, b.Min.Z), (b.Max.X, b.Max.Z) })
                Assert.True(MathF.Abs(GameMap.Terrain.Height(x, z) - c.Position.Y) < 0.1f, $"{c.Position} on a slope");
        }
    }

    [Fact]
    public void EveryContainer_IsClearOfTheBoxesDoorsAndHarvestables()
    {
        for (int i = 0; i < LootContainers.Count; i++)
        {
            LootContainer c = LootContainers.All[i];
            Box b = c.Bounds;
            var grown = new Box(b.Min - new Vector3(BoxMargin, 0f, BoxMargin), b.Max + new Vector3(BoxMargin, 0f, BoxMargin));
            foreach (Box box in GameMap.Boxes) Assert.False(Overlaps(grown, box), $"container {i} {c.Position} touches box {box.Min}..{box.Max}");
            foreach (Box door in GameMap.Doors)
            {
                Assert.False(Overlaps(grown, door), $"container {i} touches a door");
                Vector3 d = door.Center - c.Position;
                Assert.True(d.X * d.X + d.Z * d.Z > DoorClear * DoorClear, $"container {i} {c.Position} within {DoorClear} m of a door centre");
            }
            foreach (Harvestable h in GameMap.Harvestables)
                Assert.True(FootprintGap(b, h.Bounds) >= HarvestableClear, $"container {i} {c.Position} near harvestable {h.Bounds.Center}");
        }
    }

    [Fact]
    public void EveryContainer_IsAwayFromLootPointsStationsAndOtherContainers()
    {
        ReadOnlySpan<LootContainer> all = LootContainers.All;
        for (int i = 0; i < all.Length; i++)
        {
            foreach (LootPoint p in LootPoints.All)
                Assert.True(Horizontal(all[i].Position, p.Position) >= LootPointClear, $"container {i} {all[i].Position} near loot point {p.Position}");
            foreach (Vector3 s in RebootStations.All)
                Assert.True(Horizontal(all[i].Position, s) >= StationClear, $"container {i} near station {s}");
            for (int j = i + 1; j < all.Length; j++)
                Assert.True(Horizontal(all[i].Position, all[j].Position) >= ContainerSpacing, $"containers {i} and {j}");
        }
    }

    // Reachable on foot: some free standing spot on the terrain within E reach (DoorInteractRange across the ground, the
    // same floor) sees the container's centre from the eye past every box, door, harvestable and the terrain.
    [Fact]
    public void EveryContainer_HasAStandingSpotInReach_WithAClearLineOfSight()
    {
        var blockers = new List<Box>();
        foreach (Box box in GameMap.Boxes) blockers.Add(box);
        foreach (Box door in GameMap.Doors) blockers.Add(door);
        foreach (Harvestable h in GameMap.Harvestables) blockers.Add(h.Bounds);
        Box[] world = blockers.ToArray();
        for (int i = 0; i < LootContainers.Count; i++)
        {
            LootContainer c = LootContainers.All[i];
            bool found = false;
            for (int a = 0; a < 16 && !found; a++)
            {
                float angle = a * MathF.PI / 8f;
                for (float r = 1.2f; r <= 2.0f && !found; r += 0.4f)
                {
                    float x = c.Position.X + MathF.Sin(angle) * r;
                    float z = c.Position.Z + MathF.Cos(angle) * r;
                    var feet = new Vector3(x, GameMap.Terrain.Height(x, z), z);
                    if (MathF.Abs(feet.Y - c.Position.Y) > 0.5f || MovementSimulation.OverlapsAny(feet, world)) continue;
                    if (OverlapsFootprint(feet, c.Bounds)) continue;
                    Vector3 eye = feet + new Vector3(0f, CombatRules.EyeHeight, 0f);
                    Vector3 delta = c.Center - eye;
                    float distance = delta.Length();
                    found = HitScan.TraceWorld(eye, delta / distance, distance, world, GameMap.Terrain) >= distance - 1e-3f;
                }
            }
            Assert.True(found, $"container {i} {c.Position} cannot be reached");
        }
    }

    [Fact]
    public void Bounds_TurnWithTheYaw_AndCenterIsHalfwayUp()
    {
        var c = new LootContainer(LootContainerKind.Chest, new Vector3(10f, 2f, 20f), 90f);
        Box b = c.Bounds;
        Assert.Equal(0.6f, b.Max.X - b.Min.X, 4);
        Assert.Equal(1.0f, b.Max.Z - b.Min.Z, 4);
        Assert.Equal(2f, b.Min.Y);
        Assert.Equal(2.35f, c.Center.Y, 4);
        var a = new LootContainer(LootContainerKind.AmmoBox, Vector3.Zero, 0f);
        Assert.Equal(0.6f, a.Bounds.Max.X - a.Bounds.Min.X, 4);
        Assert.Equal(0.4f, a.Bounds.Max.Y, 4);
    }

    // 기능: 두 상자가 3차원으로 겹치는지 본다.
    // 입력: a, b - 상자.
    // 출력: 겹치면 true.
    private static bool Overlaps(in Box a, in Box b) =>
        a.Min.X < b.Max.X && a.Max.X > b.Min.X && a.Min.Y < b.Max.Y && a.Max.Y > b.Min.Y && a.Min.Z < b.Max.Z && a.Max.Z > b.Min.Z;

    // 기능: 두 상자의 바닥면 사이 지면 거리를 잰다.
    // 입력: a, b - 상자.
    // 출력: 거리(겹치면 0).
    private static float FootprintGap(in Box a, in Box b)
    {
        float dx = MathF.Max(0f, MathF.Max(a.Min.X - b.Max.X, b.Min.X - a.Max.X));
        float dz = MathF.Max(0f, MathF.Max(a.Min.Z - b.Max.Z, b.Min.Z - a.Max.Z));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    // 기능: 캐릭터 몸(발 위치 기준 가로 MoveSettings.HalfWidth)이 컨테이너 바닥면과 겹치는지 본다(서 있는 자리가 상자 안이면 안 된다).
    // 입력: feet - 발 위치, b - 컨테이너 상자.
    // 출력: 겹치면 true.
    private static bool OverlapsFootprint(Vector3 feet, in Box b) =>
        feet.X + MoveSettings.HalfWidth > b.Min.X && feet.X - MoveSettings.HalfWidth < b.Max.X &&
        feet.Z + MoveSettings.HalfWidth > b.Min.Z && feet.Z - MoveSettings.HalfWidth < b.Max.Z;

    // 기능: 두 점의 수평 거리를 구한다.
    // 입력: a, b - 점.
    // 출력: 거리.
    private static float Horizontal(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();
}

// Phase 16 D3, D6, D7: the new packets and the shared fall.
public class LootPacketTests
{
    [Fact]
    public void ContainerStates_RoundTrips_17Bytes()
    {
        var buffer = new byte[64];
        var writer = new PacketWriter(buffer);
        ulong spawned = (1UL << 33) | 0b1011;
        ulong opened = 0b0010;
        ContainerStatesPacket.Write(ref writer, spawned, opened);
        Assert.Equal(ContainerStatesPacket.Size, writer.Length);
        var reader = new PacketReader(buffer.AsSpan(0, writer.Length));
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(PacketId.ContainerStates, id);
        Assert.True(ContainerStatesPacket.TryRead(ref reader, out ulong s, out ulong o));
        Assert.Equal(spawned, s);
        Assert.Equal(opened, o);
    }

    [Theory]
    [InlineData(0UL, 1UL)]                  // opened but never spawned
    [InlineData(1UL << 63, 0UL)]            // a container the map does not have
    public void ContainerStates_RefusesWhatTheServerNeverSends(ulong spawned, ulong opened)
    {
        var buffer = new byte[64];
        var writer = new PacketWriter(buffer);
        ContainerStatesPacket.Write(ref writer, spawned, opened);
        var reader = new PacketReader(buffer.AsSpan(1, writer.Length - 1));
        Assert.False(ContainerStatesPacket.TryRead(ref reader, out _, out _));
        var shortReader = new PacketReader(buffer.AsSpan(1, 10));
        Assert.False(ContainerStatesPacket.TryRead(ref shortReader, out _, out _));
    }

    [Fact]
    public void SupplyDrops_RoundTrips_UpToFour()
    {
        var drops = new SupplyDropInfo[4];
        for (int i = 0; i < 4; i++)
            drops[i] = new SupplyDropInfo { Id = (byte)i, State = (SupplyDropState)(i % 3), X = 10f * i - 20f, Z = -5f * i, LandY = 1.5f, StartTick = 100u + (uint)i, LandTick = 550u + (uint)i };
        var buffer = new byte[SupplyDropsPacket.MaxSize];
        var writer = new PacketWriter(buffer);
        SupplyDropsPacket.Write(ref writer, drops);
        Assert.Equal(SupplyDropsPacket.MaxSize, writer.Length);
        Assert.False(writer.Overflowed);
        var reader = new PacketReader(buffer);
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(PacketId.SupplyDrops, id);
        var read = new SupplyDropInfo[4];
        Assert.True(SupplyDropsPacket.TryRead(ref reader, read, out int count));
        Assert.Equal(4, count);
        Assert.Equal(drops, read);
    }

    [Fact]
    public void SupplyDrops_EmptyList_Is2Bytes()
    {
        var buffer = new byte[8];
        var writer = new PacketWriter(buffer);
        SupplyDropsPacket.Write(ref writer, ReadOnlySpan<SupplyDropInfo>.Empty);
        Assert.Equal(2, writer.Length);
        var reader = new PacketReader(buffer.AsSpan(1, 1));
        Assert.True(SupplyDropsPacket.TryRead(ref reader, new SupplyDropInfo[4], out int count));
        Assert.Equal(0, count);
    }

    [Theory]
    [InlineData(4, 0, 0f, 10u, 20u)]            // id out of range
    [InlineData(0, 3, 0f, 10u, 20u)]            // unknown state
    [InlineData(0, 0, float.NaN, 10u, 20u)]     // non-finite
    [InlineData(0, 0, 200f, 10u, 20u)]          // outside the map
    [InlineData(0, 0, 0f, 20u, 10u)]            // lands before it starts
    public void SupplyDrops_RefusesWhatTheServerNeverSends(byte dropId, byte state, float x, uint start, uint land)
    {
        var drop = new SupplyDropInfo { Id = dropId, State = (SupplyDropState)state, X = x, Z = 0f, LandY = 0f, StartTick = start, LandTick = land };
        var buffer = new byte[SupplyDropsPacket.MaxSize];
        var writer = new PacketWriter(buffer);
        SupplyDropsPacket.Write(ref writer, new[] { drop });
        var reader = new PacketReader(buffer.AsSpan(1, writer.Length - 1));
        Assert.False(SupplyDropsPacket.TryRead(ref reader, new SupplyDropInfo[4], out _));
    }

    [Fact]
    public void SupplyDrops_RefusesRepeatedIds_ExtraBytes_AndASmallArray()
    {
        var drops = new[] { new SupplyDropInfo { Id = 1, LandTick = 5 }, new SupplyDropInfo { Id = 1, LandTick = 5 } };
        var buffer = new byte[SupplyDropsPacket.MaxSize + 1];
        var writer = new PacketWriter(buffer);
        SupplyDropsPacket.Write(ref writer, drops);
        var reader = new PacketReader(buffer.AsSpan(1, writer.Length - 1));
        Assert.False(SupplyDropsPacket.TryRead(ref reader, new SupplyDropInfo[4], out _));

        drops[1].Id = 2;
        writer = new PacketWriter(buffer);
        SupplyDropsPacket.Write(ref writer, drops);
        reader = new PacketReader(buffer.AsSpan(1, writer.Length));   // one byte too many
        Assert.False(SupplyDropsPacket.TryRead(ref reader, new SupplyDropInfo[4], out _));
        reader = new PacketReader(buffer.AsSpan(1, writer.Length - 1));
        Assert.False(SupplyDropsPacket.TryRead(ref reader, new SupplyDropInfo[1], out _));
        reader = new PacketReader(buffer.AsSpan(1, writer.Length - 1));
        Assert.True(SupplyDropsPacket.TryRead(ref reader, new SupplyDropInfo[4], out int count));
        Assert.Equal(2, count);
    }

    [Fact]
    public void Fall_StartsStartHeightUp_DescendsEvenly_AndStaysLanded()
    {
        Assert.Equal(60f, SupplyDropFall.StartHeight);
        Assert.Equal(63f, SupplyDropFall.HeightAt(3f, 100, 550, 50), 4);       // before the start: at the top
        Assert.Equal(63f, SupplyDropFall.HeightAt(3f, 100, 550, 100), 4);
        Assert.Equal(33f, SupplyDropFall.HeightAt(3f, 100, 550, 325), 4);      // halfway
        Assert.Equal(3f, SupplyDropFall.HeightAt(3f, 100, 550, 550), 4);
        Assert.Equal(3f, SupplyDropFall.HeightAt(3f, 100, 550, 9999), 4);
        Assert.Equal(3f, SupplyDropFall.HeightAt(3f, 100, 100, 100), 4);       // no fall at all
        // 60 m at 4 m/s = 15 s = 450 ticks at 30 Hz.
        Assert.Equal(450u, SupplyDropFall.FallTicks(4f, 30));
        Assert.Equal(1u, SupplyDropFall.FallTicks(1e6f, 30));
    }
}
