using System;
using System.Numerics;
using ProjectH.Server.Game.Combat;

namespace ProjectH.Server.Tests;

// What a client sends: yaw and pitch (camera convention) from the eye of a player standing at feet
// towards a world point. Inverse of CombatRules.TryAimDirection.
internal static class TestAim
{
    // 기능: 서 있는 플레이어의 기본 눈높이에서 목표점을 보는 Yaw·Pitch를 구한다.
    // 입력: feet - 플레이어 발 위치, targetPoint - 바라볼 월드 좌표.
    // 출력: yaw·pitch - 카메라 규약의 각도(도).
    public static void YawPitch(Vector3 feet, Vector3 targetPoint, out float yaw, out float pitch) =>
        YawPitch(feet, targetPoint, CombatRules.EyeHeight, out yaw, out pitch);

    // 기능: 주어진 눈높이에서 목표점을 보는 Yaw·Pitch를 구한다(CombatRules.TryAimDirection의 역).
    // 입력: feet - 플레이어 발 위치, targetPoint - 바라볼 월드 좌표, eyeHeight - 발에서 눈까지 높이.
    // 출력: yaw - +Z 기준 시계 방향 각도(도), pitch - 위를 볼수록 음수인 각도(도).
    // Phase 12: from another eye height (crouched: CombatRules.CrouchEyeHeight).
    public static void YawPitch(Vector3 feet, Vector3 targetPoint, float eyeHeight, out float yaw, out float pitch)
    {
        Vector3 d = targetPoint - (feet + new Vector3(0f, eyeHeight, 0f));
        yaw = MathF.Atan2(d.X, d.Z) * 180f / MathF.PI;
        pitch = -MathF.Atan2(d.Y, MathF.Sqrt(d.X * d.X + d.Z * d.Z)) * 180f / MathF.PI;
    }
}
