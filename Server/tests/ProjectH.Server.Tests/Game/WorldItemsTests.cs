using System;
using System.Collections.Generic;
using System.Numerics;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Game;

public class WorldItemsTests
{
    private readonly WorldItems _items = new();

    // 기능: 희귀도 0의 아이템 하나를 _items에 넣는다. 추가가 거부되거나 퇴거가 일어나면 테스트를 실패시킨다.
    // 입력: position - 위치, spawnPoint - 스폰 지점(-1이면 드롭), kind - 아이템 종류, defId - 정의 ID, amount - 수량.
    // 출력: 새 아이템 ID.
    private ushort Add(Vector3 position, int spawnPoint = -1, ItemKind kind = ItemKind.Ammo, byte defId = 1, ushort amount = 10)
    {
        Assert.True(_items.TryAdd(kind, defId, 0, amount, position, spawnPoint, out ushort id, out ushort evicted));
        Assert.Equal(0, evicted);
        return id;
    }

    [Fact]
    public void Add_GivesIdsFromOne_AndStoresTheData()
    {
        ushort a = Add(new Vector3(1f, 0f, 2f), kind: ItemKind.Weapon, defId: 3, amount: 0);
        ushort b = Add(new Vector3(4f, 1f, 5f), spawnPoint: 7);
        Assert.Equal(1, a);
        Assert.Equal(2, b);
        Assert.Equal(2, _items.Count);

        ref readonly WorldItem first = ref _items[_items.IndexOf(a)];
        Assert.Equal(ItemKind.Weapon, first.Data.Kind);
        Assert.Equal(3, first.Data.DefId);
        Assert.Equal(0, first.Data.Amount);
        Assert.True(first.IsDropped);
        Assert.Equal(7, _items[_items.IndexOf(b)].SpawnPoint);
    }

    // Phase 13 final review C: the Material items are counted through adds, removals and evictions.
    [Fact]
    public void MaterialCount_FollowsAddsRemovalsAndEvictions()
    {
        ushort wood = Add(Vector3.Zero, kind: ItemKind.Material, defId: 1);   // the oldest drop
        Add(Vector3.Zero, kind: ItemKind.Material, defId: 2);
        Add(Vector3.Zero);
        Assert.Equal(2, _items.MaterialCount);
        _items.RemoveAt(_items.IndexOf(wood));
        Assert.Equal(1, _items.MaterialCount);
        while (_items.Count < WorldItems.Capacity) Add(Vector3.Zero);
        Assert.True(_items.TryAdd(ItemKind.Ammo, 1, 0, 1, Vector3.Zero, -1, out _, out ushort evicted));   // evicts the stone
        Assert.NotEqual(0, evicted);
        Assert.Equal(0, _items.MaterialCount);
    }

    [Fact]
    public void Remove_And_SetAmount()
    {
        ushort a = Add(Vector3.Zero);
        ushort b = Add(Vector3.One);
        _items.SetAmount(_items.IndexOf(b), 3);
        _items.RemoveAt(_items.IndexOf(a));

        Assert.Equal(1, _items.Count);
        Assert.Equal(-1, _items.IndexOf(a));
        Assert.Equal(3, _items[_items.IndexOf(b)].Data.Amount);
    }

    // Spec §6: ids are not reused within one lap of the 16-bit counter.
    [Fact]
    public void Ids_AreNotReusedWithinOneLap()
    {
        var seen = new HashSet<ushort>();
        for (int i = 0; i < 5000; i++)
        {
            ushort id = Add(Vector3.Zero);
            Assert.True(seen.Add(id), $"id {id} reused");
            _items.RemoveAt(_items.IndexOf(id));
        }
    }

    [Fact]
    public void Ids_WrapAfter65535_SkipZero_AndSkipIdsStillInUse()
    {
        ushort keep = Add(Vector3.Zero);                      // id 1 stays in the world for the whole lap
        for (int i = 2; i <= ushort.MaxValue; i++)
        {
            ushort id = Add(Vector3.Zero);
            _items.RemoveAt(_items.IndexOf(id));
        }
        Assert.Equal(1, keep);
        Assert.Equal(2, Add(Vector3.Zero));                   // 65535 -> 1 (in use) -> 2
    }

