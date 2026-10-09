using System;
using System.Collections.Generic;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Build;

// Phase 13 D12 (request §71-§80): which pieces hold each other up, and what falls when one goes.
//  - Connection: two pieces are connected when they share an edge of the building lattice (cell corners at every level):
//    a wall's four sides and its two diagonals, a floor's or roof's four sides, a ramp's low and high edges and its two
//    sloped sides. So a wall holds the floor on its top edge, walls meet at corners and stack, a ramp meets the floor at
//    its low or high edge, and a ramp's side lies on the wall beside it (the wall's diagonal).
//  - Grounded: a piece whose bottom touches the terrain or the top of a map box (not a harvestable: it can be destroyed)
//    within GroundTolerance. The terrain and map boxes never change, so it is set at placement and recomputed only when
//    an edit turns a ramp (Match.ApplyEdit, Phase 13.5 D7: its low edge moves).
//  - Placement needs a grounded piece or a neighbour (every piece standing is supported).
//  - After a piece goes, each former neighbour's connected component is searched (breadth first, through shared edges
//    only, never the whole map, request §76, §77); a component with no grounded piece collapses at once.
// Edges are kept in an index (lattice edge -> the pieces on it) so a neighbour lookup is one dictionary read per edge.
// Per-slot arrays grow with the pieces (up to the match's piece limit), like BuildWorld's. Game loop thread only.
public sealed class BuildSupport
{
    public const int MaxEdges = 6;
    // More than any piece can have: at most about ten pieces share one lattice edge.
    public const int MaxNeighbours = 64;
    public const float GroundTolerance = 0.5f;
    private const int PointsX = BuildGrid.CellsX + 1;
    private const int PointsZ = BuildGrid.CellsZ + 1;

    private readonly Dictionary<uint, int> _heads = new();
    private readonly int _capacity;
    private uint[] _keys;
    private int[] _next;
    private int[] _prev;
    private byte[] _edgeCount;
    private int[] _visited;
    private int[] _queue;
    private int[] _collapse;
    private int _stamp;
    // This tick's search starts (former neighbours of the pieces destroyed so far), each slot once (_queued), for the one
    // search at the end of the tick. At most every slot.
    private int[] _starts;
    private bool[] _queued;
    private int _startCount;

    // 기능: 지지 색인을 만든다(slot별 배열은 256 또는 상한까지로 시작해 상한까지 자란다).
    // 입력: capacity - 경기의 조각 상한.
    // 출력: 모서리가 하나도 없는 BuildSupport.
    public BuildSupport(int capacity)
    {
        _capacity = capacity;
        int slots = Math.Min(capacity, 256);
        _keys = new uint[slots * MaxEdges];
        _next = new int[slots * MaxEdges];
        _prev = new int[slots * MaxEdges];
        _edgeCount = new byte[slots];
        _visited = new int[slots];
        _queue = new int[slots];
        _collapse = new int[slots];
        _starts = new int[slots];
        _queued = new bool[slots];
    }

    // 기능: slot별 배열이 slot을 담을 만큼 커지게 한다(두 배씩, 조각 상한까지).
    // 입력: slot - 담아야 할 slot.
    // 출력: 반환값 없음. 필요하면 모든 slot별 배열이 늘어난다.
    private void EnsureSlot(int slot)
    {
        if (slot < _edgeCount.Length) return;
        int size = Math.Min(_capacity, Math.Max(slot + 1, _edgeCount.Length * 2));
        Array.Resize(ref _keys, size * MaxEdges);
        Array.Resize(ref _next, size * MaxEdges);
        Array.Resize(ref _prev, size * MaxEdges);
        Array.Resize(ref _edgeCount, size);
        Array.Resize(ref _visited, size);
        Array.Resize(ref _queue, size);
        Array.Resize(ref _collapse, size);
        Array.Resize(ref _starts, size);
        Array.Resize(ref _queued, size);
    }

