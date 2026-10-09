using System.Numerics;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Items;

// One item lying in the world: what clients see (Data) plus server bookkeeping.
public struct WorldItem
{
    public WorldItemData Data;
    public int SpawnPoint;   // index of the loot point that made it, -1 for a dropped item
    public ulong Order;      // add order; the dropped item with the lowest Order is evicted first (D13)
    // Phase 14 D9, server only (not on the wire): a RebootCard's team (only it is told of the card and may pick it up), its
    // owner's JoinOrder (an entity id is reused, a join order is not) and the tick it disappears at. 0 for other items.
    public byte CardTeam;
    public uint CardOwner;
    public uint ExpireTick;

    public bool IsDropped => SpawnPoint < 0;
    // Phase 14 D9: a card is never evicted (at most one per participant, so it cannot fill the store).
    public bool IsEvictable => SpawnPoint < 0 && Data.Kind != ItemKind.RebootCard;
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
    // Phase 14 D9: how many of them are RebootCard items (the expiry scan and the TeamState card flags skip without any).
    public int CardCount { get; private set; }

    // index 0..Count-1
    public ref readonly WorldItem this[int index] => ref _items[index];

    // 기능: 아이템을 넣고 다음 빈 id를 준다. 가득 차 있으면 가장 오래된 떨어뜨린 아이템을 먼저 지우고 그 id를 evictedId로
    //   돌려준다. Spawn Point 아이템과 Phase 14 카드는 지우지 않는다. 지울 것이 없으면 넣지 않는다.
    // 입력: kind·defId·rarity·amount·position - 아이템, spawnPoint - 만든 Loot Point(-1 = 떨어뜨림), itemId·evictedId - 결과,
    //   cardTeam·cardOwner·expireTick - RebootCard의 팀, 주인 JoinOrder, 사라지는 Tick(다른 아이템은 0).
    // 출력: 넣었으면 true.
    public bool TryAdd(ItemKind kind, byte defId, byte rarity, ushort amount, Vector3 position, int spawnPoint,
        out ushort itemId, out ushort evictedId, byte cardTeam = 0, uint cardOwner = 0, uint expireTick = 0)
    {
        itemId = 0;
        evictedId = 0;
        if (_count == Capacity)
        {
            int oldest = -1;
            for (int i = 0; i < _count; i++)
            {
                if (_items[i].IsEvictable && (oldest < 0 || _items[i].Order < _items[oldest].Order)) oldest = i;
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
            CardTeam = cardTeam,
            CardOwner = cardOwner,
            ExpireTick = expireTick,
        };
        if (kind == ItemKind.Material) MaterialCount++;
        if (kind == ItemKind.RebootCard) CardCount++;
        _count++;
        return true;
    }

    // 기능: 아이템 id로 저장 index를 찾는다(선형 탐색, 최대 Capacity).
    // 입력: itemId - 아이템 id.
    // 출력: index(0..Count-1). 없으면 -1.
    public int IndexOf(ushort itemId)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_items[i].Data.ItemId == itemId) return i;
        }
        return -1;
    }

    // 기능: index의 아이템을 지운다(마지막 기록이 빈 자리로 온다). Material·카드 수를 맞춘다.
    // 입력: index - 0..Count-1.
    // 출력: 반환값 없음.
    public void RemoveAt(int index)
    {
        if (_items[index].Data.Kind == ItemKind.Material) MaterialCount--;
        if (_items[index].Data.Kind == ItemKind.RebootCard) CardCount--;
        _count--;
        _items[index] = _items[_count];
        _items[_count] = default;
    }

    // 기능: 아이템의 양을 바꾼다(일부만 주웠을 때).
    // 입력: index - 0..Count-1, amount - 새 양.
    // 출력: 반환값 없음.
    public void SetAmount(int index, ushort amount)
    {
        _items[index].Data.Amount = amount;
    }

    // 기능: 범위 안에서 발에 가장 가까운(3D 거리) 줍기 대상을 찾는다(D8, Material 제외). 같은 거리면 작은 ItemId(저장 순서와 무관,
    //   Client 안내도 같은 규칙). Phase 14 D9: 카드는 cardTeam의 카드만 대상이다.
    // 입력: feet - 발 위치, horizontalRange·verticalRange - 범위, cardTeam - 줍는 사람의 팀(0 = 카드는 모두 건너뜀).
    // 출력: 아이템 index, 없으면 -1.
    // D8: the item nearest to feet (3D distance) among those within horizontalRange on the ground plane
    // and verticalRange up or down, or -1. Ties go to the lower ItemId, so the result does not depend on
    // the storage order (the client prompt applies the same rule to its own list). Phase 13 D15: never a Material
    // item (those are picked up on touch).
    public int FindNearest(Vector3 feet, float horizontalRange, float verticalRange, byte cardTeam = 0)
    {
        int best = -1;
        float bestDistance = 0f;
        float rangeSq = horizontalRange * horizontalRange;
        for (int i = 0; i < _count; i++)
        {
            if (_items[i].Data.Kind == ItemKind.Material) continue;
            if (_items[i].Data.Kind == ItemKind.RebootCard && (cardTeam == 0 || _items[i].CardTeam != cardTeam)) continue;
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

    // 기능: 다음 아이템 id를 낸다(1..65535 순환, 쓰고 있는 id는 건너뛴다: 늦은 ItemRemoved가 새 아이템을 가리키지 않는다).
    // 입력: 없음.
    // 출력: 쓰고 있지 않은 id(최대 Capacity개만 사용 중이라 반드시 끝난다).
    private ushort NextId()
    {
        while (true)
        {
            _lastId = _lastId == ushort.MaxValue ? (ushort)1 : (ushort)(_lastId + 1);
            if (IndexOf(_lastId) < 0) return _lastId;
        }
    }
}
