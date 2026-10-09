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

    // 기능: 착지 속도로 낙하 피해를 정한다(Phase 12 D10): FallDamageMinSpeed까지 0, FallDamageMaxSpeed부터 FallDamageMax, 사이는 선형(반올림).
    // 입력: landingSpeed - 착지 때 수직 속도(m/s, NaN이면 0으로 본다).
    // 출력: 낙하 피해(0..FallDamageMax).
    public static int FallDamage(float landingSpeed)
    {
        if (!(landingSpeed > FallDamageMinSpeed)) return 0;
        if (landingSpeed >= FallDamageMaxSpeed) return FallDamageMax;
        float share = (landingSpeed - FallDamageMinSpeed) / (FallDamageMaxSpeed - FallDamageMinSpeed);
        return (int)MathF.Round(share * FallDamageMax, MidpointRounding.AwayFromZero);
    }

    // 기능: 피해를 보호막이 먼저 흡수하고 나머지를 체력에서 뺀다(D8, 0 아래로는 가지 않는다).
    // 입력: health - 체력(갱신된다), shield - 보호막(갱신된다), damage - 적용할 피해.
    // 출력: 이 피해로 체력이 0보다 크다가 0이 되었으면 true(피해가 0 이하이거나 이미 죽었으면 false, 아무것도 바뀌지 않는다).
    public static bool ApplyDamage(ref int health, ref int shield, int damage)
    {
        if (damage <= 0 || health <= 0) return false;
        int absorbed = Math.Min(shield, damage);
        shield -= absorbed;
        health = Math.Max(0, health - (damage - absorbed));
        return health == 0;
    }

    // 기능: 무기 피해에 등급 배율을 곱해 반올림한다(Phase 4 D4). decimal로 계산해 90 x 1.15가 데이터대로 104가 되게 한다.
    // 입력: damage - 무기 기본 피해, multiplier - 등급 배율.
    // 출력: 반올림한 피해(최소 1, ushort 상한).
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

    // 기능: 조준 각도를 단위 방향 벡터로 바꾼다(D14). Client Camera(ShoulderCameraMath.Forward)와 같은 규약: yaw 0 = +Z, yaw 90 = +X,
    //   양의 pitch = 아래. pitch는 ±89도로 자른다.
    // 입력: yawDegrees - 수평 각도, pitchDegrees - 수직 각도, direction - 결과.
    // 출력: 두 각도가 유한하면 true와 단위 방향, 아니면 false(사격 없음).
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

    // 기능: 사격이 대상을 되감을 Tick을 정한다(D14, 잘렸는지는 돌려주지 않는 판).
    // 입력: viewTick - Client가 본 Tick(uint.MaxValue = "지금"), latestTick - 마지막으로 끝난 Tick, maxRewindTicks - 되감을 수 있는 최대 Tick.
    // 출력: [latestTick - maxRewindTicks, latestTick] 안으로 자른 되감기 Tick(Tick 0 아래로는 가지 않는다).
    public static double ClampViewTick(uint viewTick, uint latestTick, int maxRewindTicks) =>
        ClampViewTick(viewTick, latestTick, maxRewindTicks, out _);

    // 기능: 사격이 대상을 되감을 Tick을 정한다(D14). Client의 ViewTick은 믿지 않는다: [latestTick - allowedTicks, latestTick] 밖이면 그 안으로
    //   자른다(Tick 0 아래로는 가지 않는다). 리뷰 수정 C3: 허용 폭은 사수의 RTT로 정한 값(AllowedRewindTicks)이다. 리뷰 수정 D2(STB-1): ViewTick은
    //   uint Tick이고 uint.MaxValue("지금", Client가 아직 그린 것이 없을 때)는 다른 미래 값처럼 latestTick이 된다.
    // 입력: viewTick - Client가 본 Tick, latestTick - 마지막으로 끝난 Tick, allowedTicks - 되감을 수 있는 최대 Tick, clamped - 잘렸는지 받을 곳.
    // 출력: 되감을 Tick. clamped는 허용보다 오래된 주장을 잘랐을 때만 true(미래·"지금"은 잘림이 아니다).
    public static double ClampViewTick(uint viewTick, uint latestTick, int allowedTicks, out bool clamped)
    {
        clamped = false;
        if (viewTick >= latestTick) return latestTick;
        uint oldest = latestTick > (uint)allowedTicks ? latestTick - (uint)allowedTicks : 0u;
        if (viewTick < oldest)
        {
            clamped = true;
            return oldest;
        }
        return viewTick;
    }

    // 기능: 사수 한 명에게 허용하는 되감기 Tick을 정한다(리뷰 수정 C3, SEC-5). ViewTick은 서버보다 보간(Snapshot 간격 2개
    //   = 2 × SnapshotEveryTicks), Snapshot이 Client로 가는 길과 입력이 돌아오는 길(합쳐서 RTT 하나 = LiteNetLib RoundTripTime),
    //   입력 버퍼 Drain(1)만큼 뒤처지므로 RTT × SimHz / 1000 + 2 × SnapshotEveryTicks + 2(Drain 1 + 흔들림 1)를 허용한다.
    //   30 Hz·Snapshot 2 Tick마다: RTT 20 ms = 6, 200 ms = 12(예전 고정 상한). 지연이 작은 사수는 더 좁게 되감는다.
    // 입력: rttMs - 사수 연결의 RTT(ms, 음수 = 0), simHz - Tick 속도, snapshotEveryTicks - Snapshot 간격, maxRewindTicks - 상한(MaxRewindTicks).
    // 출력: 2 이상 maxRewindTicks 이하의 Tick 수(maxRewindTicks가 2보다 작으면 그 값).
    public static int AllowedRewindTicks(int rttMs, int simHz, int snapshotEveryTicks, int maxRewindTicks)
    {
        long rttTicks = (long)Math.Max(0, rttMs) * simHz / 1000;
        long allowed = rttTicks + 2L * Math.Max(0, snapshotEveryTicks) + 2;
        return (int)Math.Clamp(allowed, Math.Min(2, maxRewindTicks), maxRewindTicks);
    }

    // 기능: MaxRewindSeconds를 Tick으로 바꾸되 PositionHistory가 보관하는 범위(Capacity - 1)로 자른다(128 Hz면 51 → 31).
    // 입력: simHz - Tick 속도.
    // 출력: 되감기 상한 Tick 수.
    public static int MaxRewindTicks(int simHz)
    {
        return Math.Min((int)TicksFromSeconds(MaxRewindSeconds, simHz), PositionHistory.Capacity - 1);
    }

    // 기능: 초를 simHz 기준 Tick 수로 반올림한다(3 s at 30 Hz = 90).
    // 입력: seconds - 초, simHz - Tick 속도.
    // 출력: 최소 1인 Tick 수.
    public static uint TicksFromSeconds(float seconds, int simHz)
    {
        return (uint)Math.Max(1, (int)MathF.Round(seconds * simHz, MidpointRounding.AwayFromZero));
    }
}