    // 기능: 조각 모양의 격자 모서리(4개 또는 벽은 대각선 포함 6개)를 키로 만든다(낮은 점 Index | 높은 점 Index << 16).
    // 입력: s - 조각 모양, keys - 키를 쓸 곳(MaxEdges 이상).
    // 출력: 쓴 모서리 수.
    public static int Edges(in BuildPieceShape s, Span<uint> keys)
    {
        int x = s.X, y = s.Y, z = s.Z;
        switch (s.Type)
        {
            case BuildPieceType.Wall:
                return s.Rotation == 0
                    ? Rect(keys, Point(x, y, z), Point(x + 1, y, z), Point(x, y + 1, z), Point(x + 1, y + 1, z), true)
                    : Rect(keys, Point(x, y, z), Point(x, y, z + 1), Point(x, y + 1, z), Point(x, y + 1, z + 1), true);
            case BuildPieceType.Floor:
                return Rect(keys, Point(x, y, z), Point(x + 1, y, z), Point(x, y, z + 1), Point(x + 1, y, z + 1), false);
            case BuildPieceType.Roof:
                return Rect(keys, Point(x, y + 1, z), Point(x + 1, y + 1, z), Point(x, y + 1, z + 1), Point(x + 1, y + 1, z + 1), false);
            default:
                // Ramp: low edge (a0, a1) at level y, high edge (b0, b1) at y + 1, sides a0-b0 and a1-b1.
                int a0, a1, b0, b1;
                switch (s.Rotation)
                {
                    case 0: a0 = Point(x, y, z); a1 = Point(x + 1, y, z); b0 = Point(x, y + 1, z + 1); b1 = Point(x + 1, y + 1, z + 1); break;
                    case 1: a0 = Point(x, y, z); a1 = Point(x, y, z + 1); b0 = Point(x + 1, y + 1, z); b1 = Point(x + 1, y + 1, z + 1); break;
                    case 2: a0 = Point(x, y, z + 1); a1 = Point(x + 1, y, z + 1); b0 = Point(x, y + 1, z); b1 = Point(x + 1, y + 1, z); break;
                    default: a0 = Point(x + 1, y, z); a1 = Point(x + 1, y, z + 1); b0 = Point(x, y + 1, z); b1 = Point(x, y + 1, z + 1); break;
                }
                keys[0] = Key(a0, a1);
                keys[1] = Key(b0, b1);
                keys[2] = Key(a0, b0);
                keys[3] = Key(a1, b1);
                return 4;
        }
    }

    // 기능: 네 점으로 된 사각형의 네 변 키를 쓰고, 벽이면 두 대각선도 쓴다.
    // 입력: keys - 키를 쓸 곳, p00·p10·p01·p11 - 사각형 꼭짓점 Index(p00-p10 / p01-p11이 마주 보는 변), diagonals - 대각선 포함 여부.
    // 출력: 쓴 모서리 수(4 또는 6).
    private static int Rect(Span<uint> keys, int p00, int p10, int p01, int p11, bool diagonals)
    {
        keys[0] = Key(p00, p10);
        keys[1] = Key(p01, p11);
        keys[2] = Key(p00, p01);
        keys[3] = Key(p10, p11);
        if (!diagonals) return 4;
        keys[4] = Key(p00, p11);
        keys[5] = Key(p10, p01);
        return 6;
    }

    // 기능: 격자 꼭짓점 좌표를 한 정수 Index로 바꾼다.
    // 입력: x - 꼭짓점 X, y - 층, z - 꼭짓점 Z.
    // 출력: 꼭짓점 Index.
    private static int Point(int x, int y, int z) => x + PointsX * (z + PointsZ * y);

    // 기능: 두 꼭짓점을 순서와 무관한 모서리 키로 합친다.
    // 입력: a - 꼭짓점 Index, b - 다른 꼭짓점 Index.
    // 출력: 낮은 Index | 높은 Index << 16.
    private static uint Key(int a, int b) => a < b ? (uint)a | ((uint)b << 16) : (uint)b | ((uint)a << 16);

