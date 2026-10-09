using System;
using System.Collections.Generic;

namespace ProjectH.Shared.Simulation
{
    // Phase 13 D3, D10: the building pieces a side knows, by id and by grid column (cell X, Z), for movement collision.
    // The server's BuildWorld and the client's BuildStore each own one and add or remove pieces only when the server says
    // so (the client: confirmed pieces only, never its predicted ones), so client prediction gathers the same pieces the
    // server does. Each column is a list in id order (ids grow, so an add is usually an append), which keeps the gather
    // order the same on both sides. At most Capacity pieces; the slot arrays start small and double as pieces arrive
    // (up to Capacity), so an empty match costs little, and once they are big enough add and remove allocate nothing.
    // One thread only (the server's game loop, or the client's main thread).
    public sealed class PieceGrid
    {
        private const int ColumnCount = BuildGrid.CellsX * BuildGrid.CellsZ;
        private const int InitialSlots = 256;

        private readonly Dictionary<uint, int> _slotOfId = new Dictionary<uint, int>();
        private uint[] _ids;
        private BuildPieceShape[] _shapes;
        private int[] _next;
        private int[] _prev;
        private int[] _free;
        private readonly int[] _heads = new int[ColumnCount];
        private readonly int[] _tails = new int[ColumnCount];
        private int _freeCount;
        private int _used;

        // 기능: 빈 조각 격자를 만든다. 슬롯 배열은 capacity와 InitialSlots 중 작은 크기로 시작해 필요할 때 두 배로 자란다.
        // 입력: capacity - 담을 수 있는 최대 조각 수(1 이상).
        // 출력: 조각이 없는 격자. capacity가 1 미만이면 ArgumentOutOfRangeException.
        public PieceGrid(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
            int slots = Math.Min(capacity, InitialSlots);
            _ids = new uint[slots];
            _shapes = new BuildPieceShape[slots];
            _next = new int[slots];
            _prev = new int[slots];
            _free = new int[slots];
            Clear();
        }

        public int Capacity { get; }
        public int Count => _slotOfId.Count;
        // Columns (build cells) holding at least one piece: the spatial index's size (Phase 13 D18).
        public int OccupiedColumns { get; private set; }
        // Changes on every add and remove (client views redraw only then).
        public int Version { get; private set; }
        // Slots handed out so far (every slot index is below it): the size other per-slot arrays need.
        public int SlotCount => _used;

        // 기능: 그 id의 조각이 있는지 본다.
        // 입력: id - 조각 id.
        // 출력: 있으면 true.
        public bool Contains(uint id) => _slotOfId.ContainsKey(id);

        // 기능: 조각의 슬롯 번호를 낸다. 조각이 있는 동안 바뀌지 않아 서버가 슬롯별 배열의 인덱스로 쓴다.
        // 입력: id - 조각 id.
        // 출력: 슬롯 번호(0..Capacity-1), 없으면 -1.
        // The slot index of a piece (0..Capacity-1), or -1. Stable while the piece exists: the server keeps its own
        // per-piece data in arrays indexed by it.
        public int SlotOf(uint id) => _slotOfId.TryGetValue(id, out int slot) ? slot : -1;

        // 기능: 슬롯에 든 조각의 id를 낸다(빈 슬롯이면 남아 있는 옛 값. 범위 검사 없음).
        // 입력: slot - 슬롯 번호.
        // 출력: 조각 id.
        public uint IdAt(int slot) => _ids[slot];
        // 기능: 슬롯에 든 조각의 모양을 복사 없이 참조로 낸다(빈 슬롯이면 남아 있는 옛 값. 범위 검사 없음).
        // 입력: slot - 슬롯 번호.
        // 출력: 조각 모양의 읽기 전용 참조.
        public ref readonly BuildPieceShape ShapeAt(int slot) => ref _shapes[slot];

        // 기능: id로 조각 모양을 찾는다.
        // 입력: id - 조각 id, shape - 결과 모양.
        // 출력: 있으면 true와 그 모양, 없으면 false와 default.
        public bool TryGet(uint id, out BuildPieceShape shape)
        {
            if (_slotOfId.TryGetValue(id, out int slot))
            {
                shape = _shapes[slot];
                return true;
            }
            shape = default;
            return false;
        }

        // 기능: 조각을 넣고 그 칸의 목록에 id 순으로 끼운다(보통은 끝에 붙는다). 빈 슬롯을 먼저 재사용한다.
        // 입력: id - 조각 id, shape - 모양, slot - 받은 슬롯 번호.
        // 출력: 넣었으면 true와 슬롯 번호(Count·Version·OccupiedColumns가 바뀐다). 같은 id가 이미 있거나, 꽉 찼거나,
        //   칸이 격자 밖이면 false와 -1.
        // False when the id is already here, the grid is full, or the shape's cell is off the grid.
        public bool TryAdd(uint id, in BuildPieceShape shape, out int slot)
        {
            slot = -1;
            if (_slotOfId.ContainsKey(id)) return false;
            if (shape.X >= BuildGrid.CellsX || shape.Z >= BuildGrid.CellsZ) return false;
            if (_freeCount > 0) slot = _free[--_freeCount];
            else if (_used < Capacity) slot = _used++;
            else return false;
            if (slot >= _ids.Length) Grow();
            _ids[slot] = id;
            _shapes[slot] = shape;
            _slotOfId.Add(id, slot);

            // Keep the column in id order: walk back from the tail past larger ids (an append in the usual case).
            int column = shape.X + shape.Z * BuildGrid.CellsX;
            if (_heads[column] < 0) OccupiedColumns++;
            int after = _tails[column];
            while (after >= 0 && _ids[after] > id) after = _prev[after];
            int before = after >= 0 ? _next[after] : _heads[column];
            _prev[slot] = after;
            _next[slot] = before;
            if (after >= 0) _next[after] = slot;
            else _heads[column] = slot;
            if (before >= 0) _prev[before] = slot;
            else _tails[column] = slot;
            Version++;
            return true;
        }

