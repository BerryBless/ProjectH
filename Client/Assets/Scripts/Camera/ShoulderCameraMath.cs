using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.CameraControl
{
    // Casts a sphere of the camera's collision radius. ShoulderCamera implements it with
    // Physics.SphereCast; tests use a fake, so the two-stage solve runs without Unity physics.
    public interface ISphereCaster
    {
        // 기능: Camera 충돌 반경의 구를 한 방향으로 쏜다.
        // 입력: origin - 시작점, direction - 방향, maxDistance - 최대 거리, hitDistance - 충돌까지의 거리.
        // 출력: 충돌하면 true와 hitDistance, 없으면 false.
        bool Cast(Vector3 origin, Vector3 direction, float maxDistance, out float hitDistance);
    }

    // Phase 12 D14: the hip (not aiming) camera a movement mode asks for. ShoulderCamera eases its current values towards
    // these, so a mode change never jumps.
    public struct CameraTargets
    {
        public float PivotHeight;
        public float Distance;
        public float FieldOfView;
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
        // Phase 12 D14: the mode targets, and how fast the camera eases to them (no shake anywhere).
        public const float SprintFov = 66f;
        public const float CrouchPivotHeight = 1.1f;
        public const float AirDistance = 6f;
        public const float AirFov = 70f;
        public const float TransportDistance = 12f;
        public const float ModeSharpness = 5f;

        // 기능: 기본(지상, 조준 아님) 허리 Camera 목표값을 만든다.
        // 입력: 없음.
        // 출력: PivotHeight, HipDistance, HipFov로 채운 CameraTargets.
        public static CameraTargets Hip => new CameraTargets { PivotHeight = PivotHeight, Distance = HipDistance, FieldOfView = HipFov };

        // 기능: 이동 모드와 질주 여부에 맞는 허리 Camera 목표값을 고른다.
        // 입력: mode - 따라가는 캐릭터의 이동 모드, sprinting - 질주 중 여부(지상 계열 모드에서만 반영).
        // 출력: 모드에 맞게 Pivot 높이·거리·FOV를 바꾼 CameraTargets.
        // D14: sprinting widens the view; crouched or sliding the pivot is lower; falling and gliding the camera backs off
        // with a wider view; aboard it follows the transport from further away.
        public static CameraTargets TargetsFor(MovementMode mode, bool sprinting)
        {
            CameraTargets targets = Hip;
            switch (mode)
            {
                case MovementMode.Crouch:
                case MovementMode.Slide:
                    targets.PivotHeight = CrouchPivotHeight;
                    break;
                case MovementMode.Freefall:
                case MovementMode.Glide:
                    targets.Distance = AirDistance;
                    targets.FieldOfView = AirFov;
                    break;
                case MovementMode.Transport:
                    targets.Distance = TransportDistance;
                    targets.FieldOfView = AirFov;
                    break;
                default:
                    if (sprinting) targets.FieldOfView = SprintFov;
                    break;
            }
            return targets;
        }

        // 기능: 현재 Camera 목표값의 각 항목을 목표값 쪽으로 ModeSharpness로 보간한다.
        // 입력: current - 현재 값, target - 목표 값, deltaTime - Frame 경과 시간.
        // 출력: 한 Frame만큼 목표에 다가간 CameraTargets.
        // Frame-rate independent easing of every target value.
        public static CameraTargets Approach(CameraTargets current, CameraTargets target, float deltaTime)
        {
            return new CameraTargets
            {
                PivotHeight = Approach(current.PivotHeight, target.PivotHeight, ModeSharpness, deltaTime),
                Distance = Approach(current.Distance, target.Distance, ModeSharpness, deltaTime),
                FieldOfView = Approach(current.FieldOfView, target.FieldOfView, ModeSharpness, deltaTime),
            };
        }

        // 기능: Yaw와 Pitch로 Camera 전방 단위 벡터를 계산한다.
        // 입력: yawDegrees - Yaw(도), pitchDegrees - Pitch(도, 양수면 아래).
        // 출력: Quaternion.Euler(pitch, yaw, 0) * forward와 같은 방향 벡터.
        // Same convention as Quaternion.Euler(pitch, yaw, 0) * Vector3.forward: positive pitch looks down.
        public static Vector3 Forward(float yawDegrees, float pitchDegrees)
        {
            float yaw = yawDegrees * Mathf.Deg2Rad;
            float pitch = pitchDegrees * Mathf.Deg2Rad;
            float cosPitch = Mathf.Cos(pitch);
            return new Vector3(Mathf.Sin(yaw) * cosPitch, -Mathf.Sin(pitch), Mathf.Cos(yaw) * cosPitch);
        }

        // 기능: Yaw 방향의 수평 오른쪽 단위 벡터를 계산한다.
        // 입력: yawDegrees - Yaw(도).
        // 출력: Y가 0인 오른쪽 방향 벡터.
        // Horizontal right of the yaw heading (matches MovementSimulation's right vector).
        public static Vector3 Right(float yawDegrees)
        {
            float yaw = yawDegrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));
        }

        // 기능: 현재 값을 목표 값 쪽으로 Frame Rate와 무관하게 지수 보간한다.
        // 입력: current - 현재 값, target - 목표 값, sharpness - 수렴 속도, deltaTime - Frame 경과 시간.
        // 출력: 보간된 값.
        // Frame-rate independent exponential approach.
        public static float Approach(float current, float target, float sharpness, float deltaTime)
        {
            return current + (target - current) * (1f - Mathf.Exp(-sharpness * deltaTime));
        }

        // 기능: 벽 충돌로 허용된 거리와 현재 Camera 거리로 이번 Frame 거리를 정한다.
        // 입력: current - 현재 거리, allowed - 충돌 검사로 허용된 거리, deltaTime - Frame 경과 시간.
        // 출력: allowed가 더 짧으면 allowed, 아니면 ReturnSharpness로 allowed 쪽으로 보간한 거리.
        // A wall closer than the current distance pulls the camera in at once (never show the wall's
        // inside); when the way clears, the camera eases back out.
        public static float ResolveDistance(float current, float allowed, float deltaTime)
        {
            return allowed < current ? allowed : Approach(current, allowed, ReturnSharpness, deltaTime);
        }

        // 기능: 기본 허리 Camera 목표값(Hip)으로 어깨 Camera 자세를 계산한다.
        // 입력: feet - 발 위치, yaw - Yaw(도), pitch - Pitch(도), aimBlend - 조준 Blend(0~1), currentDistance - 현재 Camera 거리, deltaTime - Frame 경과 시간, caster - 충돌 검사용 SphereCaster.
        // 출력: Hip 목표로 계산한 ShoulderPose.
        public static ShoulderPose Solve(Vector3 feet, float yaw, float pitch, float aimBlend, float currentDistance,
            float deltaTime, ISphereCaster caster) => Solve(feet, yaw, pitch, aimBlend, currentDistance, deltaTime, caster, Hip);

        // 기능: Pivot에서 어깨, 어깨에서 Camera까지 두 단계 충돌 검사로 어깨 Camera 자세를 계산한다.
        // 입력: feet - 발 위치, yaw - Yaw(도), pitch - Pitch(도), aimBlend - 조준 Blend(0~1), currentDistance - 현재 Camera 거리, deltaTime - Frame 경과 시간, caster - 충돌 검사용 SphereCaster, hip - 이동 모드에 맞춰 보간된 허리 Camera 목표값.
        // 출력: 충돌 후 어깨 위치, Camera 위치, 전방, 거리, FOV를 담은 ShoulderPose.
        // Phase 12 D14: hip is the mode's (eased) camera; aiming blends from it to the aim camera as before.
        public static ShoulderPose Solve(Vector3 feet, float yaw, float pitch, float aimBlend, float currentDistance,
            float deltaTime, ISphereCaster caster, in CameraTargets hip)
        {
            float t = Mathf.Clamp01(aimBlend);
            float targetDistance = Mathf.Lerp(hip.Distance, AimDistance, t);
            float shoulderOffset = Mathf.Lerp(HipShoulder, AimShoulder, t);
            Vector3 forward = Forward(yaw, pitch);
            Vector3 right = Right(yaw);
            Vector3 pivot = feet + new Vector3(0f, hip.PivotHeight, 0f);

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
                FieldOfView = Mathf.Lerp(hip.FieldOfView, AimFov, t),
            };
        }
    }
}
