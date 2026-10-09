using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Combat;

// Fixed ring of one player's recent feet positions for lag compensation (D6, request §19). Never grows:
// Record overwrites the oldest of Capacity entries. Game loop thread only.
// Phase 12 D13: each record also keeps the movement mode, which sets the hit box height (and a rider is not hit).
public sealed class PositionHistory
{
    // About 1 s at 30 Hz. Must stay above the rewind limit (12 ticks at 30 Hz); CombatRules.MaxRewindTicks
    // cuts the limit to Capacity - 1 at a high SimHz (0.4 s at 128 Hz would be 51 ticks).
    public const int Capacity = 32;

    private readonly uint[] _ticks = new uint[Capacity];
    private readonly Vector3[] _positions = new Vector3[Capacity];
    private readonly MovementMode[] _modes = new MovementMode[Capacity];
    private int _count;
    private int _newest = -1;

    public int Count => _count;

    // 기능: 기록을 모두 잊고 알려진 위치 하나에서 다시 시작한다(참가·부활: 되감기가 순간이동 전 위치에 닿으면 안 된다).
    // 입력: tick - 기록할 Tick, position - 발 위치, mode - 이동 모드.
    // 출력: 반환값 없음. 기록이 그 하나만 남는다.
    public void Reset(uint tick, Vector3 position, MovementMode mode = MovementMode.Ground)
    {
        _count = 0;
        _newest = -1;
        Record(tick, position, mode);
    }

    // 기능: 한 Tick의 발 위치와 이동 모드를 기록한다(가장 오래된 기록을 덮어쓴다).
    // 입력: tick - 기록할 Tick(마지막 기록보다 커야 한다), position - 발 위치, mode - 이동 모드.
    // 출력: 반환값 없음. 같거나 오래된 Tick은 무시된다.
    public void Record(uint tick, Vector3 position, MovementMode mode = MovementMode.Ground)
    {
        if (_count > 0 && tick <= _ticks[_newest]) return;
        _newest = (_newest + 1) % Capacity;
        _ticks[_newest] = tick;
        _positions[_newest] = position;
        _modes[_newest] = mode;
        if (_count < Capacity) _count++;
    }

    // 기능: 어떤 Tick의 발 위치를 구한다(이동 모드는 받지 않는 판).
    // 입력: tick - 되감을 Tick(소수 가능).
    // 출력: 보간된 발 위치.
    public Vector3 Sample(double tick) => Sample(tick, out _);

    // 기능: 어떤 Tick의 발 위치를 앞뒤 기록 사이에서 보간해 구한다. 최신보다 뒤면 최신, 가장 오래된 기록보다 앞이면 그 기록(spec §5).
    // 입력: tick - 되감을 Tick(소수 가능), mode - 결과(그 Tick 또는 바로 앞 기록의 이동 모드).
    // 출력: 보간된 발 위치. 기록이 없으면 Zero(참가 시 Reset하므로 닿지 않는다).
    public Vector3 Sample(double tick, out MovementMode mode)
    {
        mode = MovementMode.Ground;
        if (_count == 0) return Vector3.Zero;   // not reached: Match resets the history at join

        for (int i = 0; i < _count; i++)
        {
            int index = (_newest - i + Capacity) % Capacity;
            if (_ticks[index] > tick) continue;
            mode = _modes[index];
            if (i == 0) return _positions[index];

            int next = (index + 1) % Capacity;
            float t = (float)((tick - _ticks[index]) / (_ticks[next] - _ticks[index]));
            return Vector3.Lerp(_positions[index], _positions[next], t);
        }

        int oldest = (_newest - _count + 1 + Capacity) % Capacity;
        mode = _modes[oldest];
        return _positions[oldest];
    }
}