    // Review Focus / D13: the list never grows past 256; the oldest drop goes first, spawn-point items stay.
    [Fact]
    public void Full_EvictsTheOldestDrop_NeverASpawnPointItem()
    {
        var spawnIds = new List<ushort>();
        for (int i = 0; i < 20; i++) spawnIds.Add(Add(new Vector3(i, 0f, 0f), spawnPoint: i));
        ushort oldestDrop = Add(new Vector3(0f, 0f, 1f));
        ushort secondDrop = Add(new Vector3(0f, 0f, 2f));
        while (_items.Count < WorldItems.Capacity) Add(new Vector3(0f, 0f, 3f));

        Assert.True(_items.TryAdd(ItemKind.Ammo, 1, 0, 5, Vector3.Zero, -1, out ushort newId, out ushort evicted));
        Assert.Equal(oldestDrop, evicted);
        Assert.Equal(WorldItems.Capacity, _items.Count);
        Assert.True(_items.IndexOf(newId) >= 0);

        Assert.True(_items.TryAdd(ItemKind.Ammo, 1, 0, 5, Vector3.Zero, -1, out _, out evicted));
        Assert.Equal(secondDrop, evicted);

        // Many more drops: the count stays at the cap and every spawn-point item survives.
        for (int i = 0; i < 1000; i++) _items.TryAdd(ItemKind.Ammo, 1, 0, 5, Vector3.Zero, -1, out _, out _);
        Assert.Equal(WorldItems.Capacity, _items.Count);
        foreach (ushort id in spawnIds) Assert.True(_items.IndexOf(id) >= 0, $"spawn item {id} evicted");
    }

    [Fact]
    public void Full_OfSpawnPointItems_RefusesTheAdd()
    {
        for (int i = 0; i < WorldItems.Capacity; i++) Add(Vector3.Zero, spawnPoint: i);
        Assert.False(_items.TryAdd(ItemKind.Ammo, 1, 0, 5, Vector3.Zero, -1, out ushort id, out ushort evicted));
        Assert.Equal(0, id);
        Assert.Equal(0, evicted);
        Assert.Equal(WorldItems.Capacity, _items.Count);
    }

    // D8 range: 2.0 m on the ground plane and 2.0 m up or down, both inclusive.
    [Theory]
    [InlineData(2.0f, 0f, 0f, true)]
    [InlineData(2.001f, 0f, 0f, false)]
    [InlineData(1.4f, 0f, 1.4f, true)]      // 1.98 m on the diagonal
    [InlineData(1.42f, 0f, 1.42f, false)]   // 2.008 m
    [InlineData(0f, 2.0f, 0f, true)]
    [InlineData(0f, 2.01f, 0f, false)]
    [InlineData(0f, -2.0f, 0f, true)]
    [InlineData(0f, -2.01f, 0f, false)]
    public void FindNearest_RangeBoundary(float dx, float dy, float dz, bool expectedFound)
    {
        var feet = new Vector3(5f, 1f, -3f);
        Add(feet + new Vector3(dx, dy, dz));
        Assert.Equal(expectedFound, _items.FindNearest(feet, 2f, 2f) >= 0);
    }

    [Fact]
    public void FindNearest_PicksTheClosest_TiesGoToTheLowerId()
    {
        var feet = Vector3.Zero;
        ushort far = Add(new Vector3(1.5f, 0f, 0f));
        ushort tieHigh = Add(new Vector3(0f, 0f, 1f));
        ushort tieLow = Add(new Vector3(0f, 0f, -1f));
        Assert.True(tieLow > tieHigh);   // the later add has the higher id

        Assert.Equal(tieHigh, _items[_items.FindNearest(feet, 2f, 2f)].Data.ItemId);
        _items.RemoveAt(_items.IndexOf(tieHigh));
        Assert.Equal(tieLow, _items[_items.FindNearest(feet, 2f, 2f)].Data.ItemId);
        _items.RemoveAt(_items.IndexOf(tieLow));
        Assert.Equal(far, _items[_items.FindNearest(feet, 2f, 2f)].Data.ItemId);
        _items.RemoveAt(_items.IndexOf(far));
        Assert.Equal(-1, _items.FindNearest(feet, 2f, 2f));
    }

    [Fact]
    public void AddSearchRemove_AllocateNothing()
    {
        for (int i = 0; i < WorldItems.Capacity; i++) Add(new Vector3(i % 16, 0f, i / 16));
        // Warm up the same calls first: their first run may JIT or initialise statics on this thread, which would count as
        // allocation and failed this test now and then in full runs.
        _items.TryAdd(ItemKind.Ammo, 1, 0, 5, new Vector3(3f, 0f, 3f), -1, out _, out _);
        _items.RemoveAt(_items.FindNearest(new Vector3(3f, 0f, 3f), 2f, 2f));
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            _items.TryAdd(ItemKind.Ammo, 1, 0, 5, new Vector3(3f, 0f, 3f), -1, out _, out _);
            int nearest = _items.FindNearest(new Vector3(3f, 0f, 3f), 2f, 2f);
            _items.RemoveAt(nearest);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }
}
