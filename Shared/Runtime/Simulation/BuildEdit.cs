using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Phase 13.5 D1, D2: a piece's edit state (BuildPieceShape.Edit, 12 bits) and the rules both sides share for it: which
    // states exist (IsValid), what a tile selection means (FromSelection), and where each tile of a piece is (the client's
    // overlay and tile picking). Shared because the client's preview and the server's check must agree on the same state
    // (game-core-rules §4, Phase 13 exception: piece shapes). Who may edit, range and line of sight are server rules
    // (Match.TryEdit, BuildRules.CanEdit). Pure, no allocation.
    //  - Wall: bits 0-8 = the holed tiles of 3 x 3. Tile = column + 3 x row; rows bottom (0) to top (2); columns along the
    //    canonical axis (rotation 0: +X, rotation 1: +Z).
    //  - Floor: bits 0-3 = the holed quadrants. Quadrant = (x half) + 2 x (z half).
    //  - Roof: 0 pyramid, 1-4 one-way slope rising toward 0 +Z, 1 +X, 2 -Z, 3 -X (Edit - 1), 5 flat, 6 passage.
    //  - Ramp: always 0; an edit changes its rotation (rising direction) only.
    public static class BuildEdit
    {
        public const int Bits = 12;
        public const int Mask = (1 << Bits) - 1;
        public const int WallColumns = 3;
        public const int WallRows = 3;
        public const int WallTiles = WallColumns * WallRows;
        public const int QuadrantTiles = 4;
        public const int RoofPyramid = 0;
        public const int RoofSlopeFirst = 1;
        public const int RoofSlopeLast = 4;
        public const int RoofFlat = 5;
        public const int RoofPassage = 6;

        // BuildEditRequest.State and the Edited record: Edit in bits 0-11, the rotation in bits 12-13, 14-15 zero.
        public const int RotationShift = 12;

        // 기능: 편집 상태와 회전을 와이어 상태 값 하나로 묶는다.
        // 입력: edit - 12비트 편집 상태, rotation - 회전(0–3).
        // 출력: 상태 값(Edit | 회전 << 12).
        public static ushort PackState(int edit, int rotation) => (ushort)((edit & Mask) | ((rotation & 3) << RotationShift));

        // 기능: 모양의 현재 편집 상태와 회전을 와이어 상태 값으로 낸다.
        // 입력: shape - 조각 모양.
        // 출력: 상태 값.
        public static ushort StateOf(in BuildPieceShape shape) => PackState(shape.Edit, shape.Rotation);

        // 기능: 조각 종류의 칸 개수를 낸다(오버레이와 선택 비트 수).
        // 입력: type - 조각 종류.
        // 출력: 벽 9, 바닥·지붕·경사로 4.
        public static int TileCount(BuildPieceType type) => type == BuildPieceType.Wall ? WallTiles : QuadrantTiles;

        // 기능: 편집 상태가 그 종류에서 허용되는지 본다(D2).
        //   Wall: 0이거나, 뚫린 칸이 4방향으로 이어진 한 덩어리이고 남는 칸이 1개 이상. Floor: 0–14. Roof: 0–6. Ramp: 0.
        // 입력: type - 조각 종류, edit - 편집 상태.
        // 출력: 허용되면 true.
        public static bool IsValid(BuildPieceType type, int edit)
        {
            if (edit < 0 || edit > Mask) return false;
            switch (type)
            {
                case BuildPieceType.Wall:
                    if (edit == 0) return true;
                    if (edit >= 1 << WallTiles || edit == (1 << WallTiles) - 1) return false;
                    return IsConnected(edit);
                case BuildPieceType.Floor:
                    return edit < (1 << QuadrantTiles) - 1;
                case BuildPieceType.Roof:
                    return edit <= RoofPassage;
                case BuildPieceType.Ramp:
                    return edit == 0;
                default:
                    return false;
            }
        }

        // 기능: 와이어 상태 값을 조각에 적용한 새 모양을 만든다. 서버 검증(Match.TryEdit 4단계)과 Client의 Edited 적용이 같이 쓴다.
        // 입력: shape - 지금 모양, state - 와이어 상태(Edit | 회전 << 12), edited - 결과 모양.
        // 출력: 14–15비트가 0이고, Ramp가 아니면 회전이 그대로이며, BuildEdit.IsValid이면 true와 새 모양(같은 칸·종류). 아니면 false.
        public static bool TryApply(in BuildPieceShape shape, ushort state, out BuildPieceShape edited)
        {
            edited = shape;
            if ((state >> (RotationShift + 2)) != 0) return false;
            int edit = state & Mask;
            int rotation = (state >> RotationShift) & 3;
            if (shape.Type != BuildPieceType.Ramp && rotation != shape.Rotation) return false;
            if (!IsValid(shape.Type, edit)) return false;
            edited = shape.WithEdit(edit, rotation);
            return true;
        }

        // 기능: Client의 칸 선택을 편집 상태와 회전으로 바꾼다(D10).
        //   Wall·Floor: 고른 칸이 뚫린다. Roof: 0칸 사각뿔, 이웃 2칸 = 그쪽이 낮은 한쪽 경사, 대각 2칸 통로, 4칸 평지붕.
        //   Ramp: 0칸 = 지금 회전 그대로, 이웃 2칸 = 그쪽이 낮은 끝(오르는 방향은 반대).
        // 입력: type - 조각 종류, selection - 고른 칸 비트(칸 번호는 이 클래스 주석), currentRotation - 지금 회전,
        //   edit·rotation - 결과.
        // 출력: 선택이 유효한 상태가 되면 true와 그 상태. 아니면 false와 원래 모양(edit 0, 지금 회전).
        public static bool FromSelection(BuildPieceType type, int selection, int currentRotation, out int edit, out int rotation)
        {
            edit = 0;
            rotation = currentRotation;
            if (selection < 0 || selection >= 1 << TileCount(type)) return false;
            switch (type)
            {
                case BuildPieceType.Wall:
                case BuildPieceType.Floor:
                    if (!IsValid(type, selection)) return false;
                    edit = selection;
                    return true;
                case BuildPieceType.Roof:
                    if (selection == 0) return true;
                    if (selection == 15)
                    {
                        edit = RoofFlat;
                        return true;
                    }
                    if (selection == 0b1001 || selection == 0b0110)
                    {
                        edit = RoofPassage;
                        return true;
                    }
                    int roofDirection = RisingDirectionFromLowSide(selection);
                    if (roofDirection < 0) return false;
                    edit = RoofSlopeFirst + roofDirection;
                    return true;
                case BuildPieceType.Ramp:
                    if (selection == 0) return true;
                    int rampDirection = RisingDirectionFromLowSide(selection);
                    if (rampDirection < 0) return false;
                    rotation = rampDirection;
                    return true;
                default:
                    return false;
            }
        }

        // 기능: 2 x 2에서 이웃한 두 칸(낮은 쪽)을 높아지는 방향으로 바꾼다.
        // 입력: selection - 칸 비트(사분면 = x 절반 + 2 x z 절반).
        // 출력: -Z쪽 두 칸 → 0(+Z), -X쪽 → 1(+X), +Z쪽 → 2(-Z), +X쪽 → 3(-X). 그 외 -1.
        private static int RisingDirectionFromLowSide(int selection)
        {
            switch (selection)
            {
                case 0b0011: return 0;
                case 0b0101: return 1;
                case 0b1100: return 2;
                case 0b1010: return 3;
                default: return -1;
            }
        }

        // 기능: 칸 하나가 차지하는 공간(상자)을 낸다. Client 오버레이와 서버의 "새로 막히는 칸" 검사가 쓴다.
        //   Wall: 그 칸의 벽 두께 전체. Floor: 그 사분면의 판. Roof·Ramp: 조각 범위(BoundsOf)의 그 사분면 기둥.
        // 입력: shape - 조각 모양, tile - 칸 번호(0..TileCount-1. 범위 밖이면 Wall은 끝 칸으로 자르고, 나머지는 하위 2비트만 쓴다).
        // 출력: 칸의 상자.
        public static Box TileBox(in BuildPieceShape shape, int tile)
        {
            float x0 = BuildGrid.CellMinX(shape.X);
            float z0 = BuildGrid.CellMinZ(shape.Z);
            if (shape.Type == BuildPieceType.Wall)
            {
                if (tile < 0) tile = 0;
                if (tile >= WallTiles) tile = WallTiles - 1;
                int col = tile % WallColumns;
                int row = tile / WallColumns;
                float y0 = BuildGrid.LevelBase(shape.Y);
                float u0 = BuildGrid.WallU(col), u1 = BuildGrid.WallU(col + 1);
                float v0 = y0 + BuildGrid.WallV(row), v1 = y0 + BuildGrid.WallV(row + 1);
                const float half = BuildGrid.WallThickness * 0.5f;
                return shape.Rotation == 0
                    ? new Box(new Vector3(x0 + u0, v0, z0 - half), new Vector3(x0 + u1, v1, z0 + half))
                    : new Box(new Vector3(x0 - half, v0, z0 + u0), new Vector3(x0 + half, v1, z0 + u1));
            }
            tile &= 3;
            const float h = BuildGrid.CellSize * 0.5f;
            float qx = x0 + (tile & 1) * h;
            float qz = z0 + (tile >> 1) * h;
            Box bounds = BuildGrid.BoundsOf(shape);
            return new Box(new Vector3(qx, bounds.Min.Y, qz), new Vector3(qx + h, bounds.Max.Y, qz + h));
        }

        // 기능: 조각 면 위(또는 근처)의 점이 어느 칸에 드는지 낸다(Client의 조준점 → 칸 고르기).
        //   Wall: 벽 축 방향 위치로 열, 층 바닥에서의 높이로 행. 나머지: 칸의 x·z 절반.
        // 입력: shape - 조각 모양, point - 월드 위치(칸 밖이면 가장 가까운 칸으로 자른다).
        // 출력: 칸 번호(0..TileCount-1).
        public static int TileAt(in BuildPieceShape shape, Vector3 point)
        {
            float x0 = BuildGrid.CellMinX(shape.X);
            float z0 = BuildGrid.CellMinZ(shape.Z);
            if (shape.Type == BuildPieceType.Wall)
            {
                float u = shape.Rotation == 0 ? point.X - x0 : point.Z - z0;
                float v = point.Y - BuildGrid.LevelBase(shape.Y);
                int col = ClampIndex((int)MathF.Floor(u / (BuildGrid.CellSize / WallColumns)), WallColumns);
                int row = ClampIndex((int)MathF.Floor(v / (BuildGrid.LevelHeight / WallRows)), WallRows);
                return col + WallColumns * row;
            }
            int xh = point.X - x0 >= BuildGrid.CellSize * 0.5f ? 1 : 0;
            int zh = point.Z - z0 >= BuildGrid.CellSize * 0.5f ? 1 : 0;
            return xh + 2 * zh;
        }

        // 기능: 켜진 비트 수를 센다(netstandard2.1에는 BitOperations가 없다).
        // 입력: bits - 비트 값.
        // 출력: 켜진 비트 수.
        public static int PopCount(int bits)
        {
            int count = 0;
            for (uint v = (uint)bits; v != 0; v &= v - 1) count++;
            return count;
        }

        // 기능: 벽의 뚫린 칸들이 4방향으로 이어진 한 덩어리인지 본다.
        // 입력: holes - 뚫린 칸 비트(0이 아님, 9비트 안).
        // 출력: 한 덩어리면 true.
        private static bool IsConnected(int holes)
        {
            int lowest = holes & -holes;
            int reached = lowest;
            int frontier = lowest;
            while (frontier != 0)
            {
                int grown = 0;
                for (int tile = 0; tile < WallTiles; tile++)
                {
                    if ((frontier & (1 << tile)) == 0) continue;
                    int col = tile % WallColumns;
                    if (col > 0) grown |= 1 << (tile - 1);
                    if (col < WallColumns - 1) grown |= 1 << (tile + 1);
                    if (tile >= WallColumns) grown |= 1 << (tile - WallColumns);
                    if (tile < WallTiles - WallColumns) grown |= 1 << (tile + WallColumns);
                }
                frontier = grown & holes & ~reached;
                reached |= frontier;
            }
            return reached == holes;
        }

        // 기능: 칸 번호를 0..count-1 범위로 자른다.
        // 입력: value - 자를 번호, count - 칸 수.
        // 출력: 범위 안으로 자른 번호.
        private static int ClampIndex(int value, int count) => value < 0 ? 0 : value >= count ? count - 1 : value;
    }
}
