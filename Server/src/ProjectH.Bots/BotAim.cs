using System.Numerics;

namespace ProjectH.Bots;

// Phase 7 D6: where a bot aims. Same convention as the client's AimSolver and the server's
// CombatRules.TryAimDirection: yaw 0 faces +Z, yaw 90 faces +X, positive pitch looks down.
public static class BotAim
{
    public const float EyeHeight = 1.6f;    // CombatRules.EyeHeight
    public const float ChestHeight = 1.2f;
    public const float ErrorBaseDegrees = 1f;
    public const float ErrorPerMeterDegrees = 0.04f;

    // 기능: 발 위치에서 눈 높이(EyeHeight)만큼 올린 시점 위치를 구한다.
    // 입력: feet - 플레이어 발 위치.
    // 출력: 사격·시야 판정에 쓰는 눈 위치.
    public static Vector3 Eye(Vector3 feet) => feet + new Vector3(0f, EyeHeight, 0f);

    // 기능: 발 위치에서 가슴 높이(ChestHeight)만큼 올린 조준점을 구한다.
    // 입력: feet - 대상 플레이어 발 위치.
    // 출력: 봇이 조준하는 대상의 가슴 위치.
    public static Vector3 Chest(Vector3 feet) => feet + new Vector3(0f, ChestHeight, 0f);

    // 기능: 눈 위치에서 목표 지점을 바라보는 yaw·pitch를 계산한다(위 yaw 규약, 양수 pitch는 아래).
    // 입력: eye - 시점 위치, target - 바라볼 지점.
    // 출력: 반환값 없음. yaw - 0 이상 360 미만 도 단위 방향, pitch - 도 단위 상하 각도(아래가 양수).
    public static void Solve(Vector3 eye, Vector3 target, out float yaw, out float pitch)
    {
        Vector3 d = target - eye;
        float horizontal = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
        yaw = MathF.Atan2(d.X, d.Z) * (180f / MathF.PI);
        if (yaw < 0f) yaw += 360f;
        pitch = -MathF.Atan2(d.Y, horizontal) * (180f / MathF.PI);
    }

    // 기능: 거리에 따라 커지는 조준 오차의 최대값을 구한다.
    // 입력: distance - 대상까지의 거리(m).
    // 출력: 이 거리에서 허용하는 yaw·pitch 최대 오차(도).
    // The largest yaw or pitch error, in degrees, at this distance.
    public static float MaxError(float distance) => ErrorBaseDegrees + ErrorPerMeterDegrees * distance;

    // 기능: 거리에 맞는 조준 오차 하나를 균등 분포로 새로 뽑는다.
    // 입력: rng - 봇 고유 난수 생성기, distance - 대상까지의 거리(m).
    // 출력: [-MaxError, MaxError] 범위의 오차(도).
    // A fresh error in [-max, max], uniform.
    public static float RollError(Random rng, float distance) => ((float)rng.NextDouble() * 2f - 1f) * MaxError(distance);

    // 기능: 수평면에서 한 위치가 다른 위치를 향하는 방향(yaw)을 구한다.
    // 입력: from - 시작 위치, to - 바라볼 위치.
    // 출력: 0 이상 360 미만의 yaw(도). 높이 차이는 무시한다.
    // Heading from a to b on the ground (yaw convention above).
    public static float YawTo(Vector3 from, Vector3 to)
    {
        float yaw = MathF.Atan2(to.X - from.X, to.Z - from.Z) * (180f / MathF.PI);
        return yaw < 0f ? yaw + 360f : yaw;
    }

    // 기능: 두 위치 사이의 수평(XZ) 거리를 구한다.
    // 입력: a - 첫 위치, b - 둘째 위치.
    // 출력: 높이를 뺀 거리(m).
    public static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