    // 기능: 조각 밑면의 몇 지점 중 하나라도 지형이나 맵 상자 윗면에 닿는지 본다(D12. Ramp는 낮은 변, Roof는 윗 층 바닥 높이).
    // 입력: s - 조각 모양, terrain - 지형 높이, mapBoxes - 맵의 정적 상자(채집물 제외).
    // 출력: 땅에 닿아 있으면 true.
    public static bool IsGrounded(in BuildPieceShape s, HeightField terrain, ReadOnlySpan<Box> mapBoxes)
    {
        float x0 = BuildGrid.CellMinX(s.X);
        float z0 = BuildGrid.CellMinZ(s.Z);
        float y = BuildGrid.LevelBase(s.Y);
        const float c = BuildGrid.CellSize;
        const float inset = 0.1f;
        switch (s.Type)
        {
            case BuildPieceType.Wall:
                if (s.Rotation == 0)
                    return Rests(x0 + inset, y, z0, terrain, mapBoxes) || Rests(x0 + c * 0.5f, y, z0, terrain, mapBoxes) || Rests(x0 + c - inset, y, z0, terrain, mapBoxes);
                return Rests(x0, y, z0 + inset, terrain, mapBoxes) || Rests(x0, y, z0 + c * 0.5f, terrain, mapBoxes) || Rests(x0, y, z0 + c - inset, terrain, mapBoxes);
            case BuildPieceType.Floor:
            case BuildPieceType.Roof:
                if (s.Type == BuildPieceType.Roof) y = BuildGrid.LevelBase(s.Y + 1);
                return Rests(x0 + c * 0.5f, y, z0 + c * 0.5f, terrain, mapBoxes) || Rests(x0 + inset, y, z0 + inset, terrain, mapBoxes) ||
                       Rests(x0 + c - inset, y, z0 + inset, terrain, mapBoxes) || Rests(x0 + inset, y, z0 + c - inset, terrain, mapBoxes) ||
                       Rests(x0 + c - inset, y, z0 + c - inset, terrain, mapBoxes);
            default:
                // A ramp rests on its low edge.
                float lx0, lz0, lx1, lz1;
                switch (s.Rotation)
                {
                    case 0: lx0 = x0 + inset; lz0 = z0; lx1 = x0 + c - inset; lz1 = z0; break;
                    case 1: lx0 = x0; lz0 = z0 + inset; lx1 = x0; lz1 = z0 + c - inset; break;
                    case 2: lx0 = x0 + inset; lz0 = z0 + c; lx1 = x0 + c - inset; lz1 = z0 + c; break;
                    default: lx0 = x0 + c; lz0 = z0 + inset; lx1 = x0 + c; lz1 = z0 + c - inset; break;
                }
                return Rests(lx0, y, lz0, terrain, mapBoxes) || Rests((lx0 + lx1) * 0.5f, y, (lz0 + lz1) * 0.5f, terrain, mapBoxes) ||
                       Rests(lx1, y, lz1, terrain, mapBoxes);
        }
    }

    // 기능: 높이 y의 한 점이 땅에 닿는지 본다: 아래 지형이 GroundTolerance 안으로 낮거나 더 높거나(발이 땅속), 아래 맵 상자
    //   윗면이 y와 GroundTolerance 안이면 닿는다.
    // 입력: x - 점 X, y - 점 높이, z - 점 Z, terrain - 지형 높이, mapBoxes - 맵의 정적 상자.
    // 출력: 닿으면 true.
    private static bool Rests(float x, float y, float z, HeightField terrain, ReadOnlySpan<Box> mapBoxes)
    {
        if (terrain.Height(x, z) >= y - GroundTolerance) return true;
        for (int i = 0; i < mapBoxes.Length; i++)
        {
            ref readonly Box b = ref mapBoxes[i];
            if (x >= b.Min.X && x <= b.Max.X && z >= b.Min.Z && z <= b.Max.Z && MathF.Abs(b.Max.Y - y) <= GroundTolerance) return true;
        }
        return false;
    }

    // 기능: 서 있는 조각 중 이 모양과 모서리를 나누는 것이 있는지 본다(배치 지지 검사).
    // 입력: shape - 놓으려는 모양.
    // 출력: 이웃이 하나라도 있으면 true.
    public bool HasNeighbour(in BuildPieceShape shape)
    {
        Span<uint> keys = stackalloc uint[MaxEdges];
        int n = Edges(shape, keys);
        for (int k = 0; k < n; k++)
        {
            if (_heads.ContainsKey(keys[k])) return true;
        }
        return false;
    }

    // 기능: 편집 뒤 모양이 자기 자신을 뺀 다른 서 있는 조각과 모서리를 나누는지 본다(Phase 13.5 D5-8, Ramp 회전 변경).
    // 입력: shape - 편집 뒤 모양, self - 그 조각의 slot(자기 옛 모서리는 이웃으로 치지 않는다).
    // 출력: 다른 조각과 모서리 하나라도 나누면 true.
    public bool HasNeighbourOtherThan(in BuildPieceShape shape, int self)
    {
        Span<uint> keys = stackalloc uint[MaxEdges];
        int n = Edges(shape, keys);
        for (int k = 0; k < n; k++)
        {
            for (int node = _heads.TryGetValue(keys[k], out int head) ? head : -1; node >= 0; node = _next[node])
            {
                if (node / MaxEdges != self) return true;
            }
        }
        return false;
    }

