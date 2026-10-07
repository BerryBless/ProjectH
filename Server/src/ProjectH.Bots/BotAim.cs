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

    public static Vector3 Eye(Vector3 feet) => feet + new Vector3(0f, EyeHeight, 0f);

    public static Vector3 Chest(Vector3 feet) => feet + new Vector3(0f, ChestHeight, 0f);

    // Phase 14 D4: a knocked-down body is 0.9 m tall (MovementTuning.DownedHeight); its middle is aimed at instead.
    public const float DownedChestHeight = 0.45f;

    // 기능: 이 모드인 상대의 어디를 겨눌지 돌려준다(기절이면 낮은 몸의 가운데, 아니면 가슴).
    // 입력: feet - 상대의 발, mode - 상대의 이동 모드.
    // 출력: 겨눌 점.
    public static Vector3 Chest(Vector3 feet, MovementMode mode) =>
        feet + new Vector3(0f, mode == MovementMode.Downed ? DownedChestHeight : ChestHeight, 0f);

    public static void Solve(Vector3 eye, Vector3 target, out float yaw, out float pitch)
    {
        Vector3 d = target - eye;
        float horizontal = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
        yaw = MathF.Atan2(d.X, d.Z) * (180f / MathF.PI);
        if (yaw < 0f) yaw += 360f;
        pitch = -MathF.Atan2(d.Y, horizontal) * (180f / MathF.PI);
    }

    // The largest yaw or pitch error, in degrees, at this distance.
    public static float MaxError(float distance) => ErrorBaseDegrees + ErrorPerMeterDegrees * distance;

    // A fresh error in [-max, max], uniform.
    public static float RollError(Random rng, float distance) => ((float)rng.NextDouble() * 2f - 1f) * MaxError(distance);

    // Heading from a to b on the ground (yaw convention above).
    public static float YawTo(Vector3 from, Vector3 to)
    {
        float yaw = MathF.Atan2(to.X - from.X, to.Z - from.Z) * (180f / MathF.PI);
        return yaw < 0f ? yaw + 360f : yaw;
    }

    public static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
