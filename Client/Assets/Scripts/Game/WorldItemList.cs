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

        // 기능: 인덱스 위치의 아이템을 읽는다.
        // 입력: index - 아이템 인덱스(0..Count-1).
        // 출력: 해당 위치의 아이템 데이터.
        public WorldItemData this[int index] => _items[index];

        // 기능: 아이템 ID가 있으면 덮어쓰고 없으면 끝에 추가한다.
        // 입력: item - 서버가 보낸 아이템 데이터.
        // 출력: 성공하면 true와 저장된 인덱스·새로 추가됐는지 여부, 새 ID인데 목록이 가득 차면 false.
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

        // 기능: 아이템을 지우고 마지막 아이템을 빈 자리로 옮긴다.
        // 입력: itemId - 지울 아이템 ID.
        // 출력: 찾으면 true와 지운 인덱스·그 자리로 옮겨진 아이템의 이전 인덱스(지운 것이 마지막이면 -1), 없으면 false.
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

        // 기능: 목록을 비운다.
        // 입력: 없음.
        // 출력: 반환값 없음. 사용하던 항목이 default로 지워지고 Count가 0이 된다.
        public void Clear()
        {
            for (int i = 0; i < Count; i++) _items[i] = default;
            Count = 0;
        }

        // 기능: 아이템 ID의 인덱스를 찾는다.
        // 입력: itemId - 찾을 아이템 ID.
        // 출력: 인덱스, 없으면 -1.
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
