using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Fixed 8-sample history per remote player (no growth). Renders between the two samples around
    // the render tick; past the newest sample it holds position (no extrapolation in this phase).
    public sealed class RemotePlayerInterpolator
    {
        private const int Capacity = 8;
        // How far from the spawn point a sample taken after the respawn can be. The whole buffer is about 0.53 s at
        // 15 snapshots/s; at sprint speed (7 m/s) that is about 3.7 m, plus about 1.2 m jump height. If it is too
        // small, the view snaps a little early (a late post-respawn sample is dropped). If it is too large, an old
        // sample that close to the spawn point is kept, and the view slides less than this distance. Both are harmless.
        private const float TeleportKeepRadius = 5f;

        private readonly uint[] _ticks = new uint[Capacity];
        private readonly Vector3[] _positions = new Vector3[Capacity];
        private readonly float[] _yaws = new float[Capacity];
        // Phase 12: the snapshot's mode and flags per sample, so the pose and hit box follow the drawn position (about
        // 100 ms behind the newest snapshot), like the server's PositionHistory at the rewound tick.
        private readonly MovementMode[] _modes = new MovementMode[Capacity];
        private readonly bool[] _sprinting = new bool[Capacity];
        private readonly bool[] _exhausted = new bool[Capacity];
        private int _count;
        private int _newest = -1;

        // 기능: Ground 모드, 달리기·탈진 아님으로 샘플을 추가한다.
        // 입력: tick - 샘플의 서버 Tick, position - 발 위치, yaw - 방향(도).
        // 출력: 반환값 없음. 유효한 샘플이면 기록에 추가된다.
        public void Push(uint tick, Vector3 position, float yaw) => Push(tick, position, yaw, MovementMode.Ground, false, false);

        // 기능: Snapshot 샘플을 고정 크기 Ring 기록에 추가한다.
        // 입력: tick - Snapshot의 서버 Tick, position - 발 위치, yaw - 방향(도), mode - 이동 모드, sprinting - 달리기 여부, exhausted - 탈진 여부.
        // 출력: 반환값 없음. 값이 유한하고 Tick이 최신보다 크면 추가되고, Ring이 가득 차 있으면 가장 오래된 샘플을 덮어쓴다.
        public void Push(uint tick, Vector3 position, float yaw, MovementMode mode, bool sprinting, bool exhausted)
        {
            // Snapshot values come from the network: a NaN/Infinity sample would poison every
            // later Lerp and reach the Transform, so it is dropped here.
            if (!IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z) || !IsFinite(yaw)) return;

            // Sequenced delivery already drops older snapshots; this also guards duplicates.
            if (_count > 0 && tick <= _ticks[_newest]) return;
            _newest = (_newest + 1) % Capacity;
            _ticks[_newest] = tick;
            _positions[_newest] = position;
            _yaws[_newest] = yaw;
            _modes[_newest] = mode;
            _sprinting[_newest] = sprinting;
            _exhausted[_newest] = exhausted;
            if (_count < Capacity) _count++;
        }

        // 기능: 모든 샘플을 잊는다.
        // 입력: 없음.
        // 출력: 반환값 없음. 기록이 비어 다음 Push부터 새로 시작한다.
        // Forget all samples (respawn teleport): the next Push starts a new history.
        public void Clear()
        {
            _count = 0;
            _newest = -1;
        }

        // 기능: 리스폰 순간이동 전의 샘플을 버린다.
        // 입력: to - 리스폰 위치.
        // 출력: 반환값 없음. 최신부터 연속으로 to에서 TeleportKeepRadius 안인 샘플만 남고, 없으면 Clear된다. to가 유한하지 않으면 무시한다.
        // Phase 5: a respawn teleport told by PlayerRespawned (match start, round reset, dev respawn). The event is
        // ReliableOrdered and snapshots are Sequenced, on different channels, so samples from after the respawn may
        // already be here (a retransmitted event) or the dead->alive Clear in RemotePlayers.Push may already have
        // run. So only the samples from before the teleport are dropped: the newest run of samples within
        // TeleportKeepRadius of the spawn point is kept, everything older goes. Nothing kept = Clear (the same
        // reset as dead->alive). Calling it again keeps the same samples (idempotent). No allocation.
        public void Teleport(Vector3 to)
        {
            if (!IsFinite(to.x) || !IsFinite(to.y) || !IsFinite(to.z)) return;

            int kept = 0;
            for (int i = 0; i < _count; i++)
            {
                int index = (_newest - i + Capacity) % Capacity;
                if ((_positions[index] - to).sqrMagnitude > TeleportKeepRadius * TeleportKeepRadius) break;
                kept++;
            }
            if (kept == 0) Clear();
            else _count = kept;   // the ring keeps _newest; the dropped oldest slots are reused by Push
        }

        // 기능: 렌더 Tick에서 보간한 위치와 방향만 구한다.
        // 입력: renderTick - 화면에 그릴 서버 Tick(소수, ServerClock.RenderTick).
        // 출력: 샘플이 있으면 true와 위치·방향, 없으면 false.
        public bool TrySample(double renderTick, out Vector3 position, out float yaw) =>
            TrySample(renderTick, out position, out yaw, out _, out _, out _);

        // 기능: 렌더 Tick 앞뒤 두 샘플 사이를 보간해 위치·방향을 구하고 이동 상태를 고른다.
        // 입력: renderTick - 화면에 그릴 서버 Tick(소수, ServerClock.RenderTick).
        // 출력: 샘플이 있으면 true와 위치·방향·모드·달리기·탈진(최신 샘플 이후면 최신 값 유지, 가장 오래된 샘플 이전이면 그 값), 없으면 false.
        // mode, sprinting, exhausted: those of the newest sample at or before renderTick (between two samples, the older
        // one's), the same rule as the server's PositionHistory.Sample; before the oldest sample, the oldest's.
        public bool TrySample(double renderTick, out Vector3 position, out float yaw, out MovementMode mode, out bool sprinting,
            out bool exhausted)
        {
            position = default;
            yaw = 0f;
            mode = MovementMode.Ground;
            sprinting = false;
            exhausted = false;
            if (_count == 0) return false;

            for (int i = 0; i < _count; i++)
            {
                int index = (_newest - i + Capacity) % Capacity;
                if (_ticks[index] > renderTick) continue;

                mode = _modes[index];
                sprinting = _sprinting[index];
                exhausted = _exhausted[index];
                if (i == 0)
                {
                    position = _positions[index];
                    yaw = _yaws[index];
                    return true;
                }

                int next = (index + 1) % Capacity;
                float t = (float)((renderTick - _ticks[index]) / (_ticks[next] - _ticks[index]));
                position = Vector3.Lerp(_positions[index], _positions[next], t);
                yaw = Mathf.LerpAngle(_yaws[index], _yaws[next], t);
                return true;
            }

            int oldest = (_newest - _count + 1 + Capacity) % Capacity;
            position = _positions[oldest];
            yaw = _yaws[oldest];
            mode = _modes[oldest];
            sprinting = _sprinting[oldest];
            exhausted = _exhausted[oldest];
            return true;
        }

        // 기능: 값이 NaN·Infinity가 아닌지 확인한다.
        // 입력: value - 검사할 값.
        // 출력: 유한하면 true, 아니면 false.
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
