using System;
using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Combat;

// Combat constants and pure rules (D7, D8, D14). Server only: the client never decides a hit.
public static class CombatRules
{
    public const int MaxHealth = 100;   // D8 test values
    // Phase 4 D11: Shield Cells fill up to 100. Players start with the loadout's shield (0 in production, D1).
    public const int MaxShield = 100;
    // Shots start at feet + 1.6 m. The client aims from the same height (AimSolver.EyeHeight).
    public const float EyeHeight = 1.6f;
    // Phase 12 D13: crouched or sliding (a 1.2 m box) the eye is at 1.0 m, so a crouched player behind cover cannot shoot
    // over it while it cannot be hit. Must equal the client's AimSolver.CrouchEyeHeight.
    public const float CrouchEyeHeight = 1.0f;
    public const float MaxPitch = 89f;
    public const float RespawnSeconds = 3f;
    // D6: a shot may rewind other players by at most this much (12 ticks at 30 Hz). The client draws
    // remote players ~133 ms (2 snapshots at 15 Hz) behind the newest snapshot, which uses part of this
    // window; 400 ms keeps RTT up to ~200 ms hittable (CombatRulesTests.MaxRewind_*).
    public const float MaxRewindSeconds = 0.4f;

    // 기능: 이동 모드에 따른 사격 시작 높이(발 기준 눈 높이)를 정한다.
    // 입력: mode - 플레이어의 이동 모드.
    // 출력: 앉기·슬라이드면 CrouchEyeHeight, 그 외에는 EyeHeight.
    // Phase 12 D13: where a shot of a player in this mode starts above its feet.
    public static float EyeHeightOf(MovementMode mode) =>
        mode == MovementMode.Crouch || mode == MovementMode.Slide ? CrouchEyeHeight : EyeHeight;

    // Phase 12 D3, D10: fall damage by the landing speed (m/s). A server rule, so its numbers live here, not in Shared
    // (the simulation only reports the landing speed).
    public const float FallDamageMinSpeed = 13f;
    public const float FallDamageMaxSpeed = 30f;
    public const int FallDamageMax = 100;

    // 기능: 착지 속도로 낙하 피해를 계산한다.
    // 입력: landingSpeed - 착지 순간의 수직 속도(m/s).
    // 출력: 낙하 피해량(0 ~ FallDamageMax). 숫자가 아니면 0.
    // Phase 12 D10: no damage up to FallDamageMinSpeed, FallDamageMax from FallDamageMaxSpeed on, linear between
    // (rounded half away from zero). Speeds are the landing's vertical speed in m/s; anything not a number is 0.
    public static int FallDamage(float landingSpeed)
    {
        if (!(landingSpeed > FallDamageMinSpeed)) return 0;
        if (landingSpeed >= FallDamageMaxSpeed) return FallDamageMax;
        float share = (landingSpeed - FallDamageMinSpeed) / (FallDamageMaxSpeed - FallDamageMinSpeed);
        return (int)MathF.Round(share * FallDamageMax, MidpointRounding.AwayFromZero);
    }

    // 기능: 피해를 실드에 먼저, 남은 만큼 체력에 적용한다.
    // 입력: health - 대상 체력(갱신됨), shield - 대상 실드(갱신됨), damage - 적용할 피해량.
    // 출력: 이번 피해로 체력이 0이 되었으면 true, 아니면(이미 0이었거나 피해가 0 이하 포함) false.
    // D8: the shield absorbs first, the rest comes off health (never below 0). Returns true when this
    // damage took health from above 0 to 0.
    public static bool ApplyDamage(ref int health, ref int shield, int damage)
    {
        if (damage <= 0 || health <= 0) return false;
        int absorbed = Math.Min(shield, damage);
        shield -= absorbed;
        health = Math.Max(0, health - (damage - absorbed));
        return health == 0;
    }

