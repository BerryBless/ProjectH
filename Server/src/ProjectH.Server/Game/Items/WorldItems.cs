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

    // index 0..Count-1
    public ref readonly WorldItem this[int index] => ref _items[index];

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
        _count++;
        return true;
    }

    public int IndexOf(ushort itemId)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_items[i].Data.ItemId == itemId) return i;
        }
        return -1;
    }

    public void RemoveAt(int index)
    {
        _count--;
        _items[index] = _items[_count];
        _items[_count] = default;
    }

    public void SetAmount(int index, ushort amount)
    {
        _items[index].Data.Amount = amount;
    }

    // D8: the item nearest to feet (3D distance) among those within horizontalRange on the ground plane
    // and verticalRange up or down, or -1. Ties go to the lower ItemId, so the result does not depend on
    // the storage order (the client prompt applies the same rule to its own list).
    public int FindNearest(Vector3 feet, float horizontalRange, float verticalRange)
    {
        int best = -1;
        float bestDistance = 0f;
        float rangeSq = horizontalRange * horizontalRange;
        for (int i = 0; i < _count; i++)
        {
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