    // 기능: 조각의 모서리를 새 모양으로 다시 쓴다(Phase 13.5 D7). Wall·Floor·Roof 편집은 모서리가 같아 아무것도 하지 않는다.
    //   모서리가 바뀌면(Ramp 회전) 옛 이웃 중 새 이웃에 없는 조각과 그 조각 자신을 이번 Tick 끝 붕괴 탐색 시작점에 넣는다.
    // 입력: slot - 조각의 slot, before - 옛 모양, after - 새 모양.
    // 출력: 모서리가 바뀌었으면 true(호출자가 Grounded를 다시 계산한다).
    public bool Reshape(int slot, in BuildPieceShape before, in BuildPieceShape after)
    {
        Span<uint> oldKeys = stackalloc uint[MaxEdges];
        Span<uint> newKeys = stackalloc uint[MaxEdges];
        int oldCount = Edges(before, oldKeys);
        int newCount = Edges(after, newKeys);
        if (oldCount == newCount && oldKeys.Slice(0, oldCount).SequenceEqual(newKeys.Slice(0, newCount))) return false;

        Span<int> oldAround = stackalloc int[MaxNeighbours];
        int oldNeighbours = Neighbours(slot, oldAround);
        Remove(slot);
        Add(slot, after);
        Span<int> newAround = stackalloc int[MaxNeighbours];
        int newNeighbours = Neighbours(slot, newAround);
        for (int i = 0; i < oldNeighbours; i++)
        {
            if (newAround.Slice(0, newNeighbours).IndexOf(oldAround[i]) < 0) QueueStart(oldAround[i]);
        }
        // The piece itself: its new neighbours may hang only from what it no longer touches.
        QueueStart(slot);
        return true;
    }

    // 기능: slot 하나를 이번 Tick 끝 붕괴 탐색 시작점에 넣는다(slot마다 한 번).
    // 입력: slot - 조각의 slot.
    // 출력: 반환값 없음.
    private void QueueStart(int slot)
    {
        if (_queued[slot]) return;
        _queued[slot] = true;
        _starts[_startCount++] = slot;
    }

    // 기능: 조각의 모서리들을 색인에 넣는다(모서리마다 연결 목록 머리에 끼운다).
    // 입력: slot - 조각의 slot, shape - 조각 모양.
    // 출력: 반환값 없음. 그 slot의 모서리가 색인에 들어간다.
    public void Add(int slot, in BuildPieceShape shape)
    {
        EnsureSlot(slot);
        Span<uint> keys = stackalloc uint[MaxEdges];
        int n = Edges(shape, keys);
        _edgeCount[slot] = (byte)n;
        for (int k = 0; k < n; k++)
        {
            int node = slot * MaxEdges + k;
            _keys[node] = keys[k];
            _prev[node] = -1;
            _next[node] = _heads.TryGetValue(keys[k], out int head) ? head : -1;
            if (_next[node] >= 0) _prev[_next[node]] = node;
            _heads[keys[k]] = node;
        }
    }

    // 기능: 조각의 모서리들을 색인에서 뺀다(목록이 비는 모서리는 사전에서 지운다).
    // 입력: slot - 조각의 slot.
    // 출력: 반환값 없음. 그 slot의 모서리 수가 0이 된다.
    public void Remove(int slot)
    {
        int n = _edgeCount[slot];
        for (int k = 0; k < n; k++)
        {
            int node = slot * MaxEdges + k;
            int prev = _prev[node];
            int next = _next[node];
            if (prev >= 0) _next[prev] = next;
            else if (next >= 0) _heads[_keys[node]] = next;
            else _heads.Remove(_keys[node]);
            if (next >= 0) _prev[next] = prev;
        }
        _edgeCount[slot] = 0;
    }

    // 기능: 모든 모서리와 대기 중인 탐색 시작점을 지운다(라운드 Reset).
    // 입력: 없음.
    // 출력: 반환값 없음. 색인이 비워진다.
    public void Clear()
    {
        _heads.Clear();
        Array.Clear(_edgeCount);
        for (int i = 0; i < _startCount; i++) _queued[_starts[i]] = false;
        _startCount = 0;
    }

    // Search starts waiting for the end of the tick.
    public int QueuedStarts => _startCount;
    // Nodes the last Unsupported call took from its queue (tests: the work is bounded by the components, not the map).
    public int LastVisited { get; private set; }

    // 기능: 조각을 빼기 전에 그 이웃들을 Tick 끝 붕괴 탐색 시작점에 넣는다(Final review A2: 한 Tick의 파괴가 탐색 하나를 나눈다).
    // 입력: slot - 곧 빠질 조각의 slot(아직 색인에 있어야 한다).
    // 출력: 반환값 없음. 이웃 slot이 각각 한 번씩 시작점에 들어간다.
    // Final review A2: before a piece is removed, its neighbours become search starts for the end of the tick, so every
    // destroy of a tick shares one search (Unsupported's "reached by an earlier search" stop). Each slot is queued once.
    public void QueueNeighbours(int slot)
    {
        Span<int> around = stackalloc int[MaxNeighbours];
        int n = Neighbours(slot, around);
        for (int i = 0; i < n; i++) QueueStart(around[i]);
    }

