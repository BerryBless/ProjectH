using System;
using System.Numerics;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Zone;

// The safe zone of one match (request §26-28, D6-D8). Pure calculation, owned by Match on the game loop thread.
// Start rolls every circle of the match from a seed (one Random per match); after that nothing allocates:
// Advance, Sample and IsOutside only read the fixed arrays.
//
// Circle 0 is the first circle (zones.json initialCenter / initialRadius); circle p is phase p's target. During
// phase p the circle is circle p-1 until ShrinkStartTick, moves linearly to circle p until ShrinkEndTick, then
// stays circle p until the next phase starts at that ShrinkEndTick. Phase 0 = no zone (before the match).
//
// The client draws the same circle from ZoneState with ZoneMath.Sample (Client/Assets/Scripts/Game/ZoneMath.cs).
// Sample below and ZoneMath.Sample must stay the same formula; ZoneMathParityTests compares them on the same inputs.
public sealed class SafeZone
{
    // Rejection sampling for a center that is both inside the current circle and inside the arena bound. After
    // this many misses the previous center is kept, which satisfies both (the first center is validated to lie
    // inside the arena and every later one is accepted only when it does).
    private const int CenterTries = 16;

    private readonly ZoneData _data;
    private readonly float[] _centerX;
    private readonly float[] _centerZ;
    private readonly float[] _radius;

    public SafeZone(ZoneData data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _centerX = new float[data.PhaseCount + 1];
        _centerZ = new float[data.PhaseCount + 1];
        _radius = new float[data.PhaseCount + 1];
        Reset();
    }

    // 0 = no zone; 1..PhaseCount while a match runs.
    public int Phase { get; private set; }
    public uint ShrinkStartTick { get; private set; }
    public uint ShrinkEndTick { get; private set; }
    public int PhaseCount => _data.PhaseCount;
    public bool IsFinalPhase => Phase == _data.PhaseCount;
    public ushort DamagePerSecond => Phase == 0 ? (ushort)0 : _data.Phase(Phase - 1).DamagePerSecond;

    // Circle p's center and radius (0 = the first circle). For tests and the wire.
    public float CenterX(int circle) => _centerX[circle];
    public float CenterZ(int circle) => _centerZ[circle];
    public float Radius(int circle) => _radius[circle];

    // Back to "no zone": the first circle, no damage (round reset, D13).
    public void Reset()
    {
        Phase = 0;
        ShrinkStartTick = 0;
        ShrinkEndTick = 0;
        _centerX[0] = _data.InitialCenter.X;
        _centerZ[0] = _data.InitialCenter.Y;
        _radius[0] = _data.InitialRadius;
        for (int p = 1; p < _radius.Length; p++)
        {
            _centerX[p] = _centerX[0];
            _centerZ[p] = _centerZ[0];
            _radius[p] = _data.Phase(p - 1).TargetRadius;
        }
    }

    // Match start (D3): rolls every circle from seed and starts phase 1's wait at startTick. Same seed = same
    // circles. One Random per match, never per tick.
    public void Start(uint startTick, int seed)
    {
        Reset();
        var rng = new Random(seed);
        for (int p = 1; p < _radius.Length; p++) PickCenter(rng, p);

        ZonePhase first = _data.Phase(0);
        Phase = 1;
        ShrinkStartTick = startTick + first.WaitTicks;
        ShrinkEndTick = ShrinkStartTick + first.ShrinkTicks;
    }

    // Moves to the next phase once the current shrink is over. The next wait starts at the old ShrinkEndTick,
    // so the schedule does not depend on when this is called. Returns true when the phase changed. The last
    // phase never ends: its circle stays (radius 0) until the match is over.
    public bool Advance(uint now)
    {
        if (Phase == 0 || IsFinalPhase || now < ShrinkEndTick) return false;
        ZonePhase next = _data.Phase(Phase);   // index Phase = phase Phase + 1
        Phase++;
        ShrinkStartTick = ShrinkEndTick + next.WaitTicks;
        ShrinkEndTick = ShrinkStartTick + next.ShrinkTicks;
        return true;
    }

    // The circle at a (fractional) server tick. Keep in step with the client's ZoneMath.Sample.
    public void Sample(double tick, out float centerX, out float centerZ, out float radius)
    {
        int to = Phase;
        int from = Phase == 0 ? 0 : Phase - 1;
        float t;
        if (tick <= ShrinkStartTick) t = 0f;
        else if (tick >= ShrinkEndTick) t = 1f;
        else t = (float)((tick - ShrinkStartTick) / ((double)ShrinkEndTick - ShrinkStartTick));
        centerX = _centerX[from] + (_centerX[to] - _centerX[from]) * t;
        centerZ = _centerZ[from] + (_centerZ[to] - _centerZ[from]) * t;
        radius = _radius[from] + (_radius[to] - _radius[from]) * t;
    }

    // D8: outside = horizontal distance from the center above the radius. A circle of radius 0 has no inside,
    // so standing exactly on the final center cannot survive the last phase. Keep in step with ZoneMath.IsOutside.
    public bool IsOutside(Vector3 position, double tick)
    {
        Sample(tick, out float x, out float z, out float radius);
        float dx = position.X - x;
        float dz = position.Z - z;
        return radius <= 0f || dx * dx + dz * dz > radius * radius;
    }

    public ZoneState ToWire()
    {
        int to = Phase;
        int from = Phase == 0 ? 0 : Phase - 1;
        return new ZoneState
        {
            Phase = (byte)Phase,
            FromX = _centerX[from],
            FromZ = _centerZ[from],
            FromRadius = _radius[from],
            ToX = _centerX[to],
            ToZ = _centerZ[to],
            ToRadius = _radius[to],
            ShrinkStartTick = ShrinkStartTick,
            ShrinkEndTick = ShrinkEndTick,
            DamagePerSecond = DamagePerSecond,
        };
    }

    // D6: the new circle lies completely inside the previous one (center distance <= previous radius - new
    // radius) and its center stays within the arena bound. A uniform point in the allowed disc, retried when it
    // falls outside the arena; the checks run on the stored floats, so rounding cannot break the rule.
    private void PickCenter(Random rng, int p)
    {
        float px = _centerX[p - 1];
        float pz = _centerZ[p - 1];
        float maxOffset = _radius[p - 1] - _radius[p];
        float half = _data.ArenaHalfSize;
        for (int i = 0; i < CenterTries; i++)
        {
            double angle = rng.NextDouble() * 2.0 * Math.PI;
            double distance = Math.Sqrt(rng.NextDouble()) * maxOffset;
            float x = (float)(px + Math.Cos(angle) * distance);
            float z = (float)(pz + Math.Sin(angle) * distance);
            float dx = x - px;
            float dz = z - pz;
            if (MathF.Abs(x) <= half && MathF.Abs(z) <= half && MathF.Sqrt(dx * dx + dz * dz) + _radius[p] <= _radius[p - 1])
            {
                _centerX[p] = x;
                _centerZ[p] = z;
                return;
            }
        }
        _centerX[p] = px;
        _centerZ[p] = pz;
    }
}
