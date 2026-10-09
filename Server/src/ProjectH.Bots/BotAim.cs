using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Bots;

// Phase 7 D6: where a bot aims. Same convention as the client's AimSolver and the server's
// CombatRules.TryAimDirection: yaw 0 faces +Z, yaw 90 faces +X, positive pitch looks down.
public static class BotAim
{
    public const float EyeHeight = 1.6f;    // CombatRules.EyeHeight
    public const float ChestHeight = 1.2f;
    public const float ErrorBaseDegrees = 1f;
    public const float ErrorPerMeterDegrees = 0.04f;

    // 기능: 발 위치에서 눈 위치를 구한다(EyeHeight 위).
    // 입력: feet - 발 위치.
    // 출력: 눈 위치.
    public static Vector3 Eye(Vector3 feet) => feet + new Vector3(0f, EyeHeight, 0f);

    // 기능: 발 위치에서 서 있는 몸의 가슴 위치를 구한다(ChestHeight 위).
    // 입력: feet - 발 위치.
    // 출력: 가슴 위치.
    public static Vector3 Chest(Vector3 feet) => feet + new Vector3(0f, ChestHeight, 0f);

    // Phase 14 D4: a knocked-down body is 0.9 m tall (MovementTuning.DownedHeight); its middle is aimed at instead.
    public const float DownedChestHeight = 0.45f;

    // 기능: 이 모드인 상대의 어디를 겨눌지 돌려준다(기절이면 낮은 몸의 가운데, 아니면 가슴).
    // 입력: feet - 상대의 발, mode - 상대의 이동 모드.
    // 출력: 겨눌 점.
    public static Vector3 Chest(Vector3 feet, MovementMode mode) =>
        feet + new Vector3(0f, mode == MovementMode.Downed ? DownedChestHeight : ChestHeight, 0f);

    // 기능: 눈에서 목표를 보는 yaw·pitch를 구한다(yaw 0 = +Z, 90 = +X, pitch 양수 = 아래).
    // 입력: eye - 눈 위치, target - 겨눌 점, yaw·pitch - 결과를 받을 곳.
    // 출력: 반환값 없음. yaw는 0~360도, pitch는 도 단위로 채워진다.
    public static void Solve(Vector3 eye, Vector3 target, out float yaw, out float pitch)
    {
        Vector3 d = target - eye;
        float horizontal = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
        yaw = MathF.Atan2(d.X, d.Z) * (180f / MathF.PI);
        if (yaw < 0f) yaw += 360f;
        pitch = -MathF.Atan2(d.Y, horizontal) * (180f / MathF.PI);
    }

    // 기능: 이 거리에서 봇의 yaw 또는 pitch 조준 오차의 최대값을 구한다.
    // 입력: distance - 표적까지 거리(m).
    // 출력: 최대 오차(도).
    public static float MaxError(float distance) => ErrorBaseDegrees + ErrorPerMeterDegrees * distance;

    // 기능: [-max, max]에서 균등하게 새 조준 오차를 굴린다.
    // 입력: rng - 봇의 난수, distance - 표적까지 거리(m).
    // 출력: 오차(도).
    public static float RollError(Random rng, float distance) => ((float)rng.NextDouble() * 2f - 1f) * MaxError(distance);

    // 기능: 지면 위에서 from이 to를 바라보는 yaw를 구한다(위의 yaw 규약).
    // 입력: from - 출발점, to - 목표점.
    // 출력: yaw(0~360도).
    public static float YawTo(Vector3 from, Vector3 to)
    {
        float yaw = MathF.Atan2(to.X - from.X, to.Z - from.Z) * (180f / MathF.PI);
        return yaw < 0f ? yaw + 360f : yaw;
    }

    // 기능: 두 점의 수평(XZ) 거리를 잰다.
    // 입력: a·b - 두 점.
    // 출력: 수평 거리(m).
    public static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
