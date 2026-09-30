using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Game
{
    // The client's copy of the world item list (D14): filled by WorldItems at join, then changed only by
    // ItemSpawned (upsert) and ItemRemoved. Fixed arrays of Capacity, the server's limit (D13), so it never
    // grows and nothing here allocates. Removal moves the last item into the hole; Remove reports that
    // move so a parallel array of views (WorldItemViews) can follow it. Main thread only.
    public sealed class WorldItemList
    {
        public const int Capacity = 256;   // server WorldItems.Capacity

        private readonly WorldItemData[] _items = new WorldItemData[Capacity];

        public int Count { get; private set; }

        public WorldItemData this[int index] => _items[index];

        // Returns false only when a new id arrives while the list is full, which the server never causes
        // (it evicts before it adds, and the events arrive in order).
        public bool Upsert(in WorldItemData item, out int index, out bool added)
        {
            index = IndexOf(item.ItemId);
            added = index < 0;
            if (added)
            {
                if (Count == Capacity) return false;
                index = Count++;
            }
            _items[index] = item;
            return true;
        }

        // movedFrom: the old index of the item now at removedIndex, or -1 when the removed one was last.
        public bool Remove(ushort itemId, out int removedIndex, out int movedFrom)
        {
            removedIndex = IndexOf(itemId);
            movedFrom = -1;
            if (removedIndex < 0) return false;
            Count--;
            if (removedIndex != Count)
            {
                _items[removedIndex] = _items[Count];
                movedFrom = Count;
            }
            _items[Count] = default;
            return true;
        }

        public void Clear()
        {
            for (int i = 0; i < Count; i++) _items[i] = default;
            Count = 0;
        }

        public int IndexOf(ushort itemId)
        {
            for (int i = 0; i < Count; i++)
            {
                if (_items[i].ItemId == itemId) return i;
            }
            return -1;
        }
    }
}
