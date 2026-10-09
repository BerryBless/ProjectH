using System;
using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 13 D3: the piece store for collision (PieceGrid) and the world one step gathers (CollisionWorld).
public class CollisionWorldTests
{
    // 기능: 격자 좌표와 회전을 정규화해 테스트용 조각 모양을 만든다. 정규화에 실패하면 Assert로 테스트를 실패시킨다.
    // 입력: type - 조각 종류, x/y/z - 격자 좌표(셀·층), r - 회전(기본 0).
    // 출력: 정규화된 BuildPieceShape.
    private static BuildPieceShape Shape(BuildPieceType type, int x, int y, int z, int r = 0)
    {
        Assert.True(BuildGrid.TryNormalize(type, x, y, z, r, out BuildPieceShape s));
        return s;
    }

    // ---- PieceGrid ----

    [Fact]
    public void PieceGrid_AddsRemovesAndFindsById()
    {
        var grid = new PieceGrid(4);
        BuildPieceShape wall = Shape(BuildPieceType.Wall, 3, 0, 4);
        Assert.True(grid.TryAdd(7, wall, out int slot));
        Assert.False(grid.TryAdd(7, wall, out _));            // the id is taken
        Assert.True(grid.Contains(7));
        Assert.Equal(slot, grid.SlotOf(7));
        Assert.True(grid.TryGet(7, out BuildPieceShape back));
        Assert.Equal(wall, back);
        int version = grid.Version;
        Assert.True(grid.Remove(7));
        Assert.False(grid.Remove(7));
        Assert.False(grid.Contains(7));
        Assert.Equal(-1, grid.SlotOf(7));
        Assert.True(grid.Version > version);
        Assert.Equal(0, grid.Count);
    }

    [Fact]
    public void PieceGrid_IsBounded_AndClearEmptiesIt()
    {
        var grid = new PieceGrid(3);
        for (uint id = 1; id <= 3; id++) Assert.True(grid.TryAdd(id, Shape(BuildPieceType.Floor, (int)id, 0, 0), out _));
        Assert.False(grid.TryAdd(4, Shape(BuildPieceType.Floor, 9, 0, 0), out _));
        grid.Clear();
        Assert.Equal(0, grid.Count);
        Assert.Equal(-1, grid.First(1, 0));
        Assert.True(grid.TryAdd(4, Shape(BuildPieceType.Floor, 9, 0, 0), out _));
    }

    // A column is in id order whatever order its pieces arrive in (the client's sync chunks and events interleave).
    [Fact]
    public void PieceGrid_KeepsEachColumnInIdOrder()
    {
        var grid = new PieceGrid(16);
        uint[] ids = { 9, 3, 12, 5, 1 };
        for (int i = 0; i < ids.Length; i++) Assert.True(grid.TryAdd(ids[i], Shape(BuildPieceType.Floor, 2, i, 2), out _));
        Assert.True(grid.Remove(5));
        uint last = 0;
        int count = 0;
        for (int slot = grid.First(2, 2); slot >= 0; slot = grid.Next(slot))
        {
            Assert.True(grid.IdAt(slot) > last);
            last = grid.IdAt(slot);
            count++;
        }
        Assert.Equal(4, count);
        Assert.Equal(-1, grid.First(-1, 2));
        Assert.Equal(-1, grid.First(2, 32));
    }

