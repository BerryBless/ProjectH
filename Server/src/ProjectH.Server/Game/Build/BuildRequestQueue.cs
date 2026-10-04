using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Build;

// Phase 13 D8: one player's build requests waiting for the game loop, oldest first. Capacity is fixed: a request that
// does not fit is refused (RateLimited) instead of growing the queue. Game loop thread only.
public sealed class BuildRequestQueue
{
    public const int Capacity = 8;

    private readonly BuildRequest[] _items = new BuildRequest[Capacity];
    private int _head;
    private int _count;

    public int Count => _count;

    // 기능: 건설 요청을 Queue 끝에 넣는다.
    // 입력: request - 게임 루프가 나중에 처리할 건설 요청.
    // 출력: 넣었으면 true, Queue가 가득 차 거절했으면 false.
    public bool TryAdd(in BuildRequest request)
    {
        if (_count == Capacity) return false;
        _items[(_head + _count) % Capacity] = request;
        _count++;
        return true;
    }

    // 기능: 가장 오래된 건설 요청을 Queue에서 꺼낸다.
    // 입력: 없음.
    // 출력: 꺼냈으면 true와 그 요청, Queue가 비었으면 false.
    public bool TryTake(out BuildRequest request)
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

    // 기능: 대기 중인 건설 요청을 모두 버린다.
    // 입력: 없음.
    // 출력: 반환값 없음. Queue가 빈 상태가 된다.
    public void Clear()
    {
        _head = 0;
        _count = 0;
    }
}