    // 기능: 무기 기본 피해에 등급 배율을 곱한 피해를 구한다.
    // 입력: damage - 무기 기본 피해, multiplier - 등급 피해 배율.
    // 출력: 반올림(0.5는 0에서 먼 쪽)한 피해량, 1 ~ 65535로 제한.
    // Phase 4 D4: a weapon's damage times its rarity multiplier, rounded half away from zero, at least 1.
    // decimal, not float: 1.15f is 1.1499999..., and 90 x 1.15 must round to 104 as written in the data.
    // Casting the float to decimal keeps its 7 significant digits (1.15).
    public static ushort ScaledDamage(ushort damage, float multiplier)
    {
        decimal scaled = Math.Round(damage * (decimal)multiplier, MidpointRounding.AwayFromZero);
        return (ushort)Math.Clamp(scaled, 1m, ushort.MaxValue);
    }

    // 기능: Client가 보낸 조준 각도를 사격 방향 단위 벡터로 바꾼다.
    // 입력: yawDegrees - 수평 조준 각도(도), pitchDegrees - 수직 조준 각도(도, 양수가 아래).
    // 출력: 각도가 유한하면 true와 조준 방향, 아니면 false.
    // D14: non-finite angles are no shot. Pitch is clamped to +-89 degrees. Same convention as the
    // client camera (ShoulderCameraMath.Forward): yaw 0 faces +Z, yaw 90 faces +X, positive pitch looks down.
    public static bool TryAimDirection(float yawDegrees, float pitchDegrees, out Vector3 direction)
    {
        direction = default;
        if (!float.IsFinite(yawDegrees) || !float.IsFinite(pitchDegrees)) return false;

        // % keeps huge finite yaws in a range where sin/cos stay accurate.
        float yaw = (yawDegrees % 360f) * (MathF.PI / 180f);
        float pitch = Math.Clamp(pitchDegrees, -MaxPitch, MaxPitch) * (MathF.PI / 180f);
        float cosPitch = MathF.Cos(pitch);
        direction = new Vector3(MathF.Sin(yaw) * cosPitch, -MathF.Sin(pitch), MathF.Cos(yaw) * cosPitch);
        return true;
    }

    // 기능: Client가 보낸 ViewTick을 허용 되감기 범위로 제한한다.
    // 입력: viewTick - Client가 본 Tick(신뢰하지 않음), latestTick - 최신 서버 Tick, maxRewindTicks - 최대 되감기 Tick 수.
    // 출력: 대상 위치를 되감을 Tick. NaN이면 latestTick.
    // D14: the tick a shot rewinds targets to. The client's ViewTick is untrusted: NaN means "now", and
    // anything outside [latestTick - maxRewindTicks, latestTick] is clamped into it (never below tick 0).
    public static double ClampViewTick(float viewTick, uint latestTick, int maxRewindTicks)
    {
        double latest = latestTick;
        if (float.IsNaN(viewTick)) return latest;
        double oldest = latestTick > (uint)maxRewindTicks ? latestTick - (uint)maxRewindTicks : 0u;
        if (viewTick > latest) return latest;
        if (viewTick < oldest) return oldest;
        return viewTick;
    }

    // 기능: 최대 되감기 시간을 Tick 수로 바꾼다.
    // 입력: simHz - 시뮬레이션 Tick 속도.
    // 출력: 최대 되감기 Tick 수(PositionHistory.Capacity - 1 이하).
    // MaxRewindSeconds in ticks, cut to what PositionHistory holds (a high SimHz would otherwise reach
    // outside the ring: 0.4 s at 128 Hz is 51 ticks, the ring keeps Capacity - 1 = 31 behind the newest).
    public static int MaxRewindTicks(int simHz)
    {
        return Math.Min((int)TicksFromSeconds(MaxRewindSeconds, simHz), PositionHistory.Capacity - 1);
    }

    // 기능: 초 단위 시간을 Tick 수로 바꾼다.
    // 입력: seconds - 시간(초), simHz - 시뮬레이션 Tick 속도.
    // 출력: 반올림한 Tick 수, 최소 1.
    // Whole ticks at simHz, at least 1 (3 s at 30 Hz = 90).
    public static uint TicksFromSeconds(float seconds, int simHz)
    {
        return (uint)Math.Max(1, (int)MathF.Round(seconds * simHz, MidpointRounding.AwayFromZero));
    }
}
