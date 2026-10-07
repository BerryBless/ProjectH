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

        public bool Contains(uint id) => _slotOfId.ContainsKey(id);

        // The slot index of a piece (0..Capacity-1), or -1. Stable while the piece exists: the server keeps its own
        // per-piece data in arrays indexed by it.
        public int SlotOf(uint id) => _slotOfId.TryGetValue(id, out int slot) ? slot : -1;

        public uint IdAt(int slot) => _ids[slot];
        public ref readonly BuildPieceShape ShapeAt(int slot) => ref _shapes[slot];

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
        // 출력: 바꿨으면 true. 없는 id이거나 슬롯 키가 다르면 false(아무것도 바뀌지 않는다).
        public bool SetShape(uint id, in BuildPieceShape shape)
        {
            if (!_slotOfId.TryGetValue(id, out int slot)) return false;
            if (BuildGrid.SlotKey(_shapes[slot]) != BuildGrid.SlotKey(shape) || _shapes[slot].Type != shape.Type) return false;
            _shapes[slot] = shape;
            Version++;
            return true;
        }

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

        // Iteration over one column in id order: First, then Next until -1. Off-grid columns are empty.
        public int First(int x, int z) =>
            x < 0 || x >= BuildGrid.CellsX || z < 0 || z >= BuildGrid.CellsZ ? -1 : _heads[x + z * BuildGrid.CellsX];

        public int Next(int slot) => _next[slot];

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
