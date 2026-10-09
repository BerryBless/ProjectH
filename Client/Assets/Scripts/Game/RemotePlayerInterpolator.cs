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

        // 기능: 모드·질주·탈진 없이(Ground, false, false) 표본 하나를 넣는다(등장 위치용).
        // 입력: tick - 표본의 서버 Tick, position - 발 위치, yaw - 바라보는 각도.
        // 출력: 반환값 없음. 6인자 Push와 같은 규칙으로 표본이 들어간다.
        public void Push(uint tick, Vector3 position, float yaw) => Push(tick, position, yaw, MovementMode.Ground, false, false);

        // 기능: Snapshot 표본 하나를 링 버퍼에 넣는다. 유한하지 않은 값이나 최신 Tick 이하의 표본은 버린다.
        // 입력: tick - 표본의 서버 Tick, position - 발 위치, yaw - 바라보는 각도, mode - 이동 모드, sprinting - 질주 중인지, exhausted - 탈진했는지.
        // 출력: 반환값 없음. 받아들였으면 가장 오래된 칸을 덮어쓰고 최신 표본이 된다.
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

        // 기능: 모든 표본을 잊는다(재투입 순간이동). 다음 Push가 새 이력을 시작한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 표본 수가 0이 된다.
        public void Clear()
        {
            _count = 0;
            _newest = -1;
        }

        // 기능: 재투입 순간이동(PlayerRespawned)으로 순간이동 전 표본을 버린다. 도착점에서 TeleportKeepRadius 안에 있는 최신 표본 묶음만 남기고, 남는 것이 없으면 Clear한다.
        // 입력: to - 순간이동 도착 위치(유한하지 않으면 아무것도 하지 않는다).
        // 출력: 반환값 없음. 표본 수가 줄거나 0이 된다. 다시 불러도 같은 결과(멱등), 할당 없음.
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

        // 기능: renderTick에 그릴 위치·각도만 구한다(모드·질주·탈진은 버린다).
        // 입력: renderTick - 렌더 Tick, position - 그릴 발 위치, yaw - 그릴 각도.
        // 출력: 표본이 하나라도 있으면 true와 위치·각도, 없으면 false.
        public bool TrySample(double renderTick, out Vector3 position, out float yaw) =>
            TrySample(renderTick, out position, out yaw, out _, out _, out _);

        // 기능: renderTick을 둘러싼 두 표본 사이를 보간해 그릴 위치·각도·모드·질주·탈진을 구한다. 최신 표본보다 뒤면 최신 표본에 멈추고, 가장 오래된 표본보다 앞이면 그 표본을 쓴다.
        // 입력: renderTick - 렌더 Tick, position - 그릴 발 위치, yaw - 그릴 각도, mode - 이동 모드, sprinting - 질주 중인지, exhausted - 탈진했는지.
        // 출력: 표본이 하나라도 있으면 true와 보간 결과, 없으면 false(out은 기본값).
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

        // 기능: 값이 NaN도 무한대도 아닌지 본다.
        // 입력: value - 검사할 값.
        // 출력: 유한하면 true.
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
