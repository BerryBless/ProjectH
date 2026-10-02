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

    public bool TryAdd(in BuildRequest request)
    {
        if (_count == Capacity) return false;
        _items[(_head + _count) % Capacity] = request;
        _count++;
        return true;
    }

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

    public void Clear()
    {
        _head = 0;
        _count = 0;
    }
}
