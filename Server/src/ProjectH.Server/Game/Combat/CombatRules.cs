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
    // Phase 14 D4: a downed (DBNO) character's eye, where a revive's line of sight starts (it cannot shoot). Must equal the
    // client's AimSolver copy.
    public const float DownedEyeHeight = 0.6f;
    public const float MaxPitch = 89f;
    public const float RespawnSeconds = 3f;
    // D6: a shot may rewind other players by at most this much (12 ticks at 30 Hz). The client draws
    // remote players ~133 ms (2 snapshots at 15 Hz) behind the newest snapshot, which uses part of this
    // window; 400 ms keeps RTT up to ~200 ms hittable (CombatRulesTests.MaxRewind_*).
    public const float MaxRewindSeconds = 0.4f;

    // 기능: 이 모드인 플레이어의 눈(사격·시선 검사의 시작점)이 발에서 얼마나 위에 있는지 돌려준다(Phase 12 D13, Phase 14 D4).
    // 입력: mode - 이동 모드.
    // 출력: 웅크리기·슬라이드 1.0 m, 기절 0.6 m, 나머지 1.6 m.
    public static float EyeHeightOf(MovementMode mode) =>
        mode == MovementMode.Crouch || mode == MovementMode.Slide ? CrouchEyeHeight
        : mode == MovementMode.Downed ? DownedEyeHeight : EyeHeight;

    // Phase 12 D3, D10: fall damage by the landing speed (m/s). A server rule, so its numbers live here, not in Shared
    // (the simulation only reports the landing speed).
    public const float FallDamageMinSpeed = 13f;
    public const float FallDamageMaxSpeed = 30f;
    public const int FallDamageMax = 100;

    // Phase 12 D10: no damage up to FallDamageMinSpeed, FallDamageMax from FallDamageMaxSpeed on, linear between
    // (rounded half away from zero). Speeds are the landing's vertical speed in m/s; anything not a number is 0.
    public static int FallDamage(float landingSpeed)
    {
        if (!(landingSpeed > FallDamageMinSpeed)) return 0;
        if (landingSpeed >= FallDamageMaxSpeed) return FallDamageMax;
        float share = (landingSpeed - FallDamageMinSpeed) / (FallDamageMaxSpeed - FallDamageMinSpeed);
        return (int)MathF.Round(share * FallDamageMax, MidpointRounding.AwayFromZero);
    }

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

    // Phase 4 D4: a weapon's damage times its rarity multiplier, rounded half away from zero, at least 1.
    // decimal, not float: 1.15f is 1.1499999..., and 90 x 1.15 must round to 104 as written in the data.
    // Casting the float to decimal keeps its 7 significant digits (1.15).
    public static ushort ScaledDamage(ushort damage, float multiplier)
    {
        decimal scaled = Math.Round(damage * (decimal)multiplier, MidpointRounding.AwayFromZero);
        return (ushort)Math.Clamp(scaled, 1m, ushort.MaxValue);
    }

    // 기능: 감쇠·산탄 합산 뒤의 원 피해(소수)에 등급 배율을 한 번 곱한다(Phase 17 D4, D12). 정수 원 피해면 ScaledDamage(ushort)와 같다.
    // 입력: raw - 원 피해(0 이상), multiplier - 등급 배율.
    // 출력: 반올림한 피해(최소 1, ushort 상한). raw가 0 이하이거나 수가 아니면 0.
    public static ushort ScaledDamage(float raw, float multiplier)
    {
        if (!(raw > 0f) || !float.IsFinite(raw)) return 0;
        decimal scaled = Math.Round((decimal)raw * (decimal)multiplier, MidpointRounding.AwayFromZero);
        return (ushort)Math.Clamp(scaled, 1m, ushort.MaxValue);
    }

    // 기능: 거리 감쇠 비율(Phase 17 D2): start까지 1, start부터 range까지 minRatio로 선형, range 넘어서는 minRatio.
    // 입력: distance - 맞은 거리(m), start - 감쇠 시작 거리, range - 사거리, minRatio - 사거리에서의 비율(0..1).
    // 출력: 0..1 비율.
    public static float FalloffMultiplier(float distance, float start, float range, float minRatio)
    {
        if (!(distance > start) || range <= start) return 1f;
        if (distance >= range) return minRatio;
        return 1f - (1f - minRatio) * ((distance - start) / (range - start));
    }

    // 기능: 폭발 피해(Phase 17 D8): 중심 피해 × (1 − 거리/반지름) × 등급 배율, 반올림. 반지름 밖이거나 0으로 반올림되면 0.
    // 입력: centerDamage - 중심 피해, distance - 폭발점에서 대상까지 거리(m), radius - 폭발 반지름, multiplier - 등급 배율.
    // 출력: 피해(0이면 맞지 않음).
    public static ushort ExplosionDamage(ushort centerDamage, float distance, float radius, float multiplier)
    {
        if (!(radius > 0f) || !(distance < radius) || centerDamage == 0) return 0;
        float share = 1f - MathF.Max(0f, distance) / radius;
        decimal scaled = Math.Round((decimal)(centerDamage * share) * (decimal)multiplier, MidpointRounding.AwayFromZero);
        return (ushort)Math.Clamp(scaled, 0m, ushort.MaxValue);
    }

    // 기능: 폭발 구조물 피해(Phase 17 D8): 중심 구조물 피해 × (1 − 거리/반지름) × 재료 배율(반올림은 DamagePiece가 한다).
    // 입력: structureDamage - 중심 구조물 피해, distance - 조각 경계까지 거리, radius - 반지름, materialMultiplier - 재료 배율.
    // 출력: 피해(0이면 닿지 않음).
    public static float ExplosionStructureDamage(ushort structureDamage, float distance, float radius, float materialMultiplier)
    {
        if (!(radius > 0f) || !(distance < radius)) return 0f;
        return structureDamage * (1f - MathF.Max(0f, distance) / radius) * materialMultiplier;
    }

    // 기능: 점에서 상자까지의 거리(상자 안이면 0). 폭발이 플레이어 몸·조각 경계까지 재는 거리다(Phase 17 D8).
    // 입력: point - 점, min·max - 상자 모서리.
    // 출력: 거리(m).
    public static float DistanceToBox(Vector3 point, Vector3 min, Vector3 max)
    {
        Vector3 closest = Vector3.Clamp(point, min, max);
        return Vector3.Distance(point, closest);
    }

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

    // MaxRewindSeconds in ticks, cut to what PositionHistory holds (a high SimHz would otherwise reach
    // outside the ring: 0.4 s at 128 Hz is 51 ticks, the ring keeps Capacity - 1 = 31 behind the newest).
    public static int MaxRewindTicks(int simHz)
    {
        return Math.Min((int)TicksFromSeconds(MaxRewindSeconds, simHz), PositionHistory.Capacity - 1);
    }

    // Whole ticks at simHz, at least 1 (3 s at 30 Hz = 90).
    public static uint TicksFromSeconds(float seconds, int simHz)
    {
        return (uint)Math.Max(1, (int)MathF.Round(seconds * simHz, MidpointRounding.AwayFromZero));
    }
}
