using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.CameraControl
{
    // Presentation only (D8, D10): over-the-right-shoulder camera with aim (ADS) zoom and wall
    // collision. It never writes simulation or network state; its Yaw is copied into each
    // InputCommand, the only way camera input reaches the server. Pitch stays local (D15).
    public sealed class ShoulderCamera
    {
        private const float Sensitivity = 0.1f;
        private const float MinPitch = -30f;
        private const float MaxPitch = 70f;
        private const float AimBlendSharpness = 12f;
        private const float CollisionRadius = 0.2f;
        // Below CollisionRadius, so the near plane cannot reach into a wall the sphere stopped at.
        private const float NearClip = 0.05f;

        private readonly Camera _camera;
        private readonly Transform _transform;
        private readonly PhysicsSphereCaster _caster = new PhysicsSphereCaster();
        private float _aimBlend;
        private float _distance = ShoulderCameraMath.HipDistance;
        // Phase 12 D14: the hip camera, eased towards the followed character's mode.
        private CameraTargets _hip = ShoulderCameraMath.Hip;

        // 기능: Unity Camera를 어깨 시점 Camera로 감싼다.
        // 입력: camera - 제어할 Unity Camera.
        // 출력: Yaw 0, Pitch 10도, 허리 거리로 초기화된 ShoulderCamera. Camera의 near clip이 NearClip으로 바뀐다.
        public ShoulderCamera(Camera camera)
        {
            _camera = camera;
            _transform = camera.transform;
            _camera.nearClipPlane = NearClip;
        }

        public float Yaw { get; private set; }
        public float Pitch { get; private set; } = 10f;

        // Screen-centre ray. It starts at the shoulder point, which lies on the camera's forward axis,
        // so geometry between the camera and the player is never picked as the aim point.
        public Ray AimRay { get; private set; }

        // 기능: 마우스 이동량으로 Yaw와 Pitch를 바꾼다. Cursor가 잠겨 있지 않으면 무시한다.
        // 입력: lookDelta - 이번 Frame 마우스 이동량, aiming - 조준 중이면 감도를 AimSensitivityScale만큼 낮춘다.
        // 출력: 반환값 없음. Yaw(0~360도 순환)와 Pitch(MinPitch~MaxPitch로 제한)가 갱신된다.
        public void ApplyLook(Vector2 lookDelta, bool aiming)
        {
            if (Cursor.lockState != CursorLockMode.Locked) return;
            float sensitivity = aiming ? Sensitivity * ShoulderCameraMath.AimSensitivityScale : Sensitivity;
            Yaw = Mathf.Repeat(Yaw + lookDelta.x * sensitivity, 360f);
            Pitch = Mathf.Clamp(Pitch - lookDelta.y * sensitivity, MinPitch, MaxPitch);
        }

        // 기능: 대상 발 위치에 맞춰 조준 Blend·이동 모드 목표를 보간하고 벽 충돌을 반영해 Camera를 배치한다.
        // 입력: targetFeet - 따라갈 캐릭터의 렌더링 발 위치, aiming - 조준 중 여부, deltaTime - Frame 경과 시간, mode - 따라가는 캐릭터의 이동 모드, sprinting - 질주 중 여부.
        // 출력: 반환값 없음. Camera 위치·회전·FOV, 현재 거리, AimRay가 갱신된다.
        // Call from LateUpdate with the rendered feet position, and the mode of whoever is followed (D14).
        public void Follow(Vector3 targetFeet, bool aiming, float deltaTime, MovementMode mode = MovementMode.Ground, bool sprinting = false)
        {
            _aimBlend = ShoulderCameraMath.Approach(_aimBlend, aiming ? 1f : 0f, AimBlendSharpness, deltaTime);
            _hip = ShoulderCameraMath.Approach(_hip, ShoulderCameraMath.TargetsFor(mode, sprinting), deltaTime);
            ShoulderPose pose = ShoulderCameraMath.Solve(targetFeet, Yaw, Pitch, _aimBlend, _distance, deltaTime, _caster, _hip);
            _distance = pose.Distance;
            _transform.SetPositionAndRotation(pose.Position, Quaternion.Euler(Pitch, Yaw, 0f));
            _camera.fieldOfView = pose.FieldOfView;
            AimRay = new Ray(pose.Shoulder, pose.Forward);
        }

        // Single-result SphereCast: no allocation. Only the world is hit: the local view has no collider, and
        // remote views' box colliders are on the Ignore Raycast layer (PlayerViewFactory.RemoteHitLayer), which
        // DefaultRaycastLayers excludes.
        // The pivot (feet + 1.6 m, 1.1 m crouched) is inside the character's collision box, which the simulation keeps
        // out of every box, and 0.2 m < 0.35 m half-width, so stage 1 normally starts outside a wall.
        // The pose uses RenderPosition, which carries the decaying reconcile offset, so for about
        // 0.1-0.3 s after a misprediction near a wall the origin can still be inside a collider.
        private sealed class PhysicsSphereCaster : ISphereCaster
        {
            // 기능: Physics.SphereCast로 CollisionRadius 구를 쏘아 월드 Collider와의 첫 충돌을 찾는다.
            // 입력: origin - 시작점, direction - 방향, maxDistance - 최대 거리, hitDistance - 충돌까지의 거리.
            // 출력: 충돌하면 true와 hitDistance, 없으면 false와 0.
            public bool Cast(Vector3 origin, Vector3 direction, float maxDistance, out float hitDistance)
            {
                if (Physics.SphereCast(origin, CollisionRadius, direction, out RaycastHit hit, maxDistance,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    hitDistance = hit.distance;
                    return true;
                }
                hitDistance = 0f;
                return false;
            }
        }
    }
}