    // 기능: Tick 끝에 대기 중인 시작점들로 지지를 잃은 조각을 찾는다(Unsupported). 시작점 큐는 비워진다.
    // 입력: world - 조각 저장소(Grounded·빈 slot 확인).
    // 출력: 무너질 slot들(다음 호출까지만 유효한 공용 Buffer).
    // End of the tick: the pieces the queued starts no longer hold up (see Unsupported); the queue is empty afterwards.
    // The result is the same as searching after every destroy: removing all of them first and then searching every
    // former neighbour's component finds exactly the components that no longer reach a grounded piece.
    public ReadOnlySpan<int> UnsupportedQueued(BuildWorld world)
    {
        for (int i = 0; i < _startCount; i++) _queued[_starts[i]] = false;
        int count = _startCount;
        _startCount = 0;
        return Unsupported(new ReadOnlySpan<int>(_starts, 0, count), world);
    }

    // 기능: 이 slot과 모서리를 나누는 slot들을 각각 한 번씩 모은다(result가 차면 나머지는 버린다).
    // 입력: slot - 기준 조각의 slot, result - 이웃을 쓸 곳.
    // 출력: 모은 이웃 수.
    public int Neighbours(int slot, Span<int> result)
    {
        int count = 0;
        int n = _edgeCount[slot];
        for (int k = 0; k < n; k++)
        {
            for (int node = _heads.TryGetValue(_keys[slot * MaxEdges + k], out int head) ? head : -1; node >= 0; node = _next[node])
            {
                int other = node / MaxEdges;
                if (other == slot || result.Slice(0, count).IndexOf(other) >= 0 || count == result.Length) continue;
                result[count++] = other;
            }
        }
        return count;
    }

    // 기능: 시작점마다 연결 성분을 너비 우선으로 탐색해 땅에 닿은 조각(또는 이번 호출의 앞선 탐색이 지지됨을 확인한 조각)에
    //   닿지 못하는 성분을 모은다(D12). 호출당 조각을 많아야 한 번씩 본다.
    // 입력: starts - 탐색 시작 slot들(파괴된 조각의 옛 이웃), world - 조각 저장소.
    // 출력: 무너질 slot들(다음 호출까지만 유효한 공용 Buffer). LastVisited에 본 노드 수가 남는다.
    // D12: after a piece went, the pieces that lost their support, starting from its former neighbours. Each start's
    // component is searched once; a component that reaches a grounded piece (or a piece an earlier search of this call
    // found supported) stays. Returns the slots to collapse (the buffer is reused by the next call). Visits at most every
    // piece once per call.
    public ReadOnlySpan<int> Unsupported(ReadOnlySpan<int> starts, BuildWorld world)
    {
        int collapse = 0;
        // One stamp per search; stamps from callBase on belong to this call.
        if (_stamp > int.MaxValue - starts.Length - 2)
        {
            Array.Clear(_visited);
            _stamp = 0;
        }
        int callBase = _stamp + 1;
        int visited = 0;
        Span<int> around = stackalloc int[MaxNeighbours];
        for (int s = 0; s < starts.Length; s++)
        {
            int start = starts[s];
            if (_visited[start] >= callBase || world.At(start).Id == 0) continue;
            int search = ++_stamp;
            int head = 0;
            int tail = 0;
            _queue[tail++] = start;
            _visited[start] = search;
            bool supported = false;
            while (head < tail && !supported)
            {
                int slot = _queue[head++];
                visited++;
                if (world.At(slot).Grounded)
                {
                    supported = true;
                    break;
                }
                int n = Neighbours(slot, around);
                for (int i = 0; i < n; i++)
                {
                    int next = around[i];
                    if (_visited[next] == search) continue;
                    if (_visited[next] >= callBase)
                    {
                        // Reached by an earlier search of this call, which stopped at a grounded piece (a search that finds
                        // none explores its whole component, so it would have reached this start already).
                        supported = true;
                        break;
                    }
                    _visited[next] = search;
                    _queue[tail++] = next;
                }
            }
            if (supported) continue;
            for (int i = 0; i < tail; i++) _collapse[collapse++] = _queue[i];
        }
        LastVisited = visited;
        return new ReadOnlySpan<int>(_collapse, 0, collapse);
    }
}
