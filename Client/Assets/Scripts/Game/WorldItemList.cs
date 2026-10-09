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

        // 기능: 아이템을 id로 찾아 덮어쓰거나, 없으면 끝에 더한다.
        // 입력: item - 월드 아이템, index - 그 아이템이 놓인 칸, added - 새로 더했으면 true.
        // 출력: 성공하면 true. 새 id인데 목록이 가득 차 있으면 false(서버는 지우고 더하며 이벤트가 순서대로 오므로 생기지 않는다).
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

        // 기능: 아이템을 id로 지우고 마지막 아이템을 그 구멍으로 옮긴다.
        // 입력: itemId - 지울 아이템 id, removedIndex - 지운 칸, movedFrom - 그 칸으로 옮겨 온 아이템의 이전 칸(지운 것이 마지막이었으면 -1).
        // 출력: 있어서 지웠으면 true, 모르는 id면 false.
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

        // 기능: 모든 아이템을 지운다.
        // 입력: 없음.
        // 출력: 반환값 없음. Count가 0이 된다.
        public void Clear()
        {
            for (int i = 0; i < Count; i++) _items[i] = default;
            Count = 0;
        }

        // 기능: 아이템 id의 칸을 찾는다.
        // 입력: itemId - 아이템 id.
        // 출력: 칸 번호, 없으면 -1.
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