        // 기능: 조각의 모양만 바꾼다(Phase 13.5 D6 편집). 같은 id·같은 저장 위치(slot)·같은 칸 목록 자리를 유지하므로 slot으로
        //   묶인 서버 배열(BuildWorld, BuildSupport)이 어긋나지 않고, 수집 순서(id 순)도 그대로다.
        // 입력: id - 조각 id, shape - 새 모양(같은 슬롯 키: 종류·칸·층·벽 방향이 같고 편집 상태나 Ramp 회전만 다름).
        // 출력: 바꿨으면 true(Version이 오른다. Count·OccupiedColumns·칸 목록은 그대로). 없는 id이거나 슬롯 키가 다르면 false(아무것도 바뀌지 않는다).
        public bool SetShape(uint id, in BuildPieceShape shape)
        {
            if (!_slotOfId.TryGetValue(id, out int slot)) return false;
            if (BuildGrid.SlotKey(_shapes[slot]) != BuildGrid.SlotKey(shape) || _shapes[slot].Type != shape.Type) return false;
            _shapes[slot] = shape;
            Version++;
            return true;
        }

        // 기능: 조각을 빼고 그 칸의 목록에서 잇고, 슬롯을 재사용 목록에 돌려준다.
        // 입력: id - 뺄 조각 id.
        // 출력: 뺐으면 true(Count·Version·OccupiedColumns가 바뀐다). 없는 id면 false.
        public bool Remove(uint id)
        {
            if (!_slotOfId.TryGetValue(id, out int slot)) return false;
            _slotOfId.Remove(id);
            ref BuildPieceShape shape = ref _shapes[slot];
            int column = shape.X + shape.Z * BuildGrid.CellsX;
            int prev = _prev[slot];
            int next = _next[slot];
            if (prev >= 0) _next[prev] = next;
            else _heads[column] = next;
            if (next >= 0) _prev[next] = prev;
            else _tails[column] = prev;
            if (_heads[column] < 0) OccupiedColumns--;
            _free[_freeCount++] = slot;
            Version++;
            return true;
        }

        // 기능: 모든 조각을 비우고 칸 목록과 슬롯을 처음 상태로 되돌린다(배열 크기는 유지).
        // 입력: 없음.
        // 출력: 반환값 없음. Count 0, OccupiedColumns 0, SlotCount 0이 되고 Version이 오른다.
        public void Clear()
        {
            _slotOfId.Clear();
            OccupiedColumns = 0;
            for (int i = 0; i < ColumnCount; i++)
            {
                _heads[i] = -1;
                _tails[i] = -1;
            }
            _freeCount = 0;
            _used = 0;
            Version++;
        }

        // 기능: 한 칸(기둥) 목록의 첫 슬롯을 낸다(id 순 순회의 시작. 이어서 Next를 -1까지 부른다).
        // 입력: x·z - 칸 좌표(격자 밖은 빈 칸으로 친다).
        // 출력: 첫 슬롯 번호, 비었거나 격자 밖이면 -1.
        // Iteration over one column in id order: First, then Next until -1. Off-grid columns are empty.
        public int First(int x, int z) =>
            x < 0 || x >= BuildGrid.CellsX || z < 0 || z >= BuildGrid.CellsZ ? -1 : _heads[x + z * BuildGrid.CellsX];

        // 기능: 같은 칸 목록에서 다음 슬롯을 낸다.
        // 입력: slot - 현재 슬롯 번호(목록에 든 슬롯).
        // 출력: 다음 슬롯 번호, 끝이면 -1.
        public int Next(int slot) => _next[slot];

        // 기능: 슬롯 배열을 두 배로 늘린다(Capacity까지). 생성 후 유일한 할당이며, 지금까지보다 많은 조각이 생길 때만 일어난다.
        // 입력: 없음.
        // 출력: 반환값 없음. 다섯 슬롯 배열이 커진다.
        // Doubles the slot arrays (up to Capacity): the only allocation after construction, and only while the match
        // holds more pieces than ever before.
        private void Grow()
        {
            int size = Math.Min(Capacity, _ids.Length * 2);
            Array.Resize(ref _ids, size);
            Array.Resize(ref _shapes, size);
            Array.Resize(ref _next, size);
            Array.Resize(ref _prev, size);
            Array.Resize(ref _free, size);
        }
    }
}