    [Fact]
    public void PieceGrid_AddAndRemove_AllocateNothing()
    {
        var grid = new PieceGrid(64);
        BuildPieceShape floor = Shape(BuildPieceType.Floor, 4, 0, 4);
        for (uint id = 1; id <= 64; id++) grid.TryAdd(id, floor, out _);
        for (uint id = 1; id <= 64; id++) grid.Remove(id);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (uint id = 100; id < 164; id++) grid.TryAdd(id, floor, out _);
        for (uint id = 100; id < 164; id++) grid.Remove(id);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    // ---- CollisionWorld ----

    [Fact]
    public void Gather_TakesNearbyMapBoxes_AndOnlyClosedDoors_NamedByKindAndId()
    {
        // Door 0 is in the south wall of the Rustvale house at (-54, 54): x -54.75..-53.25, z 50.15..50.35.
        Box door = GameMap.Doors[0];
        var feet = new Vector3(door.Center.X, 0f, door.Min.Z - 1f);
        var world = new CollisionWorld();
        world.Gather(feet, 0, 0UL, null);
        Assert.Contains(new ColliderId(ColliderKind.Door, 0), world.BoxIds.ToArray());
        world.Gather(feet, 1, 0UL, null);
        Assert.DoesNotContain(new ColliderId(ColliderKind.Door, 0), world.BoxIds.ToArray());

        // Every map box within the radius is there, as Static with its index, in map order; far ones are not.
        int last = -1;
        for (int i = 0; i < world.Boxes.Length; i++)
        {
            ColliderId id = world.BoxIds[i];
            Assert.Equal(ColliderKind.Static, id.Kind);
            Assert.Equal(GameMap.Boxes[(int)id.Id].Min, world.Boxes[i].Min);
            Assert.True((int)id.Id > last);
            last = (int)id.Id;
        }
        for (int i = 0; i < GameMap.Boxes.Length; i++)
        {
            Box b = GameMap.Boxes[i];
            bool near = b.Max.X >= feet.X - CollisionWorld.GatherRadius && b.Min.X <= feet.X + CollisionWorld.GatherRadius &&
                        b.Max.Z >= feet.Z - CollisionWorld.GatherRadius && b.Min.Z <= feet.Z + CollisionWorld.GatherRadius;
            Assert.Equal(near, Array.IndexOf(world.BoxIds.ToArray(), new ColliderId(ColliderKind.Static, (uint)i)) >= 0);
        }
    }

    // The order is static boxes, doors, then pieces in id order (boxes and slopes each in id order), whatever order the
    // pieces were added in: both sides must gather the same list.
    [Fact]
    public void Gather_PutsPiecesAfterTheMap_InIdOrder()
    {
        var grid = new PieceGrid(32);
        grid.TryAdd(40, Shape(BuildPieceType.Wall, 16, 0, 16), out _);
        grid.TryAdd(12, Shape(BuildPieceType.Floor, 17, 0, 15), out _);
        grid.TryAdd(31, Shape(BuildPieceType.Ramp, 15, 0, 16, 1), out _);
        grid.TryAdd(25, Shape(BuildPieceType.Wall, 15, 1, 17, 1), out _);
        grid.TryAdd(2, Shape(BuildPieceType.Roof, 16, 0, 17), out _);
        var world = new CollisionWorld();
        world.Gather(new Vector3(2f, 0f, 2f), 0, 0UL, grid);
        Assert.Equal(5, world.PieceCount);
        Assert.Equal(new[] { 12u, 25u, 40u }, PieceIds(world.BoxIds));
        Assert.Equal(new[] { 2u, 31u }, PieceIds(world.SlopeIds));
        for (int i = 0; i < world.BoxIds.Length; i++)
        {
            if (world.BoxIds[i].Kind != ColliderKind.Piece) continue;
            for (int j = i; j < world.BoxIds.Length; j++) Assert.Equal(ColliderKind.Piece, world.BoxIds[j].Kind);
            break;
        }
    }

    // 기능: 충돌체 ID 목록에서 건설 조각(Piece) 종류의 ID만 순서대로 뽑는다.
    // 입력: ids - 검사할 충돌체 ID 목록.
    // 출력: Piece 종류 충돌체의 ID 배열(원래 순서 유지).
    private static uint[] PieceIds(ReadOnlySpan<ColliderId> ids)
    {
        var list = new System.Collections.Generic.List<uint>();
        foreach (ColliderId id in ids)
        {
            if (id.Kind == ColliderKind.Piece) list.Add(id.Id);
        }
        return list.ToArray();
    }

    [Fact]
    public void Gather_TakesPiecesWithinOneCell_AndTwoLevels()
    {
        var grid = new PieceGrid(32);
        grid.TryAdd(1, Shape(BuildPieceType.Floor, 17, 2, 17), out _);   // one cell over, two levels up: in
        grid.TryAdd(2, Shape(BuildPieceType.Floor, 18, 0, 16), out _);   // two cells over: out
        grid.TryAdd(3, Shape(BuildPieceType.Floor, 16, 3, 16), out _);   // three levels up: out
        grid.TryAdd(4, Shape(BuildPieceType.Floor, 15, 0, 15), out _);   // diagonal neighbour: in
        var world = new CollisionWorld();
        world.Gather(new Vector3(2f, 0.5f, 2f), 0, 0UL, grid);
        Assert.Equal(new[] { 1u, 4u }, PieceIds(world.BoxIds));
        world.Gather(new Vector3(2f, 6.5f, 2f), 0, 0UL, grid);           // level 2: now 3 is in, 4 still (0 = 2 - 2)
        Assert.Equal(new[] { 1u, 3u, 4u }, PieceIds(world.BoxIds));
    }

    [Fact]
    public void Gather_HoldsAFullBlockOfPieces()
    {
        var grid = new PieceGrid(CollisionWorld.MaxPieces + 10);
        uint id = 1;
        for (int y = 0; y < 5; y++)
            for (int z = 15; z <= 17; z++)
                for (int x = 15; x <= 17; x++)
                {
                    grid.TryAdd(id++, Shape(BuildPieceType.Wall, x, y, z, 0), out _);
                    grid.TryAdd(id++, Shape(BuildPieceType.Wall, x, y, z, 1), out _);
                    grid.TryAdd(id++, Shape(BuildPieceType.Floor, x, y, z), out _);
                    grid.TryAdd(id++, Shape(BuildPieceType.Ramp, x, y, z), out _);
                    grid.TryAdd(id++, Shape(BuildPieceType.Roof, x, y, z), out _);
                }
        var world = new CollisionWorld();
        world.Gather(new Vector3(2f, 6.5f, 2f), 0, 0UL, grid);
        Assert.Equal(CollisionWorld.MaxPieces, world.PieceCount);
        Assert.Equal(CollisionWorld.MaxPieces * 3 / 5, world.BoxIds.Length - CountMap(world.BoxIds));
        Assert.Equal(CollisionWorld.MaxPieces * 2 / 5, world.Slopes.Length);
    }

    // 기능: 충돌체 ID 목록에서 건설 조각이 아닌 것(맵 정적 상자·문)의 개수를 센다.
    // 입력: ids - 검사할 충돌체 ID 목록.
    // 출력: Piece 종류가 아닌 충돌체의 수.
    private static int CountMap(ReadOnlySpan<ColliderId> ids)
    {
        int n = 0;
        foreach (ColliderId id in ids)
        {
            if (id.Kind != ColliderKind.Piece) n++;
        }
        return n;
    }

    [Fact]
    public void Gather_AllocatesNothing()
    {
        var grid = new PieceGrid(64);
        for (uint id = 1; id <= 20; id++) grid.TryAdd(id, Shape(BuildPieceType.Floor, 14 + (int)(id % 5), (int)(id % 3), 16), out _);
        var world = new CollisionWorld();
        world.Gather(new Vector3(2f, 0f, 2f), 0, 0UL, grid);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) world.Gather(new Vector3(2f + i * 0.01f, 0f, 2f), 0, 0UL, grid);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void Gather_PastMaxPieces_KeepsTheLowestIds()
    {
        // MaxPieces is every slot of the gathered cells and levels, so only pieces sharing a slot (which PieceGrid does not
        // refuse) can overflow. The first column gathered (cell 15, 15) holds the high ids, a later one (17, 17) the low ids.
        var grid = new PieceGrid(CollisionWorld.MaxPieces + 10);
        for (uint i = 0; i < CollisionWorld.MaxPieces; i++) Assert.True(grid.TryAdd(1000 + i, Shape(BuildPieceType.Floor, 15, 0, 15), out _));
        for (uint id = 1; id <= 10; id++) Assert.True(grid.TryAdd(id, Shape(BuildPieceType.Floor, 17, 0, 17), out _));
        var world = new CollisionWorld();
        world.Gather(new Vector3(2f, 0f, 2f), 0, 0UL, grid);
        Assert.Equal(CollisionWorld.MaxPieces, world.PieceCount);
        ReadOnlySpan<ColliderId> ids = world.BoxIds;
        int first = ids.Length - CollisionWorld.MaxPieces;
        for (int i = 0; i < CollisionWorld.MaxPieces; i++)
        {
            uint expected = i < 10 ? (uint)(i + 1) : (uint)(1000 + i - 10);
            Assert.Equal(new ColliderId(ColliderKind.Piece, expected), ids[first + i]);
        }
    }

    [Fact]
    public void MapBoxesAndDoors_FitTheirGatherBuffer()
    {
        Assert.True(GameMap.Boxes.Length + GameMap.Doors.Length <= CollisionWorld.MaxStaticAndDoors);
        Assert.True(GameMap.Harvestables.Length <= GameMap.MaxHarvestables);
    }
}
