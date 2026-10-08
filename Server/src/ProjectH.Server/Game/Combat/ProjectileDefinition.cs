using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Combat;

// Phase 17 D2, D6: one validated entry of weapons.json "projectiles" (a grenade or a rocket). Immutable, shared by every
// projectile of its kind. Times are already in simulation ticks.
public sealed class ProjectileDefinition
{
    // 기능: 검증이 끝난 투사체 정의를 만든다.
    // 입력: kind - 종류, speed - 발사 속도(m/s), gravity - 아래로 당기는 가속(m/s², 0 = 직선), lifetimeTicks - 폭발까지의 최대 Tick
    //   (수류탄의 퓨즈, 로켓의 수명), explosionRadius - 폭발 반지름(m), explosionDamage - 중심 피해, structureDamage - 중심 구조물 피해,
    //   bounce - 튕김 계수(0 = 맞으면 폭발), throwIntervalTicks·throwUpDegrees - 던지는 투사체(수류탄)의 간격과 올려 던지는 각도.
    // 출력: 바뀌지 않는 ProjectileDefinition.
    public ProjectileDefinition(ProjectileKind kind, float speed, float gravity, ushort lifetimeTicks, float explosionRadius,
        ushort explosionDamage, ushort structureDamage, float bounce, ushort throwIntervalTicks, float throwUpDegrees)
    {
        Kind = kind;
        Speed = speed;
        Gravity = gravity;
        LifetimeTicks = lifetimeTicks;
        ExplosionRadius = explosionRadius;
        ExplosionDamage = explosionDamage;
        StructureDamage = structureDamage;
        Bounce = bounce;
        ThrowIntervalTicks = throwIntervalTicks;
        ThrowUpDegrees = throwUpDegrees;
    }

    public ProjectileKind Kind { get; }
    public float Speed { get; }
    public float Gravity { get; }
    public ushort LifetimeTicks { get; }
    public float ExplosionRadius { get; }
    public ushort ExplosionDamage { get; }
    public ushort StructureDamage { get; }
    // D6: 0 = it explodes on the first thing it touches (a rocket); above 0 = it bounces off with this share of its speed
    // and explodes when LifetimeTicks run out (a grenade's fuse).
    public float Bounce { get; }
    public bool ExplodesOnImpact => Bounce <= 0f;
    // D9: only for a thrown projectile (the grenade); 0 for a launched one.
    public ushort ThrowIntervalTicks { get; }
    public float ThrowUpDegrees { get; }

    // 기능: Client가 외삽·표시에 쓰는 값을 와이어 값으로 바꾼다.
    // 입력: 없음.
    // 출력: ProjectileInfo.
    public ProjectileInfo ToWire() => new ProjectileInfo
    {
        Kind = Kind, Speed = Speed, Gravity = Gravity, ExplosionRadius = ExplosionRadius, LifetimeTicks = LifetimeTicks,
    };
}
