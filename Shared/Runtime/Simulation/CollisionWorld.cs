using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Phase 13 D3: what a collider is. Values are stable (tests and logs read them).
    public enum ColliderKind : byte
    {
        None = 0,
        Static = 1,        // GameMap.Boxes[Id]
        Door = 2,          // GameMap.Doors[Id] (closed)
        Harvestable = 3,   // GameMap.Harvestables[Id] (not destroyed)
        Piece = 4,         // a building piece, Id = its piece id
    }

    // D3: a collider named by kind and id instead of its position in a span, so a door, a tree or a piece is the same
    // collider whatever else was gathered around the character.
    public readonly struct ColliderId : IEquatable<ColliderId>
    {
        public readonly ColliderKind Kind;
        public readonly uint Id;

        public ColliderId(ColliderKind kind, uint id)
        {
            Kind = kind;
            Id = id;
        }

        public static readonly ColliderId None = default;

        public bool IsNone => Kind == ColliderKind.None;

        public bool Equals(ColliderId other) => Kind == other.Kind && Id == other.Id;
        public override bool Equals(object obj) => obj is ColliderId other && Equals(other);
        public override int GetHashCode() => ((int)Kind << 28) ^ (int)Id;
        public static bool operator ==(ColliderId a, ColliderId b) => a.Equals(b);
        public static bool operator !=(ColliderId a, ColliderId b) => !a.Equals(b);
        public override string ToString() => Kind + ":" + Id;
    }

    // Phase 13 D3: the colliders one step may touch, gathered around the character before Step. The server (Match) and
    // client prediction (LocalPlayerPredictor) gather with this same code from the same inputs (open doors, destroyed
    // harvestables, the pieces they know), so both step against the same boxes and slopes in the same order:
    // static boxes, then doors, then harvestables (each in map order), then pieces in id order. Static boxes, doors and
    // harvestables come from GatherRadius around the feet on the ground plane (any height: the glider's ground distance
    // looks straight down); pieces from the build cells within PieceCellRadius and the levels within PieceLevelRadius.
    // Both cover the longest move of one step (a vault, under 4.2 m) with room to spare. MaxPieces is every slot of those
    // cells and levels; past it (pieces sharing a slot, which PieceGrid does not refuse) the lowest ids are kept, the same
    // on both sides. Pieces further below are not gathered, so the glider's ground distance does not see them (it sees
    // the terrain and the map's boxes). Fixed buffers, no allocation:
    // reuse one instance (the server one for all players, the client one for its predictor).
    public sealed class CollisionWorld
    {
        public const float GatherRadius = 6f;
        public const int PieceCellRadius = 1;
        public const int PieceLevelRadius = 2;
        // Every slot of the gathered cells and levels: (2r + 1)^2 cells x (2r + 1) levels x 5 slots.
        public const int MaxPieces = (2 * PieceCellRadius + 1) * (2 * PieceCellRadius + 1) * (2 * PieceLevelRadius + 1) * BuildGrid.SlotKinds;
        public const int MaxStaticAndDoors = 128 + GameMap.DoorCount;
        // Phase 13.5 D3: an edited piece is up to BuildGrid.MaxPartsPerPiece boxes.
        public const int MaxBoxes = MaxStaticAndDoors + GameMap.MaxHarvestables + MaxPieces * BuildGrid.MaxPartsPerPiece;

        private readonly Box[] _boxes = new Box[MaxBoxes];
        private readonly ColliderId[] _boxIds = new ColliderId[MaxBoxes];
        private readonly Slope[] _slopes = new Slope[MaxPieces];
        private readonly ColliderId[] _slopeIds = new ColliderId[MaxPieces];
        private readonly uint[] _pieceIds = new uint[MaxPieces];
        private readonly int[] _pieceSlots = new int[MaxPieces];
        private int _boxCount;
        private int _slopeCount;

        public ReadOnlySpan<Box> Boxes => new ReadOnlySpan<Box>(_boxes, 0, _boxCount);
        public ReadOnlySpan<ColliderId> BoxIds => new ReadOnlySpan<ColliderId>(_boxIds, 0, _boxCount);
        public ReadOnlySpan<Slope> Slopes => new ReadOnlySpan<Slope>(_slopes, 0, _slopeCount);
        public ReadOnlySpan<ColliderId> SlopeIds => new ReadOnlySpan<ColliderId>(_slopeIds, 0, _slopeCount);
        // Pieces gathered by the last Gather (boxes and slopes together), for counters and tests.
        public int PieceCount { get; private set; }

        // 기능: 발 주변에서 한 걸음이 닿을 수 있는 충돌체(정적 상자, 닫힌 문, 남은 채집 대상, 주변 조각)를 정한 순서로 모은다.
        //   Phase 13.5: 조각은 PartsOf의 상자들(편집된 벽은 여러 개) 또는 경사면 하나로 들어간다.
        // 입력: feet - 발 위치, openDoors - bit i = GameMap.Doors[i] 열림(DoorStates), destroyedHarvestables - bit i =
        //   GameMap.Harvestables[i] 없어짐(HarvestStates), pieces - 아는 조각(null = 조각 없음).
        // 출력: 반환값 없음. Boxes·BoxIds·Slopes·SlopeIds·PieceCount가 새로 채워진다(할당 없음).
        public void Gather(Vector3 feet, byte openDoors, ulong destroyedHarvestables, PieceGrid pieces)
        {
            _boxCount = 0;
            _slopeCount = 0;
            PieceCount = 0;
            float minX = feet.X - GatherRadius;
            float maxX = feet.X + GatherRadius;
            float minZ = feet.Z - GatherRadius;
            float maxZ = feet.Z + GatherRadius;

            ReadOnlySpan<Box> statics = GameMap.Boxes;
            for (int i = 0; i < statics.Length; i++)
            {
                if (Near(statics[i], minX, minZ, maxX, maxZ)) AddBox(statics[i], new ColliderId(ColliderKind.Static, (uint)i));
            }
            ReadOnlySpan<Box> doors = GameMap.Doors;
            for (int i = 0; i < doors.Length; i++)
            {
                if ((openDoors & (1 << i)) == 0 && Near(doors[i], minX, minZ, maxX, maxZ)) AddBox(doors[i], new ColliderId(ColliderKind.Door, (uint)i));
            }
            ReadOnlySpan<Harvestable> harvestables = GameMap.Harvestables;
            for (int i = 0; i < harvestables.Length; i++)
            {
                if ((destroyedHarvestables & (1UL << i)) == 0 && Near(harvestables[i].Bounds, minX, minZ, maxX, maxZ))
                    AddBox(harvestables[i].Bounds, new ColliderId(ColliderKind.Harvestable, (uint)i));
            }
            if (pieces == null || pieces.Count == 0) return;

            int cellX = BuildGrid.CellX(feet.X);
            int cellZ = BuildGrid.CellZ(feet.Z);
            int level = BuildGrid.Level(feet.Y);
            int count = 0;
            for (int z = cellZ - PieceCellRadius; z <= cellZ + PieceCellRadius; z++)
            {
                for (int x = cellX - PieceCellRadius; x <= cellX + PieceCellRadius; x++)
                {
                    for (int slot = pieces.First(x, z); slot >= 0; slot = pieces.Next(slot))
                    {
                        int y = pieces.ShapeAt(slot).Y;
                        if (y < level - PieceLevelRadius || y > level + PieceLevelRadius) continue;
                        // Insertion by id: every column is in id order, but the columns interleave.
                        uint id = pieces.IdAt(slot);
                        // Full (only pieces sharing a slot can get here): the lowest ids are kept, whatever the column order.
                        if (count == MaxPieces)
                        {
                            if (id >= _pieceIds[count - 1]) continue;
                            count--;
                        }
                        int k = count++;
                        while (k > 0 && _pieceIds[k - 1] > id)
                        {
                            _pieceIds[k] = _pieceIds[k - 1];
                            _pieceSlots[k] = _pieceSlots[k - 1];
                            k--;
                        }
                        _pieceIds[k] = id;
                        _pieceSlots[k] = slot;
                    }
                }
            }
            for (int i = 0; i < count; i++)
            {
                ref readonly BuildPieceShape shape = ref pieces.ShapeAt(_pieceSlots[i]);
                var id = new ColliderId(ColliderKind.Piece, _pieceIds[i]);
                // Phase 13.5 D3: the piece's parts straight into the box buffer (an edited wall is several boxes, in
                // PartsOf's fixed order), or its slope.
                int parts = BuildGrid.PartsOf(shape, new Span<Box>(_boxes, _boxCount, BuildGrid.MaxPartsPerPiece), out bool slope);
                if (slope)
                {
                    _slopes[_slopeCount] = BuildGrid.SlopeOf(shape);
                    _slopeIds[_slopeCount++] = id;
                    continue;
                }
                for (int p = 0; p < parts; p++) _boxIds[_boxCount + p] = id;
                _boxCount += parts;
            }
            PieceCount = count;
        }

        private void AddBox(in Box box, ColliderId id)
        {
            _boxes[_boxCount] = box;
            _boxIds[_boxCount++] = id;
        }

        private static bool Near(in Box box, float minX, float minZ, float maxX, float maxZ) =>
            box.Max.X >= minX && box.Min.X <= maxX && box.Max.Z >= minZ && box.Min.Z <= maxZ;
    }
}
