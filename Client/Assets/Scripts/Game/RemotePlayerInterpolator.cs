using UnityEngine;

namespace ProjectH.Client.Game
{
    // Fixed 8-sample history per remote player (no growth). Renders between the two samples around
    // the render tick; past the newest sample it holds position (no extrapolation in this phase).
    public sealed class RemotePlayerInterpolator
    {
        private const int Capacity = 8;

        private readonly uint[] _ticks = new uint[Capacity];
        private readonly Vector3[] _positions = new Vector3[Capacity];
        private readonly float[] _yaws = new float[Capacity];
        private int _count;
        private int _newest = -1;

        public void Push(uint tick, Vector3 position, float yaw)
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
            if (_count < Capacity) _count++;
        }

        // Forget all samples (respawn teleport): the next Push starts a new history.
        public void Clear()
        {
            _count = 0;
            _newest = -1;
        }

        public bool TrySample(double renderTick, out Vector3 position, out float yaw)
        {
            position = default;
            yaw = 0f;
            if (_count == 0) return false;

            for (int i = 0; i < _count; i++)
            {
                int index = (_newest - i + Capacity) % Capacity;
                if (_ticks[index] > renderTick) continue;

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
            return true;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
