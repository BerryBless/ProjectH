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

    // 기능: 자기장 계산기를 만든다(원 배열은 PhaseCount + 1개, 이후 할당 없음).
    // 입력: data - zones.json 데이터.
    // 출력: Phase 0(자기장 없음) 상태의 SafeZone. data가 null이면 ArgumentNullException.
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

    // 기능: 원의 중심 X를 돌려준다(테스트·와이어용).
    // 입력: circle - 원 번호(0 = 첫 원, p = 단계 p의 목표).
    // 출력: 중심 X.
    public float CenterX(int circle) => _centerX[circle];

    // 기능: 원의 중심 Z를 돌려준다.
    // 입력: circle - 원 번호(0 = 첫 원, p = 단계 p의 목표).
    // 출력: 중심 Z.
    public float CenterZ(int circle) => _centerZ[circle];

    // 기능: 원의 반지름을 돌려준다.
    // 입력: circle - 원 번호(0 = 첫 원, p = 단계 p의 목표).
    // 출력: 반지름.
    public float Radius(int circle) => _radius[circle];

    // 기능: "자기장 없음"으로 되돌린다(라운드 Reset, D13): 첫 원, 피해 없음, 모든 목표 원은 첫 원 중심에 데이터의 반지름.
    // 입력: 없음.
    // 출력: 반환값 없음. Phase 0, 축소 Tick 0, 원 배열이 초기화된다.
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

    // 기능: 경기 시작(D3): seed로 모든 원을 굴리고 단계 1의 대기를 startTick에서 시작한다(같은 seed = 같은 원, Random은 경기당 하나).
    // 입력: startTick - 경기 시작 Tick, seed - 자기장 Seed.
    // 출력: 반환값 없음. Phase 1이 되고 ShrinkStartTick·ShrinkEndTick이 정해진다.
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

    // 기능: 현재 축소가 끝났으면 다음 단계로 넘어간다. 다음 대기는 옛 ShrinkEndTick부터라 호출 시점과 무관하다. 마지막 단계는
    //   끝나지 않는다(반지름 0 원이 경기 끝까지 남는다).
    // 입력: now - 마지막 Tick.
    // 출력: 단계가 바뀌었으면 true(Phase·ShrinkStartTick·ShrinkEndTick 갱신), 아니면 false.
    public bool Advance(uint now)
    {
        if (Phase == 0 || IsFinalPhase || now < ShrinkEndTick) return false;
        ZonePhase next = _data.Phase(Phase);   // index Phase = phase Phase + 1
        Phase++;
        ShrinkStartTick = ShrinkEndTick + next.WaitTicks;
        ShrinkEndTick = ShrinkStartTick + next.ShrinkTicks;
        return true;
    }

    // 기능: 현재 단계의 축소를 now에 끝낸다(QA-1 setZone): 지금부터 목표 원이 되고 다음 Advance(now)가 다음 단계를 now부터 시작한다.
    // 입력: now - 마지막 Tick.
    // 출력: 반환값 없음. ShrinkEndTick = now(ShrinkStartTick도 now를 넘지 않게). Phase 0이면 그대로.
    internal void EndShrink(uint now)
    {
        if (Phase == 0) return;
        if (ShrinkStartTick > now) ShrinkStartTick = now;
        ShrinkEndTick = now;
    }

    // 기능: 어떤 Tick의 원을 구한다(이전 원에서 목표 원으로 축소 구간 안에서 선형 보간). Client의 ZoneMath.Sample과 같은 공식.
    // 입력: tick - 서버 Tick(소수 가능), centerX·centerZ·radius - 결과.
    // 출력: 반환값 없음. 그 Tick의 원 중심과 반지름이 out으로 나간다.
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

    // 기능: 위치가 그 Tick의 원 밖인지 본다(D8: 중심까지 수평 거리 > 반지름. 반지름 0인 원은 안이 없다). ZoneMath.IsOutside와 같은 규칙.
    // 입력: position - 발 위치, tick - 서버 Tick(소수 가능).
    // 출력: 밖이면 true.
    public bool IsOutside(Vector3 position, double tick)
    {
        Sample(tick, out float x, out float z, out float radius);
        float dx = position.X - x;
        float dz = position.Z - z;
        return radius <= 0f || dx * dx + dz * dz > radius * radius;
    }

    // 기능: ZoneState 패킷 값을 만든다(이전 원, 목표 원, 축소 Tick, 초당 피해).
    // 입력: 없음.
    // 출력: 현재 단계의 ZoneState.
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

    // 기능: 단계 p의 원 중심을 굴린다(D6): 이전 원 안에 완전히 들어가고(중심 거리 <= 이전 반지름 - 새 반지름) 경기장 안인 점.
    //   허용 원판에서 균등하게 뽑고 CenterTries번 실패하면 이전 중심을 쓴다. 검사는 저장된 float로 해 반올림이 규칙을 깨지 않는다.
    // 입력: rng - 경기의 Random, p - 단계(1..PhaseCount).
    // 출력: 반환값 없음. _centerX[p]·_centerZ[p]가 정해진다.
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
