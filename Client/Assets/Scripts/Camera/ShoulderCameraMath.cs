using UnityEngine;

namespace ProjectH.Client.CameraControl
{
    // Casts a sphere of the camera's collision radius. ShoulderCamera implements it with
    // Physics.SphereCast; tests use a fake, so the two-stage solve runs without Unity physics.
    public interface ISphereCaster
    {
        bool Cast(Vector3 origin, Vector3 direction, float maxDistance, out float hitDistance);
    }

    public struct ShoulderPose
    {
        public Vector3 Shoulder;     // shoulder point after collision; the aim ray starts here
        public Vector3 Position;     // camera position
        public Vector3 Forward;      // camera forward (screen centre)
        public float Distance;       // camera distance behind the shoulder after collision
        public float FieldOfView;
    }

    // Pure over-the-shoulder camera math (D8, D10). No Physics and no Quaternion.Euler/LookRotation:
    // those are native engine calls, and EditMode tests of this class also run outside Unity.
    // All tuning values live here.
    public static class ShoulderCameraMath
    {
        public const float PivotHeight = 1.6f;
        public const float HipDistance = 3.5f;
        public const float HipShoulder = 0.55f;
        public const float HipFov = 60f;
        public const float AimDistance = 1.6f;
        public const float AimShoulder = 0.65f;
        public const float AimFov = 42f;
        public const float AimSensitivityScale = 0.6f;
        public const float ReturnSharpness = 6f;   // how fast the camera moves back out after a wall
        // Stage 2 must not start touching the wall stage 1 hit: SphereCast ignores colliders the sphere
        // starts inside, so float error at exact contact would let the camera sweep through that wall.
        public const float ShoulderClearance = 0.02f;

        // Same convention as Quaternion.Euler(pitch, yaw, 0) * Vector3.forward: positive pitch looks down.
        public static Vector3 Forward(float yawDegrees, float pitchDegrees)
        {
            float yaw = yawDegrees * Mathf.Deg2Rad;
            float pitch = pitchDegrees * Mathf.Deg2Rad;
            float cosPitch = Mathf.Cos(pitch);
            return new Vector3(Mathf.Sin(yaw) * cosPitch, -Mathf.Sin(pitch), Mathf.Cos(yaw) * cosPitch);
        }

        // Horizontal right of the yaw heading (matches MovementSimulation's right vector).
        public static Vector3 Right(float yawDegrees)
        {
            float yaw = yawDegrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));
        }

        // Frame-rate independent exponential approach.
        public static float Approach(float current, float target, float sharpness, float deltaTime)
        {
            return current + (target - current) * (1f - Mathf.Exp(-sharpness * deltaTime));
        }

        // A wall closer than the current distance pulls the camera in at once (never show the wall's
        // inside); when the way clears, the camera eases back out.
        public static float ResolveDistance(float current, float allowed, float deltaTime)
        {
            return allowed < current ? allowed : Approach(current, allowed, ReturnSharpness, deltaTime);
        }

        public static ShoulderPose Solve(Vector3 feet, float yaw, float pitch, float aimBlend, float currentDistance,
            float deltaTime, ISphereCaster caster)
        {
            float t = Mathf.Clamp01(aimBlend);
            float targetDistance = Mathf.Lerp(HipDistance, AimDistance, t);
            float shoulderOffset = Mathf.Lerp(HipShoulder, AimShoulder, t);
            Vector3 forward = Forward(yaw, pitch);
            Vector3 right = Right(yaw);
            Vector3 pivot = feet + new Vector3(0f, PivotHeight, 0f);

            // Stage 1: pivot -> shoulder. Against a wall on the right the shoulder point itself would be
            // inside the wall, and a cast starting there would not see it (D10).
            float reach = caster.Cast(pivot, right, shoulderOffset, out float hit)
                ? Mathf.Max(0f, hit - ShoulderClearance)
                : shoulderOffset;
            Vector3 shoulder = pivot + right * reach;

            // Stage 2: (possibly shortened) shoulder -> wanted camera position.
            float allowed = caster.Cast(shoulder, -forward, targetDistance, out hit) ? hit : targetDistance;
            float distance = ResolveDistance(currentDistance, allowed, deltaTime);

            return new ShoulderPose
            {
                Shoulder = shoulder,
                Position = shoulder - forward * distance,
                Forward = forward,
                Distance = distance,
                FieldOfView = Mathf.Lerp(HipFov, AimFov, t),
            };
        }
    }
}
