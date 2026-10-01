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

    // Forget everything and start from one known position (join, respawn): a rewind must never reach a
    // position from before a teleport.
    public void Reset(uint tick, Vector3 position, MovementMode mode = MovementMode.Ground)
    {
        _count = 0;
        _newest = -1;
        Record(tick, position, mode);
    }

    // Ticks must increase; a repeated or older tick is ignored.
    public void Record(uint tick, Vector3 position, MovementMode mode = MovementMode.Ground)
    {
        if (_count > 0 && tick <= _ticks[_newest]) return;
        _newest = (_newest + 1) % Capacity;
        _ticks[_newest] = tick;
        _positions[_newest] = position;
        _modes[_newest] = mode;
        if (_count < Capacity) _count++;
    }

    public Vector3 Sample(double tick) => Sample(tick, out _);

    // Position at a (fractional) tick, interpolated between the two records around it. Past the newest
    // record it holds the newest; before the oldest it uses the oldest (short history, spec §5).
    // mode: the mode of the record at or before the tick (between two records, the older one's).
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
