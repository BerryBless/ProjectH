using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game.Audio
{
    // Phase 18 D5 (request §80): when each player's next footstep sounds, for every player the client draws. No server packet:
    // the steps come from the movement the client already has (the local prediction, the remote interpolation).
    //  - Intervals: walk 0.5 s, sprint 0.33 s, crouch 0.7 s, the downed drag 0.9 s; a slide makes one sound when it starts.
    //  - No sound in the air (Freefall, Glide, Transport, Vault) or below MinSpeed.
    //  - Teleport guard: the first sample after a spawn, a respawn or a resume (Forget, Clear, a slot new to this frame or a
    //    dead sample) only sets the starting point, and a frame whose horizontal move is faster than TeleportSpeed (faster than
    //    sprint plus slide) is a teleport, not a step. Transport -> Freefall is in the air, so a drop makes no step.
    // One fixed slot per player id (Capacity: every snapshot entity plus the local player). The caller samples the players it
    // wants this frame between BeginFrame and EndFrame; a slot not sampled in a frame (out of range, despawned) is freed, so
    // nothing grows and a player coming back starts with a skipped sample. Pure (no UnityEngine). Main thread only.
    public sealed class FootstepModel
    {
        public const int Capacity = ProtocolConstants.MaxSnapshotEntities + 1;
        public const float WalkInterval = 0.5f;
        public const float SprintInterval = 0.33f;
        public const float CrouchInterval = 0.7f;
        public const float DragInterval = 0.9f;
        public const float MinSpeed = 0.5f;
        public const float TeleportSpeed = 12f;
        private const float MinDeltaTime = 1e-4f;

        private struct Slot
        {
            public bool Used;
            public ushort Id;
            public int SeenFrame;
            public bool HasPrevious;
            public Vector3 Previous;
            public MovementMode PreviousMode;
            public float Next;
        }

        private readonly Slot[] _slots = new Slot[Capacity];
        private int _frame;

        // How many slots are in use (tests, debug).
        public int Count
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i].Used) count++;
                }
                return count;
            }
        }

        // 기능: 새 프레임의 표본을 받기 시작한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 이번 프레임에 Sample되지 않은 칸은 EndFrame이 비운다.
        public void BeginFrame() => _frame++;

        // 기능: 이번 프레임에 표본이 없었던 칸(거리 밖, 퇴장)을 비운다. 다시 들어오면 첫 표본을 건너뛴다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void EndFrame()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Used && _slots[i].SeenFrame != _frame) _slots[i] = default;
            }
        }

        // 기능: 한 플레이어의 상태를 잊는다(부활·순간이동·퇴장). 다음 표본은 시작점만 정한다.
        // 입력: id - 플레이어 Entity id.
        // 출력: 반환값 없음. 모르는 id면 아무것도 하지 않는다.
        public void Forget(ushort id)
        {
            int slot = Find(id);
            if (slot >= 0) _slots[slot] = default;
        }

        // 기능: 모두 잊는다(끊김, 입장·재개).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Clear() => Array.Clear(_slots, 0, _slots.Length);

        // 기능: 한 플레이어의 이번 프레임 표본으로 발소리를 낼지 정한다(위 규칙).
        // 입력: id - 플레이어 id, feet - 그려지는(로컬은 렌더) 발 위치, mode - 이동 모드, sprinting - 질주 중, alive - 살아 있음(기절 포함),
        //   now - 현재 시각(초), deltaTime - 지난 프레임부터의 시간(초), knownSpeed - 알고 있는 수평 속도(로컬 예측, m/s; 음수면 발 위치 변화로 계산).
        // 출력: 낼 걸음 종류(None이면 소리 없음). 칸이 모두 차 있으면 None. 할당 없음.
        public FootstepGait Sample(ushort id, Vector3 feet, MovementMode mode, bool sprinting, bool alive, float now, float deltaTime,
            float knownSpeed = -1f)
        {
            int index = Find(id);
            if (index < 0) index = Allocate(id);
            if (index < 0) return FootstepGait.None;
            ref Slot s = ref _slots[index];
            s.SeenFrame = _frame;
            if (!alive)
            {
                s.HasPrevious = false;
                return FootstepGait.None;
            }
            if (!s.HasPrevious)
            {
                s.HasPrevious = true;
                s.Previous = feet;
                s.PreviousMode = mode;
                s.Next = now;
                return FootstepGait.None;
            }

            float dx = feet.X - s.Previous.X;
            float dz = feet.Z - s.Previous.Z;
            float moved = deltaTime > MinDeltaTime ? MathF.Sqrt(dx * dx + dz * dz) / deltaTime : 0f;
            MovementMode previousMode = s.PreviousMode;
            s.Previous = feet;
            s.PreviousMode = mode;
            if (moved > TeleportSpeed) return FootstepGait.None;
            if (IsAirborne(mode)) return FootstepGait.None;
            if (mode == MovementMode.Slide)
            {
                if (previousMode == MovementMode.Slide) return FootstepGait.None;
                s.Next = now + WalkInterval;
                return FootstepGait.Slide;
            }

            float speed = knownSpeed >= 0f ? knownSpeed : moved;
            if (speed < MinSpeed)
            {
                if (s.Next < now) s.Next = now;
                return FootstepGait.None;
            }
            FootstepGait gait = GaitOf(mode, sprinting);
            if (now < s.Next) return FootstepGait.None;
            s.Next = now + IntervalOf(gait);
            return gait;
        }

        // 기능: 모드가 발소리 없는 공중 모드인지 본다.
        // 입력: mode - 이동 모드.
        // 출력: Freefall·Glide·Transport·Vault면 true.
        public static bool IsAirborne(MovementMode mode) =>
            mode == MovementMode.Freefall || mode == MovementMode.Glide || mode == MovementMode.Transport || mode == MovementMode.Vault;

        // 기능: 모드·질주로 걸음 종류를 고른다(땅 위, 미끄럼 아님).
        // 입력: mode - 이동 모드, sprinting - 질주 중.
        // 출력: Downed = Drag, Crouch = Crouch, 질주 = Sprint, 그 밖 = Walk.
        public static FootstepGait GaitOf(MovementMode mode, bool sprinting)
        {
            if (mode == MovementMode.Downed) return FootstepGait.Drag;
            if (mode == MovementMode.Crouch) return FootstepGait.Crouch;
            return sprinting ? FootstepGait.Sprint : FootstepGait.Walk;
        }

        // 기능: 걸음 종류의 간격을 낸다.
        // 입력: gait - 걸음 종류.
        // 출력: 다음 발소리까지의 초.
        public static float IntervalOf(FootstepGait gait)
        {
            switch (gait)
            {
                case FootstepGait.Sprint: return SprintInterval;
                case FootstepGait.Crouch: return CrouchInterval;
                case FootstepGait.Drag: return DragInterval;
                default: return WalkInterval;
            }
        }

        // 기능: id의 칸을 찾는다.
        // 입력: id - 플레이어 id.
        // 출력: 칸 번호, 없으면 -1.
        private int Find(ushort id)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Used && _slots[i].Id == id) return i;
            }
            return -1;
        }

        // 기능: 빈 칸을 id에 준다(첫 표본은 시작점만 정한다).
        // 입력: id - 플레이어 id.
        // 출력: 칸 번호, 빈 칸이 없으면 -1.
        private int Allocate(ushort id)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Used) continue;
                _slots[i] = new Slot { Used = true, Id = id };
                return i;
            }
            return -1;
        }
    }

    // Phase 18 D5: what is under a player's feet, looked up only when a footstep sounds (not every frame). The highest surface
    // at or slightly above the feet wins: the terrain = Ground, a map box top = Stone, a confirmed building piece = its
    // record's material. Feet more than GroundTolerance above that surface are in the air (a jump or a fall in Ground mode),
    // and make no step. Pure (no UnityEngine).
    public static class SurfaceProbe
    {
        // Feet this far above the surface below still stand on it (interpolation and slopes put them a little off).
        public const float GroundTolerance = 0.35f;
        // A surface this far above the feet still counts (the drawn feet can sink slightly into a ramp or a step).
        public const float StepUp = 0.3f;

        // 기능: 발밑 재질을 찾는다.
        // 입력: feet - 발 위치, store - 확정 조각 저장소(null이면 조각을 보지 않는다), material - 결과.
        // 출력: 발이 표면 위(GroundTolerance 안)에 있으면 true와 그 표면의 재질, 공중이면 false(material은 가장 가까운 아래 표면의 것).
        //   발 칸의 조각만 본다. 할당 없음.
        public static bool TryFind(Vector3 feet, BuildStore store, out SurfaceMaterial material)
        {
            float limit = feet.Y + StepUp;
            float best = float.NegativeInfinity;
            material = SurfaceMaterial.Ground;

            float ground = GameMap.Terrain.Height(feet.X, feet.Z);
            if (ground <= limit) best = ground;

            ReadOnlySpan<Box> boxes = GameMap.Boxes;
            for (int i = 0; i < boxes.Length; i++)
            {
                Box b = boxes[i];
                if (!Inside(feet, b.Min.X, b.Min.Z, b.Max.X, b.Max.Z)) continue;
                float top = b.Max.Y;
                if (top > limit || top <= best) continue;
                best = top;
                material = SurfaceMaterial.Stone;
            }

            if (store != null)
            {
                PieceGrid grid = store.Grid;
                Span<Box> parts = stackalloc Box[BuildGrid.MaxPartsPerPiece];
                for (int slot = grid.First(BuildGrid.CellX(feet.X), BuildGrid.CellZ(feet.Z)); slot >= 0; slot = grid.Next(slot))
                {
                    BuildPieceShape shape = grid.ShapeAt(slot);
                    if (!TryTopOf(shape, feet, limit, parts, out float top) || top <= best) continue;
                    if (!store.TryGetConfirmed(grid.IdAt(slot), out BuildPieceRecord record)) continue;
                    best = top;
                    material = AudioCatalog.SurfaceOf(record.Material);
                }
            }
            return !float.IsNegativeInfinity(best) && feet.Y - best <= GroundTolerance;
        }

        // 기능: 조각에서 발 위치 바로 위·아래의 표면 높이를 찾는다(경사면은 그 점의 높이, 상자는 발이 위에 있는 부분의 윗면).
        // 입력: shape - 조각 모양, feet - 발, limit - 이보다 높은 표면은 무시, parts - 상자를 쓸 곳(MaxPartsPerPiece칸), top - 결과.
        // 출력: limit 이하의 표면이 있으면 true와 그중 가장 높은 것.
        private static bool TryTopOf(in BuildPieceShape shape, Vector3 feet, float limit, Span<Box> parts, out float top)
        {
            top = float.NegativeInfinity;
            int count = BuildGrid.PartsOf(shape, parts, out bool slope);
            if (slope)
            {
                Slope s = BuildGrid.SlopeOf(shape);
                if (!Inside(feet, s.MinX, s.MinZ, s.MaxX, s.MaxZ)) return false;
                float h = s.HeightAt(feet.X, feet.Z);
                if (h > limit) return false;
                top = h;
                return true;
            }
            for (int i = 0; i < count; i++)
            {
                Box b = parts[i];
                if (!Inside(feet, b.Min.X, b.Min.Z, b.Max.X, b.Max.Z) || b.Max.Y > limit || b.Max.Y <= top) continue;
                top = b.Max.Y;
            }
            return !float.IsNegativeInfinity(top);
        }

        // 기능: 발의 수평 위치가 사각형 안(경계 포함)인지 본다.
        // 입력: feet - 발, minX·minZ·maxX·maxZ - 사각형.
        // 출력: 안이면 true.
        private static bool Inside(Vector3 feet, float minX, float minZ, float maxX, float maxZ) =>
            feet.X >= minX && feet.X <= maxX && feet.Z >= minZ && feet.Z <= maxZ;
    }
}
