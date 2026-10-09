using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.CameraControl
{
    // Casts a sphere of the camera's collision radius. ShoulderCamera implements it with
    // Physics.SphereCast; tests use a fake, so the two-stage solve runs without Unity physics.
    public interface ISphereCaster
    {
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
        // Phase 14 D14: downed, the camera drops near the ground (the downed eye is 0.6 m).
        public const float DownedPivotHeight = 0.7f;
        public const float AirDistance = 6f;
        public const float AirFov = 70f;
        public const float TransportDistance = 12f;
        public const float ModeSharpness = 5f;
        // Phase 19 D11: seated in a vehicle, a third-person camera 8 m back and 2.5 m above the vehicle's centre.
        public const float VehicleDistance = 8f;
        public const float VehiclePivotHeight = 2.5f;
        public const float VehicleFov = 66f;

        public static CameraTargets Hip => new CameraTargets { PivotHeight = PivotHeight, Distance = HipDistance, FieldOfView = HipFov };
        // Phase 19 D11: the vehicle camera (its feet are the vehicle's centre on the ground).
        public static CameraTargets Vehicle =>
            new CameraTargets { PivotHeight = VehiclePivotHeight, Distance = VehicleDistance, FieldOfView = VehicleFov };

        // 기능: 모드가 원하는 허리 카메라 값을 고른다(D14: 질주는 넓게, 웅크리기·슬라이드는 낮게, 낙하·글라이드는 멀고 넓게,
        //   탑승은 더 멀리. Phase 14: 기절은 더 낮게).
        // 입력: mode - 따라가는 캐릭터의 이동 모드, sprinting - 질주 중인지.
        // 출력: 기준 높이·거리·FOV 목표.
        public static CameraTargets TargetsFor(MovementMode mode, bool sprinting)
        {
            CameraTargets targets = Hip;
            switch (mode)
            {
                case MovementMode.Crouch:
                case MovementMode.Slide:
                    targets.PivotHeight = CrouchPivotHeight;
                    break;
                case MovementMode.Downed:
                    targets.PivotHeight = DownedPivotHeight;
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

        // 기능: 카메라 목표(기준 높이·거리·FOV) 세 값을 ModeSharpness로 각각 목표 쪽으로 완화한다.
        // 입력: current - 지금 값, target - 목표 값, deltaTime - 프레임 시간.
        // 출력: 한 프레임만큼 목표에 가까워진 CameraTargets.
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

        // 기능: Yaw·Pitch(도)에서 카메라 앞 방향 단위 벡터를 삼각함수로 구한다.
        // 입력: yawDegrees - 수평 회전(도), pitchDegrees - 수직 회전(도, 양수가 아래를 본다).
        // 출력: Quaternion.Euler(pitch, yaw, 0) * Vector3.forward와 같은 앞 방향 벡터.
        // Same convention as Quaternion.Euler(pitch, yaw, 0) * Vector3.forward: positive pitch looks down.
        public static Vector3 Forward(float yawDegrees, float pitchDegrees)
        {
            float yaw = yawDegrees * Mathf.Deg2Rad;
            float pitch = pitchDegrees * Mathf.Deg2Rad;
            float cosPitch = Mathf.Cos(pitch);
            return new Vector3(Mathf.Sin(yaw) * cosPitch, -Mathf.Sin(pitch), Mathf.Cos(yaw) * cosPitch);
        }

        // 기능: Yaw 방향의 수평 오른쪽 단위 벡터를 구한다.
        // 입력: yawDegrees - 수평 회전(도).
        // 출력: MovementSimulation의 right 벡터와 같은 수평 오른쪽 벡터(Y는 0).
        // Horizontal right of the yaw heading (matches MovementSimulation's right vector).
        public static Vector3 Right(float yawDegrees)
        {
            float yaw = yawDegrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));
        }

        // 기능: 값 하나를 프레임 시간에 무관한 지수 완화로 목표 쪽으로 옮긴다.
        // 입력: current - 지금 값, target - 목표 값, sharpness - 완화 속도(클수록 빠르다), deltaTime - 프레임 시간.
        // 출력: current에서 목표 쪽으로 (1 - e^(-sharpness * deltaTime)) 비율만큼 간 값.
        // Frame-rate independent exponential approach.
        public static float Approach(float current, float target, float sharpness, float deltaTime)
        {
            return current + (target - current) * (1f - Mathf.Exp(-sharpness * deltaTime));
        }

        // 기능: 충돌이 허용하는 거리로 카메라 거리를 정한다. 벽이 더 가까우면 즉시 당기고, 길이 트이면 ReturnSharpness로 천천히 되돌린다.
        // 입력: current - 지금 카메라 거리, allowed - 충돌 검사가 허용한 최대 거리, deltaTime - 프레임 시간.
        // 출력: allowed가 current보다 작으면 allowed, 아니면 current에서 allowed 쪽으로 완화한 거리.
        // A wall closer than the current distance pulls the camera in at once (never show the wall's
        // inside); when the way clears, the camera eases back out.
        public static float ResolveDistance(float current, float allowed, float deltaTime)
        {
            return allowed < current ? allowed : Approach(current, allowed, ReturnSharpness, deltaTime);
        }

        // 기능: 기본 허리 카메라(Hip)로 어깨 카메라 자세를 푼다(모드 목표를 받는 Solve의 축약형).
        // 입력: feet - 따라갈 발 위치, yaw·pitch - 카메라 회전(도), aimBlend - 조준 혼합(0 허리, 1 조준), currentDistance - 지금 카메라 거리,
        //   deltaTime - 프레임 시간, caster - 충돌 검사용 구 Cast.
        // 출력: 충돌을 반영한 어깨점·카메라 위치·앞 방향·거리·FOV의 ShoulderPose.
        public static ShoulderPose Solve(Vector3 feet, float yaw, float pitch, float aimBlend, float currentDistance,
            float deltaTime, ISphereCaster caster) => Solve(feet, yaw, pitch, aimBlend, currentDistance, deltaTime, caster, Hip);

        // 기능: 어깨 카메라 자세를 두 단계 충돌로 푼다(1단계 기준점→어깨점을 오른쪽으로 Cast, 2단계 어깨점→카메라 위치를 뒤로 Cast).
        // 입력: feet - 따라갈 발 위치, yaw·pitch - 카메라 회전(도), aimBlend - 조준 혼합(0 허리, 1 조준), currentDistance - 지금 카메라 거리,
        //   deltaTime - 프레임 시간, caster - 충돌 검사용 구 Cast, hip - 모드별로 완화된 허리 카메라 목표.
        // 출력: 충돌을 반영한 어깨점·카메라 위치·앞 방향·거리·FOV의 ShoulderPose. 거리는 ResolveDistance로 당기거나 되돌린 값.
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
