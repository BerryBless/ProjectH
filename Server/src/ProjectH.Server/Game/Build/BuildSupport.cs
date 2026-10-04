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
//    within GroundTolerance. Fixed for the piece's life (neither ever changes).
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

    // 기능: 조각 연결 색인과 탐색 버퍼를 만든다.
    // 입력: capacity - Match당 최대 조각 수(슬롯별 배열이 커질 수 있는 상한).
    // 출력: 등록된 조각과 대기 중인 탐색 시작점이 없는 BuildSupport.
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

    // 기능: 슬롯별 배열이 주어진 슬롯을 담을 만큼 크도록 늘린다.
    // 입력: slot - 곧 사용할 저장 슬롯 번호.
    // 출력: 반환값 없음. 필요하면 모든 슬롯별 배열이 두 배(최대 조각 수까지)로 커진다.
    // Every per-slot array reaches past slot (doubling, up to the piece limit).
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

    // 기능: 조각이 차지하는 건설 격자 모서리들을 키로 구한다.
    // 입력: s - 조각의 격자 위치·종류·회전, keys - 모서리 키를 받을 버퍼(MaxEdges 이상).
    // 출력: keys에 채운 모서리 수(벽 6, 그 외 4).
    // The lattice edges of a piece (4 or 6), as keys: (lower point index) | (higher point index << 16).
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

    // 기능: 사각형 조각의 네 변(벽이면 두 대각선까지)을 모서리 키로 채운다.
    // 입력: keys - 모서리 키를 받을 버퍼, p00·p10·p01·p11 - 사각형 네 꼭짓점의 격자점 번호, diagonals - 대각선도 넣을지 여부.
    // 출력: 채운 모서리 수(대각선 포함 6, 아니면 4).
    // A rectangle p00-p10 / p01-p11 (p00-p01 and p10-p11 its other sides), with both diagonals for a wall.
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

    // 기능: 격자 꼭짓점 좌표를 하나의 격자점 번호로 바꾼다.
    // 입력: x, z - 꼭짓점의 격자 X·Z 좌표, y - 층 번호.
    // 출력: 격자점 번호.
    private static int Point(int x, int y, int z) => x + PointsX * (z + PointsZ * y);

    // 기능: 두 격자점을 잇는 모서리의 키를 방향과 무관하게 만든다.
    // 입력: a, b - 모서리 양 끝의 격자점 번호.
    // 출력: 작은 번호를 아래 16비트, 큰 번호를 위 16비트에 둔 모서리 키.
    private static uint Key(int a, int b) => a < b ? (uint)a | ((uint)b << 16) : (uint)b | ((uint)a << 16);

    // 기능: 조각 바닥이 지형이나 맵 상자 윗면에 닿아 있는지(접지) 검사한다.
    // 입력: s - 검사할 조각, terrain - 높이 격자 지형, mapBoxes - 맵 정적 상자(채집 대상 제외).
    // 출력: 바닥의 검사점 중 하나라도 땅에 닿으면 true, 아니면 false.
    // D12: the piece's bottom rests on the terrain or a map box top, at any of a few points along it.
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

    // 기능: 한 점이 지형이나 맵 상자 윗면에 닿아 있는지 검사한다.
    // 입력: x, z - 점의 수평 위치, y - 점의 높이, terrain - 높이 격자 지형, mapBoxes - 맵 정적 상자.
    // 출력: 지형이나 상자 윗면이 GroundTolerance 안에 있으면 true, 아니면 false.
    // A point at height y rests on the ground: the terrain under it is at most GroundTolerance lower (or higher: the
    // piece's foot is in the ground), or a map box top under it is within GroundTolerance of y.
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

    // 기능: 놓으려는 조각과 모서리를 공유하는 기존 조각이 있는지 검사한다.
    // 입력: shape - 놓으려는 조각.
    // 출력: 모서리를 공유하는 조각이 하나라도 있으면 true, 없으면 false.
    // Placement: some standing piece shares an edge with this shape.
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

    // 기능: 새 조각의 모서리들을 모서리 색인에 등록한다.
    // 입력: slot - 조각의 저장 슬롯 번호, shape - 조각의 격자 위치·종류·회전.
    // 출력: 반환값 없음. 조각이 각 모서리 목록의 맨 앞에 연결된다.
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

    // 기능: 조각의 모서리들을 모서리 색인에서 떼어 낸다.
    // 입력: slot - 지울 조각의 저장 슬롯 번호.
    // 출력: 반환값 없음. 조각이 모든 모서리 목록에서 빠지고, 빈 모서리는 색인에서 지워진다.
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

    // 기능: 모서리 색인과 대기 중인 탐색 시작점을 모두 비운다(라운드 리셋, Match 시작).
    // 입력: 없음.
    // 출력: 반환값 없음. 등록된 조각과 탐색 시작점이 없는 상태가 된다.
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

    // 기능: 제거될 조각의 이웃들을 이번 Tick 끝 지지 탐색의 시작점으로 등록한다.
    // 입력: slot - 곧 제거될 조각의 저장 슬롯 번호(아직 색인에 남아 있어야 한다).
    // 출력: 반환값 없음. 아직 등록되지 않은 이웃 슬롯이 탐색 시작점 목록에 추가된다.
    // Final review A2: before a piece is removed, its neighbours become search starts for the end of the tick, so every
    // destroy of a tick shares one search (Unsupported's "reached by an earlier search" stop). Each slot is queued once.
    public void QueueNeighbours(int slot)
    {
        Span<int> around = stackalloc int[MaxNeighbours];
        int n = Neighbours(slot, around);
        for (int i = 0; i < n; i++)
        {
            int start = around[i];
            if (_queued[start]) continue;
            _queued[start] = true;
            _starts[_startCount++] = start;
        }
    }

    // 기능: 이번 Tick에 쌓인 탐색 시작점으로 지지를 잃은 조각들을 한 번에 찾는다.
    // 입력: world - 현재 Match의 건설 조각 목록(파괴된 조각은 이미 제거된 상태).
    // 출력: 무너뜨릴 조각의 슬롯 목록(다음 호출에서 버퍼가 재사용됨). 호출 후 시작점 목록은 비워진다.
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

    // 기능: 한 조각과 모서리를 공유하는 이웃 조각 슬롯을 중복 없이 모은다.
    // 입력: slot - 기준 조각의 저장 슬롯 번호, result - 이웃 슬롯을 받을 버퍼.
    // 출력: result에 채운 이웃 수. 버퍼가 차면 나머지 이웃은 버린다.
    // The slots sharing an edge with this one (each once), into result; returns how many.
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

    // 기능: 시작점마다 연결된 조각 묶음을 BFS로 탐색해 접지 조각에 닿지 않는 묶음을 찾는다.
    // 입력: starts - 탐색 시작 슬롯(제거된 조각의 이전 이웃), world - 현재 Match의 건설 조각 목록.
    // 출력: 무너뜨릴 조각의 슬롯 목록(다음 호출에서 버퍼가 재사용됨). LastVisited가 이번 탐색 수로 갱신된다.
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
