using System;
using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Phase 13 D16 (request §40-§42): what the player has chosen in build mode: the piece (Z wall, X floor, V ramp,
    // B roof), the material (T cycles) and a rotation offset (R turns walls and ramps a quarter). Local only: nothing of it
    // goes to the server until a placement. Pure.
    public sealed class BuildSelection
    {
        public BuildPieceType Piece { get; private set; } = BuildPieceType.Wall;
        public BuildMaterialType Material { get; private set; } = BuildMaterialType.Wood;
        public int RotationOffset { get; private set; }

        // 기능: 건설할 조각 종류를 고른다.
        // 입력: piece - 고를 조각 종류.
        // 출력: 반환값 없음. Piece가 바뀐다.
        public void Select(BuildPieceType piece) => Piece = piece;

        // 기능: 재료를 나무, 돌, 금속 순으로 돌린다.
        // 입력: 없음.
        // 출력: 반환값 없음. Material이 다음 재료로 바뀐다.
        public void NextMaterial() => Material = (BuildMaterialType)(((int)Material + 1) % 3);

        // 기능: 회전 Offset을 1/4 바퀴 돌린다.
        // 입력: 없음.
        // 출력: 반환값 없음. RotationOffset이 0~3 안에서 1 증가한다.
        public void Rotate() => RotationOffset = (RotationOffset + 1) % 4;

        // 기능: 선택을 벽, 나무, 회전 0으로 되돌린다.
        // 입력: 없음.
        // 출력: 반환값 없음. Piece·Material·RotationOffset이 초기값이 된다.
        public void Reset()
        {
            Piece = BuildPieceType.Wall;
            Material = BuildMaterialType.Wood;
            RotationOffset = 0;
        }
    }

    // Phase 13 D16 (request §38): where the chosen piece goes, from the player's feet and where it looks, in grid math
    // only (no physics query): the cell the feet are in and the next one in the look direction (the yaw's nearest axis).
    //  - Looking ahead (pitch within LookBand of level): a wall on the edge between the two cells, a floor, ramp or roof in
    //    the next cell.
    //  - Looking down: a wall on that edge, a floor, ramp or roof in the player's own cell.
    //  - Looking up: one level higher (a wall over the edge, a floor or roof over the player, a ramp ahead).
    // The level is the one the feet are in, counted from FeetLift above them, so standing past half way up a ramp aims at
    // the next level (ramp rushing, §116). A ramp rises in the look direction; R turns walls (round the player's cell)
    // and ramps (in place) a quarter each; floors and roofs ignore it. Off the grid: no target. Pure, no allocation.
    public static class BuildTargeting
    {
        public const float LookBand = 35f;
        // Looking up: ShoulderCamera clamps pitch to [-30, 70], so the up threshold must lie inside that (30 is its limit).
        public const float UpBand = 20f;
        public const float FeetLift = 1.5f;

        // 기능: 발 위치와 시선으로 선택한 조각을 놓을 격자 슬롯을 고른다(물리 질의 없음).
        // 입력: piece - 조각 종류, feet - 발 위치, yaw - 수평 시선 각도(도), pitch - 수직 시선 각도(도, 아래가 +), rotationOffset - 회전 Offset(0~3).
        // 출력: 격자 안이면 true와 정규화된 조각 모양, 값이 유한하지 않거나 격자 밖이면 false.
        public static bool TryPick(BuildPieceType piece, Vector3 feet, float yaw, float pitch, int rotationOffset, out BuildPieceShape shape)
        {
            shape = default;
            if (!IsFinite(feet.X) || !IsFinite(feet.Y) || !IsFinite(feet.Z) || !IsFinite(yaw) || !IsFinite(pitch)) return false;
            int x = BuildGrid.CellX(feet.X);
            int z = BuildGrid.CellZ(feet.Z);
            int level = Math.Max(0, BuildGrid.Level(feet.Y + FeetLift));
            int look = Direction(yaw);                                          // 0 +Z, 1 +X, 2 -Z, 3 -X
            int direction = (look + (rotationOffset & 3)) % 4;                  // a wall's side, a ramp's rise
            int aheadX = x + (look == 1 ? 1 : look == 3 ? -1 : 0);
            int aheadZ = z + (look == 0 ? 1 : look == 2 ? -1 : 0);
            bool down = pitch >= LookBand;
            bool up = pitch <= -UpBand;

            switch (piece)
            {
                case BuildPieceType.Wall:
                    // The edge on the direction's side of the player's cell: north 2, east 3, south 0, west 1.
                    int edge = direction == 0 ? 2 : direction == 1 ? 3 : direction == 2 ? 0 : 1;
                    return BuildGrid.TryNormalize(piece, x, up ? level + 1 : level, z, edge, out shape);
                case BuildPieceType.Ramp:
                    // A ramp's rotation is its rising direction: 0 +Z, 1 +X, 2 -Z, 3 -X (the same order).
                    if (down) return BuildGrid.TryNormalize(piece, x, level, z, direction, out shape);
                    return BuildGrid.TryNormalize(piece, aheadX, up ? level + 1 : level, aheadZ, direction, out shape);
                case BuildPieceType.Floor:
                    if (down) return BuildGrid.TryNormalize(piece, x, level, z, 0, out shape);
                    if (up) return BuildGrid.TryNormalize(piece, x, level + 1, z, 0, out shape);
                    return BuildGrid.TryNormalize(piece, aheadX, level, aheadZ, 0, out shape);
                default:
                    if (down || up) return BuildGrid.TryNormalize(piece, x, level, z, 0, out shape);
                    return BuildGrid.TryNormalize(piece, aheadX, level, aheadZ, 0, out shape);
            }
        }

        // 기능: yaw에 가장 가까운 축 방향을 구한다.
        // 입력: yaw - 수평 시선 각도(도, 음수·360 이상 허용).
        // 출력: 0 +Z, 1 +X, 2 -Z, 3 -X 중 하나.
        // The yaw's nearest axis: 0 +Z (yaw 0), 1 +X (90), 2 -Z (180), 3 -X (270).
        public static int Direction(float yaw)
        {
            float y = yaw % 360f;
            if (y < 0f) y += 360f;
            return (int)MathF.Floor((y + 45f) / 90f) % 4;
        }

        // 기능: 값이 NaN이나 무한대가 아닌지 확인한다.
        // 입력: v - 확인할 값.
        // 출력: 유한하면 true, 아니면 false.
        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }

    // Phase 13 D16 (request §56-§58): when a held build button places again. A new press tries at once; while it is held
    // the candidate is looked at again every Interval and sent only when it is a different slot from the last one sent
    // (so holding still never resends one slot, and moving or turning lays the next piece). Pure.
    public sealed class TurboGate
    {
        private float _nextAt;
        private uint _lastSlot;
        private bool _hasLast;

        public float Interval { get; set; } = 0.1f;

        // 기능: 건설 버튼 상태와 후보로 지금 배치 요청을 보낼지 정한다. 새로 누르면 바로, 누르고 있으면 Interval마다 직전과 다른 슬롯일 때만 보낸다.
        // 입력: now - 현재 시간(초), pressed - 이번 프레임에 눌렀는지, held - 누르고 있는지, valid - 후보가 있고 시도 가능한지, slotKey - 후보 슬롯 키.
        // 출력: 보내야 하면 true(마지막 전송 슬롯과 다음 시도 시각이 기록됨), 아니면 false.
        // valid: the candidate exists and may be tried (in range, free, affordable). Returns true when it should be sent
        // now; then it counts as the last one sent.
        public bool ShouldSend(float now, bool pressed, bool held, bool valid, uint slotKey)
        {
            if (!held)
            {
                _hasLast = false;
                return false;
            }
            if (pressed) _hasLast = false;
            else if (now < _nextAt) return false;
            if (!valid || (_hasLast && slotKey == _lastSlot))
            {
                if (pressed) _nextAt = now + Interval;
                return false;
            }
            _lastSlot = slotKey;
            _hasLast = true;
            _nextAt = now + Interval;
            return true;
        }
    }
}
