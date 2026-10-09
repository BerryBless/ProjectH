using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Build;

// Phase 13.5 D4: one waiting building request of either kind (a placement or an edit), so both share one queue, one
// sequence space (the duplicate check) and one interval. A tagged pair of the two small structs, no allocation.
public readonly struct BuildQueueItem
{
    public readonly bool IsEdit;
    public readonly BuildRequest Place;
    public readonly BuildEditRequest Edit;

    // 기능: 배치 요청을 담는다.
    // 입력: place - 배치 요청.
    // 출력: IsEdit = false인 항목.
    public BuildQueueItem(in BuildRequest place)
    {
        IsEdit = false;
        Place = place;
        Edit = default;
    }

    // 기능: 편집 요청을 담는다.
    // 입력: edit - 편집 요청.
    // 출력: IsEdit = true인 항목.
    public BuildQueueItem(in BuildEditRequest edit)
    {
        IsEdit = true;
        Place = default;
        Edit = edit;
    }

    // The connection's shared building sequence of whichever request this is.
    public ushort Sequence => IsEdit ? Edit.Sequence : Place.Sequence;
}

// Phase 13 D8: one player's build requests waiting for the game loop, oldest first. Capacity is fixed: a request that
// does not fit is refused (RateLimited) instead of growing the queue. Phase 13.5 D4: edits wait in the same queue (one
// capacity for both kinds). Game loop thread only.
public sealed class BuildRequestQueue
{
    public const int Capacity = 8;

    private readonly BuildQueueItem[] _items = new BuildQueueItem[Capacity];
    private int _head;
    private int _count;

    public int Count => _count;

    // 기능: 요청 하나를 큐 끝에 넣는다.
    // 입력: request - 배치 또는 편집 요청.
    // 출력: 넣었으면 true, 큐가 가득 차 있으면 false(호출자가 RateLimited로 답한다).
    public bool TryAdd(in BuildQueueItem request)
    {
        if (_count == Capacity) return false;
        _items[(_head + _count) % Capacity] = request;
        _count++;
        return true;
    }

    // 기능: 가장 오래된 요청을 꺼낸다.
    // 입력: request - 결과.
    // 출력: 꺼냈으면 true와 요청, 비어 있으면 false.
    public bool TryTake(out BuildQueueItem request)
    {
        if (_count == 0)
        {
            request = default;
            return false;
        }
        request = _items[_head];
        _head = (_head + 1) % Capacity;
        _count--;
        return true;
    }

    // 기능: 대기 중인 요청을 모두 버린다.
    // 입력: 없음.
    // 출력: 반환값 없음. 큐가 비워진다.
    public void Clear()
    {
        _head = 0;
        _count = 0;
    }
}
