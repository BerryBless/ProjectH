using System.Numerics;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Items;

// One item lying in the world: what clients see (Data) plus server bookkeeping.
public struct WorldItem
{
    public WorldItemData Data;
    public int SpawnPoint;   // index of the loot point that made it, -1 for a dropped item
    public ulong Order;      // add order; the dropped item with the lowest Order is evicted first (D13)

    public bool IsDropped => SpawnPoint < 0;
}

// Every item in the world (D13): a fixed array of Capacity records, so the list, the join packets and
// the search cost are bounded. Game loop thread only; nothing here allocates after construction.
// Removal swaps the last record into the hole, so indexes are only valid until the next change.
public sealed class WorldItems
{
    public const int Capacity = 256;

    private readonly WorldItem[] _items = new WorldItem[Capacity];
    private int _count;
    private ushort _lastId;
    private ulong _nextOrder;

    public int Count => _count;
    // Phase 13 final review C: how many of them are Material items (the touch pickup skips its scan without any).
    public int MaterialCount { get; private set; }

    // index 0..Count-1
    public ref readonly WorldItem this[int index] => ref _items[index];

    // 기능: 월드에 아이템을 추가하고 새 ID를 부여한다. 가득 차 있으면 가장 오래된 버려진 아이템을 먼저 제거한다.
    // 입력: kind - 아이템 종류, defId - 정의 ID, rarity - 등급, amount - 수량, position - 놓일 위치, spawnPoint - 만든 루트 지점 번호(버린 아이템은 -1), itemId - 부여된 ID, evictedId - 제거된 아이템 ID.
    // 출력: 추가했으면 true와 itemId·evictedId(제거 없으면 0), 모든 칸이 스폰 지점 아이템이라 추가할 수 없으면 false.
    // Adds an item and gives it the next free id. When the store is full the oldest dropped item is
    // evicted first and its id returned in evictedId (0 when nothing was evicted). Spawn-point items are
    // never evicted; if every record is one (impossible while spawn points < Capacity) nothing is added.
    public bool TryAdd(ItemKind kind, byte defId, byte rarity, ushort amount, Vector3 position, int spawnPoint,
        out ushort itemId, out ushort evictedId)
    {
        itemId = 0;
        evictedId = 0;
        if (_count == Capacity)
        {
            int oldest = -1;
            for (int i = 0; i < _count; i++)
            {
                if (_items[i].IsDropped && (oldest < 0 || _items[i].Order < _items[oldest].Order)) oldest = i;
            }
            if (oldest < 0) return false;
            evictedId = _items[oldest].Data.ItemId;
            RemoveAt(oldest);
        }

        itemId = NextId();
        _items[_count] = new WorldItem
        {
            Data = new WorldItemData { ItemId = itemId, Kind = kind, DefId = defId, Rarity = rarity, Amount = amount, Position = position },
            SpawnPoint = spawnPoint,
            Order = _nextOrder++,
        };
        if (kind == ItemKind.Material) MaterialCount++;
        _count++;
        return true;
    }

    // 기능: 아이템 ID로 현재 저장 위치를 찾는다.
    // 입력: itemId - 찾을 아이템 ID.
    // 출력: 저장 위치(다음 변경 전까지만 유효), 없으면 -1.
    public int IndexOf(ushort itemId)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_items[i].Data.ItemId == itemId) return i;
        }
        return -1;
    }

    // 기능: 저장 위치의 아이템을 월드에서 제거한다. 마지막 기록을 빈자리로 옮긴다.
    // 입력: index - 제거할 아이템의 저장 위치.
    // 출력: 반환값 없음. 아이템 수(Material이면 MaterialCount도)가 줄고 마지막 아이템의 저장 위치가 바뀐다.
    public void RemoveAt(int index)
    {
        if (_items[index].Data.Kind == ItemKind.Material) MaterialCount--;
        _count--;
        _items[index] = _items[_count];
        _items[_count] = default;
    }

    // 기능: 땅에 놓인 아이템의 수량을 바꾼다.
    // 입력: index - 저장소 안의 아이템 번호, amount - 새 수량.
    // 출력: 반환값 없음. 그 아이템의 Amount가 바뀐다.
    public void SetAmount(int index, ushort amount)
    {
        _items[index].Data.Amount = amount;
    }

    // 기능: 발 위치에서 줍기 범위 안에 있는 가장 가까운 아이템(Material 제외)을 찾는다.
    // 입력: feet - 플레이어 발 위치, horizontalRange - 수평 줍기 거리, verticalRange - 위아래 허용 높이 차.
    // 출력: 가장 가까운 아이템의 저장 위치(거리가 같으면 ItemId가 작은 쪽), 없으면 -1.
    // D8: the item nearest to feet (3D distance) among those within horizontalRange on the ground plane
    // and verticalRange up or down, or -1. Ties go to the lower ItemId, so the result does not depend on
    // the storage order (the client prompt applies the same rule to its own list). Phase 13 D15: never a Material
    // item (those are picked up on touch).
    public int FindNearest(Vector3 feet, float horizontalRange, float verticalRange)
    {
        int best = -1;
        float bestDistance = 0f;
        float rangeSq = horizontalRange * horizontalRange;
        for (int i = 0; i < _count; i++)
        {
            if (_items[i].Data.Kind == ItemKind.Material) continue;
            Vector3 d = _items[i].Data.Position - feet;
            float horizontalSq = d.X * d.X + d.Z * d.Z;
            if (horizontalSq > rangeSq || d.Y > verticalRange || d.Y < -verticalRange) continue;
            float distance = horizontalSq + d.Y * d.Y;
            if (best < 0 || distance < bestDistance ||
                (distance == bestDistance && _items[i].Data.ItemId < _items[best].Data.ItemId))
            {
                best = i;
                bestDistance = distance;
            }
        }
        return best;
    }

    // 기능: 현재 사용 중이 아닌 다음 아이템 ID를 순환 방식으로 고른다.
    // 입력: 없음.
    // 출력: 1..65535 중 사용 중이 아닌 다음 ID. 마지막 ID 기록이 갱신된다.
    // Ids go 1, 2, ... 65535, 1, ... skipping ids still in use, so a freed id is reused only after a full
    // lap (a late ItemRemoved can never name a newer item). At most Capacity ids are in use, so this ends.
    private ushort NextId()
    {
        while (true)
        {
            _lastId = _lastId == ushort.MaxValue ? (ushort)1 : (ushort)(_lastId + 1);
            if (IndexOf(_lastId) < 0) return _lastId;
        }
    }
}
