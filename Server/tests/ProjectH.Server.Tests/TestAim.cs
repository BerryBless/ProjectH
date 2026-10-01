using System;
using System.Numerics;
using ProjectH.Server.Game.Combat;

namespace ProjectH.Server.Tests;

// What a client sends: yaw and pitch (camera convention) from the eye of a player standing at feet
// towards a world point. Inverse of CombatRules.TryAimDirection.
internal static class TestAim
{
    public static void YawPitch(Vector3 feet, Vector3 targetPoint, out float yaw, out float pitch) =>
        YawPitch(feet, targetPoint, CombatRules.EyeHeight, out yaw, out pitch);

    // Phase 12: from another eye height (crouched: CombatRules.CrouchEyeHeight).
    public static void YawPitch(Vector3 feet, Vector3 targetPoint, float eyeHeight, out float yaw, out float pitch)
    {
        Vector3 d = targetPoint - (feet + new Vector3(0f, eyeHeight, 0f));
        yaw = MathF.Atan2(d.X, d.Z) * 180f / MathF.PI;
        pitch = -MathF.Atan2(d.Y, MathF.Sqrt(d.X * d.X + d.Z * d.Z)) * 180f / MathF.PI;
    }
}
