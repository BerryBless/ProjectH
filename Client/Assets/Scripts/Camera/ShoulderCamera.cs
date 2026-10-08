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
        // Phase 17 D3: our own shots' camera kick, added on top of Pitch (Pitch grows downwards, so the kick is subtracted).
        private readonly RecoilKick _recoil = new RecoilKick();

        public ShoulderCamera(Camera camera)
        {
            _camera = camera;
            _transform = camera.transform;
            _camera.nearClipPlane = NearClip;
        }

        public float Yaw { get; private set; }
        public float Pitch { get; private set; } = 10f;
        // Phase 17 D3: the pitch the camera shows: Pitch kicked up by the recoil, inside the pitch limits. AimRay uses it.
        public float AimPitch => Mathf.Clamp(Pitch - _recoil.Offset, MinPitch, MaxPitch);
        public float RecoilOffset => _recoil.Offset;

        // 기능: 내 사격 하나의 반동을 카메라에 더한다(Phase 17 D3, 표시만; 서버로 가는 조준은 다음 프레임 카메라를 따른다).
        // 입력: degrees - 무기의 RecoilDegrees.
        // 출력: 반환값 없음. 반동 Offset이 늘어난다.
        public void Kick(float degrees) => _recoil.Kick(degrees);

        // 기능: 반동을 없앤다(사망·부활·끊김).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void ResetRecoil() => _recoil.Reset();

        // Screen-centre ray. It starts at the shoulder point, which lies on the camera's forward axis,
        // so geometry between the camera and the player is never picked as the aim point.
        public Ray AimRay { get; private set; }

        // 기능: 마우스 이동만큼 카메라 Yaw·Pitch를 돌린다(조준 중이면 감도를 줄인다).
        // 입력: lookDelta - 이번 프레임의 마우스 이동(입력이 막혀 있으면 호출자가 0을 넘긴다), aiming - 조준 중인지.
        // 출력: 반환값 없음. Yaw(0–360)와 Pitch(MinPitch–MaxPitch)가 바뀐다. lookDelta가 0이면 아무것도 바뀌지 않는다.
        // The cursor-lock gate lives in the caller (GameClient passes zero when input is blocked, which includes a free
        // cursor). Checking Cursor.lockState here too stopped QA look, whose real cursor never locks (QaAssumeCursorLocked).
        public void ApplyLook(Vector2 lookDelta, bool aiming)
        {
            if (lookDelta == Vector2.zero) return;
            float sensitivity = aiming ? Sensitivity * ShoulderCameraMath.AimSensitivityScale : Sensitivity;
            Yaw = Mathf.Repeat(Yaw + lookDelta.x * sensitivity, 360f);
            Pitch = Mathf.Clamp(Pitch - lookDelta.y * sensitivity, MinPitch, MaxPitch);
        }

        // Call from LateUpdate with the rendered feet position, and the mode of whoever is followed (D14).
        // 기능: 카메라를 따라갈 발 위치에 놓는다(조준 줌, 모드별 거리, 벽 충돌). Phase 17 D3: 반동을 프레임 시간만큼 되돌리고 반동이 더해진
        //   AimPitch로 회전과 조준 광선을 정한다.
        // 입력: targetFeet - 따라갈 발 위치, aiming - 조준 중, deltaTime - 프레임 시간, mode - 따라가는 사람의 이동 모드, sprinting - 달리기 중.
        // 출력: 반환값 없음. 카메라 Transform·FOV와 AimRay가 바뀐다.
        public void Follow(Vector3 targetFeet, bool aiming, float deltaTime, MovementMode mode = MovementMode.Ground, bool sprinting = false)
        {
            _aimBlend = ShoulderCameraMath.Approach(_aimBlend, aiming ? 1f : 0f, AimBlendSharpness, deltaTime);
            _hip = ShoulderCameraMath.Approach(_hip, ShoulderCameraMath.TargetsFor(mode, sprinting), deltaTime);
            _recoil.Step(deltaTime);
            float pitch = AimPitch;
            ShoulderPose pose = ShoulderCameraMath.Solve(targetFeet, Yaw, pitch, _aimBlend, _distance, deltaTime, _caster, _hip);
            _distance = pose.Distance;
            _transform.SetPositionAndRotation(pose.Position, Quaternion.Euler(pitch, Yaw, 0f));
            _camera.fieldOfView = pose.FieldOfView;
            AimRay = new Ray(pose.Shoulder, pose.Forward);
        }

        // Single-result SphereCast: no allocation. Only the world is hit: the local view has no collider, and
        // remote views' box colliders are on the Ignore Raycast layer (PlayerViewFactory.RemoteHitLayer), which
        // DefaultRaycastLayers excludes.
        // The pivot (feet + 1.6 m) is inside the character's collision box, which the simulation keeps
        // out of every box, and 0.2 m < 0.35 m half-width, so stage 1 normally starts outside a wall.
        // The pose uses RenderPosition, which carries the decaying reconcile offset, so for about
        // 0.1-0.3 s after a misprediction near a wall the origin can still be inside a collider.
        private sealed class PhysicsSphereCaster : ISphereCaster
        {
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
