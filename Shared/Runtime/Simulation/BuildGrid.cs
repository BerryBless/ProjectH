using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Phase 13 D1: the four pieces. Values are the wire format: never renumber, never add a piece in this phase.
    public enum BuildPieceType : byte
    {
        Wall = 0,
        Floor = 1,
        Ramp = 2,
        Roof = 3,
    }

    // Phase 13 request §5: the three building materials (and resources). Values are the wire format.
    public enum BuildMaterialType : byte
    {
        Wood = 0,
        Stone = 1,
        Metal = 2,
    }

    // Final review C: how many materials there are (the wire's per-material arrays, the resources).
    public static class BuildMaterials
    {
        public const int Count = 3;
    }

    // D1: the slot a piece takes. A cell owns a floor, a ramp and a roof slot and its south and west edges; its north and
    // east walls are the next cells' south and west walls, so one edge has one key.
    public enum BuildSlotKind : byte
    {
        WallSouth = 0,
        WallWest = 1,
        Floor = 2,
        Ramp = 3,
        Roof = 4,
    }

    // The geometry of one piece in canonical grid coordinates (BuildGrid.TryNormalize). Rotation: a wall's edge (0 south,
    // 1 west), a ramp's rising direction (0 +Z, 1 +X, 2 -Z, 3 -X, the yaw convention); 0 for a floor and a roof.
    // Phase 13.5 D1: Edit is the piece's 12-bit edit state (BuildEdit): 0 = the unedited Phase 13 shape. It is part of the
    // shape (Equals, hash) but not of the slot: an edited piece keeps its slot key (BuildGrid.SlotKey).
    public readonly struct BuildPieceShape : IEquatable<BuildPieceShape>
    {
        public readonly BuildPieceType Type;
        public readonly byte X;
        public readonly byte Y;
        public readonly byte Z;
        public readonly byte Rotation;
        public readonly ushort Edit;

        // 기능: 조각 모양을 만든다(검증하지 않는다. 검증은 BuildGrid.TryNormalize와 BuildEdit.IsValid).
        // 입력: type - 조각 종류, x·y·z - 칸 좌표와 층, rotation - 회전, edit - 12비트 편집 상태(기본 0 = 원래 모양).
        // 출력: 주어진 값을 그대로 담은 모양.
        public BuildPieceShape(BuildPieceType type, int x, int y, int z, int rotation, int edit = 0)
        {
            Type = type;
            X = (byte)x;
            Y = (byte)y;
            Z = (byte)z;
            Rotation = (byte)rotation;
            Edit = (ushort)(edit & BuildEdit.Mask);
        }

        // 기능: 같은 칸·종류에서 편집 상태와 회전만 바꾼 모양을 만든다(Phase 13.5 D6: 편집은 자리를 바꾸지 않는다).
        // 입력: edit - 새 편집 상태, rotation - 새 회전(Ramp만 실제로 바뀐다).
        // 출력: 새 모양. 유효성은 확인하지 않는다(BuildEdit.TryApply가 확인한다).
        public BuildPieceShape WithEdit(int edit, int rotation) => new BuildPieceShape(Type, X, Y, Z, rotation, edit);

        // 기능: 두 모양이 종류·칸·회전·편집 상태까지 모두 같은지 본다.
        // 입력: other - 비교할 모양.
        // 출력: 모두 같으면 true.
        public bool Equals(BuildPieceShape other) =>
            Type == other.Type && X == other.X && Y == other.Y && Z == other.Z && Rotation == other.Rotation && Edit == other.Edit;

        public override bool Equals(object obj) => obj is BuildPieceShape other && Equals(other);

        // 기능: 슬롯 키·회전·편집 상태로 해시를 만든다.
        // 입력: 없음.
        // 출력: 해시 값.
        public override int GetHashCode() => (int)BuildGrid.SlotKey(this) ^ (Rotation << 24) ^ (Edit << 19);

        public override string ToString() => Edit == 0 ? $"{Type}({X},{Y},{Z} r{Rotation})" : $"{Type}({X},{Y},{Z} r{Rotation} e{Edit})";
    }

    // Phase 13 D1, D2: the building grid and the shape of every piece, computed from integers so pieces always meet
    // exactly (no accumulated float error). Shared because movement collides with pieces (game-core-rules §4, Phase 13
    // exception): client prediction and the server must see the same boxes and slopes. Which piece may go where
    // (validation, support) is server-only. Pure, no allocation.
    public static class BuildGrid
    {
        public const float CellSize = 5f;
        public const float LevelHeight = 3f;
        public const float OriginX = -GameMap.HalfSize;
        public const float OriginZ = -GameMap.HalfSize;
        public const int CellsX = 32;
        public const int CellsZ = 32;
        public const int Levels = 16;
        public const int SlotKinds = 5;

        public const float WallThickness = 0.25f;
        public const float FloorThickness = 0.25f;
        // D2: a ramp rises one level over one cell (3 / 5 = 0.6 = MoveSettings.MaxSlope); a roof is a pyramid whose top is
        // RoofRise above its eaves, the same slope.
        public const float RampRise = LevelHeight;
        public const float RoofRise = 1.5f;
        // The slab under a ramp's or a roof's surface: walking from below is blocked by it.
        public const float SlopeThickness = 0.25f;

        // D1: validates a piece the client asked for and returns its canonical shape: a north or east wall becomes the
        // next cell's south or west wall. False for an unknown type, a rotation above 3, or a cell or level off the grid.
        public static bool TryNormalize(BuildPieceType type, int x, int y, int z, int rotation, out BuildPieceShape shape)
        {
            shape = default;
            if ((uint)type > (uint)BuildPieceType.Roof || rotation < 0 || rotation > 3) return false;
            if (y < 0 || y >= Levels) return false;
            if (type == BuildPieceType.Wall)
            {
                if (rotation == 2)
                {
                    z += 1;
                    rotation = 0;
                }
                else if (rotation == 3)
                {
                    x += 1;
                    rotation = 1;
                }
            }
            else if (type != BuildPieceType.Ramp)
            {
                rotation = 0;
            }
            if (x < 0 || x >= CellsX || z < 0 || z >= CellsZ) return false;
            shape = new BuildPieceShape(type, x, y, z, rotation);
            return true;
        }

        public static BuildSlotKind SlotOf(in BuildPieceShape shape)
        {
            switch (shape.Type)
            {
                case BuildPieceType.Wall: return shape.Rotation == 0 ? BuildSlotKind.WallSouth : BuildSlotKind.WallWest;
                case BuildPieceType.Floor: return BuildSlotKind.Floor;
                case BuildPieceType.Ramp: return BuildSlotKind.Ramp;
                default: return BuildSlotKind.Roof;
            }
        }

        // D1: one key per slot: x 6 bits, z 6 bits, level 4 bits, slot kind 3 bits.
        public static uint SlotKey(in BuildPieceShape shape) => SlotKey(shape.X, shape.Y, shape.Z, SlotOf(shape));

        public static uint SlotKey(int x, int y, int z, BuildSlotKind slot) =>
            (uint)(x & 63) | ((uint)(z & 63) << 6) | ((uint)(y & 15) << 12) | ((uint)slot << 16);

        public static float CellMinX(int x) => OriginX + x * CellSize;
        public static float CellMinZ(int z) => OriginZ + z * CellSize;
        public static float LevelBase(int y) => y * LevelHeight;

        // The cell or level a world coordinate lies in (may be off the grid: callers check the range).
        public static int CellX(float x) => (int)MathF.Floor((x - OriginX) / CellSize);
        public static int CellZ(float z) => (int)MathF.Floor((z - OriginZ) / CellSize);
        public static int Level(float y) => (int)MathF.Floor(y / LevelHeight);

        // Phase 13 kinds whose unedited shape is a slope. Since Phase 13.5 a roof may be flat (BuildEdit.RoofFlat,
        // RoofPassage): ask HasSlope(shape) for a piece's actual shape.
        public static bool IsSlope(BuildPieceType type) => type == BuildPieceType.Ramp || type == BuildPieceType.Roof;

        // Phase 13.5 D3: the most boxes PartsOf gives one piece (a wall: at most two runs per row over three rows).
        public const int MaxPartsPerPiece = 6;
        // D3: the hole of a roof passage (BuildEdit.RoofPassage), a square in the middle of the roof's slab.
        public const float RoofHoleSize = 2.5f;

        // 기능: 조각의 실제 모양이 경사면(Slope)인지 본다(Phase 13.5 D3).
        // 입력: shape - 조각 모양.
        // 출력: Ramp, 또는 Edit가 사각뿔·한쪽 경사(0–4)인 Roof면 true. 벽·바닥·평지붕·통로는 false(상자, PartsOf).
        public static bool HasSlope(in BuildPieceShape shape) =>
            shape.Type == BuildPieceType.Ramp || (shape.Type == BuildPieceType.Roof && shape.Edit < BuildEdit.RoofFlat);

        // 기능: 조각의 충돌 모양을 상자 목록 또는 경사면 하나로 낸다(Phase 13.5 D3). 이동·사격·채집·배치 검사가 모두 이것을 쓴다.
        //   벽·바닥: 남은 칸을 행마다 이어진 구간으로 묶고, 위아래 행의 같은 구간을 세로로 합친 상자들(Edit 0 = BoxOf 하나).
        //   Roof 5: 처마 높이의 판 하나. Roof 6: 가운데 RoofHoleSize 정사각을 뺀 판 4개(앞, 뒤, 왼쪽, 오른쪽 띠).
        //   Ramp와 Roof 0–4: 상자 없음, slope = true(SlopeOf로 경사면을 얻는다).
        // 입력: shape - 조각 모양(유효한 Edit, BuildEdit.IsValid), boxes - 상자를 쓸 곳(MaxPartsPerPiece칸 이상), slope - 결과 종류.
        // 출력: 쓴 상자 수(0–MaxPartsPerPiece). 경사면이면 0과 slope = true. 할당 없음.
        public static int PartsOf(in BuildPieceShape shape, Span<Box> boxes, out bool slope)
        {
            slope = HasSlope(shape);
            if (slope) return 0;
            if (shape.Edit == 0 && shape.Type != BuildPieceType.Roof)
            {
                boxes[0] = BoxOf(shape);
                return 1;
            }
            float x0 = CellMinX(shape.X);
            float z0 = CellMinZ(shape.Z);
            if (shape.Type == BuildPieceType.Roof)
            {
                float top = LevelBase(shape.Y + 1);
                float bottom = top - FloorThickness;
                if (shape.Edit == BuildEdit.RoofFlat)
                {
                    boxes[0] = new Box(new Vector3(x0, bottom, z0), new Vector3(x0 + CellSize, top, z0 + CellSize));
                    return 1;
                }
                const float a = (CellSize - RoofHoleSize) * 0.5f;
                const float b = a + RoofHoleSize;
                boxes[0] = new Box(new Vector3(x0, bottom, z0), new Vector3(x0 + CellSize, top, z0 + a));
                boxes[1] = new Box(new Vector3(x0, bottom, z0 + b), new Vector3(x0 + CellSize, top, z0 + CellSize));
                boxes[2] = new Box(new Vector3(x0, bottom, z0 + a), new Vector3(x0 + a, top, z0 + b));
                boxes[3] = new Box(new Vector3(x0 + b, bottom, z0 + a), new Vector3(x0 + CellSize, top, z0 + b));
                return 4;
            }

            bool wall = shape.Type == BuildPieceType.Wall;
            int cols = wall ? BuildEdit.WallColumns : 2;
            int rows = wall ? BuildEdit.WallRows : 2;
            Span<int> rects = stackalloc int[MaxPartsPerPiece];
            int count = MergeSolidTiles(shape.Edit, cols, rows, rects);
            float y0 = LevelBase(shape.Y);
            const float half = WallThickness * 0.5f;
            for (int i = 0; i < count; i++)
            {
                int r = rects[i];
                int c0 = r & 15, c1 = (r >> 4) & 15, r0 = (r >> 8) & 15, r1 = (r >> 12) & 15;
                if (wall)
                {
                    float u0 = WallU(c0), u1 = WallU(c1 + 1);
                    float v0 = y0 + WallV(r0), v1 = y0 + WallV(r1 + 1);
                    boxes[i] = shape.Rotation == 0
                        ? new Box(new Vector3(x0 + u0, v0, z0 - half), new Vector3(x0 + u1, v1, z0 + half))
                        : new Box(new Vector3(x0 - half, v0, z0 + u0), new Vector3(x0 + half, v1, z0 + u1));
                }
                else
                {
                    // Floor: columns along +X, rows along +Z (quadrant = x half + 2 x z half).
                    boxes[i] = new Box(new Vector3(x0 + c0 * (CellSize * 0.5f), y0 - FloorThickness, z0 + r0 * (CellSize * 0.5f)),
                        new Vector3(x0 + (c1 + 1) * (CellSize * 0.5f), y0, z0 + (r1 + 1) * (CellSize * 0.5f)));
                }
            }
            return count;
        }

        // 기능: 벽 칸 경계의 가로 위치(열 경계 k, 0–3)를 정수에서 계산한다. 끝은 정확히 0과 CellSize다.
        // 입력: k - 열 경계 번호.
        // 출력: 벽 시작점에서의 거리(m).
        public static float WallU(int k) => k >= BuildEdit.WallColumns ? CellSize : k * CellSize / BuildEdit.WallColumns;

        // 기능: 벽 칸 경계의 높이(행 경계 k, 0–3)를 정수에서 계산한다. 끝은 정확히 0과 LevelHeight다.
        // 입력: k - 행 경계 번호.
        // 출력: 층 바닥에서의 높이(m).
        public static float WallV(int k) => k >= BuildEdit.WallRows ? LevelHeight : k * LevelHeight / BuildEdit.WallRows;

        // 기능: 칸 격자에서 뚫리지 않은 칸을 행 구간으로 묶고, 바로 아래 행의 같은 열 구간과 세로로 합친 직사각형을 만든다.
        //   행을 아래부터, 행 안에서는 열 순서로 본다(같은 입력이면 항상 같은 순서).
        // 입력: holes - 뚫린 칸 비트(칸 = 열 + cols x 행), cols·rows - 격자 크기(3 x 3 이하), rects - 결과
        //   (c0 | c1 << 4 | r0 << 8 | r1 << 12, 양 끝 포함).
        // 출력: 직사각형 수. 행마다 구간은 (cols + 1) / 2개 이하라 3 x 3에서도 MaxPartsPerPiece를 넘지 않는다.
        private static int MergeSolidTiles(int holes, int cols, int rows, Span<int> rects)
        {
            int count = 0;
            for (int row = 0; row < rows; row++)
            {
                int col = 0;
                while (col < cols)
                {
                    if ((holes & (1 << (col + cols * row))) != 0)
                    {
                        col++;
                        continue;
                    }
                    int start = col;
                    while (col < cols && (holes & (1 << (col + cols * row))) == 0) col++;
                    int end = col - 1;
                    bool merged = false;
                    for (int i = 0; i < count; i++)
                    {
                        int r = rects[i];
                        if ((r & 15) == start && ((r >> 4) & 15) == end && ((r >> 12) & 15) == row - 1)
                        {
                            rects[i] = (r & 0x0FFF) | (row << 12);
                            merged = true;
                            break;
                        }
                    }
                    if (!merged) rects[count++] = start | (end << 4) | (row << 8) | (row << 12);
                }
            }
            return count;
        }

        // D2: a wall stands on its edge (WallThickness, centred on it, one level high); a floor is a thin box whose top
        // is the level's height. Only for walls and floors.
        public static Box BoxOf(in BuildPieceShape shape)
        {
            float x0 = CellMinX(shape.X);
            float z0 = CellMinZ(shape.Z);
            float y0 = LevelBase(shape.Y);
            const float half = WallThickness * 0.5f;
            if (shape.Type == BuildPieceType.Wall)
            {
                return shape.Rotation == 0
                    ? new Box(new Vector3(x0, y0, z0 - half), new Vector3(x0 + CellSize, y0 + LevelHeight, z0 + half))
                    : new Box(new Vector3(x0 - half, y0, z0), new Vector3(x0 + half, y0 + LevelHeight, z0 + CellSize));
            }
            return new Box(new Vector3(x0, y0 - FloorThickness, z0), new Vector3(x0 + CellSize, y0, z0 + CellSize));
        }

        // 기능: 경사면 조각의 경사면을 낸다. D2: Ramp는 층 높이에서 회전 방향으로 한 층 오른다. Roof는 그 층 벽 위(처마 =
        //   다음 층 높이)에 놓인다: Edit 0 사각뿔, Phase 13.5 D3 Edit 1–4 한쪽 경사(높아지는 방향 = Edit - 1, RoofSlope).
        // 입력: shape - HasSlope가 true인 모양(평지붕·통로를 넘기면 사각뿔을 돌려준다. 호출자는 HasSlope를 먼저 본다).
        // 출력: 조각의 경사면.
        public static Slope SlopeOf(in BuildPieceShape shape)
        {
            float x0 = CellMinX(shape.X);
            float z0 = CellMinZ(shape.Z);
            if (shape.Type == BuildPieceType.Ramp)
                return new Slope(x0, z0, x0 + CellSize, z0 + CellSize, LevelBase(shape.Y), SlopeKind.Ramp, shape.Rotation);
            if (shape.Edit >= BuildEdit.RoofSlopeFirst && shape.Edit <= BuildEdit.RoofSlopeLast)
                return new Slope(x0, z0, x0 + CellSize, z0 + CellSize, LevelBase(shape.Y + 1), SlopeKind.RoofSlope, (byte)(shape.Edit - BuildEdit.RoofSlopeFirst));
            return new Slope(x0, z0, x0 + CellSize, z0 + CellSize, LevelBase(shape.Y + 1), SlopeKind.Roof, 0);
        }

        // 기능: 조각 전체를 담는 상자를 낸다. 벽·바닥은 편집과 관계없이 틀(편집하지 않은 상자)이다(Phase 13.5: 사거리·시선·
        //   위치 기준이 편집으로 바뀌지 않게). 경사면은 그 칸에서 판의 가장 낮은 곳부터 꼭대기까지, 평지붕·통로는 처마의 판이다.
        // 입력: shape - 조각 모양.
        // 출력: 조각을 담는 상자.
        public static Box BoundsOf(in BuildPieceShape shape)
        {
            if (shape.Type == BuildPieceType.Wall || shape.Type == BuildPieceType.Floor) return BoxOf(shape);
            if (!HasSlope(shape))
            {
                float x0 = CellMinX(shape.X);
                float z0 = CellMinZ(shape.Z);
                float top = LevelBase(shape.Y + 1);
                return new Box(new Vector3(x0, top - FloorThickness, z0), new Vector3(x0 + CellSize, top, z0 + CellSize));
            }
            // The slab's lowest point: under a ramp's low edge, or a roof's flat ceiling.
            Slope slope = SlopeOf(shape);
            return new Box(new Vector3(slope.MinX, slope.BaseY - SlopeThickness, slope.MinZ), new Vector3(slope.MaxX, slope.Top, slope.MaxZ));
        }

        public static Vector3 CenterOf(in BuildPieceShape shape) => BoundsOf(shape).Center;
    }

    public enum SlopeKind : byte
    {
        Ramp = 0,
        Roof = 1,
        // Phase 13.5 D3: an edited roof sloping one way: a plane rising RoofRise over the cell (slope 0.3) from its low eaves,
        // solid down to the roof's ceiling like the pyramid.
        RoofSlope = 2,
    }

    // Phase 13 D2: a walkable sloped surface over one cell: a ramp (a plane), a roof (a four-sided pyramid) or, Phase 13.5,
    // a one-way roof (a plane). Movement treats it like the terrain (the floor height under the feet) plus a solid under
    // the surface that blocks from below: a thin slab under a ramp, the roof down to its ceiling. Immutable; all queries
    // are pure and allocate nothing.
    public readonly struct Slope
    {
        public readonly float MinX;
        public readonly float MinZ;
        public readonly float MaxX;
        public readonly float MaxZ;
        // Ramp: the height of its low edge. Roof and RoofSlope: the height of its eaves (the base).
        public readonly float BaseY;
        public readonly SlopeKind Kind;
        // Ramp and RoofSlope: the rising direction, 0 +Z, 1 +X, 2 -Z, 3 -X.
        public readonly byte Direction;

        // 기능: 경사면을 만든다.
        // 입력: minX·minZ·maxX·maxZ - 칸 범위, baseY - 낮은 끝(Ramp) 또는 처마(Roof·RoofSlope) 높이, kind - 종류,
        //   direction - 높아지는 방향(Ramp·RoofSlope).
        // 출력: 경사면.
        public Slope(float minX, float minZ, float maxX, float maxZ, float baseY, SlopeKind kind, byte direction)
        {
            MinX = minX;
            MinZ = minZ;
            MaxX = maxX;
            MaxZ = maxZ;
            BaseY = baseY;
            Kind = kind;
            Direction = direction;
        }

        // The plane's rise over the whole cell (Ramp: one level, RoofSlope: RoofRise); the pyramid's rise at its centre.
        public float Rise => Kind == SlopeKind.Ramp ? BuildGrid.RampRise : BuildGrid.RoofRise;

        public float Top => BaseY + Rise;

        // The surface is a plane (a ramp or a one-way roof), not the pyramid.
        public bool IsPlane => Kind != SlopeKind.Roof;

        // 기능: 한 점에서 경사면 높이를 낸다(점은 칸 안으로 자른다).
        // 입력: x·z - 점의 위치.
        // 출력: 표면 높이.
        public float HeightAt(float x, float z)
        {
            x = Clamp(x, MinX, MaxX);
            z = Clamp(z, MinZ, MaxZ);
            if (IsPlane) return PlaneHeight(Along(x, z));
            float cx = (MinX + MaxX) * 0.5f;
            float cz = (MinZ + MaxZ) * 0.5f;
            return RoofHeight(MathF.Max(MathF.Abs(x - cx), MathF.Abs(z - cz)));
        }

        // 기능: 사각형(x0..x1, z0..z1)이 칸과 겹치는 곳에서 표면의 가장 낮은·높은 높이와 그곳 고체의 바닥을 낸다.
        // 입력: x0·z0·x1·z1 - 사각형, low·high·bottom - 결과.
        // 출력: 겹치면 true(변이 닿기만 하면 false). Ramp의 바닥은 판 두께만큼 아래, 지붕(사각뿔·한쪽 경사)은 천장이다.
        public bool Range(float x0, float z0, float x1, float z1, out float low, out float high, out float bottom)
        {
            low = high = bottom = 0f;
            if (!(x0 < MaxX && x1 > MinX && z0 < MaxZ && z1 > MinZ)) return false;
            float ax = MathF.Max(x0, MinX);
            float bx = MathF.Min(x1, MaxX);
            float az = MathF.Max(z0, MinZ);
            float bz = MathF.Min(z1, MaxZ);
            if (IsPlane)
            {
                float a = Along(ax, az);
                float b = Along(bx, bz);
                float alongLow = MathF.Min(a, b);
                float alongHigh = MathF.Max(a, b);
                low = PlaneHeight(alongLow);
                high = PlaneHeight(alongHigh);
                // A one-way roof is solid down to the ceiling like the pyramid: shots and heads stop at the ceiling.
                bottom = Kind == SlopeKind.Ramp ? low - BuildGrid.SlopeThickness : BaseY - BuildGrid.SlopeThickness;
                return true;
            }
            float cx = (MinX + MaxX) * 0.5f;
            float cz = (MinZ + MaxZ) * 0.5f;
            // Chebyshev distance from the centre: the nearest point of the rectangle is the highest, its farthest corner
            // the lowest.
            float nearX = cx < ax ? ax - cx : cx > bx ? cx - bx : 0f;
            float nearZ = cz < az ? az - cz : cz > bz ? cz - bz : 0f;
            float farX = MathF.Max(MathF.Abs(ax - cx), MathF.Abs(bx - cx));
            float farZ = MathF.Max(MathF.Abs(az - cz), MathF.Abs(bz - cz));
            high = RoofHeight(MathF.Max(nearX, nearZ));
            low = RoofHeight(MathF.Max(farX, farZ));
            // The roof is a solid pyramid down to its flat ceiling: shots and heads stop at the ceiling.
            bottom = BaseY - BuildGrid.SlopeThickness;
            return true;
        }

        // Distance from the low edge in the rising direction, 0..CellSize.
        private float Along(float x, float z)
        {
            switch (Direction)
            {
                case 0: return z - MinZ;
                case 1: return x - MinX;
                case 2: return MaxZ - z;
                default: return MaxX - x;
            }
        }

        // rise = along x Rise / CellSize: exact at both ends (0 and Rise), so a ramp's top edge is exactly the next level's
        // height, the same float a floor's top has (BuildGrid.LevelBase).
        private float PlaneHeight(float along)
        {
            float rise = Rise;
            if (!(along > 0f)) return BaseY;
            if (along >= BuildGrid.CellSize) return BaseY + rise;
            return BaseY + along * rise / BuildGrid.CellSize;
        }

        // The pyramid: RoofRise at the centre, the eaves (BaseY) at Chebyshev distance CellSize / 2.
        private float RoofHeight(float distance)
        {
            const float half = BuildGrid.CellSize * 0.5f;
            if (!(distance < half)) return BaseY;
            if (!(distance > 0f)) return BaseY + BuildGrid.RoofRise;
            return BaseY + (half - distance) * BuildGrid.RoofRise / half;
        }

        private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
    }
}
